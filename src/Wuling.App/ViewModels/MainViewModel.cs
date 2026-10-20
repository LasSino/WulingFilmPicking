using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wuling.Core.Commit;
using Wuling.Core.Models;
using Wuling.Core.Scanning;
using Wuling.Core.Sessions;
using Wuling.Core.Tagging;
using Wuling.Imaging;

namespace Wuling.App.ViewModels;

/// <summary>
/// 主窗口 VM — M0 骨架: 打开目录 + 浏览条 + 对比区骨架 + 标记。
/// 槽位可调与模式插件按设计文档 04 在 M1/M3 接入。
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly DirectoryScanner _scanner = new();
    private readonly ExifProvider _exif = new();
    private ThumbnailService? _thumbnails;

    [ObservableProperty]
    private string _greeting = "武陵选片局 — 打开一个照片目录开始";

    [ObservableProperty]
    private string? _currentDirectory;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private PhotoCardViewModel? _selectedCard;

    public ObservableCollection<PhotoCardViewModel> Cards { get; } = [];

    /// <summary>对比区槽位: 由浏览条多选驱动,数量 = 选中数(0-9)。</summary>
    public ObservableCollection<PhotoCardViewModel> Slots { get; } = [];

    /// <summary>槽位上限(设计文档 01 §2.3: 1-9 宫格)。</summary>
    public const int MaxSlots = 9;

    /// <summary>槽位整体被替换(目录加载/会话恢复)后通知视图同步列表选中态。</summary>
    public event EventHandler? SlotsReplaced;

    /// <summary>浏览条多选 → 重建槽位(按 Cards 顺序,超出上限截断)。</summary>
    public void SyncSlotsFromSelection(System.Collections.IList selected)
    {
        var chosen = selected.OfType<PhotoCardViewModel>()
            .OrderBy(c => Cards.IndexOf(c))
            .Take(MaxSlots)
            .ToList();

        ApplySlots(chosen, saveSession: true);
    }

    /// <summary>槽位构建统一入口(用户多选与会话恢复共用一条管线,Bug1 修复)。</summary>
    private void ApplySlots(IReadOnlyList<PhotoCardViewModel> chosen, bool saveSession)
    {
        Slots.Clear();
        foreach (var card in chosen)
        {
            Slots.Add(card);
        }

        // 对比区加载全清晰度视图(M2: 与浏览条 96px 缩略图分级)
        _ = LoadCompareViewsAsync(chosen);
        OnSlotsChanged(saveSession);
    }

    /// <summary>对比区全尺寸视图加载(边长按槽位估算;M3 随模式插件精确化)。</summary>
    private async Task LoadCompareViewsAsync(IReadOnlyList<PhotoCardViewModel> slots)
    {
        var service = _thumbnails;
        if (service is null)
        {
            return;
        }

        foreach (var card in slots)
        {
            await card.LoadCompareViewAsync(service, CompareViewEdge);
        }
    }

    /// <summary>对比区解码边长: 2560px 足以覆盖 4K 屏 3×3 宫格的单格需求。</summary>
    public const int CompareViewEdge = 2560;

    /// <summary>当前对比模式(M2: jpeg/exif;M3 起模式由插件提供)。</summary>
    [ObservableProperty]
    private string _currentMode = "jpeg";

    partial void OnCurrentModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsExifMode));
        OnPropertyChanged(nameof(ExifCompare));
        _ = SaveSessionAsync();
    }

    public bool IsExifMode => CurrentMode == "exif";

    /// <summary>EXIF 对比模式数据(仅在 exif 模式且槽位非空时非 null)。</summary>
    public ExifCompareViewModel? ExifCompare => IsExifMode && Slots.Count > 0 ? new ExifCompareViewModel(Slots) : null;

    [RelayCommand]
    private void SetMode(string mode) => CurrentMode = mode;
    /// <summary>提交选项(默认: 废弃=软删除;由设置对话框修改后持久化)。</summary>
    public CommitOptions CommitOptions { get; private set; } = new();

    public void SetCommitOptions(CommitOptions options)
    {
        CommitOptions = options;
        _ = SaveSessionAsync();
    }

    /// <summary>会话恢复时还原提交选项。</summary>
    private void RestoreCommitOptions(CommitOptions options) => CommitOptions = options;

    /// <summary>会话恢复时还原模式。</summary>
    private void RestoreMode(string? mode)
    {
        if (mode is "jpeg" or "exif")
        {
            CurrentMode = mode;
        }
    }

    /// <summary>槽位集合变化后的派生属性通知与持久化。</summary>
    private void OnSlotsChanged(bool saveSession)
    {
        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(HasNoSlots));
        OnPropertyChanged(nameof(Slots)); // 通知视图重建容器并刷新浮动按钮态
        if (saveSession && CurrentDirectory is not null)
        {
            _ = SaveSessionAsync();
        }
    }

    /// <summary>对比区为空(尚未多选任何照片)。</summary>
    public bool HasNoSlots => Slots.Count == 0;

    public string Columns => Slots.Count switch
    {
        <= 1 => "1", 2 => "2", 3 => "3",
        4 => "2", 5 => "3", 6 => "3",
        _ => "3",
    };

    public string StatusText => CurrentDirectory is null
        ? "未打开目录"
        : $"{Cards.Count} 张 (✅{Count(PhotoTag.Selected)} 🗑{Count(PhotoTag.Rejected)} ⬜{Count(PhotoTag.Unmarked)})";

    [RelayCommand]
    private async Task OpenDirectoryAsync()
    {
        var window = Avalonia.Application.Current!.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        if (window is null)
        {
            return;
        }

        // Avalonia 12: 经 StorageProvider 打开文件夹选择器
        var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "选择照片目录",
            AllowMultiple = false,
        };

        var folders = await window.StorageProvider.OpenFolderPickerAsync(options);
        // Avalonia 12: IStorageFolder.Path 是 file:// URI, 本地目录转回文件系统路径
        var dir = folders.Count > 0 && folders[0].Path.IsAbsoluteUri && folders[0].Path.Scheme == "file"
            ? folders[0].Path.LocalPath
            : null;
        if (dir is not null)
        {
            await LoadDirectoryAsync(dir);
        }
    }

    /// <summary>加载目录。测试与 CLI 可直接调用。</summary>
    public async Task LoadDirectoryAsync(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            Greeting = "目录不存在";
            return;
        }

        IsBusy = true;
        try
        {
            _thumbnails?.Dispose();
            _thumbnails = new ThumbnailService();
            Cards.Clear();
            Slots.Clear();
            CurrentDirectory = directory;

            // M0: 一次性同步扫描;M1 换 IAsyncEnumerable 分批渲染(02 §5)
            var pairs = await Task.Run(() => _scanner.Scan(directory));
            foreach (var pair in pairs)
            {
                var card = new PhotoCardViewModel(pair);
                card.AttachExif(await Task.Run(() => _exif.GetSummary(pair.PreviewPath)));
                card.PropertyChanged += CardOnPropertyChanged;
                Cards.Add(card);
            }

            Greeting = pairs.Count == 0 ? "目录中没有找到照片" : $"已加载 {pairs.Count} 张";
            OnPropertyChanged(nameof(StatusText));
            var restored = TryRestoreSession(pairs);
            ApplySlots(restored, saveSession: false);
            SlotsReplaced?.Invoke(this, EventArgs.Empty);
            _ = LoadThumbnailsAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>M1 缩略图流水线: 顺序后台解码回填(M1+ 升级为并发闸门+可视优先)。</summary>
    private async Task LoadThumbnailsAsync()
    {
        var service = _thumbnails;
        if (service is null)
        {
            return;
        }

        foreach (var card in Cards)
        {
            if (service != _thumbnails)
            {
                return; // 目录已切换,丢弃流水线
            }

            await card.LoadThumbnailAsync(service, ThumbnailEdge);
        }
    }

    /// <summary>当前缩略图边长(M1 固定 96;Ctrl+滚轮缩放后续接入)。</summary>
    public const int ThumbnailEdge = 96;

    private void CardOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhotoCardViewModel.Tag))
        {
            OnPropertyChanged(nameof(StatusText));
            _ = SaveSessionAsync(); // 防抖自动保存(02 §9: 1s 防抖,此处简化为后台写入)
        }
    }

    /// <summary>保存会话(后台线程,不阻塞 UI)。</summary>
    [RelayCommand]
    private Task SaveSession() => SaveSessionAsync();

    /// <summary>保存会话(后台线程,不阻塞 UI)。</summary>
    public async Task SaveSessionAsync()
    {
        if (CurrentDirectory is null)
        {
            return;
        }

        var root = CurrentDirectory;
        var cards = Cards.Select(c => c.Pair).ToList();
        var slots = Slots.Select(s => s.Pair).ToList();
        var count = Slots.Count;
        var mode = CurrentMode;
        await Task.Run(() =>
        {
            var state = SessionStore.Capture(root, cards, slots, count);
            state.Mode = mode;
            state.CommitOptions = StoredCommitOptions.From(CommitOptions);
            SessionStore.Save(state);
        });
    }

    /// <summary>恢复会话(打开目录时调用;03 F5)。返回恢复出的槽位卡片列表。</summary>
    private List<PhotoCardViewModel> TryRestoreSession(IReadOnlyList<PhotoPair> pairs)
    {
        var restoredSlots = new List<PhotoCardViewModel>();
        var state = SessionStore.Load(CurrentDirectory!);
        if (state is null)
        {
            return restoredSlots;
        }

        SessionStore.Apply(state, pairs);
        RestoreMode(state.Mode);
        RestoreCommitOptions(state.CommitOptions.ToOptions());

        // 恢复槽位内容(按保存时的顺序)
        foreach (var key in state.SlotOrder.Take(MaxSlots))
        {
            var card = Cards.FirstOrDefault(c =>
                string.Equals(c.PreviewPath, key, StringComparison.OrdinalIgnoreCase));
            if (card is not null)
            {
                restoredSlots.Add(card);
            }
        }

        return restoredSlots;
    }

    private int Count(PhotoTag tag)
    {
        var count = 0;
        foreach (var card in Cards)
        {
            if (card.Tag == tag)
            {
                count++;
            }
        }

        return count;
    }

    [RelayCommand]
    private void MarkSelected(PhotoCardViewModel? card) => ApplyTag(card, PhotoTag.Selected);

    [RelayCommand]
    private void MarkRejected(PhotoCardViewModel? card) => ApplyTag(card, PhotoTag.Rejected);

    [RelayCommand]
    private void ClearMark(PhotoCardViewModel? card) => ApplyTag(card, PhotoTag.Unmarked);

    /// <summary>对比区浮动按钮的 toggle(01 §2.3): 同一按钮再点即取消;两类标记互斥。</summary>
    public void ToggleMark(PhotoCardViewModel card, PhotoTag target)
    {
        var opposite = target == PhotoTag.Selected ? PhotoTag.Rejected : PhotoTag.Selected;
        var current = card.Tag;
        var next = current == target ? PhotoTag.Unmarked : target;
        if (TagStateMachine.CanTransition(current, next))
        {
            card.Tag = TagStateMachine.Apply(card.Pair, next);
        }
        else if (current == opposite)
        {
            // 当前是另一类标记 → 直接切换到目标标记
            card.Tag = TagStateMachine.Apply(card.Pair, target);
        }
    }

    private static void ApplyTag(PhotoCardViewModel? card, PhotoTag target)
    {
        if (card is not null && TagStateMachine.CanTransition(card.Tag, target))
        {
            card.Tag = TagStateMachine.Apply(card.Pair, target);
        }
    }

    /// <summary>完成(提交): 弹出确认对话框执行处置计划。由 View 调用(需要窗口句柄)。</summary>
    [RelayCommand]
    private async Task CommitAsync()
    {
        if (CurrentDirectory is null)
        {
            return;
        }

        var window = Avalonia.Application.Current!.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        if (window is null)
        {
            return;
        }

        var plan = await Task.Run(() => CommitPlanner.Build(Cards.Select(c => c.Pair), CommitOptions));
        var dialog = new Views.CommitDialog(plan, CurrentDirectory);
        await dialog.ShowDialog(window);
    }
}
