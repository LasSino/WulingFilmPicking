using Wuling.Core.Models;

namespace Wuling.Core.Scanning;

/// <summary>
/// 目录扫描与 RAW/JPEG 同名配对(设计文档 03 F1)。
/// 规则: 同目录 + 主文件名匹配(大小写不敏感);RAW 为主卡,JPEG 为附属预览源。
/// </summary>
public sealed class DirectoryScanner
{
    public static readonly IReadOnlyCollection<string> RawExtensions = new[]
    {
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf",
        ".rw2", ".dng", ".pef", ".srw", ".x3f",
    };

    public static readonly IReadOnlyCollection<string> JpegExtensions = new[] { ".jpg", ".jpeg", ".jpe" };

    /// <summary>附属文件等被忽略的扩展名。</summary>
    private static readonly string[] s_ignoredExtensions =
    {
        ".xmp", ".aae", ".thm", ".wav", ".txt", ".ini", ".db",
    };

    /// <summary>枚举目录并配对,返回按文件名自然排序的卡片列表。</summary>
    public IReadOnlyList<PhotoPair> Scan(string directory, bool recursive = false)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var raws = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var jpegs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(directory, "*", enumerationOptions))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.'))
            {
                continue;
            }

            var relative = Path.GetRelativePath(directory, path);
            // 递归时排除任何 .wuling 工作目录
            if (relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                    .Any(seg => string.Equals(seg, ".wuling", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var ext = Path.GetExtension(path);
            if (RawExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                AddLongest(raws, KeyOf(path), path);
            }
            else if (JpegExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                AddLongest(jpegs, KeyOf(path), path);
            }
            // 其余扩展名(含被忽略的附属文件)一律跳过
        }

        var pairs = new List<PhotoPair>(raws.Count + jpegs.Count);

        foreach (var (key, rawPath) in raws)
        {
            jpegs.Remove(key, out var jpegPath);
            pairs.Add(new PhotoPair
            {
                DisplayName = Path.GetFileNameWithoutExtension(rawPath),
                RawPath = rawPath,
                JpegPath = jpegPath,
            });
        }

        foreach (var (key, jpegPath) in jpegs)
        {
            pairs.Add(new PhotoPair
            {
                DisplayName = Path.GetFileNameWithoutExtension(jpegPath),
                JpegPath = jpegPath,
            });
        }

        return pairs
            .OrderBy(p => p.DisplayName, NaturalSortComparer.Instance)
            .ThenBy(p => p.PreviewPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>同名键: 目录 + 主文件名(大小写不敏感)。递归模式下目录参与键。</summary>
    private static string KeyOf(string path) => Path.Combine(
        Path.GetDirectoryName(path) ?? string.Empty,
        Path.GetFileNameWithoutExtension(path)).ToUpperInvariant();

    /// <summary>同名冲突时保留较大的文件(03 F1: 优先较大者)。</summary>
    private static void AddLongest(Dictionary<string, string> map, string key, string path)
    {
        if (!map.TryGetValue(key, out var existing)
            || new FileInfo(path).Length > new FileInfo(existing).Length)
        {
            map[key] = path;
        }
    }
}

/// <summary>文件名自然排序(数字感知): IMG_2 排在 IMG_10 前。</summary>
public sealed class NaturalSortComparer : IComparer<string>
{
    public static readonly NaturalSortComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var xi = 0;
        var yi = 0;
        while (xi < x.Length && yi < y.Length)
        {
            if (char.IsDigit(x[xi]) && char.IsDigit(y[yi]))
            {
                var xStart = xi;
                var yStart = yi;
                while (xi < x.Length && char.IsDigit(x[xi]))
                {
                    xi++;
                }

                while (yi < y.Length && char.IsDigit(y[yi]))
                {
                    yi++;
                }

                var xNum = x.AsSpan(xStart, xi - xStart).TrimStart('0');
                var yNum = y.AsSpan(yStart, yi - yStart).TrimStart('0');
                var cmp = xNum.Length.CompareTo(yNum.Length);
                if (cmp != 0)
                {
                    return cmp;
                }

                cmp = xNum.CompareTo(yNum, StringComparison.Ordinal);
                if (cmp != 0)
                {
                    return cmp;
                }
            }
            else
            {
                var cmp = char.ToUpperInvariant(x[xi]).CompareTo(char.ToUpperInvariant(y[yi]));
                if (cmp != 0)
                {
                    return cmp;
                }

                xi++;
                yi++;
            }
        }

        return (x.Length - xi).CompareTo(y.Length - yi);
    }
}
