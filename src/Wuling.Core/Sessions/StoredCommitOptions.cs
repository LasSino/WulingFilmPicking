using Wuling.Core.Commit;

namespace Wuling.Core.Sessions;

/// <summary>CommitOptions 的 JSON 持久化形态(session.json 内嵌,字段名稳定)。</summary>
public sealed class StoredCommitOptions
{
    public string SelectedAction { get; set; } = "None";
    public string? SelectedTarget { get; set; }
    public string RejectedAction { get; set; } = "SoftDelete";
    public string? RejectedTarget { get; set; }
    public string UnmarkedAction { get; set; } = "None";
    public string? UnmarkedTarget { get; set; }
    public bool KeepPairsTogether { get; set; } = true;

    public static StoredCommitOptions From(CommitOptions options) => new()
    {
        SelectedAction = options.OnSelected.Kind.ToString(),
        SelectedTarget = options.OnSelected.TargetDirectory,
        RejectedAction = options.OnRejected.Kind.ToString(),
        RejectedTarget = options.OnRejected.TargetDirectory,
        UnmarkedAction = options.OnUnmarked.Kind.ToString(),
        UnmarkedTarget = options.OnUnmarked.TargetDirectory,
        KeepPairsTogether = options.KeepPairsTogether,
    };

    public CommitOptions ToOptions() => new()
    {
        OnSelected = Parse(SelectedAction, SelectedTarget),
        OnRejected = Parse(RejectedAction, RejectedTarget) with { TargetDirectory = RejectedTarget },
        OnUnmarked = Parse(UnmarkedAction, UnmarkedTarget),
        KeepPairsTogether = KeepPairsTogether,
    };

    private static TagAction Parse(string kind, string? target) => kind switch
    {
        "Move" => new TagAction { Kind = CommitActionKind.Move, TargetDirectory = target },
        "Delete" => new TagAction { Kind = CommitActionKind.Delete },
        "SoftDelete" => new TagAction { Kind = CommitActionKind.SoftDelete },
        _ => TagAction.None,
    };
}
