using Microsoft.UI.Dispatching;

namespace PocketDesk;

/// <summary>应用入口：单实例、服务初始化、布局恢复。</summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogService.Error("未处理异常", e.Exception);
        };
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var exitCode = await SelfTest.RunAsync();
            Environment.Exit(exitCode);
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();

        // 二次启动：把意图交给已有实例后立即退出，避免重复窗口。
        if (!SingleInstanceService.TryAcquire(dispatcher))
        {
            SingleInstanceService.NotifyExistingInstance("show");
            Exit();
            return;
        }

        if (!AppServices.Initialize())
        {
            Exit();
            return;
        }

        WindowManager.Initialize();

        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase)))
        {
            await DemoMode.ShowAsync();
            return;
        }

        if (AppSettingsService.Current.RestoreLayoutOnStartup)
        {
            WindowManager.RestoreLayout();
        }
        else
        {
            WindowManager.ShowAll();
        }

        var recovery = AppServices.Recovery;
        if (recovery.Failed > 0)
        {
            NativeMethods.MessageBox(
                IntPtr.Zero,
                "启动时发现未完成的文件移动，其中有 " + recovery.Failed + " 项无法自动恢复。\n\n" +
                "请检查收纳盒中的文件，或在设置中导出诊断日志后反馈。",
                "PocketDesk",
                NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
        }
    }
}
