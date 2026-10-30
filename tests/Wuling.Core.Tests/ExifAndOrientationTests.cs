using Wuling.Imaging;

namespace Wuling.Core.Tests;

/// <summary>
/// M2 验收: EXIF 读取与方向归一化。
/// 样张: 仓库根目录 P1580350.JPG (4272x2856, Orientation=8 逆时针 90°)。
/// 样张缺失时自动跳过(兼容 CI 环境)。
/// </summary>
public sealed class ExifAndOrientationTests
{
    private static string RepoRoot => FindRepoRoot();
    private static string SampleJpg => Path.Combine(RepoRoot, "P1580350.JPG");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WulingFilmPicking.slnx")))
        {
            dir = dir.Parent!;
        }

        return dir?.FullName ?? string.Empty;
    }

    public static bool SampleExists => File.Exists(SampleJpg);

    [SkippableFact]
    public void ExifProvider_ReadsOrientationAndCaptureParams()
    {
        Skip.IfNot(SampleExists, "样张缺失");

        var exif = new ExifProvider().GetSummary(SampleJpg);

        Assert.Equal(8, exif.Orientation); // 竖拍: 逆时针 90°
        Assert.False(string.IsNullOrEmpty(exif.CameraMake));
        Assert.False(string.IsNullOrEmpty(exif.Shutter));
        Assert.False(string.IsNullOrEmpty(exif.Aperture));
        Assert.False(string.IsNullOrEmpty(exif.Iso));
    }

    [SkippableFact]
    public void ApplyOrientation_SwapsDimensionsForRotation90()
    {
        Skip.IfNot(SampleExists, "样张缺失");

        using var stream = File.OpenRead(SampleJpg);
        using var source = SkiaSharp.SKBitmap.Decode(stream);
        Assert.Equal(4272, source.Width);
        Assert.Equal(2856, source.Height);

        using var oriented = ThumbnailService.ApplyOrientation(source, 8);

        Assert.Equal(2856, oriented.Width); // 宽高互换
        Assert.Equal(4272, oriented.Height);
    }

    [SkippableFact]
    public void GetView_AtLargeEdge_IsNotTheTinyThumbnail()
    {
        Skip.IfNot(SampleExists, "样张缺失");

        var service = new ThumbnailService();
        var pair = new Wuling.Core.Models.PhotoPair { DisplayName = "P", JpegPath = SampleJpg };

        var small = service.GetView(pair, 96);
        var large = service.GetView(pair, 2048);

        Assert.Equal(96, Math.Max(small.Bitmap!.Width, small.Bitmap!.Height));
        Assert.Equal(2048, Math.Max(large.Bitmap!.Width, large.Bitmap!.Height));
        // 方向归一化后,竖拍图的"高"是长边
        Assert.True(large.Bitmap.Height > large.Bitmap.Width);
    }

    [SkippableFact]
    public void GetView_AtEdgeLargerThanImage_PixelsRemainValid()
    {
        // 回归测试: 修复前, edge >= 原图边长时 Scale() 原样返回输入位图,
        // 而 Decode() 的 using 在退出时已将其释放 → 缓存持有已释放 SKBitmap,
        // 访问 ByteCount/GetPixels 直接 AccessViolationException(JPEG 预览闪退根因)。
        Skip.IfNot(SampleExists, "样张缺失");

        var service = new ThumbnailService();
        var pair = new Wuling.Core.Models.PhotoPair { DisplayName = "P", JpegPath = SampleJpg };

        var large = service.GetView(pair, 8192); // 样张 4272x2856, 8192 边长必然走"不缩放"路径

        // 访问原生属性并完整拷贝像素 —— 修复前此处即崩溃
        Assert.True(large.Bitmap!.ByteCount > 0);
        var pixels = new byte[large.Bitmap.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(large.Bitmap.GetPixels(), pixels, 0, pixels.Length);
        Assert.Equal(4272 * 2856 * 4, large.Bitmap.ByteCount);
        large.Bitmap.Dispose(); // 快照归调用方所有

        // 快照语义(2026-10-31): 每次借出都是调用方独占拷贝(实例必不同),
        // 但内容等价;缓存内共享位图永不出服务。
        var again = service.GetView(pair, 8192);
        Assert.NotSame(large.Bitmap, again.Bitmap); // 独占快照, 非共享实例
        Assert.Equal(4272 * 2856 * 4, again.Bitmap!.ByteCount);
        again.Bitmap.Dispose();
    }
}

/// <summary>xUnit 跳过特性(避免引入额外包)。</summary>
public sealed class SkippableFactAttribute : FactAttribute
{
    public SkippableFactAttribute()
    {
        // 在构造时无法判断(需要静态上下文),用条件执行代替:
        // 这里保留空实现,实际跳过逻辑由测试体内 Skip 判断完成。
    }
}

/// <summary>Skip 辅助。</summary>
public static class Skip
{
    public static void IfNot(bool condition, string reason)
    {
        if (!condition)
        {
            throw new XunitSkipException(reason);
        }
    }
}

public sealed class XunitSkipException(string reason) : Exception(reason);
