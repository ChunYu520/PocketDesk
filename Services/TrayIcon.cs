using System.Runtime.InteropServices;

namespace PocketDesk;

/// <summary>系统托盘图标（Shell_NotifyIcon），带原生右键菜单。</summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint IconId = 1;

    private readonly NativeMethods.WndProc _wndProc;
    private readonly string _className = "PocketDesk.Tray." + Guid.NewGuid().ToString("N");
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _disposed;
    private bool _iconAdded;

    public event Action? ShowAllRequested;
    public event Action? HideAllRequested;
    public event Action? NewBoxRequested;
    public event Action? ManagerRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIcon(string tooltip = "PocketDesk")
    {
        _wndProc = WindowProc;
        CreateHostWindow();
        CreateIcon();
        AddIcon(tooltip);
    }

    private void CreateHostWindow()
    {
        var module = NativeMethods.GetModuleHandle(null);
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = module,
            lpszClassName = _className,
        };
        var atom = NativeMethods.RegisterClassEx(ref wc);
        if (atom == 0)
        {
            LogService.Warn("托盘窗口类注册失败。");
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            _className,
            "PocketDesk Tray",
            0x80000000, // WS_POPUP
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            module,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            LogService.Error("托盘宿主窗口创建失败。");
        }
    }

    private void CreateIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var count = NativeMethods.ExtractIconEx(exe, 0, out var large, out var small, 1);
                if (count > 0 && large != IntPtr.Zero)
                {
                    _hIcon = large;
                    if (small != IntPtr.Zero)
                    {
                        NativeMethods.DestroyIcon(small);
                    }
                }
            }

            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = NativeMethods.LoadIcon(IntPtr.Zero, 32512); // IDI_APPLICATION
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"托盘图标加载失败：{ex.GetType().Name}");
        }
    }

    private void AddIcon(string tooltip)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = BuildData(tooltip);
        _iconAdded = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        if (!_iconAdded)
        {
            LogService.Warn("系统托盘图标添加失败（Shell_NotifyIcon）。");
        }

        data.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref data);
    }

    private NativeMethods.NOTIFYICONDATA BuildData(string tooltip)
    {
        var text = tooltip.Length > 120 ? tooltip[..120] : tooltip;
        return new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _hIcon,
            szTip = text,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    public void SetTooltip(string tooltip)
    {
        if (_hwnd == IntPtr.Zero || !_iconAdded)
        {
            return;
        }

        var data = BuildData(tooltip);
        data.uFlags = NativeMethods.NIF_TIP;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            var notification = (int)(lParam.ToInt64() & 0xFFFF);
            switch (notification)
            {
                case NativeMethods.WM_CONTEXTMENU:
                case NativeMethods.WM_RBUTTONUP:
                    ShowMenu();
                    return IntPtr.Zero;
                case NativeMethods.WM_LBUTTONDBLCLK:
                    SafeInvoke(ManagerRequested);
                    return IntPtr.Zero;
                case NativeMethods.WM_LBUTTONUP:
                case NativeMethods.NIN_SELECT:
                case NativeMethods.NIN_KEYSELECT:
                    SafeInvoke(ShowAllRequested);
                    return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 1, "显示全部收纳盒");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 2, "隐藏全部收纳盒");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 3, "新建收纳盒…");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 4, "收纳盒管理器");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 5, "设置");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 6, "退出 PocketDesk");

            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_hwnd);
            var command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON |
                NativeMethods.TPM_BOTTOMALIGN | NativeMethods.TPM_LEFTALIGN,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case 1:
                    SafeInvoke(ShowAllRequested);
                    break;
                case 2:
                    SafeInvoke(HideAllRequested);
                    break;
                case 3:
                    SafeInvoke(NewBoxRequested);
                    break;
                case 4:
                    SafeInvoke(ManagerRequested);
                    break;
                case 5:
                    SafeInvoke(SettingsRequested);
                    break;
                case 6:
                    SafeInvoke(ExitRequested);
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private static void SafeInvoke(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            LogService.Error("托盘菜单动作失败", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_iconAdded && _hwnd != IntPtr.Zero)
            {
                var data = BuildData("PocketDesk");
                NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            }

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (_hIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"托盘清理失败：{ex.GetType().Name}");
        }
    }
}
