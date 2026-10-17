using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.UI.Dispatching;

namespace PocketDesk;

/// <summary>单实例：命名 Mutex 保证唯一，命名管道把二次启动的意图转交给已有实例。</summary>
public static class SingleInstanceService
{
    private const string MutexName = @"Local\PocketDesk.SingleInstance";
    private static readonly string PipeName = $"PocketDesk.Activate.{Environment.UserName}";

    private static Mutex? _mutex;
    private static DispatcherQueue? _dispatcher;
    private static volatile bool _running;
    private static Thread? _serverThread;

    public static bool TryAcquire(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            return false;
        }

        _running = true;
        _serverThread = new Thread(ServerLoop)
        {
            IsBackground = true,
            Name = "PocketDesk.SingleInstance",
        };
        _serverThread.Start();
        return true;
    }

    /// <summary>向已有实例发送激活命令。返回是否成功送达。</summary>
    public static bool NotifyExistingInstance(string command = "show")
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None,
                TokenImpersonationLevel.Identification);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn($"连接已有实例失败：{ex.GetType().Name}");
            return false;
        }
    }

    public static void Shutdown()
    {
        _running = false;
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 未持有 mutex。
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    private static void ServerLoop()
    {
        while (_running)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);
                server.WaitForConnection();
                using var reader = new StreamReader(server);
                var command = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(command))
                {
                    command = "show";
                }

                var dispatcher = _dispatcher;
                if (dispatcher is null)
                {
                    continue;
                }

                dispatcher.TryEnqueue(() => HandleCommand(command.Trim()));
            }
            catch (Exception ex)
            {
                if (!_running)
                {
                    break;
                }

                LogService.Warn($"单实例管道异常：{ex.GetType().Name}");
                Thread.Sleep(200);
            }
        }
    }

    private static void HandleCommand(string command)
    {
        LogService.Info($"收到二次启动命令：{command}");
        switch (command.ToLowerInvariant())
        {
            case "settings":
                WindowManager.OpenSettings();
                break;
            case "manager":
                WindowManager.OpenManager();
                break;
            case "hide":
                WindowManager.HideAll();
                break;
            case "exit":
                AppServices.Shutdown();
                break;
            default:
                WindowManager.ShowAll();
                break;
        }
    }
}
