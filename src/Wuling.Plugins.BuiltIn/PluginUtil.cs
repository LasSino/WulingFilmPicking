using Wuling.Plugins.Abstractions;
using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins.BuiltIn;

/// <summary>插件实现共享的小工具。</summary>
internal static class PluginUtil
{
    /// <summary>把每个槽位的字段值序列化, 供跨槽差异计算(Python 插件也用同语义)。</summary>
    public static bool Differs(IReadOnlyList<string?> values)
    {
        if (values.Count < 2)
        {
            return false;
        }

        var first = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            if (!string.Equals(first, values[i], System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string? JoinMake(ExifInfo? e) => e?.CameraModel is not null
        ? (e.CameraMake is not null && !e.CameraModel.StartsWith(e.CameraMake, System.StringComparison.OrdinalIgnoreCase)
            ? $"{e.CameraMake} {e.CameraModel}"
            : e.CameraModel)
        : null;
}
