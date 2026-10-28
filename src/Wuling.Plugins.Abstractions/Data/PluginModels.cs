namespace Wuling.Plugins.Abstractions.Data;

/// <summary>照片标记三态(与 Wuling.Core 的 PhotoTag 对应;契约层不依赖 Core,便于插件独立编译)。</summary>
public enum PluginPhotoTag
{
    Unmarked = 0,
    Selected = 1,
    Rejected = 2,
}

/// <summary>
/// 契约层的一张"照片卡"(槽位单元)。只暴露插件需要的数据;
/// 文件路径仅限宿主声明的可访问清单内(04 文档 §4 安全)。
/// </summary>
public sealed record PhotoCard
{
    public required string DisplayName { get; init; }

    /// <summary>首选预览文件路径(JPEG 优先, 否则 RAW)。</summary>
    public required string PreviewPath { get; init; }

    /// <summary>RAW 主文件路径;纯 JPEG 时为 null。</summary>
    public string? RawPath { get; init; }

    public string? JpegPath { get; init; }

    /// <summary>是否为 RAW+JPEG 同名对。</summary>
    public bool HasRawJpegPair { get; init; }

    public PluginPhotoTag Tag { get; init; }
}

/// <summary>单张照片的 EXIF 关键字段摘要(04 文档 §2 IExifProvider 的返回)。</summary>
public sealed record ExifInfo
{
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public string? LensModel { get; init; }
    public string? FocalLength { get; init; }
    public string? Aperture { get; init; }
    public string? Shutter { get; init; }
    public string? Iso { get; init; }
    public string? ExposureBias { get; init; }

    /// <summary>对焦模式(03 F6;厂商 MakerNote 扫描, 不可得 null)。</summary>
    public string? FocusMode { get; init; }

    public DateTimeOffset? TakenAt { get; init; }

    /// <summary>摘要行(如 "f/4 1/250 ISO400")。</summary>
    public string ShortSummary
    {
        get
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrEmpty(Aperture))
            {
                parts.Add(Aperture);
            }

            if (!string.IsNullOrEmpty(Shutter))
            {
                parts.Add(Shutter);
            }

            if (!string.IsNullOrEmpty(Iso))
            {
                parts.Add($"ISO {Iso}");
            }

            return parts.Count == 0 ? string.Empty : string.Join(' ', parts);
        }
    }
}
