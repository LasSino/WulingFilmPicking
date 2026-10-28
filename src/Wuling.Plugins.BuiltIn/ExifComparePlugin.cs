using Wuling.Plugins.Abstractions;
using Wuling.Plugins.Abstractions.Data;

namespace Wuling.Plugins.BuiltIn;

/// <summary>
/// 内置 EXIF 元数据对比模式(M3 起走插件契约)。
/// 每槽一张参数卡;跨槽差异的字段由宿主高亮(Values 各槽值齐全,由视图层判定)。
/// </summary>
public sealed class ExifComparePlugin : ICompareModePlugin
{
    public CompareModeDescriptor Descriptor { get; } = new()
    {
        Id = "builtin.exif",
        Title = "EXIF",
        Kind = PluginKind.CSharp,
        IsBuiltIn = true,
    };

    private IPluginServices? _services;

    public Task InitializeAsync(IPluginServices services, CancellationToken cancellationToken = default)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public object CreateView(ICompareContext context) => new ExifCompareViewModel(context, _services!);

    public void Dispose()
    {
        // 无非托管资源
    }
}

/// <summary>EXIF 对比模式的视图模型(App 的 DataTemplate 据此渲染)。</summary>
public sealed class ExifCompareViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private static readonly string[] FieldLabels =
    [
        "机身", "镜头", "焦距", "光圈", "快门", "ISO", "曝光补偿", "对焦模式", "拍摄时间",
    ];

    public ExifCompareViewModel(ICompareContext context, IPluginServices services)
    {
        Context = context;
        Services = services;
        Context.SlotsChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public ICompareContext Context { get; }

    public IPluginServices Services { get; }

    /// <summary>每槽一张卡。</summary>
    public IReadOnlyList<ExifSlotCard> Cards { get; private set; } = [];

    private void Rebuild()
    {
        var slots = Context.Slots;
        var exifs = slots.Select(s => Services.GetExif(s.PreviewPath)).ToList();

        // 每个字段收集所有槽位的值, 供差异计算
        string?[][] values =
        [
            new string?[slots.Count], // 机身
            new string?[slots.Count], // 镜头
            new string?[slots.Count], // 焦距
            new string?[slots.Count], // 光圈
            new string?[slots.Count], // 快门
            new string?[slots.Count], // ISO
            new string?[slots.Count], // 曝光补偿
            new string?[slots.Count], // 对焦模式
            new string?[slots.Count], // 拍摄时间
        ];

        for (var i = 0; i < slots.Count; i++)
        {
            values[0][i] = PluginUtil.JoinMake(exifs[i]);
            values[1][i] = exifs[i].LensModel;
            values[2][i] = exifs[i].FocalLength;
            values[3][i] = exifs[i].Aperture;
            values[4][i] = exifs[i].Shutter;
            values[5][i] = exifs[i].Iso;
            values[6][i] = exifs[i].ExposureBias;
            values[7][i] = exifs[i].FocusMode;
            values[8][i] = exifs[i].TakenAt?.LocalDateTime.ToString("HH:mm:ss.fff");
        }

        var cards = new List<ExifSlotCard>(slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            var rows = new List<ExifFieldRow>(FieldLabels.Length);
            for (var f = 0; f < FieldLabels.Length; f++)
            {
                rows.Add(new ExifFieldRow(FieldLabels[f], values[f], i));
            }

            cards.Add(new ExifSlotCard(slots[i].DisplayName, rows));
        }

        Cards = cards;
        PropertyChanged?.Invoke(this, new(nameof(Cards)));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>一个槽位的参数卡。</summary>
public sealed class ExifSlotCard(string fileName, IReadOnlyList<ExifFieldRow> rows)
{
    public string FileName { get; } = fileName;

    public IReadOnlyList<ExifFieldRow> Rows { get; } = rows;
}

/// <summary>一个字段行(OwnValue 为本卡值;Differs 由视图层跨卡比较)。</summary>
public sealed class ExifFieldRow
{
    public ExifFieldRow(string label, string?[] values, int ownIndex)
    {
        Label = label;
        Values = values;
        OwnIndex = ownIndex;
        Differs = HasDifference(values);
    }

    public string Label { get; }

    public string?[] Values { get; }

    public int OwnIndex { get; }

    public string OwnValue => Values[OwnIndex] ?? "—";

    public bool Differs { get; }

    private static bool HasDifference(string?[] values)
    {
        if (values.Length < 2)
        {
            return false;
        }

        var first = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            if (!string.Equals(first, values[i], System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
