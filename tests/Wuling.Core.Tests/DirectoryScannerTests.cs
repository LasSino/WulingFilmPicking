using Wuling.Core.Models;
using Wuling.Core.Scanning;

namespace Wuling.Core.Tests;

/// <summary>M0 验收: 扫描与同名配对(03 F1)。</summary>
public sealed class DirectoryScannerTests : IDisposable
{
    private readonly string _root;

    public DirectoryScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wuling-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string fileName, int size = 10)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public void Pairs_RawWithSameNameJpeg()
    {
        Write("IMG_0001.CR2", 100);
        Write("IMG_0001.JPG", 20);

        var pairs = new DirectoryScanner().Scan(_root);

        var pair = Assert.Single(pairs);
        Assert.Equal(PhotoKind.RawWithJpeg, pair.Kind);
        Assert.Equal("IMG_0001.CR2", Path.GetFileName(pair.RawPath));
        Assert.Equal("IMG_0001.JPG", Path.GetFileName(pair.JpegPath));
        Assert.Equal("IMG_0001", pair.DisplayName);
    }

    [Fact]
    public void Pairs_MatchIsCaseInsensitive()
    {
        Write("img_0002.nef", 100);
        Write("IMG_0002.jpg", 20);

        var pairs = new DirectoryScanner().Scan(_root);

        var pair = Assert.Single(pairs);
        Assert.Equal(PhotoKind.RawWithJpeg, pair.Kind);
    }

    [Fact]
    public void OrphanJpeg_BecomesOwnCard()
    {
        Write("PIC_A.jpg");

        var pairs = new DirectoryScanner().Scan(_root);

        var pair = Assert.Single(pairs);
        Assert.Equal(PhotoKind.JpegOnly, pair.Kind);
        Assert.Equal("PIC_A.jpg", Path.GetFileName(pair.PreviewPath));
    }

    [Fact]
    public void SameNameJpegAndJpegPrefersLarger()
    {
        Write("IMG_0003.CR2", 100);
        Write("IMG_0003.jpg", 20);
        Write("IMG_0003.jpeg", 50);

        var pairs = new DirectoryScanner().Scan(_root);

        var pair = Assert.Single(pairs);
        Assert.Equal("IMG_0003.jpeg", Path.GetFileName(pair.JpegPath));
    }

    [Fact]
    public void IgnoresAccessoryFiles_AndWulingDirectory()
    {
        Write("IMG_0004.CR2");
        Write("IMG_0004.xmp");
        Write("IMG_0004.wav");
        var wuling = Directory.CreateDirectory(Path.Combine(_root, ".wuling"));
        File.WriteAllText(Path.Combine(wuling.FullName, "session.json"), "{}");

        var pairs = new DirectoryScanner().Scan(_root);

        var pair = Assert.Single(pairs);
        Assert.Equal(PhotoKind.RawOnly, pair.Kind);
        Assert.Null(pair.JpegPath);
    }

    [Fact]
    public void SortsNaturally_NumberAware()
    {
        Write("IMG_2.jpg");
        Write("IMG_10.jpg");
        Write("IMG_1.jpg");

        var pairs = new DirectoryScanner().Scan(_root);

        Assert.Equal(["IMG_1", "IMG_2", "IMG_10"], pairs.Select(p => p.DisplayName).ToArray());
    }

    [Fact]
    public void EmptyDirectory_ReturnsEmpty()
    {
        var pairs = new DirectoryScanner().Scan(_root);

        Assert.Empty(pairs);
    }
}
