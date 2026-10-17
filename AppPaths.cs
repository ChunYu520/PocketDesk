namespace PocketDesk;

/// <summary>应用数据目录与文件路径。</summary>
public static class AppPaths
{
    private static readonly Lazy<string> _root = new(() =>
    {
        // 测试/便携模式：显式指定数据根目录。
        var overrideRoot = Environment.GetEnvironmentVariable("POCKETDESK_HOME");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot);
        }

        // 显式使用环境变量而不是 KnownFolder，避免 MSIX 虚拟化把数据写进包容器，
        // 保证「卸载不删数据」与用户可见的 %LOCALAPPDATA%\PocketDesk 一致。
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%");
        }

        return Path.Combine(localAppData, "PocketDesk");
    });

    public static string Root => _root.Value;
    public static string DatabasePath => Path.Combine(Root, "pocketdesk.db");
    public static string StorageRoot => Path.Combine(Root, "Storage");
    public static string TrashRoot => Path.Combine(Root, "Trash");
    public static string LogsDir => Path.Combine(Root, "Logs");
    public static string BackupDir => Path.Combine(Root, "backup");
    public static string IconCacheDir => Path.Combine(Root, "IconCache");
    public static string ExportDir => Path.Combine(Root, "Export");

    public static string BoxStorage(string boxId) => Path.Combine(StorageRoot, boxId);

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(IconCacheDir);
    }

    public static string DesktopPath
    {
        get
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop))
            {
                desktop = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Desktop");
            }

            return desktop;
        }
    }

    public static string DownloadsPath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile
        ? Path.Combine(profile, "Downloads")
        : DesktopPath;

    /// <summary>诊断日志导出目录（zip 文件路径）。</summary>
    public static string NewDiagnosticsArchivePath() =>
        Path.Combine(ExportDir, $"PocketDesk-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

    /// <summary>把日志文本中的用户路径替换为占位符，避免导出真实文件路径。</summary>
    public static string SanitizeForLog(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        text = text.Replace(Root, "%LOCALAPPDATA%\\PocketDesk", StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
