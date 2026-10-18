using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PocketDesk;

/// <summary>系统 Shell 交互。</summary>
public static class ShellService
{
    public static void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.Error("打开失败", ex);
        }
    }

    public static void OpenUrl(string url) => Open(url);

    public static void RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                });
                return;
            }

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
                return;
            }

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                RevealInExplorer(parent);
            }
            else
            {
                LogService.Warn("无法定位：路径不存在");
            }
        }
        catch (Exception ex)
        {
            LogService.Error("打开所在位置失败", ex);
        }
    }

    public static void ShowProperties(nint hwnd, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !PathUtil.Exists(path))
        {
            return;
        }

        try
        {
            NativeMethods.SHObjectProperties(hwnd, NativeMethods.SHOP_FILEPATH, path, null);
        }
        catch (Exception ex)
        {
            LogService.Error("打开属性失败", ex);
        }
    }

    public static async Task<bool> CopyTextAsync(string text)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("复制到剪贴板失败", ex);
            return false;
        }
    }

    /// <summary>读取剪贴板中的文件/文件夹路径（“粘贴”与“粘贴路径收纳”共用）。</summary>
    public static async Task<List<string>> GetClipboardPathsAsync()
    {
        var result = new List<string>();
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (!string.IsNullOrEmpty(item.Path))
                    {
                        result.Add(item.Path);
                    }
                }
            }
            else if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = line.Trim().Trim('"');
                    if (PathUtil.Exists(candidate))
                    {
                        result.Add(candidate);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取剪贴板失败：{ex.GetType().Name}");
        }

        return result;
    }

    public static async Task<string?> GetClipboardTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                return await content.GetTextAsync();
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取剪贴板文本失败：{ex.GetType().Name}");
        }

        return null;
    }

    public static async Task<StorageFile?> PickSingleFileAsync(nint hwnd)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");
            return await picker.PickSingleFileAsync();
        }
        catch (Exception ex)
        {
            LogService.Warn($"文件选择器失败：{ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>在桌面创建指向目标文件/文件夹的快捷方式，或网址的 .url 文件。</summary>
    public static string? CreateShortcutOnDesktop(string displayName, string? targetPath, string? url)
    {
        try
        {
            var baseName = PathUtil.SanitizeFileName($"{displayName} - 快捷方式", "快捷方式");
            if (!string.IsNullOrEmpty(url))
            {
                FileSystemService.WriteUrlShortcut(AppPaths.DesktopPath, url, baseName, out var createdUrl);
                return createdUrl;
            }

            if (string.IsNullOrEmpty(targetPath) || !PathUtil.Exists(targetPath))
            {
                return null;
            }

            var linkPath = PathUtil.UniquePathIn(AppPaths.DesktopPath, baseName + ".lnk");
            return ShortcutService.CreateShortcut(linkPath, targetPath, Path.GetDirectoryName(targetPath), null, displayName)
                ? linkPath
                : null;
        }
        catch (Exception ex)
        {
            LogService.Error("创建桌面快捷方式失败", ex);
            return null;
        }
    }
}
