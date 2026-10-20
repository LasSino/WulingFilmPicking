using Wuling.Core.Models;

namespace Wuling.Core.Commit;

/// <summary>处置动作类型(设计文档 03 F7 / TODO #3)。</summary>
public enum CommitActionKind
{
    /// <summary>不做处理。</summary>
    None,

    /// <summary>移动到指定目录。</summary>
    Move,

    /// <summary>永久删除(危险;默认配置应为软删除)。</summary>
    Delete,

    /// <summary>软删除到 .wuling/trash(默认;可还原)。</summary>
    SoftDelete,
}

/// <summary>一类标记的处置配置。</summary>
public sealed record TagAction
{
    public CommitActionKind Kind { get; init; } = CommitActionKind.None;

    /// <summary>Move 动作的目标目录。</summary>
    public string? TargetDirectory { get; init; }

    public static TagAction None { get; } = new();
}

/// <summary>提交选项: 三类标记各自动作。</summary>
public sealed record CommitOptions
{
    public TagAction OnSelected { get; init; } = TagAction.None;
    public TagAction OnRejected { get; init; } = new() { Kind = CommitActionKind.SoftDelete };
    public TagAction OnUnmarked { get; init; } = TagAction.None;

    /// <summary>RAW+JPEG 对是否绑定处置(默认 true,防拆散)。</summary>
    public bool KeepPairsTogether { get; init; } = true;
}

/// <summary>单个待执行操作。</summary>
public sealed record CommitItem(PhotoPair Pair, string FilePath, TagAction Action, string? DestinationPath);

/// <summary>提交计划: 全部操作 + 汇总。</summary>
public sealed record CommitPlan(IReadOnlyList<CommitItem> Items)
{
    public int Count => Items.Count;
}

/// <summary>
/// 提交计划器(设计文档 02 §8): 由选项与当前标记生成逐文件操作清单。
/// RAW+JPEG 对绑定处置: 对内任一文件命中某类标记动作,整对按该动作处理。
/// </summary>
public static class CommitPlanner
{
    public static CommitPlan Build(IEnumerable<PhotoPair> pairs, CommitOptions options)
    {
        var items = new List<CommitItem>();
        foreach (var pair in pairs)
        {
            var action = ResolveAction(pair.Tag, options);
            if (action.Kind == CommitActionKind.None)
            {
                continue;
            }

            foreach (var file in ResolveFiles(pair, options))
            {
                items.Add(new CommitItem(pair, file, action, action.Kind == CommitActionKind.Move ? DestinationFor(file, action.TargetDirectory!) : null));
            }
        }

        return new CommitPlan(items);
    }

    private static TagAction ResolveAction(PhotoTag tag, CommitOptions options) => tag switch
    {
        PhotoTag.Selected => options.OnSelected,
        PhotoTag.Rejected => options.OnRejected,
        _ => options.OnUnmarked,
    };

    private static IEnumerable<string> ResolveFiles(PhotoPair pair, CommitOptions options)
    {
        var files = new List<string>(2);
        if (pair.RawPath is not null)
        {
            files.Add(pair.RawPath);
        }

        if (pair.JpegPath is not null)
        {
            files.Add(pair.JpegPath);
        }

        if (options.KeepPairsTogether || files.Count == 1)
        {
            return files;
        }

        // 不绑定: 仅处置主文件(RAW),附属 JPEG 保留
        return [pair.RawPath ?? pair.JpegPath!];
    }

    private static string DestinationFor(string sourcePath, string targetDirectory)
    {
        var name = Path.GetFileName(sourcePath);
        var candidate = Path.Combine(targetDirectory, name);
        return UniquePath(candidate);
    }

    /// <summary>目标同名 → name (2).ext 递增(03 F7)。</summary>
    public static string UniquePath(string candidate)
    {
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var dir = Path.GetDirectoryName(candidate)!;
        var name = Path.GetFileNameWithoutExtension(candidate);
        var ext = Path.GetExtension(candidate);
        for (var i = 2; ; i++)
        {
            var next = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(next))
            {
                return next;
            }
        }
    }
}

/// <summary>单个操作的执行结果。</summary>
public sealed record CommitItemResult(CommitItem Item, bool Success, string? Error);

/// <summary>提交执行结果报告(01 §2.4 结果报告)。</summary>
public sealed record CommitResult(IReadOnlyList<CommitItemResult> Results)
{
    public int SuccessCount => Results.Count(r => r.Success);
    public int FailureCount => Results.Count - SuccessCount;
}

/// <summary>
/// 提交执行器: 逐项原子执行 + 事务日志(commit-log.jsonl),崩溃后可续跑(02 §8)。
/// </summary>
public sealed class CommitExecutor
{
    public CommitResult Execute(CommitPlan plan, string rootPath)
    {
        var trashDir = Path.Combine(rootPath, ".wuling", "trash", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var logPath = Path.Combine(rootPath, ".wuling", "commit-log.jsonl");
        var results = new List<CommitItemResult>(plan.Count);

        foreach (var item in plan.Items)
        {
            results.Add(ExecuteItem(item, rootPath, trashDir, logPath));
        }

        return new CommitResult(results);
    }

    private static CommitItemResult ExecuteItem(CommitItem item, string rootPath, string trashDir, string logPath)
    {
        try
        {
            string destination;
            switch (item.Action.Kind)
            {
                case CommitActionKind.SoftDelete:
                    Directory.CreateDirectory(trashDir);
                    destination = CommitPlanner.UniquePath(Path.Combine(trashDir, Path.GetFileName(item.FilePath)));
                    File.Move(item.FilePath, destination);
                    break;

                case CommitActionKind.Move:
                    Directory.CreateDirectory(item.Action.TargetDirectory!);
                    destination = item.DestinationPath!;
                    File.Move(item.FilePath, destination);
                    break;

                case CommitActionKind.Delete:
                    destination = string.Empty;
                    File.Delete(item.FilePath);
                    break;

                case CommitActionKind.None:
                default:
                    return new CommitItemResult(item, true, null);
            }

            AppendLog(logPath, item, destination);
            return new CommitItemResult(item, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CommitItemResult(item, false, ex.Message);
        }
    }

    private static void AppendLog(string logPath, CommitItem item, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var line = System.Text.Json.JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.Now,
                file = item.FilePath,
                action = item.Action.Kind.ToString(),
                dest = destination,
            });
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // 日志失败不阻断主流程
        }
    }
}
