using Wuling.Plugins.Abstractions;
using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins.BuiltIn;

/// <summary>插件实现共享的小工具。</summary>
internal static class PluginUtil
{
    /// <summary>把每个槽位的字段值序列化, 供跨槽差异计算(Python 插件也用同语义)。</summary>
    public static bool Differs(IReadOnlyList<string?> values)
    {
        if (values.Count < 2)
        {
            return false;
        }

        var first = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            if (!string.Equals(first, values[i], System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string? JoinMake(ExifInfo? e) => e?.CameraModel is not null
        ? (e.CameraMake is not null && !e.CameraModel.StartsWith(e.CameraMake, System.StringComparison.OrdinalIgnoreCase)
            ? $"{e.CameraMake} {e.CameraModel}"
            : e.CameraModel)
        : null;

    /// <summary>文件大小人类可读格式(JPEG/RAW 摘要行共用)。</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1 << 20 => $"{bytes / 1048576.0:0.#} MB",
        >= 1 << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>
    /// 宿主解码像素(Bgra8888 Premul) → Avalonia 位图。
    /// WriteableBitmap 拷贝构造在 ctor 内完成, 钉住期间拷贝即安全(同 App 侧 PhotoCardViewModel.ToBitmap 模式)。
    /// 像素不完整/解码失败返回 null(视图层显示占位符)。
    /// </summary>
    public static Avalonia.Media.Imaging.Bitmap? ToBitmap(Abstractions.DecodedPixels pixels)
    {
        if (pixels.Width <= 0 || pixels.Height <= 0 || pixels.Pixels.Length < pixels.Stride * pixels.Height)
        {
            return null;
        }

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            pixels.Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return new Avalonia.Media.Imaging.WriteableBitmap(
                Avalonia.Platform.PixelFormats.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul,
                handle.AddrOfPinnedObject(),
                new Avalonia.PixelSize(pixels.Width, pixels.Height),
                new Avalonia.Vector(96, 96),
                pixels.Stride);
        }
        finally
        {
            handle.Free();
        }
    }
}
