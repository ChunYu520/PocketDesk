namespace PocketDesk;

/// <summary>所有应用窗口的生命周期与布局。</summary>
public static class WindowManager
{
    private static readonly Dictionary<string, BoxWindow> BoxWindows = new(StringComparer.OrdinalIgnoreCase);
    private static BoxManagerWindow? _manager;
    private static SettingsWindow? _settings;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        AppSettingsService.Changed += ApplySettingsToWindows;
        BoxService.BoxesChanged += OnBoxesChanged;
        ImportService.BoxChanged += OnBoxContentChanged;
    }

    public static void RestoreLayout()
    {
        var boxes = BoxService.GetBoxes();
        var restored = 0;
        foreach (var box in boxes.Where(b => b.IsVisible))
        {
            ShowBox(box, activate: false);
            restored++;
        }

        if (restored > 0)
        {
            var first = BoxWindows.Values.FirstOrDefault();
            first?.Activate();
        }
        else
        {
            OpenManager();
        }
    }

    public static void ShowBox(Box box, bool activate = true)
    {
        if (BoxWindows.TryGetValue(box.Id, out var existing))
        {
            existing.AppWindow.Show(activate);
            if (activate)
            {
                existing.Activate();
            }

            return;
        }

        try
        {
            var window = new BoxWindow(box);
            BoxWindows[box.Id] = window;
            window.AppWindow.Show(activate);
            if (activate)
            {
                window.Activate();
            }

            window.SendToBack();
        }
        catch (Exception ex)
        {
            LogService.Error($"创建收纳盒窗口失败：{box.Name}", ex);
            NativeMethods.MessageBox(
                IntPtr.Zero,
                $"无法显示收纳盒“{box.Name}”。\n\n{ex.Message}",
                "PocketDesk",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
        }
    }

    public static void ShowBox(string boxId, bool activate = true)
    {
        var box = BoxService.GetBox(boxId);
        if (box is not null)
        {
            ShowBox(box, activate);
        }
    }

    public static void ShowAll()
    {
        var boxes = BoxService.GetBoxes();
        foreach (var box in boxes)
        {
            if (!box.IsVisible)
            {
                box.IsVisible = true;
                BoxRepository.SetVisibility(box.Id, true);
            }

            ShowBox(box, activate: false);
        }

        BoxWindows.Values.FirstOrDefault()?.Activate();
    }

    public static void HideAll()
    {
        foreach (var box in BoxService.GetBoxes())
        {
            if (box.IsVisible)
            {
                BoxRepository.SetVisibility(box.Id, false);
            }
        }

        foreach (var window in BoxWindows.Values.ToList())
        {
            window.HideToTray();
        }

        BoxService.NotifyBoxesChanged();
    }

    public static void OpenManager(BoxType? createType = null, bool createImmediately = false)
    {
        if (_manager is null)
        {
            try
            {
                _manager = new BoxManagerWindow();
                _manager.Closed += (_, _) => _manager = null;
            }
            catch (Exception ex)
            {
                LogService.Error("打开收纳盒管理器失败", ex);
                return;
            }
        }

        _manager.AppWindow.Show(true);
        _manager.Activate();
        if (createImmediately && createType is { } type)
        {
            _manager.BeginCreate(type);
        }
    }

    public static void OpenSettings()
    {
        if (_settings is null)
        {
            try
            {
                _settings = new SettingsWindow();
                _settings.Closed += (_, _) => _settings = null;
            }
            catch (Exception ex)
            {
                LogService.Error("打开设置失败", ex);
                return;
            }
        }

        _settings.AppWindow.Show(true);
        _settings.Activate();
    }

    public static void CloseBoxWindow(string boxId)
    {
        if (BoxWindows.TryGetValue(boxId, out var window))
        {
            window.CloseForcibly();
        }
    }

    public static void OnBoxWindowClosed(string boxId)
    {
        BoxWindows.Remove(boxId);
        if (AppServices.IsShuttingDown)
        {
            return;
        }

        if (BoxWindows.Count == 0 &&
            !AppSettingsService.Current.CloseToTray &&
            _manager is null &&
            _settings is null)
        {
            AppServices.Shutdown();
        }
    }

    public static void PersistAllPlacements()
    {
        foreach (var window in BoxWindows.Values)
        {
            window.PersistPlacement();
        }
    }

    /// <summary>一键贴边排列：在主屏工作区内按网格平铺所有可见收纳盒。</summary>
    public static void TileVisibleBoxes()
    {
        var boxes = BoxService.GetBoxes().Where(b => b.IsVisible).ToList();
        if (boxes.Count == 0)
        {
            return;
        }

        var display = Microsoft.UI.Windowing.DisplayArea.Primary;
        var work = display.WorkArea;
        var gap = DpiUtil.Scale(12);
        var width = Math.Min(DpiUtil.Scale(400), Math.Max(DpiUtil.Scale(280), (work.Width - gap * 4) / 3));
        var height = Math.Max(DpiUtil.Scale(240), (work.Height - gap * 3) / 2);
        var columns = Math.Max(1, (work.Width - gap) / (width + gap));

        for (var i = 0; i < boxes.Count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            var x = work.X + gap + column * (width + gap);
            var y = work.Y + gap + row * (height + gap);
            if (y + height > work.Y + work.Height)
            {
                y = work.Y + gap;
            }

            var window = GetOrCreate(boxes[i]);
            window.SetPlacement(new WindowPlacement
            {
                Left = x,
                Top = y,
                Width = width,
                Height = height,
                MonitorDevice = display.DisplayId.Value.ToString(),
                State = "Normal",
            });
        }
    }

    public static BoxWindow? GetWindow(string boxId) =>
        BoxWindows.TryGetValue(boxId, out var window) ? window : null;

    private static BoxWindow GetOrCreate(Box box)
    {
        if (BoxWindows.TryGetValue(box.Id, out var existing))
        {
            return existing;
        }

        ShowBox(box, activate: false);
        return BoxWindows[box.Id];
    }

    private static void OnBoxesChanged()
    {
        if (_manager is null)
        {
            return;
        }

        _manager.RefreshBoxes();
    }

    private static void OnBoxContentChanged(string boxId)
    {
        if (BoxWindows.TryGetValue(boxId, out var window))
        {
            window.ReloadItemsFromStorage();
        }
    }

    private static void ApplySettingsToWindows()
    {
        _manager?.ApplyTheme();
        _settings?.ApplyTheme();
        foreach (var window in BoxWindows.Values)
        {
            window.ApplyGlobalAppearance();
        }

        Tray?.SetTooltip(AppSettingsService.Current.CloseToTray
            ? "PocketDesk · 关闭窗口后仍在托盘运行"
            : "PocketDesk");
    }

    private static TrayIcon? Tray => AppServices.Tray;

    public static void Shutdown()
    {
        foreach (var window in BoxWindows.Values.ToList())
        {
            try
            {
                window.PrepareForShutdown();
                window.CloseForcibly();
            }
            catch (Exception ex)
            {
                LogService.Warn($"关闭收纳盒窗口失败：{ex.Message}");
            }
        }

        try
        {
            _manager?.Close();
        }
        catch
        {
            // ignore
        }

        try
        {
            _settings?.Close();
        }
        catch
        {
            // ignore
        }

        BoxWindows.Clear();
        _manager = null;
        _settings = null;
    }

    public static int OpenBoxWindowCount => BoxWindows.Count;
}
