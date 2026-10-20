using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wuling.Core.Models;
using Wuling.Imaging;

namespace Wuling.App.ViewModels;

/// <summary>EXIF 对比模式的一个字段行(差异高亮, 03 F6)。</summary>
public sealed class ExifFieldRow
{
    /// <param name="values">全部槽位的值(供差异计算)。</param>
    /// <param name="ownIndex">本卡对应槽位的索引。</param>
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

    /// <summary>本卡(槽位)显示的值。</summary>
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
            if (!string.Equals(first, values[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>EXIF 对比模式: 一个槽位(照片)的参数卡, 含多行字段。</summary>
public sealed class ExifSlotCard
{
    public ExifSlotCard(string fileName, IReadOnlyList<ExifFieldRow> rows)
    {
        FileName = fileName;
        Rows = rows;
    }

    public string FileName { get; }

    public IReadOnlyList<ExifFieldRow> Rows { get; }
}

/// <summary>
/// EXIF 元数据对比模式(M2 / 03 F6): 每个槽位一张参数卡,
/// 卡内多行字段;值不一致的字段着色高亮,便于判断连拍中哪张参数最优。
/// </summary>
public class ExifCompareViewModel : ObservableObject
{
    private static readonly string[] s_fieldLabels =
    [
        "机身", "镜头", "焦距", "光圈", "快门", "ISO", "曝光补偿", "拍摄时间",
    ];

    public ExifCompareViewModel(IReadOnlyList<PhotoCardViewModel> slots)
    {
        Cards = BuildCards(slots);
    }

    /// <summary>每槽一张卡(与对比区槽位一一对应)。</summary>
    public IReadOnlyList<ExifSlotCard> Cards { get; }

    private static IReadOnlyList<ExifSlotCard> BuildCards(IReadOnlyList<PhotoCardViewModel> slots)
    {
        // 先收集所有槽位的值, 供差异计算
        List<string?[]> values =
        [
            new string?[slots.Count], // 机身
            new string?[slots.Count], // 镜头
            new string?[slots.Count], // 焦距
            new string?[slots.Count], // 光圈
            new string?[slots.Count], // 快门
            new string?[slots.Count], // ISO
            new string?[slots.Count], // 曝光补偿
            new string?[slots.Count], // 拍摄时间
        ];

        for (var i = 0; i < slots.Count; i++)
        {
            var e = slots[i].Exif;
            values[0][i] = JoinMake(e);
            values[1][i] = e?.LensModel;
            values[2][i] = e?.FocalLength;
            values[3][i] = e?.Aperture;
            values[4][i] = e?.Shutter;
            values[5][i] = e?.Iso;
            values[6][i] = e?.ExposureBias;
            values[7][i] = e?.TakenAt?.LocalDateTime.ToString("HH:mm:ss.fff");
        }

        var cards = new List<ExifSlotCard>(slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            var rows = new List<ExifFieldRow>(s_fieldLabels.Length);
            for (var f = 0; f < s_fieldLabels.Length; f++)
            {
                rows.Add(new ExifFieldRow(s_fieldLabels[f], values[f], i));
            }

            cards.Add(new ExifSlotCard(slots[i].DisplayName, rows));
        }

        return cards;
    }

    private static string? JoinMake(ExifSummary? e) => e?.CameraModel is not null
        ? (e.CameraMake is not null && !e.CameraModel.StartsWith(e.CameraMake, StringComparison.OrdinalIgnoreCase) ? $"{e.CameraMake} {e.CameraModel}" : e.CameraModel)
        : null;

    private static bool Differs(string?[] values)
    {
        if (values.Length < 2)
        {
            return false;
        }

        var first = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            if (!string.Equals(first, values[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
