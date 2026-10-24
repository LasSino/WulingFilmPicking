using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins.Abstractions;

/// <summary>插件种类。</summary>
public enum PluginKind
{
    /// <summary>C# 原生插件(Avalonia Control 直渲染)。</summary>
    CSharp,

    /// <summary>Python 插件(声明式视图树, 宿主渲染)。</summary>
    Python,
}

/// <summary>对比模式插件描述与能力声明(04 文档 §2)。</summary>
public sealed record CompareModeDescriptor
{
    /// <summary>全局唯一 id, 如 "builtin.jpeg" / "py.my-mode"。</summary>
    public required string Id { get; init; }

    /// <summary>已本地化标题。</summary>
    public required string Title { get; init; }

    /// <summary>槽位数量范围(1-9)。</summary>
    public int MinSlots { get; init; } = 1;

    public int MaxSlots { get; init; } = 9;

    public PluginKind Kind { get; init; }

    /// <summary>是否随应用分发的内置插件(UI 分组用)。</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>Python 插件的声明式视图能力;C# 插件为 null。</summary>
    public IReadOnlyList<string>? Capabilities { get; init; }
}

/// <summary>
/// 对比模式插件契约。实例在模式首次激活时创建,随模式切换释放。
/// C# 插件实现此接口;Python 插件由 PythonPluginAdapter 代理同一契约。
/// </summary>
public interface ICompareModePlugin : IDisposable
{
    CompareModeDescriptor Descriptor { get; }

    /// <summary>初始化(宿主注入服务)。失败抛异常 → 模式禁用。</summary>
    Task InitializeAsync(IPluginServices services, CancellationToken cancellationToken = default);

    /// <summary>当前模式应渲染的视图。C# 插件返回 Avalonia Control;Python 插件返回宿主提供的 PythonPluginView。</summary>
    object CreateView(ICompareContext context);
}

/// <summary>模式视图上下文: 当前槽位与变更通知(契约保证槽位变化时 PropertyChanged)。</summary>
public interface ICompareContext : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>当前槽位中的照片(按槽位顺序;数量即槽位数)。</summary>
    IReadOnlyList<PhotoCard> Slots { get; }

    /// <summary>当槽位内容变化(增/删/重排)后触发;插件应刷新渲染。</summary>
    event EventHandler? SlotsChanged;
}
