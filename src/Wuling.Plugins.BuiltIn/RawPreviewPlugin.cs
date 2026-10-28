using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using Avalonia.Media.Imaging;
using Wuling.Plugins.Abstractions;
using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins.BuiltIn;

/// <summary>
/// RAW 预览模式(03 F6): 显示 RAW 内嵌预览;无内嵌的格式显示占位。
/// 摘要: RAW 格式 / 内嵌预览尺寸 / 文件大小。
/// 提取在宿主侧完成(IPluginServices.GetRawPreviewPng, 02 §6 首选层), 插件只引用结果。
/// </summary>
public sealed class RawPreviewPlugin : ICompareModePlugin
{
    public CompareModeDescriptor Descriptor { get; } = new()
    {
        Id = "builtin.raw",
        Title = "RAW预览",
        Kind = PluginKind.CSharp,
        IsBuiltIn = true,
    };

    public IPluginServices? Services { get; private set; }

    public Task InitializeAsync(IPluginServices services, CancellationToken cancellationToken = default)
    {
        Services = services;
        return Task.CompletedTask;
    }

    public object CreateView(ICompareContext context) => new RawPreviewViewModel(context, Services);

    public void Dispose()
    {
    }
}

public sealed class RawPreviewViewModel : INotifyPropertyChanged
{
    public RawPreviewViewModel(ICompareContext context, IPluginServices? services)
    {
        Context = context;
        _services = services;
        Context.SlotsChanged += OnSlotsChanged;
        OnSlotsChanged();
    }

    private readonly IPluginServices? _services;

    public ICompareContext Context { get; }

    public IReadOnlyList<RawSlotViewModel> Cards { get; private set; } = [];

    private void OnSlotsChanged(object? sender, EventArgs e) => OnSlotsChanged();

    private void OnSlotsChanged()
    {
        Cards = Context.Slots.Select(s => new RawSlotViewModel(s, _services)).ToList();
        PropertyChanged?.Invoke(this, new(nameof(Cards)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>RAW 槽位: 内嵌预览图(后台解码)+ 摘要行(RAW 格式/内嵌预览尺寸/大小)。</summary>
public sealed class RawSlotViewModel : System.ComponentModel.INotifyPropertyChanged, IDisposable
{
    private Bitmap? _bitmap;
    private readonly IPluginServices? _services;

    public RawSlotViewModel(PhotoCard source, IPluginServices? services)
    {
        Source = source;
        _services = services;
        // 后台解码, 就绪后回 UI 线程通知(避免 UI 冻结;M3 性能结论 04 §4.6.6)
        _ = System.Threading.Tasks.Task.Run(LoadBitmap);
    }

    private static readonly ConcurrentDictionary<string, byte> FailureCache = new(StringComparer.OrdinalIgnoreCase);

    public PhotoCard Source { get; }

    public string DisplayName => Source.DisplayName;

    /// <summary>RAW 主文件路径(优先 RawPath;纯 JPEG 槽位回退 PreviewPath, 显示占位)。</summary>
    public string? RawFilePath => Source.RawPath ?? Source.PreviewPath;

    /// <summary>摘要行(F6: RAW 格式/内嵌预览尺寸;RAW 文件大小)。</summary>
    public string SummaryText
    {
        get
        {
            if (_services is null || RawFilePath is null)
            {
                return string.Empty;
            }

            try
            {
                var meta = _services.GetFileMeta(RawFilePath);
                var parts = new List<string>(3);
                if (meta.RawFormat is not null)
                {
                    parts.Add(meta.RawFormat);
                }

                if (meta.EmbeddedPreviewWidth is > 0 && meta.EmbeddedPreviewHeight is > 0)
                {
                    parts.Add($"内嵌 {meta.EmbeddedPreviewWidth}×{meta.EmbeddedPreviewHeight}");
                }
                else if (meta.RawFormat is not null)
                {
                    parts.Add("无内嵌预览");
                }

                if (meta.Length > 0)
                {
                    parts.Add(PluginUtil.FormatBytes(meta.Length));
                }

                return string.Join(" · ", parts);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    public Bitmap? Bitmap
    {
        get => _bitmap;
        private set
        {
            _bitmap = value;
            PropertyChanged?.Invoke(this, new(nameof(Bitmap)));
            PropertyChanged?.Invoke(this, new(nameof(IsBitmapReady)));
        }
    }

    public bool IsBitmapReady => Bitmap is not null;

    /// <summary>加载失败详情(诊断用;正常流程为 null)。</summary>
    public string? LoadError { get; private set; }

    private void LoadBitmap()
    {
        var rawPath = RawFilePath;
        if (_services is null || rawPath is null || !File.Exists(rawPath))
        {
            return;
        }

        if (FailureCache.ContainsKey(rawPath))
        {
            return; // 已知无内嵌预览 → 保持占位
        }

        try
        {
            var png = _services.GetRawPreviewPng(rawPath);
            if (png is null)
            {
                System.Diagnostics.Trace.WriteLine($"[raw-slot] no preview for {Path.GetFileName(rawPath)}");
                FailureCache[rawPath] = 1;
                return;
            }

            using var ms = new MemoryStream(png);
            var bitmap = new Bitmap(ms);
            System.Diagnostics.Trace.WriteLine($"[raw-slot] decoded {bitmap.Size.Width}x{bitmap.Size.Height}");
            // Bitmap 已在后台构造完成(无 UI 依赖);赋值回 UI 线程仅是属性提交
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Bitmap = bitmap;
                System.Diagnostics.Trace.WriteLine($"[raw-slot] Bitmap set, IsBitmapReady={IsBitmapReady}");
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[raw-slot] FAILED {Path.GetFileName(rawPath)}: {ex.GetType().Name}: {ex.Message}");
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
            FailureCache[rawPath] = 1;
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
