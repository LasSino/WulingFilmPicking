using Wuling.Core.Models;
using Wuling.Core.Sessions;

namespace Wuling.Core.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _root;

    public SessionStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wuling-sess-{Guid.NewGuid():N}");
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

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var state = new SessionState
        {
            RootPath = _root,
            SlotCount = 5,
            Tags = { [Path.Combine(_root, "A.CR2").ToUpperInvariant()] = 1 },
        };

        SessionStore.Save(state);
        var loaded = SessionStore.Load(_root);

        Assert.NotNull(loaded);
        Assert.Equal(5, loaded.SlotCount);
        Assert.Equal(1, loaded.Tags[Path.Combine(_root, "A.CR2").ToUpperInvariant()]);
    }

    [Fact]
    public void Load_Missing_ReturnsNull()
    {
        Assert.Null(SessionStore.Load(_root));
    }

    [Fact]
    public void Load_Corrupt_BackupAndNull()
    {
        Directory.CreateDirectory(SessionStore.SessionDirectory(_root));
        File.WriteAllText(SessionStore.SessionFilePath(_root), "{ not json ");

        Assert.Null(SessionStore.Load(_root));
        Assert.True(File.Exists(SessionStore.SessionFilePath(_root) + ".corrupt"));
    }

    [Fact]
    public void Apply_ReplaysTagsByPath()
    {
        var rawPath = Path.Combine(_root, "IMG_1.CR2");
        var jpegPath = Path.Combine(_root, "IMG_1.JPG");
        File.WriteAllBytes(rawPath, [1]);
        File.WriteAllBytes(jpegPath, [2]);
        var pair = new PhotoPair { DisplayName = "IMG_1", RawPath = rawPath, JpegPath = jpegPath };

        var state = new SessionState();
        state.Tags[rawPath.ToUpperInvariant()] = (int)PhotoTag.Rejected;
        SessionStore.Apply(state, [pair]);

        Assert.Equal(PhotoTag.Rejected, pair.Tag);
    }

    [Fact]
    public void Capture_UnmarkedOnly_SkipsUnmarked()
    {
        var jpegPath = Path.Combine(_root, "B.jpg");
        File.WriteAllBytes(jpegPath, [3]);
        var marked = new PhotoPair { DisplayName = "B", JpegPath = jpegPath, Tag = PhotoTag.Selected };
        var unmarked = new PhotoPair { DisplayName = "C", JpegPath = Path.Combine(_root, "C.jpg") };

        var state = SessionStore.Capture(_root, [marked, unmarked], [marked], 1);

        Assert.Single(state.Tags);
        Assert.Single(state.SlotOrder);
        Assert.Equal(1, state.SlotCount);
    }
}
