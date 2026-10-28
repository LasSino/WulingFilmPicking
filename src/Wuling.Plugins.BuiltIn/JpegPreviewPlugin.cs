using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Media.Imaging;
using Wuling.Plugins.Abstractions;
using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins.BuiltIn;

/// <summary>
/// 内置 JPEG 预览模式(M3 起走插件契约)。
/// 视图模型由 App 的 DataTemplate 按 VM 类型解析渲染;像素经由 IPluginServices.GetPixels 拉取。
/// </summary>
public sealed class JpegPreviewPlugin : ICompareModePlugin
{
    public CompareModeDescriptor Descriptor { get; } = new()
    {
        Id = "builtin.jpeg",
        Title = "JPEG预览",
        Kind = PluginKind.CSharp,
        IsBuiltIn = true,
    };

    public IPluginServices? Services { get; private set; }

    public Task InitializeAsync(IPluginServices services, CancellationToken cancellationToken = default)
    {
        Services = services;
        return Task.CompletedTask;
    }

    public object CreateView(ICompareContext context) => new JpegPreviewViewModel(context, Services);

    public void Dispose()
    {
        // 无非托管资源
    }
}

/// <summary>JPEG 预览模式的视图模型(App 的 DataTemplate 据此渲染;03 F6: 摘要 = 尺寸/大小)。</summary>
public sealed class JpegPreviewViewModel : INotifyPropertyChanged
{
    public JpegPreviewViewModel(ICompareContext context) : this(context, services: null)
    {
    }

    public JpegPreviewViewModel(ICompareContext context, IPluginServices? services)
    {
        Context = context;
        _services = services;
        Context.SlotsChanged += OnSlotsChanged;
        OnSlotsChanged();
    }

    private readonly IPluginServices? _services;

    public ICompareContext Context { get; }

    public IReadOnlyList<SlotViewModel> Cards { get; private set; } = [];

    private void OnSlotsChanged(object? sender, EventArgs e) => OnSlotsChanged();

    private void OnSlotsChanged()
    {
        Cards = Context.Slots.Select(s => new SlotViewModel(s, _services)).ToList();
        PropertyChanged?.Invoke(this, new(nameof(Cards)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>一个槽位的展示单元: 文件名 + 延迟解码的预览图 + 摘要行(尺寸/大小)。</summary>
public sealed class SlotViewModel : IDisposable
{
    private Bitmap? _bitmap;
    private readonly IPluginServices? _services;

    public SlotViewModel(PhotoCard source, IPluginServices? services)
    {
        Source = source;
        _services = services;
    }

    public PhotoCard Source { get; }

    public string DisplayName => Source.DisplayName;

    public bool HasRawJpegPair => Source.HasRawJpegPair;

    /// <summary>摘要行(F6: 尺寸/大小;meta 不可得时空串)。</summary>
    public string SummaryText
    {
        get
        {
            if (_services is null || Source.PreviewPath is null)
            {
                return string.Empty;
            }

            try
            {
                var meta = _services.GetFileMeta(Source.PreviewPath);
                var parts = new List<string>(2);
                if (meta.PixelWidth is > 0 && meta.PixelHeight is > 0)
                {
                    parts.Add($"{meta.PixelWidth}×{meta.PixelHeight}");
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

    /// <summary>延迟解码: 首次绑定时经宿主服务解码(已按 EXIF 方向归一化, 与目录缩略图一致);契约层只有路径引用(04 §3.1)。</summary>
    public Bitmap? Bitmap
    {
        get
        {
            if (_bitmap is null && Source.PreviewPath is not null && System.IO.File.Exists(Source.PreviewPath))
            {
                try
                {
                    // edge=0: 原尺寸(≤8192 不缩), 宿主侧已完成方向归一化(ThumbnailService.ApplyOrientation)
                    _bitmap = _services is not null
                        ? PluginUtil.ToBitmap(_services.GetPixels(Source.PreviewPath, edge: 0))
                        : new Bitmap(Source.PreviewPath); // 无服务回退(设计时/测试), 无方向处理
                }
                catch (Exception)
                {
                    // 解码失败 → 保持 null, 视图层显示占位符
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

