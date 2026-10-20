using Wuling.Core.Commit;
using Wuling.Core.Models;

namespace Wuling.Core.Tests;

public sealed class CommitPlannerTests : IDisposable
{
    private readonly string _root;

    public CommitPlannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wuling-commit-{Guid.NewGuid():N}");
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

    private PhotoPair WritePair(string name, PhotoTag tag)
    {
        var raw = Path.Combine(_root, $"{name}.CR2");
        var jpeg = Path.Combine(_root, $"{name}.JPG");
        File.WriteAllBytes(raw, [1]);
        File.WriteAllBytes(jpeg, [2]);
        return new PhotoPair { DisplayName = name, RawPath = raw, JpegPath = jpeg, Tag = tag };
    }

    [Fact]
    public void Build_Unmarked_WithNoneAction_ProducesEmptyPlan()
    {
        var pair = WritePair("A", PhotoTag.Unmarked);
        var plan = CommitPlanner.Build([pair], new CommitOptions());

        Assert.Empty(plan.Items);
    }

    [Fact]
    public void Build_Rejected_SoftDelete_KeepsPairsTogether()
    {
        var pair = WritePair("A", PhotoTag.Rejected);
        var plan = CommitPlanner.Build([pair], new CommitOptions());

        Assert.Equal(2, plan.Count);
        Assert.All(plan.Items, i => Assert.Equal(CommitActionKind.SoftDelete, i.Action.Kind));
    }

    [Fact]
    public void Build_KeepPairsTogetherFalse_OnlyRaw()
    {
        var pair = WritePair("A", PhotoTag.Selected);
        var options = new CommitOptions
        {
            OnSelected = new TagAction { Kind = CommitActionKind.Move, TargetDirectory = _root },
            KeepPairsTogether = false,
        };

        var plan = CommitPlanner.Build([pair], options);

        var item = Assert.Single(plan.Items);
        Assert.EndsWith(".CR2", item.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UniquePath_IncrementsOnConflict()
    {
        var existing = Path.Combine(_root, "X.jpg");
        File.WriteAllBytes(existing, [1]);

        var unique = CommitPlanner.UniquePath(existing);

        Assert.EndsWith("X (2).jpg", unique, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_SoftDelete_MovesToTrash_AndWritesLog()
    {
        var pair = WritePair("A", PhotoTag.Rejected);
        var plan = CommitPlanner.Build([pair], new CommitOptions());

        var result = new CommitExecutor().Execute(plan, _root);

        Assert.Equal(plan.Count, result.SuccessCount);
        Assert.All(result.Results, r => Assert.True(r.Success));
        // 源文件已不在原位,trash 有文件
        Assert.False(File.Exists(pair.RawPath));
        Assert.False(File.Exists(pair.JpegPath));
        var trash = Path.Combine(_root, ".wuling", "trash");
        Assert.Equal(2, Directory.EnumerateFiles(trash, "*", SearchOption.AllDirectories).Count());
        Assert.True(File.Exists(Path.Combine(_root, ".wuling", "commit-log.jsonl")));
    }

    [Fact]
    public void Execute_Move_ToTargetDirectory()
    {
        var pair = WritePair("A", PhotoTag.Selected);
        var target = Path.Combine(_root, "keep");
        var options = new CommitOptions
        {
            OnSelected = new TagAction { Kind = CommitActionKind.Move, TargetDirectory = target },
        };
        var plan = CommitPlanner.Build([pair], options);

        var result = new CommitExecutor().Execute(plan, _root);

        Assert.Equal(plan.Count, result.SuccessCount);
        Assert.Equal(2, Directory.EnumerateFiles(target).Count());
        Assert.False(File.Exists(pair.RawPath));
    }
}
