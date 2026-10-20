using Wuling.Core.Models;

namespace Wuling.Core.Tagging;

/// <summary>
/// 标记状态机(设计文档 03 F4): 集中校验三态迁移合法性。
/// </summary>
public static class TagStateMachine
{
    /// <summary>全部合法迁移。</summary>
    public static bool CanTransition(PhotoTag from, PhotoTag to) => from != to;

    /// <summary>应用标记,非法迁移抛出异常。</summary>
    public static PhotoTag Apply(PhotoPair pair, PhotoTag target)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (!CanTransition(pair.Tag, target))
        {
            throw new InvalidOperationException($"Invalid tag transition: {pair.Tag} -> {target}.");
        }

        pair.Tag = target;
        return target;
    }
}
