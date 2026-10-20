using Wuling.Core.Models;

namespace Wuling.Core.Models;

/// <summary>
/// 一张"卡片": 同名 RAW/JPEG 的配对单元。RAW 为主文件,JPEG 为附属预览源。
/// </summary>
public sealed record PhotoPair
{
    public required string DisplayName { get; init; }

    /// <summary>RAW 主文件路径;纯 JPEG 时为 null。</summary>
    public string? RawPath { get; init; }

    /// <summary>附属 JPEG 路径;纯 RAW 时为 null。</summary>
    public string? JpegPath { get; init; }

    public PhotoKind Kind => (RawPath, JpegPath) switch
    {
        (null, null) => throw new InvalidOperationException("PhotoPair must have at least one file."),
        (_, null) => PhotoKind.RawOnly,
        (null, _) => PhotoKind.JpegOnly,
        (_, _) => PhotoKind.RawWithJpeg,
    };

    /// <summary>用于对比展示的首选文件(JPEG 存在时优先,解码快)。</summary>
    public string PreviewPath => JpegPath ?? RawPath!;

    /// <summary>标记状态。会话恢复时回填。</summary>
    public PhotoTag Tag { get; set; }
}
