using Microsoft.Win32;

namespace PocketDesk;

/// <summary>开机自启（HKCU Run，无需管理员权限）。</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PocketDesk";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取开机自启失败：{ex.GetType().Name}");
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                LogService.Warn("无法打开 HKCU Run 注册表项。");
                return false;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Warn("当前进程不是可执行文件，无法写入开机自启。");
                    return false;
                }

                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("写入开机自启失败", ex);
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        if (enabled == IsEnabled())
        {
            return;
        }

        SetEnabled(enabled);
    }
}
