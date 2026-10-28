using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wuling.Plugins.Abstractions;
using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins.Python;

/// <summary>
/// Python 插件的 ICompareModePlugin 代理: CreateView 返回视图树 JSON 包裹对象,
/// 由宿主 App 的声明式渲染器(DataTemplate/渲染器)显示 (设计 04 §3)。
/// </summary>
public sealed class PythonPluginAdapter : ICompareModePlugin
{
    private readonly PythonWorkerConnection _worker;

    public PythonPluginAdapter(PythonWorkerConnection worker, CompareModeDescriptor descriptor)
    {
        _worker = worker;
        Descriptor = descriptor;
    }

    public CompareModeDescriptor Descriptor { get; }

    /// <summary>创建视图: 返回 PythonViewEnvelope(内含声明式视图树), 由 App 渲染器解析。</summary>
    public object CreateView(ICompareContext context)
        => new PythonViewEnvelope(Descriptor.Id, _worker, context, Descriptor.Capabilities is { } caps && caps.Contains("histogram") ? DataHintsFor : null);

    /// <summary>datahints: 声明了 histogram 能力的插件, 宿主随 render 请求内联直方图数据(04 §3 通道③)。</summary>
    private object DataHintsFor(PhotoCard card)
    {
        try
        {
            var h = _services?.GetHistogram(card.PreviewPath);
            return new
            {
                histogram = h is null ? null : new { r = h.Red, g = h.Green, b = h.Blue },
            };
        }
        catch (Exception)
        {
            return new { };
        }
    }

    private IPluginServices? _services;

    public Task InitializeAsync(IPluginServices services, CancellationToken cancellationToken = default)
    {
        _services = services; // 装载已在 Worker initialize 阶段完成; 这里记服务供 datahints 用
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // Worker 生命周期归 PythonPluginHostRegistry; 这里不杀进程
    }
}

/// <summary>声明式视图树包裹: App 绑定此对象;槽位变化时向 Worker 拉取并解析为槽位视图。</summary>
public sealed class PythonViewEnvelope : System.ComponentModel.INotifyPropertyChanged, IDisposable
{
    private readonly PythonWorkerConnection _worker;
    private readonly System.Func<PhotoCard, object>? _dataHints;

    public PythonViewEnvelope(string modeId, PythonWorkerConnection worker, ICompareContext context,
        System.Func<PhotoCard, object>? dataHints = null)
    {
        ModeId = modeId;
        _worker = worker;
        Context = context;
        _dataHints = dataHints;
        Context.SlotsChanged += OnSlotsChanged;
        // 首拉渲染( envelopes 可能晚于槽位就绪 )
        _ = RefreshAsync();
    }

    private void OnSlotsChanged(object? sender, EventArgs e) => _ = RefreshAsync();

    public string ModeId { get; }

    public ICompareContext Context { get; }

    /// <summary>解析后的槽位视图(声明式树 → PythonSlotView 列表)。</summary>
    public IReadOnlyList<PythonSlotView> Cards { get; private set; } = [];

    public string? Error { get; private set; }

    public bool HasError => Error is not null;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ViewChanged;

    /// <summary>拉取渲染。超时/失败 → 显示错误态, Worker 不杀 (04 §4 决策表)。</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var view = await _worker.RenderAsync(ModeId, Context.Slots, _dataHints).ConfigureAwait(false);
            var cards = DeclarativeRenderer.Interpret(view);

            // 回 UI 线程后统一提交(绑定期望属性变化在 UI 线程通知)
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var old in Cards)
                {
                    old.Dispose();
                }

                Cards = cards;
                Error = null;
                PropertyChanged?.Invoke(this, new(nameof(Cards)));
                PropertyChanged?.Invoke(this, new(nameof(HasError)));
                ViewChanged?.Invoke(this, EventArgs.Empty);
            }).GetTask();
        }
        catch (Exception ex)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Error = ex.Message;
                PropertyChanged?.Invoke(this, new(nameof(HasError)));
                ViewChanged?.Invoke(this, EventArgs.Empty);
            }).GetTask();
        }
    }

    public void Dispose()
    {
        Context.SlotsChanged -= OnSlotsChanged;
        foreach (var card in Cards)
        {
            card.Dispose();
        }
    }
}

/// <summary>声明式视图树 → 槽位视图的轻量解释器(04 §3 kind: image/text/kv/stack/row;未知 kind 忽略)。</summary>
public static class DeclarativeRenderer
{
    public static IReadOnlyList<PythonSlotView> Interpret(JsonElement root)
    {
        var slots = new List<PythonSlotView>();
        foreach (var child in ChildrenOf(root))
        {
            slots.Add(InterpretSlot(child));
        }

        return slots;
    }

    private static IEnumerable<JsonElement> ChildrenOf(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in children.EnumerateArray())
            {
                yield return c;
            }
        }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            yield return node;
        }
    }

    private static PythonSlotView InterpretSlot(JsonElement node)
    {
        string? imageSrc = null;
        string? title = null;
        var kvPairs = new List<PythonKvPair>();
        PythonHistogramData? hist = null;
        Walk(node, ref imageSrc, ref title, kvPairs, ref hist);
        return new PythonSlotView(title ?? "(未命名)", imageSrc, kvPairs, hist);
    }

    private static void Walk(JsonElement node, ref string? imageSrc, ref string? title, List<PythonKvPair> kvPairs, ref PythonHistogramData? hist)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var kind = node.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        switch (kind)
        {
            case "image":
                imageSrc ??= node.TryGetProperty("src", out var src) && src.ValueKind == JsonValueKind.String && File.Exists(src.GetString()) ? src.GetString() : null;
                break;
            case "text":
                if (title is null && node.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                {
                    title = v.GetString();
                }

                break;
            case "kv":
                if (node.TryGetProperty("pairs", out var pairs) && pairs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pair in pairs.EnumerateArray())
                    {
                        if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() >= 2)
                        {
                            kvPairs.Add(new PythonKvPair(pair[0].GetString() ?? "", pair[1].ToString() ?? ""));
                        }
                    }
                }

                break;
            case "stack" or "row" or "grid":
                if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in children.EnumerateArray())
                    {
                        Walk(child, ref imageSrc, ref title, kvPairs, ref hist);
                    }
                }

                break;
            case "histogram":
                hist ??= ParseHistogram(node);
                break;
            default:
                // 未知 kind → 忽略(向前兼容, 04 §3)
                break;
        }
    }

    private static PythonHistogramData? ParseHistogram(JsonElement node)
    {
        static int[] ReadArray(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<int>(256);
            foreach (var v in arr.EnumerateArray())
            {
                list.Add(v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0);
            }

            return [.. list];
        }

        var r = ReadArray(node, "r");
        var g = ReadArray(node, "g");
        var b = ReadArray(node, "b");
        return r.Length == 0 && g.Length == 0 && b.Length == 0 ? null : new PythonHistogramData(r, g, b);
    }
}

/// <summary>一个 Python 插件槽位的渲染结果。</summary>
public sealed class PythonSlotView : IDisposable
{
    private Avalonia.Media.Imaging.Bitmap? _bitmap;

    public PythonSlotView(string displayName, string? previewPath, IReadOnlyList<PythonKvPair> kvPairs,
        PythonHistogramData? histogram = null)
    {
        DisplayName = displayName;
        PreviewPath = previewPath;
        KvPairs = kvPairs;
        Histogram = histogram;
    }

    public string DisplayName { get; }

    public string? PreviewPath { get; }

    public IReadOnlyList<PythonKvPair> KvPairs { get; }

    /// <summary>直方图数据(插件视图声明 histogram 元素时非 null)。</summary>
    public PythonHistogramData? Histogram { get; }

    public bool HasHistogram => Histogram is not null;

    /// <summary>延迟解码预览图(通道①: 路径引用, 宿主/插件都不搬像素)。</summary>
    public Avalonia.Media.Imaging.Bitmap? PreviewBitmap
    {
        get
        {
            if (_bitmap is null && PreviewPath is not null)
            {
                try
                {
                    _bitmap = new Avalonia.Media.Imaging.Bitmap(PreviewPath);
                }
                catch (Exception)
                {
                    // 解码失败 → 占位符
                }
            }

            return _bitmap;
        }
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

public sealed record PythonKvPair(string Key, string Value);

/// <summary>直方图数据(256 bin × RGB;声明式 histogram 元素的载荷)。</summary>
public sealed record PythonHistogramData(int[] Red, int[] Green, int[] Blue);
