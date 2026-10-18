using System.Diagnostics;

namespace PocketDesk;

/// <summary>应用服务组装与退出清理。</summary>
public static class AppServices
{
    public static TrayIcon? Tray { get; private set; }

    public static bool IsShuttingDown { get; private set; }

    public static RecoverySummary Recovery { get; private set; } = new(0, 0, 0);

    public sealed record RecoverySummary(int Recovered, int RolledBack, int Failed);

    public static bool Initialize()
    {
        LogService.Initialize();
        try
        {
            Database.Initialize();
            AppSettingsService.Load();
        }
        catch (Exception ex)
        {
            LogService.Error("初始化数据库失败", ex);
            NativeMethods.MessageBox(
                IntPtr.Zero,
                "PocketDesk 无法打开本地数据库。\n\n" +
                $"原因：{ex.Message}\n\n" +
                "为避免数据损坏，应用不会继续启动。你可以在设置中的数据目录找到备份，或导出诊断日志后反馈。",
                "PocketDesk 启动失败",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR | NativeMethods.MB_SETFOREGROUND);
            return false;
        }

        var (recovered, rolledBack, failed) = FileSystemService.RecoverPending();
        Recovery = new RecoverySummary(recovered, rolledBack, failed);
        if (recovered + rolledBack + failed > 0)
        {
            LogService.Info($"启动恢复：完成 {recovered}，回滚 {rolledBack}，失败 {failed}");
        }

        AutostartService.Apply(AppSettingsService.Current.LaunchAtStartup);
        AccessibilityService.Initialize();

        try
        {
            Tray = new TrayIcon("PocketDesk");
            Tray.ShowAllRequested += () => WindowManager.ShowAll();
            Tray.HideAllRequested += () => WindowManager.HideAll();
            Tray.NewBoxRequested += () => WindowManager.CreateQuickBox();
            Tray.ManagerRequested += () => WindowManager.OpenManager();
            Tray.SettingsRequested += () => WindowManager.OpenSettings();
            Tray.ExitRequested += Shutdown;
        }
        catch (Exception ex)
        {
            LogService.Error("托盘初始化失败", ex);
        }

        return true;
    }

    public static void Shutdown()
    {
        if (IsShuttingDown)
        {
            return;
        }

        IsShuttingDown = true;
        LogService.Info("PocketDesk 正在退出");
        try
        {
            WindowManager.PersistAllPlacements();
        }
        catch (Exception ex)
        {
            LogService.Warn($"退出前保存窗口位置失败：{ex.Message}");
        }

        Tray?.Dispose();
        Tray = null;
        WindowManager.Shutdown();
        SingleInstanceService.Shutdown();
        try
        {
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            LogService.Warn($"退出 UI 线程失败：{ex.Message}");
        }

        // 托盘应用必须确保进程结束（WinUI 在仍有窗口引用时 Exit 可能不终止进程）。
        Environment.Exit(0);
    }

    public static void Restart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            LogService.Error("重启失败", ex);
        }

        Shutdown();
    }
}
