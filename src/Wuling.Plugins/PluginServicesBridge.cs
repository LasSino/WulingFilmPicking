using Wuling.Core.Models;
using Wuling.Imaging;
using Wuling.Plugins.Abstractions;
using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins;

/// <summary>
/// IPluginServices 宿主实现: 桥接 Wuling.Imaging 的 ExifProvider / ThumbnailService。
/// 全部线程安全(底层服务本就并发安全)。
/// 底层 ThumbnailService 可替换(SwapThumbnails): 切目录时宿主重建服务,
/// 插件持有的桥自动指向新实例 —— 不再踩已 Dispose 的旧服务(2026-10-31)。
/// </summary>
public sealed class PluginServicesBridge : IPluginServices
{
    private readonly ExifProvider _exif;
    private ThumbnailService _thumbnails;

    public PluginServicesBridge(ExifProvider exif, ThumbnailService thumbnails)
    {
        _exif = exif;
        _thumbnails = thumbnails;
    }

    /// <summary>切目录后由宿主调用: 原子替换底层服务(插件无感)。</summary>
    public void SwapThumbnails(ThumbnailService thumbnails) =>
        System.Threading.Volatile.Write(ref _thumbnails, thumbnails);

    public ExifInfo GetExif(string previewPath)
    {
        var s = _exif.GetSummary(previewPath);
        return new ExifInfo
        {
            CameraMake = s.CameraMake,
            CameraModel = s.CameraModel,
            LensModel = s.LensModel,
            FocalLength = s.FocalLength,
            Aperture = s.Aperture,
            Shutter = s.Shutter,
            Iso = s.Iso,
            ExposureBias = s.ExposureBias,
            TakenAt = s.TakenAt,
        };
    }

    public DecodedPixels GetPixels(string previewPath, int edge)
    {
        var pair = new PhotoPair { DisplayName = Path.GetFileNameWithoutExtension(previewPath), JpegPath = previewPath };
        var decoded = edge <= 0
            ? decodedFull(pair)
            : _thumbnails.GetView(pair, edge);

        if (decoded.Status != DecodeStatus.Ok || decoded.Bitmap is null)
        {
            return new DecodedPixels(0, 0, []);
        }

        // DecodedThumbnail.Bitmap 是调用方独占快照(ThumbnailService 快照语义)——拷完即释
        using var bmp = decoded.Bitmap;
        var pixels = new byte[bmp.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), pixels, 0, pixels.Length);
        return new DecodedPixels(bmp.Width, bmp.Height, pixels);

        DecodedThumbnail decodedFull(PhotoPair p)
        {
            // edge=0: 原尺寸 —— ThumbnailService 没有原尺寸入口, 用一个大边长代替(≈原尺寸上限)
            return _thumbnails.GetView(p, 8192);
        }
    }

    public HistogramData GetHistogram(string previewPath)
    {
        var pixels = GetPixels(previewPath, 512);
        return HistogramCalculator.Compute(pixels);
    }

    public string GetPluginStorageDirectory(string pluginId)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wuling", "plugins", pluginId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public FileMetaInfo GetFileMeta(string path)
    {
        if (!File.Exists(path))
        {
            return new FileMetaInfo { Length = 0 };
        }

        var info = new FileInfo(path);
        if (!RawPreviewExtractor.IsRawExtension(path))
        {
            // JPEG/普通图片: 只读文件头解析尺寸(不解码像素)
            var (w, h) = RawPreviewExtractor.ProbeJpegSize(path);
            return new FileMetaInfo { PixelWidth = w, PixelHeight = h, Length = info.Length };
        }

        var raw = RawPreviewExtractor.Extract(path);
        return new FileMetaInfo
        {
            Length = info.Length,
            RawFormat = RawPreviewExtractor.FormatName(path),
            EmbeddedPreviewWidth = raw.Width,
            EmbeddedPreviewHeight = raw.Height,
        };
    }

    public byte[]? GetRawPreviewPng(string rawPath)
    {
        if (!File.Exists(rawPath) || !RawPreviewExtractor.IsRawExtension(rawPath))
        {
            return null;
        }

        var extracted = RawPreviewExtractor.Extract(rawPath);
        if (extracted.JpegBytes is null)
        {
            return null;
        }

        // 内嵌 JPEG 解码 → 按 RAW 主文件 EXIF 方向旋转 → PNG 编码
        using var sk = SkiaSharp.SKBitmap.Decode(extracted.JpegBytes);
        if (sk is null)
        {
            return null;
        }

        ushort orientation;
        try
        {
            orientation = _exif.GetSummary(rawPath).Orientation;
        }
        catch (Exception)
        {
            orientation = 1;
        }

        // ApplyOrientation 在 orientation 0/1 时原样返回输入(同一对象),
        // 交给外层 using var sk 统一释放, 保持所有权清晰。
        var oriented = ThumbnailService.ApplyOrientation(sk, orientation);
        using var img = SkiaSharp.SKImage.FromBitmap(oriented);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>直方图计算(256 bin, 基于 Bgra8888 像素;跳过 alpha=0 像素)。</summary>
public static class HistogramCalculator
{
    public static HistogramData Compute(DecodedPixels pixels)
    {
        var r = new int[256];
        var g = new int[256];
        var b = new int[256];

        var data = pixels.Pixels;
        var stride = pixels.Stride;
        for (var y = 0; y < pixels.Height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < pixels.Width; x++)
            {
                var o = row + (x * 4);
                // Bgra8888: B G R A
                var a = data[o + 3];
                if (a == 0)
                {
                    continue;
                }

                b[data[o]]++;
                g[data[o + 1]]++;
                r[data[o + 2]]++;
            }
        }

        return new HistogramData(r, g, b);
    }
}
