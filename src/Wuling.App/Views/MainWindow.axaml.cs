using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Wuling.App.ViewModels;
using Wuling.Core.Models;

namespace Wuling.App.Views;

/// <summary>差异字段高亮: Differs=true 橙色;否则浅灰白(固定色, 不随主题变化导致看不见)。</summary>
public sealed class DiffersToBrushConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly DiffersToBrushConverter Instance = new();

    private static readonly IBrush DiffersBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x9E, 0x2C));
    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? DiffersBrush : NormalBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 标记按钮激活态(声明式): Tag 命中实例对应的标记 → 激活底色;否则半透明中性底。
/// 两个实例分别服务 ✅/🗑 按钮,替代命令式刷视觉树(问题 3)。
/// </summary>
public sealed class MarkToBrushConverter : Avalonia.Data.Converters.IValueConverter
{
    private readonly PhotoTag _activeTag;

    private MarkToBrushConverter(PhotoTag activeTag) => _activeTag = activeTag;

    public static readonly MarkToBrushConverter SelectedInstance = new(PhotoTag.Selected);
    public static readonly MarkToBrushConverter RejectedInstance = new(PhotoTag.Rejected);

    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x7D, 0x44));
    private static readonly IBrush RejectedBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x40, 0x40, 0x40));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is PhotoTag tag && tag == _activeTag
            ? (_activeTag == PhotoTag.Selected ? SelectedBrush : RejectedBrush)
            : IdleBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>浏览条徽点颜色: 选中=绿, 废弃=红, 未标记=透明(不显示)。</summary>
public sealed class BadgeBrushConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly BadgeBrushConverter Instance = new();

    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x7D, 0x44));
    private static readonly IBrush RejectedBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly IBrush Transparent = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        PhotoTag.Selected => SelectedBrush,
        PhotoTag.Rejected => RejectedBrush,
        _ => Transparent,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>模式条激活态: 当前模式 id 等于 Descriptor.Id → 选中。</summary>
public sealed class ModeIdEqualsConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly ModeIdEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string current && parameter is string id && current == id;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>槽位数 → UniformGrid 列数(与主 VM Columns 同规则, 供插件视图内绑定)。</summary>
public sealed class SlotCountToColumnsConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly SlotCountToColumnsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            int n and <= 1 => "1",
            2 => "2",
            3 => "3",
            4 => "2",
            5 => "3",
            6 => "3",
            _ => "3",
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    /// <summary>槽位被程序重建时抑制事件回环。</summary>
    private bool _syncingSelection;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void HookViewModel()
    {
        if (Vm is not null)
        {
            // 会话恢复/目录切换后,按槽位反向同步列表选中(按钮激活态已声明式绑定 Tag, 无需命令式刷新)
            Vm.SlotsReplaced += OnSlotsReplaced;
        }
    }

    private void OnStripSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || Vm is null)
        {
            return;
        }

        Vm.SyncSlotsFromSelection(StripList.SelectedItems!);
    }

    /// <summary>打开处理设置对话框(03 F8)。</summary>
    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var dialog = new SettingsDialog(Vm.CommitOptions);
        await dialog.ShowDialog(this);
        Vm.SetCommitOptions(dialog.Result);
    }

    /// <summary>标记按钮 toggle(03 F4): 同一按钮再次点击即取消标记;两类标记互斥。</summary>
    private void OnToggleSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PhotoCardViewModel card } && Vm is not null)
        {
            Vm.ToggleMark(card, PhotoTag.Selected);
        }
    }

    private void OnToggleRejected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PhotoCardViewModel card } && Vm is not null)
        {
            Vm.ToggleMark(card, PhotoTag.Rejected);
        }
    }

    private void OnSlotsReplaced(object? sender, EventArgs e)
    {
        // 反向同步: 槽位内容 → 列表选中态(会话恢复)
        if (Vm is null || StripList.SelectedItems is null)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            StripList.SelectedItems.Clear();
            foreach (var slot in Vm.Slots)
            {
                StripList.SelectedItems.Add(slot);
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }
}
