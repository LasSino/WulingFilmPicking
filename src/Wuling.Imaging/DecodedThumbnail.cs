namespace Wuling.Imaging;

/// <summary>解码结果。</summary>
public enum DecodeStatus
{
    Ok,
    /// <summary>M2 前暂不支持 RAW 内嵌预览提取。</summary>
    UnsupportedFormat,
    IoError,
    Corrupt,
}

/// <summary>已解码的缩略图(SKBitmap 统一 Bgra8888,渲染端直接拷贝到 WriteableBitmap)。</summary>
public sealed record DecodedThumbnail(DecodeStatus Status, SkiaSharp.SKBitmap? Bitmap, int SourcePixelWidth, int SourcePixelHeight)
{
    public static DecodedThumbnail Fail(DecodeStatus status) => new(status, null, 0, 0);
}
