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
}

/// <summary>解码像素(行对齐 = Width×4 字节, Bgra8888 Premul)。</summary>
public sealed record DecodedPixels(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}
