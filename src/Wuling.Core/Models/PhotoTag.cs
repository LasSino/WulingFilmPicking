namespace Wuling.Core.Models;

/// <summary>照片标记三态。</summary>
public enum PhotoTag
{
    Unmarked = 0,
    Selected = 1,
    Rejected = 2,
}

/// <summary>RAW/JPEG 组合类型。</summary>
public enum PhotoKind
{
    JpegOnly,
    RawOnly,
    RawWithJpeg,
}
