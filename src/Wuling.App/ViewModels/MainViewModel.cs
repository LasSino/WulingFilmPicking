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
using Wuling.Plugins.Abstractions;
using Wuling.Plugins.BuiltIn;

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
    private Wuling.Plugins.PluginServicesBridge? _servicesBridge; // 单例服务桥: 切目录 SwapThumbnails 原子换底层
    private Wuling.Plugins.PluginHost? _pluginHost;
    private Wuling.Plugins.CompareContext? _pluginContext;

    /// <summary>插件宿主初始化(M3): 内置 C# 插件 + Python 插件发现(失败不阻塞宿主)。</summary>
    private async Task EnsurePluginHostAsync()
    {
        if (_pluginHost is not null)
        {
            // 切目录: 底层 ThumbnailService 已重建, 原子同步给插件侧服务桥(插件无感)。
            _servicesBridge!.SwapThumbnails(_thumbnails!);
            return;
        }

        _servicesBridge = new Wuling.Plugins.PluginServicesBridge(_exif, _thumbnails!);
        _pluginHost = new Wuling.Plugins.PluginHost(() => _servicesBridge);
        await _pluginHost.RegisterAsync(new Wuling.Plugins.BuiltIn.JpegPreviewPlugin());
        await _pluginHost.RegisterAsync(new Wuling.Plugins.BuiltIn.RawPreviewPlugin());
        await _pluginHost.RegisterAsync(new Wuling.Plugins.BuiltIn.ExifComparePlugin());

        // Python 插件发现(04 §5): plugins/python/*/plugin.json; 解释器不可用则静默跳过
        var pythonExe = FindPythonInterpreter();
        if (pythonExe is not null)
        {
            try
            {
                _pythonRegistry = new Wuling.Plugins.Python.PythonPluginRegistry(pythonExe);
                foreach (var adapter in await _pythonRegistry.DiscoverAsync())
                {
                    await _pluginHost.RegisterAsync(adapter);
                }
            }
            catch
            {
                // Worker 启动失败 → Python 插件全部灰态(04 §4);宿主继续运行
            }
        }

        RebuildModeButtons();
    }

    /// <summary>注册完成后重建模式按钮列表, 并按当前模式刷高亮。</summary>
    private void RebuildModeButtons()
    {
        _modeButtons.Clear();
        foreach (var descriptor in _pluginHost?.Plugins.Select(p => p.Descriptor) ?? [])
        {
            _modeButtons.Add(new ModeButtonViewModel(descriptor));
        }

        RefreshModeButtons();
        OnPropertyChanged(nameof(Modes));
    }

    private void RefreshModeButtons()
    {
        foreach (var button in _modeButtons)
        {
            button.SetChecked(button.Descriptor.Id == CurrentMode);
        }
    }

    private Wuling.Plugins.Python.PythonPluginRegistry? _pythonRegistry;

    /// <summary>解释器发现: 设置优先 → PATH → 常见安装位置(M3 首版简化)。</summary>
    private static string? FindPythonInterpreter()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("WULING_PYTHON"),
            "C:\\Installations\\Programming\\Python\\python.exe",
        }.Where(p => !string.IsNullOrEmpty(p) && System.IO.File.Exists(p));

        var fromPath = "python.exe";
        try
        {
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';');
            var hit = pathDirs.Select(d => System.IO.Path.Combine(d, fromPath)).FirstOrDefault(System.IO.File.Exists);
            if (hit is not null)
            {
                return hit;
            }
        }
        catch
        {
            // PATH 解析失败 → 落到固定候选
        }

        return candidates.FirstOrDefault();
    }

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

        // M3: 向插件上下文分发槽位变化(契约 SlotsChanged)
        var pluginCards = Slots.Select(s => Wuling.Plugins.PluginHost.ToCard(s.Pair)).ToList();
        if (_pluginContext is null)
        {
            _pluginContext = new Wuling.Plugins.CompareContext(pluginCards);
        }
        else
        {
            _pluginContext.UpdateSlots(pluginCards);
        }

        OnPropertyChanged(nameof(ModeView));

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

    /// <summary>可用对比模式(M3 插件驱动;包装为按钮 VM, IsChecked 随 CurrentMode 刷新)。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ModeButtonViewModel> Modes => _modeButtons;

    private readonly System.Collections.ObjectModel.ObservableCollection<ModeButtonViewModel> _modeButtons = [];

    /// <summary>当前激活模式 id。</summary>
    [ObservableProperty]
    private string _currentMode = "builtin.jpeg";

    /// <summary>按模式缓存的插件视图(首次 CreateView 后复用;上下文订阅随视图保持稳定)。</summary>
    private readonly Dictionary<string, object?> _modeViews = new();

    /// <summary>当前模式的插件视图(契约 CreateView 的产物;App DataTemplate 按 VM 类型渲染)。</summary>
    public object? ModeView
    {
        get
        {
            var plugin = _pluginHost?.GetPlugin(CurrentMode);
            if (plugin is null || _pluginContext is null)
            {
                return null;
            }

            if (!_modeViews.TryGetValue(CurrentMode, out var view))
            {
                view = plugin.CreateView(_pluginContext);
                _modeViews[CurrentMode] = view;
            }

            return view;
        }
    }

    partial void OnCurrentModeChanged(string value)
    {
        RefreshModeButtons();
        OnPropertyChanged(nameof(ModeView));
        _ = SaveSessionAsync();
    }

    [RelayCommand]
    private void SetMode(string mode)
    {
        if (CurrentMode == mode)
        {
            // 重复点击同一模式: CurrentMode 不变不会触发 OnCurrentModeChanged,
            // 但 ToggleButton 本地视觉态已被翻转为 false —— 显式刷新回弹选中态(单选锁定)。
            RefreshModeButtons();
            return;
        }

        CurrentMode = mode;
    }
    /// <summary>提交选项(默认: 废弃=软删除;由设置对话框修改后持久化)。</summary>
    public CommitOptions CommitOptions { get; private set; } = new();

    public void SetCommitOptions(CommitOptions options)
    {
        CommitOptions = options;
        _ = SaveSessionAsync();
    }

    /// <summary>会话恢复时还原提交选项。</summary>
    private void RestoreCommitOptions(CommitOptions options) => CommitOptions = options;

    /// <summary>会话恢复时还原模式(兼容 M2 短 id 与 M3 插件 id)。</summary>
    private void RestoreMode(string? mode)
    {
        var normalized = mode switch
        {
            "jpeg" => "builtin.jpeg",
            "exif" => "builtin.exif",
            _ => mode,
        };

        if (normalized is not null && _pluginHost!.GetPlugin(normalized) is not null)
        {
            CurrentMode = normalized;
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
            await EnsurePluginHostAsync();
            CurrentDirectory = directory;

            // M0: 一次性同步扫描;M1 换 IAsyncEnumerable 分批渲染(02 §5)
            var pairs = await Task.Run(() => _scanner.Scan(directory));
            // 切换目录: 清空上一目录的卡片与选中态(此前只 Add 不 Clear, 导致列表/状态栏不更新)
            foreach (var old in Cards)
            {
                old.PropertyChanged -= CardOnPropertyChanged;
            }

            Cards.Clear();

            // 会话标记先回放到 pairs, 再构造卡片 —— 卡片 ctor 捕获 pair.Tag 作为初值;
            // 若后回放(Apply 只改 Pair.Tag 模型), 卡片 VM 与 UI 徽点全部停留在未标记,
            // 但提交仍按 Pair 处理 → "标记在、界面不显示"(2026-10-31 bug 实锤)。
            var restoredState = SessionStore.Load(directory);
            if (restoredState is not null)
            {
                SessionStore.Apply(restoredState, pairs);
            }

            foreach (var pair in pairs)
            {
                var card = new PhotoCardViewModel(pair);
                card.AttachExif(await Task.Run(() => _exif.GetSummary(pair.PreviewPath)));
                card.PropertyChanged += CardOnPropertyChanged;
                Cards.Add(card);
            }

            Greeting = pairs.Count == 0 ? "目录中没有找到照片" : $"已加载 {pairs.Count} 张";
            OnPropertyChanged(nameof(StatusText));
            var restored = TryRestoreSession(directory, pairs, restoredState);
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
        });    }

    /// <summary>恢复会话(打开目录时调用;03 F5)。返回恢复出的槽位卡片列表。preloaded=调用方已读过并回放过标记的状态。</summary>
    private List<PhotoCardViewModel> TryRestoreSession(string directory, IReadOnlyList<PhotoPair> pairs, SessionState? preloaded = null)
    {
        var restoredSlots = new List<PhotoCardViewModel>();
        var state = preloaded ?? SessionStore.Load(directory);
        if (state is null)
        {
            return restoredSlots;
        }

        SessionStore.Apply(state, pairs);
        RestoreMode(state.Mode);
        RestoreCommitOptions(state.CommitOptions?.ToOptions() ?? new CommitOptions());

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

        // 执行过且有关键成果 → 从界面移除已处置文件(列表/槽位/插件上下文/会话),
        // 防止: 二次提交重复处置已删除文件(必失败)、点击已删除图各模式加载空转(2026-10-31 bug)。
        if (dialog.Result is { } result && result.SuccessCount > 0)
        {
            ApplyCommitResult(result);
        }
    }

    /// <summary>
    /// 提交执行后清理: 移除所有至少成功处置了一个文件的 Pair 对应卡片。
    /// 槽位同步 ApplySlots 管线(插件上下文一并刷新), 会话重新保存(已处置项出标签表)。
    /// </summary>
    private void ApplyCommitResult(Wuling.Core.Commit.CommitResult result)
    {
        // 提交计划以文件为单位(RAW+JPEG 对同处置), 按 Pair 归并成功项
        var processedPairs = result.Results
            .Where(r => r.Success)
            .Select(r => r.Item.Pair)
            .Distinct()
            .ToHashSet();

        // 1) 浏览条: 移除已处置卡片(退订 PropertyChanged, 防泄漏)
        var removedCards = Cards.Where(c => processedPairs.Contains(c.Pair)).ToList();
        foreach (var card in removedCards)
        {
            card.PropertyChanged -= CardOnPropertyChanged;
            Cards.Remove(card);
        }

        // 2) 槽位: 剩余槽位卡片重走 ApplySlots(上下文/插件/持久化同一路径)
        var remainingSlots = Slots.Where(s => !processedPairs.Contains(s.Pair)).ToList();
        ApplySlots(remainingSlots, saveSession: true);
        SlotsReplaced?.Invoke(this, EventArgs.Empty); // 浏览条选中态同步剔除已处置卡片

        Greeting = $"已处置 {removedCards.Count} 张, 剩余 {Cards.Count} 张";
        OnPropertyChanged(nameof(StatusText));
    }
}

/// <summary>模式条按钮的包装 VM:IsChecked 由 CurrentMode 变化统一驱动(而非 ConverterParameter)。</summary>
public sealed partial class ModeButtonViewModel : ObservableObject
{
    public ModeButtonViewModel(CompareModeDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public CompareModeDescriptor Descriptor { get; }

    public string Title => Descriptor.Title;

    public string Id => Descriptor.Id;

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>
    /// 设置选中态并确保 UI 回推:值未变也要强制通知 —— 同模式重复点击时 ToggleButton
    /// 本地视觉态已被翻转为 false,但 VM 值仍是 true,ObservableObject 对同值赋值
    /// 不发 PropertyChanged → OneWay 绑定不回推 → 高亮丢失(bug 实锤 2026-10-31)。
    /// </summary>
    public void SetChecked(bool value)
    {
        if (IsChecked != value)
        {
            IsChecked = value;
        }
        else
        {
            OnPropertyChanged(nameof(IsChecked)); // 同值强制回推, 纠正控件本地翻转
        }
    }
}
