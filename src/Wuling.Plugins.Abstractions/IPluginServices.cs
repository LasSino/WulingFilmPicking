using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins.Abstractions;

/// <summary>直方图数据(256 bin × 通道)。</summary>
public sealed record HistogramData(int[] Red, int[] Green, int[] Blue);

/// <summary>
/// 宿主注入插件的数据服务(04 文档 §2)。
/// 全部带缓存;实现方保证线程安全。契约层不依赖 UI 程序集。
/// </summary>
public interface IPluginServices
{
    /// <summary>EXIF 读取(带 mtime 缓存)。</summary>
    ExifInfo GetExif(string previewPath);

    /// <summary>解码图像为像素数据(带缓存;edge 为目标长边, 0 = 原尺寸)。
    /// 返回 Bgra8888 行序像素与尺寸;插件需要像素级运算(直方图/差分)时使用。</summary>
    DecodedPixels GetPixels(string previewPath, int edge);

    /// <summary>图像直方图(带缓存;基于缩放到 ≤512px 的快速图计算)。</summary>
    HistogramData GetHistogram(string previewPath);

    /// <summary>插件私有存储目录(自动创建);插件配置/缓存放这里。</summary>
    string GetPluginStorageDirectory(string pluginId);

    /// <summary>文件元数据(尺寸/大小/RAW 格式与内嵌预览尺寸;只 stat+读头, 不解码, 带 mtime 缓存)。</summary>
    FileMetaInfo GetFileMeta(string path);

    /// <summary>提取 RAW 内嵌预览为 PNG 编码字节(带 mtime 缓存;无内嵌预览返回 null)。
    /// 插件把它当作普通图片引用/展示即可 —— 02 §6 首选层(内嵌 JPEG 提取)在宿主侧完成。</summary>
    byte[]? GetRawPreviewPng(string rawPath);
}

/// <summary>解码像素(行对齐 = Width×4 字节, Bgra8888 Premul)。</summary>
public sealed record DecodedPixels(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}

/// <summary>文件的轻量元数据(展示类模式摘要行用;只 stat, 不解码)。</summary>
public sealed record FileMetaInfo
{
    /// <summary>显示尺寸(像素);不可得时 null。</summary>
    public int? PixelWidth { get; init; }

    public int? PixelHeight { get; init; }

    /// <summary>文件大小(字节)。</summary>
    public long Length { get; init; }

    /// <summary>RAW 格式名(仅 RAW 预览模式用, 如 "RAF"/"RW2");非 RAW 为 null。</summary>
    public string? RawFormat { get; init; }

    /// <summary>内嵌预览尺寸(仅 RAW);null = 不可得/无内嵌。</summary>
    public int? EmbeddedPreviewWidth { get; init; }

    public int? EmbeddedPreviewHeight { get; init; }
}
