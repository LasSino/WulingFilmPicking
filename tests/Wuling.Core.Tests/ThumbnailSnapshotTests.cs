using Wuling.Core.Models;
using Wuling.Imaging;

namespace Wuling.Core.Tests;

/// <summary>
/// 快照语义回归(2026-10-31 切目录闪退):
/// GetView 借出的是调用方独占拷贝, 服务 Dispose/LRU 驱逐后已借出的快照仍可安全使用。
/// 修复前共享位图在借出窗口期被 Dispose → ToBitmap 读已释放内存 → AccessViolationException。
/// </summary>
public sealed class ThumbnailSnapshotTests
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

    private static string SampleJpg => Path.Combine(RepoRoot, "P1580350.JPG");

    [SkippableFact]
    public void Snapshot_SurvivesServiceDispose()
    {
        // 崩溃路径复现: 后台拿到快照 → 服务被 Dispose(切目录) → 消费方仍可用
        Skip.IfNot(File.Exists(SampleJpg), "样张缺失");

        var service = new ThumbnailService();
        var pair = new PhotoPair { DisplayName = "P", JpegPath = SampleJpg };
        var borrowed = service.GetView(pair, 2048).Bitmap;
        service.Dispose(); // 修复前: 共享位图在此被释放, 借出方变悬空

        // 修复后: 快照独立存活 —— 读取原生属性+拷贝像素全程合法
        Assert.NotNull(borrowed);
        Assert.Equal(2048, Math.Max(borrowed!.Width, borrowed.Height));
        var pixels = new byte[borrowed.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(borrowed.GetPixels(), pixels, 0, pixels.Length);
        borrowed.Dispose();
    }

    [SkippableFact]
    public async Task Snapshot_ConcurrentBorrowersAreIndependent()
    {
        // 多借出方并发: 各自持有独立快照, 任一方 Dispose 不影响其他方(与共享语义的根本区别)
        Skip.IfNot(File.Exists(SampleJpg), "样张缺失");

        var service = new ThumbnailService();
        var pair = new PhotoPair { DisplayName = "P", JpegPath = SampleJpg };

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await Task.Yield();
            var decoded = service.GetView(pair, 1024);
            Assert.Equal(DecodeStatus.Ok, decoded.Status);
            var bytes = decoded.Bitmap!.ByteCount;
            var buf = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(decoded.Bitmap.GetPixels(), buf, 0, buf.Length);
            decoded.Bitmap.Dispose();
            return bytes;
        });

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r > 0));
        service.Dispose();
    }
}
