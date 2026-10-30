using System.Collections.Concurrent;
using Wuling.Core.Models;
using Wuling.Imaging;

namespace Wuling.Imaging;

/// <summary>
/// 两级图像源(设计文档 02 §7 M2 版):
/// - 浏览条: GetThumbnail(edge=96) 快速小图;
/// - 对比区: GetView(edge) 按槽位实际尺寸解码/缩放的全清晰度视图;
/// - 全部输出经 ApplyOrientation 归一化 EXIF 方向(修复 M1 旋转问题);
/// - 同 key 并发单飞,内存 LRU 按字节计量,双级共用一个缓存池。
///
/// 所有权契约(2026-10-31 快照语义): DecodedThumbnail.Bitmap 是调用方独占的拷贝,
/// 调用方负责 Dispose;缓存内的共享位图永不出服务 —— LRU 驱逐与 Dispose 都不会
/// 悬空任何借出方(此前共享外借在切目录/驱逐窗口期产生 AccessViolationException)。
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<CacheEntry?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly object _gate = new();
    private readonly long _capacityBytes;
    private long _currentBytes;
    private readonly ExifProvider _exif;

    public ThumbnailService(long capacityBytes = 512 * 1024 * 1024, ExifProvider? exifProvider = null)
    {
        _capacityBytes = capacityBytes;
        _exif = exifProvider ?? new ExifProvider();
    }

    /// <summary>浏览条小图(线程安全;同 key 并发只解一次)。</summary>
    public DecodedThumbnail GetThumbnail(PhotoPair pair, int edge = 96) => GetView(pair, edge);

    /// <summary>对比区视图: 按需要的边长解码/缩放(线程安全)。</summary>
    public DecodedThumbnail GetView(PhotoPair pair, int edge)
    {
        var key = CacheKey(pair.PreviewPath, edge);
        var entry = _cache.GetOrAdd(key, static (k, self) => new Lazy<CacheEntry?>(() => self.Decode(k), LazyThreadSafetyMode.ExecutionAndPublication), this)
            .Value;

        if (entry is null)
        {
            return DecodedThumbnail.Fail(DecodeStatus.IoError);
        }

        Touch(key);
        // 快照语义: 与驱逐/Dispose 互斥地做一次深拷贝, 借出方持有独占副本。
        // 缓存内共享位图绝不离开服务, 调用方用完 Dispose 即可。
        SkiaSharp.SKBitmap? copy;
        lock (_gate)
        {
            copy = entry.Snapshot();
        }

        if (copy is null)
        {
            // 极小概率: 拿到 entry 后被 Dispose 竞态释放 → 按缓存未命中重试一次
            return GetView(pair, edge);
        }

        return new DecodedThumbnail(DecodeStatus.Ok, copy, entry.SourceWidth, entry.SourceHeight);
    }

    internal static string CacheKey(string path, int edge) => $"{path}|{edge}";

    private CacheEntry? Decode(string key)
    {
        var separator = key.LastIndexOf('|');
        var path = key[..separator];
        var edge = int.Parse(key[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var stream = File.OpenRead(path);
            var original = SkiaSharp.SKBitmap.Decode(stream);
            if (original is null)
            {
                return null;
            }

            // 1) 方向归一化(修 M1 的旋转问题;浏览条与对比区一次修复)。
            //    无需处理(orientation 0/1)时返回的就是 original 本身 —— 所有权转移。
            var oriented = ApplyOrientation(original, _exif.GetSummary(path).Orientation);
            if (!ReferenceEquals(oriented, original))
            {
                original.Dispose();
            }

            // 2) 按需缩放(目标边长;原图不足目标时保留原尺寸)。
            //    scale>=1 时返回的就是 oriented 本身, 而它将进入缓存条目被长期持有,
            //    因此绝不能用 using 在本方法退出时释放 —— 否则缓存里存的是已释放
            //    的 SKBitmap, 之后 GetPixels 访问 ByteCount/GetPixels 即
            //    AccessViolationException(JPEG 预览 edge=0 路径必现闪退的根因)。
            var scaled = Scale(oriented, edge);
            var sourceWidth = oriented.Width;
            var sourceHeight = oriented.Height;
            if (!ReferenceEquals(scaled, oriented))
            {
                oriented.Dispose();
            }

            var entry = new CacheEntry(scaled, sourceWidth, sourceHeight);
            lock (_gate)
            {
                _currentBytes += entry.Bytes;
                EvictWhileOverCapacity(keepKey: key);
            }

            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>按 EXIF Orientation(1-8) 归一化像素方向;无需处理时返回原对象。</summary>
    public static SkiaSharp.SKBitmap ApplyOrientation(SkiaSharp.SKBitmap source, ushort orientation)
    {
        if (orientation is 0 or 1)
        {
            return source;
        }

        // 输出尺寸: 90° 倍数方向(5-8)时宽高互换
        var swap = orientation is >= 5 and <= 8;
        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;

        var dest = new SkiaSharp.SKBitmap(width, height, source.ColorType, source.AlphaType);
        using var canvas = new SkiaSharp.SKCanvas(dest);
        using var paint = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.Medium };

        // 显式矩阵组合(轴心统一在原点,再平移回正区间), 几何可推导:
        // EXIF 文档定义"存储像素 → 显示方向"的变换,逆推即得此处矩阵。
        var sw = (float)source.Width;
        var sh = (float)source.Height;
        var matrix = orientation switch
        {
            2 => SkiaSharp.SKMatrix.CreateScale(-1, 1, sw, 0),                       // 水平镜像
            3 => SkiaSharp.SKMatrix.CreateRotationDegrees(180, sw / 2f, sh / 2f),    // 旋转 180°
            4 => SkiaSharp.SKMatrix.CreateScale(1, -1, 0, sh),                       // 垂直镜像
            5 => MirrorThenRotate(sw, sh, width, clockwise: true),                   // 镜像 + CW 90°
            6 => SkiaSharp.SKMatrix.CreateRotationDegrees(90)                        // CW 90°: (x,y)->(-y,x) 再平移
                  .PostConcat(SkiaSharp.SKMatrix.CreateTranslation(sh, 0)),
            7 => MirrorThenRotate(sw, sh, width, clockwise: false),                  // 镜像 + CCW 90°
            8 => SkiaSharp.SKMatrix.CreateRotationDegrees(-90)                       // CCW 90°: (x,y)->(y,-x) 再平移
                  .PostConcat(SkiaSharp.SKMatrix.CreateTranslation(0, sw)),
            _ => SkiaSharp.SKMatrix.CreateIdentity(),
        };

        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0, paint);
        canvas.Flush();
        return dest;
    }

    /// <summary>orientation 5/7: 镜像与 90° 旋转的组合矩阵。</summary>
    private static SkiaSharp.SKMatrix MirrorThenRotate(float sw, float sh, float destWidth, bool clockwise)
    {
        // 镜像(x -> -x), 再旋转 ±90°, 最后平移进非负区间
        var mirror = SkiaSharp.SKMatrix.CreateScale(-1, 1);
        var rotate = SkiaSharp.SKMatrix.CreateRotationDegrees(clockwise ? 90 : -90);
        mirror.PostConcat(rotate);
        var dx = clockwise ? 0 : destWidth;
        var dy = clockwise ? sw : sh;
        return mirror.PostConcat(SkiaSharp.SKMatrix.CreateTranslation(dx, dy));
    }

    /// <summary>按目标边长等比缩放;不足目标尺寸时原样返回。</summary>
    private static SkiaSharp.SKBitmap Scale(SkiaSharp.SKBitmap original, int edge)
    {
        var scale = Math.Min((double)edge / original.Width, (double)edge / original.Height);
        if (scale >= 1)
        {
            return original;
        }

        var width = Math.Max(1, (int)Math.Round(original.Width * scale));
        var height = Math.Max(1, (int)Math.Round(original.Height * scale));
        var scaled = new SkiaSharp.SKBitmap(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        if (!original.ScalePixels(scaled, SkiaSharp.SKFilterQuality.Medium))
        {
            scaled.Dispose();
            return original;
        }

        return scaled;
    }

    private void Touch(string key)
    {
        lock (_gate)
        {
            var node = _lru.Find(key);
            if (node is not null)
            {
                _lru.Remove(node);
            }

            _lru.AddFirst(key);
        }
    }

    private void EvictWhileOverCapacity(string keepKey)
    {
        while (_currentBytes > _capacityBytes && _lru.Count > 1)
        {
            var last = _lru.Last!;
            if (last.Value == keepKey)
            {
                break;
            }

            if (_cache.TryRemove(last.Value, out var removed))
            {
                var entry = removed.Value;
                if (entry is not null)
                {
                    _currentBytes -= entry.Bytes;
                    entry.Dispose();
                }
            }

            _lru.RemoveLast();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var lazy in _cache.Values)
            {
                lazy.Value?.Dispose();
            }

            _cache.Clear();
            _lru.Clear();
        }
    }

    private sealed class CacheEntry : IDisposable
    {
        public CacheEntry(SkiaSharp.SKBitmap bitmap, int sourceWidth, int sourceHeight)
        {
            Bitmap = bitmap;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            Bytes = (long)bitmap.ByteCount;
        }

        public SkiaSharp.SKBitmap Bitmap { get; }
        public int SourceWidth { get; }
        public int SourceHeight { get; }

        /// <summary>字节计量在构造时固化(此后不再读位图, 避免 Dispose 竞态)。</summary>
        public long Bytes { get; }

        /// <summary>
        /// 深拷贝位图(必须在宿主 _gate 锁内调用, 与驱逐/Dispose 互斥)。
        /// 返回 null = 本条目已被并发释放(调用方按未命中重试)。
        /// </summary>
        public SkiaSharp.SKBitmap? Snapshot()
        {
            if (_disposed)
            {
                return null;
            }

            try
            {
                return Bitmap.Copy();
            }
            catch (AccessViolationException)
            {
                // 已释放(理论不可达, 锁已互斥);防御性兜底
                return null;
            }
        }

        private bool _disposed;

        public void Dispose()
        {
            _disposed = true;
            Bitmap.Dispose();
        }
    }
}
