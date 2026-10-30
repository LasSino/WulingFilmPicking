using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Wuling.Core.Models;
using Wuling.Imaging;

namespace Wuling.App.ViewModels;

/// <summary>目录预览区中的一张卡片。</summary>
public partial class PhotoCardViewModel : ObservableObject
{
    public PhotoCardViewModel(PhotoPair pair)
    {
        Pair = pair;
        _tag = pair.Tag;
    }

    public PhotoPair Pair { get; }

    /// <summary>EXIF 摘要(M2: 由 MainViewModel 加载后附上)。</summary>
    public ExifSummary? Exif { get; private set; }

    public void AttachExif(ExifSummary summary) => Exif = summary;

    public string ExifSummaryText => Exif?.ShortSummary ?? string.Empty;

    public string DisplayName => Pair.DisplayName;

    public string PreviewPath => Pair.PreviewPath;

    public bool HasRawJpegPair => Pair.Kind == PhotoKind.RawWithJpeg;

    /// <summary>缩略图位图(由解码流水线回填,UI 线程构造)。</summary>
    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isThumbnailReady;

    [ObservableProperty]
    private PhotoTag _tag;

    partial void OnTagChanged(PhotoTag value)
    {
        Pair.Tag = value;
        OnPropertyChanged(nameof(TagGlyph));
        OnPropertyChanged(nameof(IsMarked));
    }

    /// <summary>角标字符(状态可见性依赖配色, 见 XAML 徽点)。</summary>
    public string TagGlyph => Tag switch
    {
        PhotoTag.Selected => "✓",
        PhotoTag.Rejected => "✕",
        _ => "·",
    };

    /// <summary>是否已标记(徽点显隐)。</summary>
    public bool IsMarked => Tag != PhotoTag.Unmarked;

    /// <summary>
    /// 把解码结果包装为 Avalonia 位图(SKBitmap 统一 Bgra8888)。
    /// DecodedThumbnail.Bitmap 是调用方独占快照 —— 像素已拷入 WriteableBitmap, 拷贝完立即释放。
    /// </summary>
    public static Bitmap? ToBitmap(DecodedThumbnail decoded)
    {
        using var bmp = decoded.Bitmap;
        if (bmp is null)
        {
            return null;
        }

        var size = new PixelSize(bmp.Width, bmp.Height);
        var dpi = new Vector(96, 96);
        return new WriteableBitmap(
            PixelFormats.Bgra8888,
            AlphaFormat.Premul,
            bmp.GetPixels(),
            size,
            dpi,
            bmp.RowBytes);
    }

    /// <summary>加载缩略图(后台线程解码,由调用方保证在 UI 线程回填)。</summary>
    public async Task LoadThumbnailAsync(ThumbnailService service, int edge)
    {
        if (Thumbnail is not null)
        {
            return;
        }

        var decoded = await Task.Run(() => service.GetView(Pair, edge));
        if (decoded.Status == DecodeStatus.Ok)
        {
            var bitmap = ToBitmap(decoded); // 内部释放快照
            if (bitmap is not null)
            {
                Thumbnail = bitmap;
                IsThumbnailReady = true;
            }
        }
    }

    /// <summary>加载对比区全尺寸视图(按槽位边长);首次点击多选后调用。</summary>
    public async Task LoadCompareViewAsync(ThumbnailService service, int edge)
    {
        if (CompareView is not null)
        {
            return;
        }

        var decoded = await Task.Run(() => service.GetView(Pair, edge));
        if (decoded.Status == DecodeStatus.Ok)
        {
            var bitmap = ToBitmap(decoded); // 内部释放快照
            if (bitmap is not null)
            {
                CompareView = bitmap;
            }
        }
    }

    /// <summary>对比区用全清晰度位图(与浏览条缩略图分开)。</summary>
    [ObservableProperty]
    private Bitmap? _compareView;
}
