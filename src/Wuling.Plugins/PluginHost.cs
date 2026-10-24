using Wuling.Core.Models;
using Wuling.Plugins.Abstractions;
using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins;

/// <summary>
/// 插件宿主(04 文档 §5): 注册/发现插件, 管理激活状态与上下文, 分发槽位变化。
/// M3 内置注册 + Python 插件注册;文件系统发现(dotnet 插件目录扫描)M3 后期接入。
/// </summary>
public sealed class PluginHost : IDisposable
{
    private readonly List<ICompareModePlugin> _plugins = [];
    private readonly Dictionary<string, CompareContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IPluginServices> _servicesFactory;
    private readonly object _gate = new();

    public PluginHost(Func<IPluginServices> servicesFactory)
    {
        _servicesFactory = servicesFactory;
    }

    /// <summary>全部已注册插件(含内置与 Python)。</summary>
    public IReadOnlyList<ICompareModePlugin> Plugins
    {
        get
        {
            lock (_gate)
            {
                return _plugins.ToArray();
            }
        }
    }

    /// <summary>注册一个插件(初始化失败 → 丢弃并记日志, 不阻塞其他插件)。</summary>
    public async Task<bool> RegisterAsync(ICompareModePlugin plugin, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_plugins.Any(p => p.Descriptor.Id == plugin.Descriptor.Id))
            {
                return false;
            }
        }

        try
        {
            await plugin.InitializeAsync(_servicesFactory(), cancellationToken);
        }
        catch (Exception)
        {
            return false;
        }

        lock (_gate)
        {
            _plugins.Add(plugin);
        }

        return true;
    }

    /// <summary>按 id 取插件。</summary>
    public ICompareModePlugin? GetPlugin(string pluginId)
    {
        lock (_gate)
        {
            return _plugins.FirstOrDefault(p => p.Descriptor.Id == pluginId);
        }
    }

    /// <summary>为指定模式创建视图上下文, 并以当前槽位初始化。</summary>
    public ICompareContext CreateContext(string pluginId, IReadOnlyList<PhotoPair> slots)
    {
        var cards = slots.Select(ToCard).ToList();
        var context = new CompareContext(cards);
        lock (_gate)
        {
            _contexts[pluginId] = context;
        }

        return context;
    }

    /// <summary>槽位变化 → 通知所有活跃上下文(04 §2: 宿主负责变更通知)。</summary>
    public void UpdateSlots(IReadOnlyList<PhotoPair> slots)
    {
        lock (_gate)
        {
            foreach (var context in _contexts.Values)
            {
                context.UpdateSlots(slots.Select(ToCard).ToList());
            }
        }
    }

    /// <summary>声明式 chrome: 槽位卡的契约层投影。</summary>
    public static PhotoCard ToCard(PhotoPair pair) => new()
    {
        DisplayName = pair.DisplayName,
        PreviewPath = pair.PreviewPath,
        RawPath = pair.RawPath,
        JpegPath = pair.JpegPath,
        HasRawJpegPair = pair.Kind == PhotoKind.RawWithJpeg,
        Tag = (PluginPhotoTag)(int)pair.Tag,
    };

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var plugin in _plugins)
            {
                try
                {
                    plugin.Dispose();
                }
                catch
                {
                    // 单个插件释放失败不影响其他
                }
            }

            _plugins.Clear();
            _contexts.Clear();
        }
    }
}
