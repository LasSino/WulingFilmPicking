using Wuling.Imaging;

namespace Wuling.Core.Tests;

/// <summary>
/// RAW 内嵌预览提取回归测试。
/// 样张(仓库根, 不入库): P1580350.RW2(松下), DSCF7264.RAF(富士 X-T20, 2026-10-31)。
/// 样张缺失时自动跳过(兼容 CI 环境)。
/// </summary>
public sealed class RawPreviewExtractionTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WulingFilmPicking.slnx")))
        {
            dir = dir.Parent!;
        }

        return dir?.FullName ?? string.Empty;
    }

    private static string Rw2Path => Path.Combine(RepoRoot, "P1580350.RW2");
    private static string RafPath => Path.Combine(RepoRoot, "DSCF7264.RAF");

    [SkippableFact]
    public void Extract_Rw2_HasEmbeddedJpeg()
    {
        Skip.IfNot(File.Exists(Rw2Path), "RW2 样张缺失");

        var result = RawPreviewExtractor.Extract(Rw2Path);

        Assert.True(result.HasPreview, "RW2 应有内嵌预览");
        Assert.NotNull(result.JpegBytes);
        Assert.Equal(0xFF, result.JpegBytes![0]);
        Assert.Equal(0xD8, result.JpegBytes[1]);
    }

    [SkippableFact]
    public void Extract_Raf_HasFullSizeEmbeddedJpeg()
    {
        // 回归测试: 2026-10-31 修复前 ExtractRaf 读错头部字段(0x5C/0x60 是 CFA 块非预览)
        // 且 stackalloc 84B 越界读 0x60 → 异常被吞 → 富士 RAF 永远"无内嵌预览"。
        // 修复后: 0x54=JPEG 起点/0x58=JPEG 长度, X-T20 样张应提取出 1920x1280 预览。
        Skip.IfNot(File.Exists(RafPath), "RAF 样张缺失");

        Assert.True(RawPreviewExtractor.IsRawExtension(RafPath));
        Assert.Equal("RAF", RawPreviewExtractor.FormatName(RafPath));

        var result = RawPreviewExtractor.Extract(RafPath);

        Assert.True(result.HasPreview, "RAF 应有内嵌预览(修复前恒为空)");
        Assert.NotNull(result.JpegBytes);
        Assert.Equal(0xFF, result.JpegBytes![0]);
        Assert.Equal(0xD8, result.JpegBytes[1]);
        // X-T20 样张实测: 内嵌全尺寸预览 1920x1280
        Assert.Equal(1920, result.Width);
        Assert.Equal(1280, result.Height);
    }

    [SkippableFact]
    public void Extract_Raf_ResultDecodableBySkia()
    {
        Skip.IfNot(File.Exists(RafPath), "RAF 样张缺失");

        var result = RawPreviewExtractor.Extract(RafPath);
        Assert.True(result.HasPreview);

        using var sk = SkiaSharp.SKBitmap.Decode(result.JpegBytes);
        Assert.NotNull(sk);
        Assert.Equal(1920, sk!.Width);
        Assert.Equal(1280, sk.Height);
    }
}
