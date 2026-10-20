using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wuling.Core.Sessions;

/// <summary>
/// 会话状态(设计文档 02 §9 / 03 F5): 标记表、槽位、模式、UI 偏好。
/// 按文件路径键控,目录内容变化后仍可重放恢复。
/// </summary>
public sealed class SessionState
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("rootPath")]
    public string RootPath { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public Dictionary<string, int> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("slotOrder")]
    public List<string> SlotOrder { get; set; } = [];

    [JsonPropertyName("slotCount")]
    public int SlotCount { get; set; } = 3;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "jpeg";

    /// <summary>提交选项持久化形态(与领域 record 解耦,便于 JSON 演进)。</summary>
    [JsonPropertyName("commitOptions")]
    public StoredCommitOptions? CommitOptions { get; set; }

    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>
/// 会话存储: &lt;目录&gt;/.wuling/session.json,原子写(tmp + rename)。
/// </summary>
public sealed class SessionStore
{
    private static readonly string DirName = ".wuling";
    private static readonly string FileName = "session.json";

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SessionDirectory(string rootPath) => Path.Combine(rootPath, DirName);

    public static string SessionFilePath(string rootPath) => Path.Combine(SessionDirectory(rootPath), FileName);

    /// <summary>读取会话;不存在或损坏返回 null(损坏文件备份为 .corrupt)。</summary>
    public static SessionState? Load(string rootPath)
    {
        var path = SessionFilePath(rootPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SessionState>(json, s_options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            TryBackupCorrupt(path);
            return null;
        }
    }

    /// <summary>保存会话(原子写: 同名 tmp + File.Move overwrite)。</summary>
    public static void Save(SessionState state)
    {
        var dir = SessionDirectory(state.RootPath);
        Directory.CreateDirectory(dir);
        var path = SessionFilePath(state.RootPath);
        var tmp = path + ".tmp";
        state.SavedAt = DateTimeOffset.Now;
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, s_options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>扫描结果 + 会话 → 重放标记与槽位(03 F5)。</summary>
    public static void Apply(SessionState state, IEnumerable<Models.PhotoPair> pairs)
    {
        foreach (var pair in pairs)
        {
            foreach (var path in new[] { pair.RawPath, pair.JpegPath })
            {
                if (path is not null && state.Tags.TryGetValue(Normalize(path), out var tagValue))
                {
                    pair.Tag = (Models.PhotoTag)tagValue;
                    break; // RAW 为主,先命中先用
                }
            }
        }
    }

    public static SessionState Capture(string rootPath, IEnumerable<Models.PhotoPair> pairs, IReadOnlyList<Models.PhotoPair?> slots, int slotCount)
    {
        var state = new SessionState { RootPath = rootPath, SlotCount = slotCount };
        foreach (var pair in pairs)
        {
            if (pair.Tag != Models.PhotoTag.Unmarked)
            {
                var keyPath = pair.RawPath ?? pair.JpegPath!;
                state.Tags[Normalize(keyPath)] = (int)pair.Tag;
            }
        }

        foreach (var slot in slots)
        {
            if (slot is not null)
            {
                state.SlotOrder.Add(Normalize(slot.PreviewPath));
            }
        }

        return state;
    }

    private static string Normalize(string path) => Path.GetFullPath(path).ToUpperInvariant();

    private static void TryBackupCorrupt(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}
