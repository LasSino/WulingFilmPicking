using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wuling.Plugins.Abstractions;
using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins.Python;

/// <summary>
/// Python 插件经 JSON-RPC NDJSON over stdio 的宿主侧代理 (设计 04 §4)。
/// 一个 Worker 进程承载多个 Python 插件;本 Adapter 管进程 + 消息多路分发。
/// </summary>
public sealed class PythonWorkerConnection : IDisposable
{
    private const int RenderTimeoutMs = 3000;

    private readonly string _pythonExe;
    private readonly string _workerPath;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentQueue<string> _outgoing = new();
    private Process? _process;
    private Task? _readerTask;
    private Task? _writerTask;
    private Task? _stderrTask;
    private readonly ConcurrentQueue<string> _stderrLines = new();
    private int _nextId;
    private int _timeoutStreak;

    public PythonWorkerConnection(string pythonExe, string workerPath)
    {
        _pythonExe = pythonExe;
        _workerPath = workerPath;
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>启动 Worker 并等待 ready 握手。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return;
        }

        _timeoutStreak = 0;
        _pending.Clear();
        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            // -X utf8: 全进程 IO 强制 UTF-8(双保险, 防 Windows GBK 默认编码)
            Arguments = $"-X utf8 \"{_workerPath}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        _process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start python worker");
        _readerTask = Task.Run(() => ReadLoopAsync(_process.StandardOutput));
        _writerTask = Task.Run(() => WriteLoopAsync(_process.StandardInput));
        // stderr 排水(04 §4.6.6): 不排会写满管道缓冲死锁 Worker → 假性 3 次超时禁用
        _stderrTask = Task.Run(async () =>
        {
            while (await _process!.StandardError.ReadLineAsync() is { } line && line.Length > 0)
            {
                Debug.WriteLine($"[wuling-worker] {line}");
                _stderrLines.Enqueue(line);
                if (_stderrLines.Count > 100)
                {
                    _stderrLines.TryDequeue(out _);
                }
            }
        });

        // Worker 启动即发 ready 通知; 这里以 initialize 探活代替等待
        await SendRequestAsync("initialize", new { plugins = Array.Empty<object>() }, cancellationToken);
    }

    /// <summary>注册一批插件清单(initialize 由 StartAsync 探活, 装载走这里)。</summary>
    public async Task<JsonElement> LoadPluginsAsync(object[] manifests, CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync("initialize", new { plugins = manifests }, cancellationToken);
        _timeoutStreak = 0;
        return result;
    }

    /// <summary>渲染: 传入槽位与数据提示(04 §4 datahints), 返回声明式视图树 (JsonElement)。</summary>
    public async Task<JsonElement> RenderAsync(string modeId, IReadOnlyList<PhotoCard> slots,
        Func<PhotoCard, object>? dataHints = null, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            mode = modeId,
            slots = slots.Select(s =>
            {
                var hints = dataHints?.Invoke(s);
                return new Dictionary<string, object?>
                {
                    ["display_name"] = s.DisplayName,
                    ["preview_path"] = s.PreviewPath,
                    ["raw_path"] = s.RawPath,
                    ["jpeg_path"] = s.JpegPath,
                    ["has_raw"] = s.HasRawJpegPair,
                    ["tag"] = s.Tag.ToString().ToLowerInvariant(),
                    // datahints: 小数值数组内联(04 §3 通道③);直方图 256bin×3 ≈ 3KB
                    ["datahints"] = hints,
                };
            }).ToList(),
        };
        var result = await SendRequestAsync("render", payload, cancellationToken, RenderTimeoutMs);
        _timeoutStreak = 0;
        return result.TryGetProperty("view", out var view) ? view : JsonSerializer.SerializeToElement(new { kind = "empty" });
    }

    /// <summary>失败计数(超时/崩溃); 达到阈值 → 禁用信号。</summary>
    public bool ShouldDisable => _timeoutStreak >= 3;

    private async Task<JsonElement> SendRequestAsync(string method, object? @params, CancellationToken ct, int timeoutMs = 10_000)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });
        _outgoing.Enqueue(msg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await using var reg = timeout.Token.Register(() => tcs.TrySetCanceled(timeout.Token));
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _timeoutStreak++;
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"worker '{method}' timed out after {timeoutMs}ms (streak {_timeoutStreak})");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(StreamReader reader)
    {
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement.Clone();
                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                {
                    var id = idEl.GetInt32();
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("error", out var err))
                        {
                            tcs.TrySetException(new InvalidOperationException(err.TryGetProperty("message", out var m) ? m.GetString() : "worker error"));
                        }
                        else
                        {
                            tcs.TrySetResult(root.GetProperty("result"));
                        }
                    }
                }

                // 通知类消息 (ready/log) 暂忽略 — 记 stderr 便于诊断
                if (root.TryGetProperty("method", out var methodEl) && methodEl.GetString() == "ready")
                {
                    Debug.WriteLine("[wuling] python worker ready");
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[wuling] worker bad json: {ex.Message}");
            }
        }
    }

    private async Task WriteLoopAsync(StreamWriter writer)
    {
        writer.AutoFlush = true;
        while (_process is { HasExited: false })
        {
            if (_outgoing.TryDequeue(out var msg))
            {
                await writer.WriteLineAsync(msg).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(15).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _outgoing.Enqueue(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 0, method = "shutdown", @params = (object?)null }));
        }
        catch
        {
            // 忽略
        }

        if (_process is { HasExited: false })
        {
            try
            {
                // 给 worker 200ms 优雅退出, 然后强杀
                if (!_process.WaitForExit(200))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 忽略
            }
        }

        _process?.Dispose();
        _process = null;
    }
}
