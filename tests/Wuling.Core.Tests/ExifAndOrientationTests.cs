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

    [SkippableFact("P1580350.JPG")]
    public void ExifProvider_ReadsOrientationAndCaptureParams()
    {
        var exif = new ExifProvider().GetSummary(SampleJpg);

        Assert.Equal(8, exif.Orientation); // 竖拍: 逆时针 90°
        Assert.False(string.IsNullOrEmpty(exif.CameraMake));
        Assert.False(string.IsNullOrEmpty(exif.Shutter));
        Assert.False(string.IsNullOrEmpty(exif.Aperture));
        Assert.False(string.IsNullOrEmpty(exif.Iso));
    }

    [SkippableFact("P1580350.JPG")]
    public void ApplyOrientation_SwapsDimensionsForRotation90()
    {
        using var stream = File.OpenRead(SampleJpg);
        using var source = SkiaSharp.SKBitmap.Decode(stream);
        Assert.Equal(4272, source.Width);
        Assert.Equal(2856, source.Height);

        using var oriented = ThumbnailService.ApplyOrientation(source, 8);

        Assert.Equal(2856, oriented.Width); // 宽高互换
        Assert.Equal(4272, oriented.Height);
    }

    [SkippableFact("P1580350.JPG")]
    public void GetView_AtLargeEdge_IsNotTheTinyThumbnail()
    {
        var service = new ThumbnailService();
        var pair = new Wuling.Core.Models.PhotoPair { DisplayName = "P", JpegPath = SampleJpg };

        var small = service.GetView(pair, 96);
        var large = service.GetView(pair, 2048);

        Assert.Equal(96, Math.Max(small.Bitmap!.Width, small.Bitmap!.Height));
        Assert.Equal(2048, Math.Max(large.Bitmap!.Width, large.Bitmap!.Height));
        // 方向归一化后,竖拍图的"高"是长边
        Assert.True(large.Bitmap.Height > large.Bitmap.Width);
    }

    [SkippableFact("P1580350.JPG")]
    public void GetView_AtEdgeLargerThanImage_PixelsRemainValid()
    {

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

/// <summary>
/// xUnit 跳过特性: 构造期检查样张文件, 缺失则设置 Skip 属性。
/// 必须在 attribute 构造期跳过 —— 测试体内抛自定义异常在 xUnit 2.x 里按 FAIL 处理
/// 而非 Skip(CI 无样张曾致 9 个测试假失败, 2026-11-09)。
/// </summary>
public sealed class SkippableFactAttribute : FactAttribute
{
    public SkippableFactAttribute(params string[] requiredFiles)
    {
        var root = FindRepoRoot();
        foreach (var file in requiredFiles)
        {
            if (!File.Exists(Path.Combine(root, file)))
            {
                Skip = $"样张缺失: {file}";
                return;
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WulingFilmPicking.slnx")))
        {
            dir = dir.Parent!;
        }

        return dir?.FullName ?? string.Empty;
    }
}
