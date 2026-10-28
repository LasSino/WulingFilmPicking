using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace Wuling.Imaging;

/// <summary>RAW 内嵌预览提取结果。</summary>
public sealed record RawPreviewResult
{
    /// <summary>提取出的 JPEG 字节(可直接 SKBitmap.Decode);null = 无内嵌预览。</summary>
    public byte[]? JpegBytes { get; init; }

    /// <summary>内嵌预览像素尺寸(JPEG SOF 解析可得;不可得 null)。</summary>
    public int? Width { get; init; }

    public int? Height { get; init; }

    public bool HasPreview => JpegBytes is not null;
}

/// <summary>
/// RAW 内嵌预览提取(设计 02 §6 分层降级的首选层;选片不需要 100% 画质)。
/// 覆盖: TIFF 系(CR2/NEF/NRW/ARW/ORF/PEF/DNG/RW2 变体)读 IFD0 Preview 条目;
/// RAF(富士)按头部偏移字段直取, 并取全尺寸预览而非 ~160px 缩略图(§6.1 必测注意点)。
/// 结果带进程内缓存(路径+mtime)。
/// </summary>
public static class RawPreviewExtractor
{
    private static readonly ConcurrentDictionary<string, (DateTime Mtime, RawPreviewResult Result)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已知 RAW 扩展名(与 03 F1 一致;不含 dng 配对特例)。</summary>
    public static bool IsRawExtension(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext is "cr2" or "cr3" or "nef" or "nrw" or "arw" or "srf" or "sr2"
            or "raf" or "orf" or "rw2" or "dng" or "pef" or "srw" or "x3f";
    }

    /// <summary>RAW 格式短名(摘要行显示用)。</summary>
    public static string FormatName(string path) => Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

    public static RawPreviewResult Extract(string path)
    {
        var mtime = File.GetLastWriteTimeUtc(path);
        if (Cache.TryGetValue(path, out var hit) && hit.Mtime == mtime)
        {
            return hit.Result;
        }

        RawPreviewResult result;
        try
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            result = ext == "raf" ? ExtractRaf(path) : ExtractTiffLike(path);
            System.Diagnostics.Debug.WriteLine($"[raw-extract] {Path.GetFileName(path)}: start/len via IFD, result bytes={result.JpegBytes?.Length ?? 0} {result.Width}x{result.Height}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[raw-extract] {Path.GetFileName(path)} failed: {ex.Message}");
            result = new RawPreviewResult();
        }

        Cache[path] = (mtime, result);
        return result;
    }

    // ---- TIFF 系: IFD0 的 PreviewImageStart/Length(0x0111/0x0112) 或MakerNote 变体 ----

    private static RawPreviewResult ExtractTiffLike(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        if (fs.Read(header) < 8)
        {
            return new RawPreviewResult();
        }

        var little = header[0] == (byte)'I' && header[1] == (byte)'I';
        if (!little && !(header[0] == (byte)'M' && header[1] == (byte)'M'))
        {
            return new RawPreviewResult(); // 非 TIFF 容器(CR3 是 ISO-BMFF, 后续批次)
        }

        var ifdOffset = little ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) : BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        var (start, length) = FindPreviewInIfd(fs, ifdOffset, little);
        if (start <= 0 || length <= 0)
        {
            return new RawPreviewResult();
        }

        return BuildResult(ReadAt(fs, start, length));
    }

    /// <summary>在单个 IFD 里找 Preview 条目;若无, 深入第一个 SubIFD(厂商常把预览放 SubIFD/MakerNote)。</summary>
    private static (long Start, int Length) FindPreviewInIfd(FileStream fs, uint ifdOffset, bool little)
    {
        // RW2/RW(松下私有变体): 内嵌 JPEG 在 JpgFromRaw(0x002E)/JpgFromRaw2(0x002F);
        // 标准 PreviewImageStart(0x0111) 在松下变体里常为 0xFFFFFFFF(无效占位) —— 优先私有 tag
        Span<uint> startTags = [0x002E, 0x002F, 0x0111, 0x0201];
        Span<uint> lengthTags = [0x002E, 0x002F, 0x0112, 0x0202];
        var (start, length) = ScanIfd(fs, ifdOffset, little, startTags, lengthTags);
        if (start > 0)
        {
            return (start, length);
        }

        // SubIFD 指针(0x014A): DNG/NEF 常把全尺寸预览放这里
        var subOffsets = ReadTagValues(fs, ifdOffset, little, 0x014A, maxCount: 4);
        foreach (var sub in subOffsets)
        {
            (start, length) = ScanIfd(fs, sub, little, startTags, lengthTags);
            if (start > 0)
            {
                return (start, length);
            }
        }

        return (0, 0);
    }

    private static (long Start, int Length) ScanIfd(FileStream fs, uint ifdOffset, bool little, Span<uint> startTags, Span<uint> lengthTags)
    {
        // 松下私有 tag(0x002E/0x002F)语义与标准不同: Count=字节长度, ValueOffset=数据偏移 —— 同一 tag 双信息,
        // 与标准 PreviewImageStart(0x0111: value=offset)/PreviewImageLength(0x0112: value=length)分开处理。
        const uint PanasonicJpeg = 0x002E;
        const uint PanasonicJpeg2 = 0x002F;

        foreach (var tag in ScanTags(fs, ifdOffset, little))
        {
            if (tag.Tag is PanasonicJpeg or PanasonicJpeg2
                && tag.Count is > 4 and < 512 * 1024 * 1024
                && tag.ValueOffset is not 0xFFFFFFFF and > 0)
            {
                return (tag.ValueOffset, unchecked((int)tag.Count));
            }
        }

        long start = 0;
        var length = 0;
        foreach (var tag in ScanTags(fs, ifdOffset, little))
        {
            var si = startTags.IndexOf(tag.Tag);
            var li = lengthTags.IndexOf(tag.Tag);
            // LONG(4)/SHORT 值内联在 valueOffset 字段;无效占位(0xFFFFFFFF)按缺失处理
            if (si >= 0 && tag.ValueOffset is not 0xFFFFFFFF)
            {
                if (si == 0 || start == 0)
                {
                    start = tag.ValueOffset;
                }
            }
            else if (li >= 0 && tag.ValueOffset is not 0xFFFFFFFF)
            {
                length = unchecked((int)tag.ValueOffset);
            }
        }

        return (start, length);
    }

    private readonly record struct IfdTag(uint Tag, uint Count, uint ValueOffset);

    private static IEnumerable<IfdTag> ScanTags(FileStream fs, uint ifdOffset, bool little)
    {
        Span<byte> buf = stackalloc byte[2];
        if (!ReadExact(fs, ifdOffset, buf))
        {
            yield break;
        }

        var count = little ? BinaryPrimitives.ReadUInt16LittleEndian(buf) : BinaryPrimitives.ReadUInt16BigEndian(buf);
        if (count is 0 or > 512)
        {
            yield break;
        }

        var entry = new byte[12];
        for (var i = 0; i < count; i++)
        {
            var at = ifdOffset + 2 + (uint)(i * 12);
            if (!ReadExact(fs, at, entry))
            {
                yield break;
            }

            var tag = little ? BinaryPrimitives.ReadUInt16LittleEndian(entry) : BinaryPrimitives.ReadUInt16BigEndian(entry);
            var type = little ? BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(2)) : BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(2));
            var cnt = little ? BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(4));
            var value = little ? BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(8)) : BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(8));
            yield return new IfdTag(tag, type == 3 ? cnt : cnt, value);
        }
    }

    private static uint[] ReadTagValues(FileStream fs, uint ifdOffset, bool little, uint wantTag, int maxCount)
    {
        var results = new List<uint>();
        foreach (var tag in ScanTags(fs, ifdOffset, little))
        {
            if (tag.Tag != wantTag)
            {
                continue;
            }

            // LONG 类型内联在 valueOffset; 数组在偏移处
            if (tag.Count == 1)
            {
                results.Add(tag.ValueOffset);
            }
            else if (tag.Count >= 4 && tag.Count <= 16)
            {
                var buf = new byte[4]; // 循环内不能用 stackalloc(CA2014)
                for (var i = 0; i < tag.Count && results.Count < maxCount; i++)
                {
                    if (ReadExact(fs, tag.ValueOffset + (uint)(i * 4), buf))
                    {
                        results.Add(little ? BinaryPrimitives.ReadUInt32LittleEndian(buf) : BinaryPrimitives.ReadUInt32BigEndian(buf));
                    }
                }
            }

            if (results.Count >= maxCount)
            {
                break;
            }
        }

        return [.. results];
    }

    // ---- RAF: 头部偏移字段直取(02 §6.1: 取全尺寸预览, 而非 ~160px 缩略图) ----

    private static RawPreviewResult ExtractRaf(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[84];
        if (!ReadExact(fs, 0, header))
        {
            return new RawPreviewResult();
        }

        // RAF 头: "FUJIFILMCCD-RAW ..." 后 4 组 u32 偏移
        // [0x54] jpegThumbStart? 布局: 0x54=objectData... 实测字段(社区逆向与 MetadataExtractor 一致):
        //   0x54: CFA 头起点, 0x58: CFA 长度, 0x5C: PreviewImageStart(全尺寸 JPEG), 0x60: PreviewImageLength
        var previewStart = BinaryPrimitives.ReadUInt32BigEndian(header[0x5C..]);
        var previewLen = BinaryPrimitives.ReadUInt32BigEndian(header[0x60..]);
        if (previewStart <= 0 || previewLen <= 0 || previewStart + previewLen > fs.Length)
        {
            return new RawPreviewResult();
        }

        return BuildResult(ReadAt(fs, previewStart, unchecked((int)previewLen)));
    }

    // ---- 共用: JPEG SOF0/2 解析内嵌预览尺寸 ----

    private static RawPreviewResult BuildResult(byte[]? jpeg)
    {
        if (jpeg is null || jpeg.Length < 4)
        {
            return new RawPreviewResult();
        }

        // 快速验证是 JPEG(FFD8) —— 内嵌预览尾部常有填充字节(如 RW2 的 ffffffff), 按 FFD9 截断
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return new RawPreviewResult();
        }

        var end = FindJpegEnd(jpeg);
        if (end is > 4 && end < jpeg.Length)
        {
            jpeg = jpeg[..end.Value];
        }

        var (w, h) = ParseJpegSize(jpeg);
        return new RawPreviewResult { JpegBytes = jpeg, Width = w, Height = h };
    }

    /// <summary>找 JPEG 真实尾部(FFD9)。</summary>
    /// <remarks>
    /// 注意: SOS(FFDA) 之后是熵编码数据, 不能再按"段长跳转"走 —— 熵数据中的 FF00(转义)与 FFD0-D7(RST)
    /// 会被误读为标记导致走飞(实测 RW2 走到 8 字节截断)。正确做法: SOS 前按段链走, SOS 后线性扫 FFD9。
    /// </remarks>
    private static int? FindJpegEnd(ReadOnlySpan<byte> jpeg)
    {
        var i = 2;
        while (i + 1 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = jpeg[i + 1];
            if (marker == 0xD9)
            {
                return i + 2;
            }

            if (marker == 0x01 || marker == 0xD8 || marker is >= 0xD0 and <= 0xD7)
            {
                i += 2;
                continue;
            }

            if (i + 3 >= jpeg.Length)
            {
                break;
            }

            var segLen = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (segLen < 2)
            {
                break;
            }

            if (marker == 0xDA)
            {
                // SOS: 段结构到此结束, 从段尾开始线性扫描熵数据找 FFD9
                var j = i + 2 + segLen;
                while (j + 1 < jpeg.Length)
                {
                    if (jpeg[j] == 0xFF && jpeg[j + 1] == 0xD9)
                    {
                        return j + 2;
                    }

                    j++;
                }

                return null;
            }

            i += 2 + segLen;
        }

        return null;
    }

    private static (int? Width, int? Height) ParseJpegSize(ReadOnlySpan<byte> jpeg)
    {
        var i = 2;
        while (i + 4 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = jpeg[i + 1];
            if (marker is 0xC0 or 0xC1 or 0xC2) // SOF0 基线 / SOF1 扩展 / SOF2 渐进
            {
                var h = (jpeg[i + 5] << 8) | jpeg[i + 6];
                var w = (jpeg[i + 7] << 8) | jpeg[i + 8];
                return (w, h);
            }

            if (marker == 0xD8 || marker is >= 0xD0 and <= 0xD7)
            {
                i += 2;
                continue;
            }

            var segLen = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (segLen < 2)
            {
                break;
            }

            i += 2 + segLen;
        }

        return (null, null);
    }

    /// <summary>只读文件头解析 JPEG 尺寸(不解码像素;展示类摘要行用)。</summary>
    public static (int? Width, int? Height) ProbeJpegSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[2];
            if (fs.Read(head) < 2 || head[0] != 0xFF || head[1] != 0xD8)
            {
                return (null, null);
            }

            Span<byte> seg = stackalloc byte[4];
            long pos = 2;
            while (pos + 4 < fs.Length)
            {
                if (!ReadExact(fs, pos, seg))
                {
                    break;
                }

                if (seg[0] != 0xFF)
                {
                    pos++;
                    continue;
                }

                var marker = seg[1];
                if (marker is 0xC0 or 0xC1 or 0xC2)
                {
                    Span<byte> sof = stackalloc byte[7];
                    if (!ReadExact(fs, pos + 2, sof))
                    {
                        break;
                    }

                    var h = (sof[1] << 8) | sof[2];
                    var w = (sof[3] << 8) | sof[4];
                    return (w, h);
                }

                if (marker == 0xD8 || marker is >= 0xD0 and <= 0xD7)
                {
                    pos += 2;
                    continue;
                }

                var len = (seg[2] << 8) | seg[3];
                if (len < 2)
                {
                    break;
                }

                pos += 2 + len;
            }
        }
        catch (Exception)
        {
            // 读不了 → 未知尺寸
        }

        return (null, null);
    }

    private static byte[] ReadAt(FileStream fs, long start, int length)
    {
        if (length <= 0 || length > 512 * 1024 * 1024)
        {
            return [];
        }

        var buf = new byte[length];
        fs.Seek(start, SeekOrigin.Begin);
        var read = 0;
        while (read < length)
        {
            var n = fs.Read(buf, read, length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        Array.Resize(ref buf, read);
        return buf;
    }

    private static bool ReadExact(FileStream fs, long at, Span<byte> buffer)
    {
        if (at < 0 || at >= fs.Length)
        {
            return false;
        }

        fs.Seek(at, SeekOrigin.Begin);
        var total = 0;
        while (total < buffer.Length)
        {
            var n = fs.Read(buffer[total..]);
            if (n <= 0)
            {
                return false;
            }

            total += n;
        }

        return true;
    }
}
