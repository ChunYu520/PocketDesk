using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PocketDesk;

/// <summary>路径与文件属性辅助。</summary>
public static class PathUtil
{
    private const int FileAttributeOffline = 0x00001000;
    private const int FileAttributeRecallOnOpen = 0x00040000;
    private const int FileAttributeRecallOnDataAccess = 0x00400000;

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }
        catch
        {
            return path;
        }
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    public static bool IsRecycleBin(string path)
    {
        if (path.IndexOf(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) &&
               path.StartsWith(Path.Combine(root, "$Recycle.Bin"), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCloudPlaceholder(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var raw = (int)attributes;
            var mask = FileAttributeOffline | FileAttributeRecallOnOpen | FileAttributeRecallOnDataAccess;
            return (raw & mask) != 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSymlink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 && !IsCloudPlaceholder(path);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static bool Exists(string path) =>
        !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

    public static bool IsSameVolume(string a, string b)
    {
        var rootA = Path.GetPathRoot(a);
        var rootB = Path.GetPathRoot(b);
        return !string.IsNullOrEmpty(rootA) &&
               string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetDisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            name = path;
        }

        var ext = Path.GetExtension(name);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return name[..^ext.Length];
        }

        return name;
    }

    public static ItemKind DetectKind(string path)
    {
        if (IsDirectory(path))
        {
            return ItemKind.Folder;
        }

        var ext = Path.GetExtension(path);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Shortcut;
        }

        if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return ItemKind.Url;
        }

        return ItemKind.File;
    }

    /// <summary>在目标目录内生成不冲突的名称，例如 "报告 (2).docx"。</summary>
    public static string UniquePathIn(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!Exists(candidate))
        {
            return candidate;
        }

        var isDirectory = Directory.Exists(candidate) || string.IsNullOrEmpty(Path.GetExtension(fileName));
        var stem = isDirectory ? fileName : Path.GetFileNameWithoutExtension(fileName);
        var ext = isDirectory ? string.Empty : Path.GetExtension(fileName);
        for (var i = 2; i < 10000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    public static string SanitizeFileName(string? name, string fallback = "未命名")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrEmpty(result) ? fallback : result;
    }

    public static string SanitizeFileNameForUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var name = uri.Host.Replace('.', '_');
            if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            {
                name += "_" + Path.GetFileName(uri.AbsolutePath);
            }

            return SanitizeFileName(name, "网址");
        }
        catch
        {
            return SanitizeFileName("网址");
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }

    public static long GetSize(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            if (!Directory.Exists(path))
            {
                return 0;
            }

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // ignore single file
                }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    public static long GetSizeOrCount(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            // 目录用 (文件数 << 32) + 总大小，避免仅比较大小而漏掉差异。
            var count = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).LongCount();
            var size = GetSize(path);
            return (count << 32) + size;
        }
        catch
        {
            return -1;
        }
    }

    public static string ShortHash(string text, int length = 12)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..length].ToLowerInvariant();
    }

    public static string RelativeTime(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes} 分钟前";
        }

        if (span.TotalHours < 24)
        {
            return $"{(int)span.TotalHours} 小时前";
        }

        if (span.TotalDays < 30)
        {
            return $"{(int)span.TotalDays} 天前";
        }

        return time.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }

    /// <summary>把路径转为 shell 可识别的短格式（超长路径交给系统长路径支持）。</summary>
    public static string ToShellPath(string path) => Normalize(path);
}
