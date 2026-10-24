using Wuling.Plugins.Abstractions;
using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins;

/// <summary>ICompareContext 宿主实现: 包装 App 的槽位状态,向插件发变更通知。</summary>
public sealed class CompareContext : ICompareContext
{
    private IReadOnlyList<PhotoCard> _slots;

    public CompareContext(IReadOnlyList<PhotoCard> slots)
    {
        _slots = slots;
    }

    public IReadOnlyList<PhotoCard> Slots => _slots;

    public event EventHandler? SlotsChanged;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>槽位变化时由宿主调用(整体替换)。</summary>
    public void UpdateSlots(IReadOnlyList<PhotoCard> slots)
    {
        _slots = slots;
        SlotsChanged?.Invoke(this, EventArgs.Empty);
        PropertyChanged?.Invoke(this, new(nameof(Slots)));
    }
}
