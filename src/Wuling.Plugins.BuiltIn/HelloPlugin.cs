using System;
using System.ComponentModel;
using System.IO;
using Wuling.Plugins.Abstractions;

namespace Wuling.Plugins.BuiltIn;

/// <summary>
/// Hello 对比模式(C# 侧, 验收用 设计 04 §6): 每槽显示文件名与来源。
/// 与 Python Hello 同语义, 验证双语言契约等价。
/// </summary>
public sealed class HelloPlugin : ICompareModePlugin
{
    public CompareModeDescriptor Descriptor { get; } = new()
    {
        Id = "builtin.hello",
        Title = "Hello (C#)",
        Kind = PluginKind.CSharp,
        IsBuiltIn = true,
    };

    public IPluginServices? Services { get; private set; }

    public Task InitializeAsync(IPluginServices services, CancellationToken cancellationToken = default)
    {
        Services = services;
        return Task.CompletedTask;
    }

    public object CreateView(ICompareContext context) => new HelloViewModel(context);

    public void Dispose()
    {
    }
}

/// <summary>Hello 视图模型: 槽位文件名列表。</summary>
public sealed class HelloViewModel : INotifyPropertyChanged
{
    public HelloViewModel(ICompareContext context)
    {
        Context = context;
        Context.SlotsChanged += (_, _) => OnSlotsChanged();
        OnSlotsChanged();
    }

    public ICompareContext Context { get; }

    public System.Collections.Generic.IReadOnlyList<HelloRow> Rows { get; private set; } = [];

    private void OnSlotsChanged()
    {
        Rows = Context.Slots
            .Select((s, i) => new HelloRow(
                i + 1,
                s.DisplayName,
                s.PreviewPath ?? string.Empty,
                s.HasRawJpegPair ? "RAW+JPG" : "JPG",
                s.Tag.ToString()))
            .ToList();
        PropertyChanged?.Invoke(this, new(nameof(Rows)));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record HelloRow(int Index, string DisplayName, string PreviewPath, string Source, string Tag);
