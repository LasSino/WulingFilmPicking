using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Wuling.Plugins.Abstractions;

namespace Wuling.Plugins.Python;

/// <summary>
/// Python 插件注册表: 扫描 plugins/python/*/plugin.json, 启动共享 Worker, 装载全部插件。
/// 发现规则 (04 §5): 应用目录 plugins/python/ 与 AppData/Wuling/plugins/python/, 同名以用户目录为准。
/// </summary>
public sealed class PythonPluginRegistry : IDisposable
{
    private readonly string _pythonExe;
    private PythonWorkerConnection? _worker;

    public PythonPluginRegistry(string pythonExe)
    {
        _pythonExe = pythonExe;
    }

    /// <summary>发现并装载全部 Python 插件; 返回可用的适配器列表。</summary>
    public async Task<IReadOnlyList<PythonPluginAdapter>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "plugins", "python"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wuling", "plugins", "python"),
        };

        var manifests = new List<(string Dir, JsonElement Json, string Id)>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Distinct())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                try
                {
                    var json = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
                    if (json.TryGetProperty("id", out var idEl) && seenIds.Add(idEl.GetString()!))
                    {
                        manifests.Add((dir, json.Clone(), idEl.GetString()!));
                    }
                }
                catch (Exception)
                {
                    // 清单损坏 → 跳过该插件
                }
            }
        }

        if (manifests.Count == 0)
        {
            return [];
        }

        // Worker 脚本随应用分发
        var workerPath = Path.Combine(AppContext.BaseDirectory, "plugins", "python", "wuling_worker.py");
        if (!File.Exists(workerPath))
        {
            return [];
        }

        _worker = new PythonWorkerConnection(_pythonExe, workerPath);
        await _worker.StartAsync(cancellationToken).ConfigureAwait(false);

        var payload = manifests.Select(m => new
        {
            id = m.Id,
            api = m.Json.TryGetProperty("api", out var apiEl) ? apiEl.GetInt32() : 1,
            title = m.Json.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : m.Id,
            entry = m.Json.TryGetProperty("entry", out var entryEl) ? entryEl.GetString() : "main:WulingPlugin",
            minSlots = m.Json.TryGetProperty("minSlots", out var minEl) ? minEl.GetInt32() : 1,
            maxSlots = m.Json.TryGetProperty("maxSlots", out var maxEl) ? maxEl.GetInt32() : 9,
            pluginDir = m.Dir,
        }).ToArray();

        var loadResult = await _worker.LoadPluginsAsync(payload, cancellationToken).ConfigureAwait(false);
        var okIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (loadResult.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in okEl.EnumerateArray())
            {
                okIds.Add(id.GetString()!);
            }
        }

        var adapters = new List<PythonPluginAdapter>();
        foreach (var (dir, json, id) in manifests)
        {
            if (!okIds.Contains(id))
            {
                continue; // 装载失败 → 禁用 (04 §4)
            }

            adapters.Add(new PythonPluginAdapter(
                _worker,
                new CompareModeDescriptor
                {
                    Id = id,
                    Title = json.TryGetProperty("title", out var t) ? t.GetString() ?? id : id,
                    MinSlots = json.TryGetProperty("minSlots", out var mn) ? mn.GetInt32() : 1,
                    MaxSlots = json.TryGetProperty("maxSlots", out var mx) ? mx.GetInt32() : 9,
                    Kind = PluginKind.Python,
                    IsBuiltIn = false,
                    // datahints 能力声明(04 §3 通道③): histogram → 宿主内联直方图数据
                    Capabilities = json.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                        ? caps.EnumerateArray().Select(c => c.GetString() ?? "").Where(s => s.Length > 0).ToList()
                        : null,
                }));
        }

        return adapters;
    }

    public void Dispose()
    {
        _worker?.Dispose();
        _worker = null;
    }
}
