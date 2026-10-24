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

    public object CreateView(ICompareContext context) => new JpegPreviewViewModel(context);

    public void Dispose()
    {
        // 无非托管资源
    }
}

/// <summary>JPEG 预览模式的视图模型(App 的 DataTemplate 据此渲染)。</summary>
public sealed class JpegPreviewViewModel : INotifyPropertyChanged
{
    public JpegPreviewViewModel(ICompareContext context)
    {
        Context = context;
        Context.SlotsChanged += (_, _) => OnSlotsChanged();
        OnSlotsChanged();
    }

    public ICompareContext Context { get; }

    public IReadOnlyList<SlotViewModel> Cards { get; private set; } = [];

    private void OnSlotsChanged()
    {
        Cards = Context.Slots.Select(s => new SlotViewModel(s)).ToList();
        PropertyChanged?.Invoke(this, new(nameof(Cards)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>一个槽位的展示单元: 文件名 + 延迟解码的预览图。</summary>
public sealed class SlotViewModel : IDisposable
{
    private Bitmap? _bitmap;

    public SlotViewModel(PhotoCard source)
    {
        Source = source;
    }

    public PhotoCard Source { get; }

    public string DisplayName => Source.DisplayName;

    public bool HasRawJpegPair => Source.HasRawJpegPair;

    /// <summary>延迟解码: 首次绑定时从 PreviewPath 解码;契约层只有路径引用(04 §3.1)。</summary>
    public Bitmap? Bitmap
    {
        get
        {
            if (_bitmap is null && Source.PreviewPath is not null && System.IO.File.Exists(Source.PreviewPath))
            {
                try
                {
                    _bitmap = new Bitmap(Source.PreviewPath);
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

