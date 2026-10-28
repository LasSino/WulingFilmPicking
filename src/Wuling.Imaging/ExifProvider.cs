using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Wuling.Imaging;

/// <summary>对比/展示所需的关键 EXIF 字段(设计文档 03 F6)。</summary>
public sealed record ExifSummary
{
    /// <summary>EXIF Orientation 原始值(1-8;0 = 缺失,视作 1)。</summary>
    public ushort Orientation { get; init; } = 1;

    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public string? LensModel { get; init; }
    public string? FocalLength { get; init; }
    public string? Aperture { get; init; }
    public string? Shutter { get; init; }
    public string? Iso { get; init; }
    public string? ExposureBias { get; init; }

    /// <summary>对焦模式(03 F6;厂商优先 EXIF FlashPixVersion 下 AF 字段, 回退 MeteringMode 不可得时为 null)。</summary>
    public string? FocusMode { get; init; }

    public DateTimeOffset? TakenAt { get; init; }

    /// <summary>摘要行(槽位信息条,如 "f/4 1/250 ISO400")。</summary>
    public string ShortSummary
    {
        get
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrEmpty(Aperture))
            {
                parts.Add(Aperture);
            }

            if (!string.IsNullOrEmpty(Shutter))
            {
                parts.Add(Shutter);
            }

            if (!string.IsNullOrEmpty(Iso))
            {
                parts.Add($"ISO {Iso}");
            }

            return parts.Count == 0 ? string.Empty : string.Join(' ', parts);
        }
    }
}

/// <summary>
/// EXIF 读取(设计文档 02 §1: MetadataExtractor 纯托管优先,ExifTool 兜底为 M3+ 可选)。
/// 带按路径+mtime 的进程内缓存。
/// </summary>
public sealed class ExifProvider
{
    private readonly Dictionary<string, (DateTime Mtime, ExifSummary Summary)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ExifSummary GetSummary(string path)
    {
        lock (_gate)
        {
            var mtime = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var hit) && hit.Mtime == mtime)
            {
                return hit.Summary;
            }

            var summary = Read(path);
            _cache[path] = (mtime, summary);
            return summary;
        }
    }

    private static ExifSummary Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var directories = ImageMetadataReader.ReadMetadata(stream);

            var ifd = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();

            ushort orientation = 1;
            string? make = null, model = null, lens = null;
            DateTimeOffset? taken = null;

            if (ifd is not null)
            {
                orientation = ifd.ContainsTag(ExifDirectoryBase.TagOrientation) ? ifd.GetUInt16(ExifDirectoryBase.TagOrientation) : (ushort)1;
                make = ifd.GetString(ExifDirectoryBase.TagMake);
                model = ifd.GetString(ExifDirectoryBase.TagModel);
                if (ifd.ContainsTag(ExifDirectoryBase.TagDateTime) && ifd.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt))
                {
                    taken = dt;
                }
            }

            string? focal = null, aperture = null, shutter = null, iso = null, bias = null;
            if (subIfd is not null)
            {
                if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dto))
                {
                    taken = dto;
                }

                if (subIfd.ContainsTag(ExifDirectoryBase.TagFocalLength))
                {
                    focal = $"{subIfd.GetSingle(ExifDirectoryBase.TagFocalLength):0.#}mm";
                }

                if (subIfd.ContainsTag(ExifDirectoryBase.TagFNumber))
                {
                    aperture = $"f/{subIfd.GetSingle(ExifDirectoryBase.TagFNumber):0.#}";
                }

                if (subIfd.ContainsTag(ExifDirectoryBase.TagExposureTime))
                {
                    var sec = subIfd.GetDouble(ExifDirectoryBase.TagExposureTime);
                    shutter = sec >= 1 ? $"{sec:0.#}s" : $"1/{Math.Round(1 / sec):0}s";
                }

                if (subIfd.ContainsTag(ExifDirectoryBase.TagIsoEquivalent))
                {
                    iso = subIfd.GetInt32(ExifDirectoryBase.TagIsoEquivalent).ToString();
                }

                if (subIfd.ContainsTag(ExifDirectoryBase.TagExposureBias))
                {
                    var v = subIfd.GetSingle(ExifDirectoryBase.TagExposureBias);
                    bias = $"{(v >= 0 ? "+" : string.Empty)}{v:0.#}EV";
                }

                lens = subIfd.GetString(ExifDirectoryBase.TagLensModel);
            }

            // 对焦模式: 无跨厂商标准 tag;取各厂商 AF 字段(MakerNote)常见落点, 不可得为 null
            var focus = ReadFocusMode(directories);

            return new ExifSummary
            {
                Orientation = orientation is >= 1 and <= 8 ? orientation : (ushort)1,
                CameraMake = NullIfEmpty(make),
                CameraModel = NullIfEmpty(model),
                LensModel = NullIfEmpty(lens),
                FocalLength = focal,
                Aperture = aperture,
                Shutter = shutter,
                Iso = iso,
                ExposureBias = bias,
                FocusMode = focus,
                TakenAt = taken,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MetadataException)
        {
            return new ExifSummary();
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>对焦模式: 厂商 MakerNote 无统一 tag, 扫描已知 AF 相关 tag 名(佳能/索尼/富士/尼康常见值)。</summary>
    private static string? ReadFocusMode(IEnumerable<MetadataExtractor.Directory> directories)
    {
        foreach (var dir in directories)
        {
            foreach (var tag in dir.Tags)
            {
                var name = tag.Name;
                var isFocusTag = name.Contains("FocusMode", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("AFFocusMode", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("FocusSetting", StringComparison.OrdinalIgnoreCase);
                if (!isFocusTag)
                {
                    continue;
                }

                var desc = tag.Description;
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40)
                {
                    return desc.Trim();
                }
            }
        }

        return null;
    }
}
