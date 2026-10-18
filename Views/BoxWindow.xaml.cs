using System.Collections.ObjectModel;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PocketDesk;

/// <summary>单个收纳盒的无边框窗口。</summary>
public sealed partial class BoxWindow : Window
{
    private const string InternalDragFormat = "PocketDesk.BoxItems";

    private readonly Box _box;
    private readonly ObservableCollection<BoxItem> _items = new();
    private readonly BoxImportInteraction _interaction;
    private readonly DispatcherQueueTimer _placementTimer;
    private readonly DispatcherQueueTimer _infoTimer;
    private readonly DispatcherQueueTimer _undoTimer;
    private readonly DispatcherQueueTimer _dropOverlayTimer;

    private bool _closed;
    private bool _forceClose;
    private int _interactiveDepth;
    private readonly NativeMethods.SubclassProc _subclassProc;
    private bool _isRolledUp;
    private bool _placementApplied;
    private int _expandedHeight = 320;
    private int _iconGeneration;
    private double _lastTileWidth;

    private bool _draggingWindow;
    private bool _dragMoved;
    private NativeMethods.POINT _dragStartCursor;
    private PointInt32 _dragStartPosition;

    private bool _marqueeActive;
    private bool _marqueeAdditive;
    private Point _marqueeStart;
    private double _rasterScale = 1.0;
    private BoxItem? _reorderItem;
    private Point _reorderStart;
    private bool _reorderActive;

    private List<DeletedItemInfo> _lastDeleted = new();

    public BoxWindow(Box box)
    {
        _box = box;
        _interaction = new BoxImportInteraction(this);
        _subclassProc = BoxSubclassProc;
        InitializeComponent();

        Title = box.Name;
        TitleText.Text = box.Name;
        TypeIcon.Glyph = box.TypeGlyph;

        ConfigureWindow();
        ApplyTheme();
        ApplyBackdrop();
        ApplyAppearance();
        HookEvents();

        _placementTimer = DispatcherQueue.CreateTimer();
        _placementTimer.Interval = TimeSpan.FromMilliseconds(500);
        _placementTimer.IsRepeating = false;
        _placementTimer.Tick += (_, _) => PersistPlacement();

        _infoTimer = DispatcherQueue.CreateTimer();
        _infoTimer.IsRepeating = false;
        _infoTimer.Tick += (_, _) => NotifyBar.IsOpen = false;

        _undoTimer = DispatcherQueue.CreateTimer();
        _undoTimer.IsRepeating = false;
        _undoTimer.Tick += (_, _) =>
        {
            NotifyBar.IsOpen = false;
            _lastDeleted.Clear();
        };

        _dropOverlayTimer = DispatcherQueue.CreateTimer();
        _dropOverlayTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _dropOverlayTimer.IsRepeating = false;
        _dropOverlayTimer.Tick += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;

        ItemsView.ItemsSource = _items;
        RootGrid.Loaded += OnRootGridLoaded;
        ItemsView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnMarqueePointerPressed), true);
        ItemsView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMarqueePointerMoved), true);
        ItemsView.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnMarqueePointerReleased), true);
        ItemsView.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnMarqueePointerReleased), true);
        ReloadItems();

        // 窗口显示后再应用位置：非激活显示（ShowAll / 启动恢复）不会触发 Activated。
        DispatcherQueue.TryEnqueue(ApplyInitialPlacement);
    }

    public XamlRoot Root => ((FrameworkElement)Content).XamlRoot;

    private void ConfigureWindow()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                // 保留原生窗口/圆角，隐藏标题栏；边框颜色随后与背景色一致，消除白缝。
                presenter.SetBorderAndTitleBar(true, false);
                ExtendsContentIntoTitleBar = true;
                // 把 1px 非客户区边框并入客户区，Mica/透明背景下不会露出一圈原生边框。
                var margins = new NativeMethods.MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
                NativeMethods.DwmExtendFrameIntoClientArea(
                    WinRT.Interop.WindowNative.GetWindowHandle(this), ref margins);
                var titleBar = AppWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
                {
                    titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
                }
                SetTitleBar(TitleBarHost);
                presenter.IsResizable = true;
                presenter.IsMinimizable = true;
                presenter.IsMaximizable = false;
                presenter.IsAlwaysOnTop = _box.IsPinned;
            }

            // 收纳盒是桌面层组件：不进入任务栏 / Alt+Tab，避免挤占任务栏。
            AppWindow.IsShownInSwitchers = false;
            var exStyle = NativeMethods.GetWindowLongPtrSafe(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtrSafe(
                hwnd,
                NativeMethods.GWL_EXSTYLE,
                (IntPtr)(exStyle | NativeMethods.WS_EX_TOOLWINDOW));
            LogService.Info(
                $"收纳盒窗口初始化：{AppWindow.Size.Width}x{AppWindow.Size.Height}px，" +
                $"DPI {DpiUtil.WindowDpi(hwnd)}，shownInSwitchers={AppWindow.IsShownInSwitchers}");

            var corner = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            var border = NativeMethods.DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch (Exception ex)
        {
            LogService.Warn($"配置收纳盒窗口失败：{ex.GetType().Name}");
        }
    }

    private void HookEvents()
    {
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;
        Activated += OnFirstActivated;
        Activated += OnWindowActivated;

        DragArea.PointerPressed += OnDragAreaPointerPressed;
        DragArea.PointerMoved += OnDragAreaPointerMoved;
        DragArea.PointerReleased += OnDragAreaPointerReleased;
        DragArea.PointerCaptureLost += OnDragAreaPointerReleased;

        BoxService.BoxesChanged += OnBoxesChanged;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        ApplyInitialPlacement();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            SetDropOverlay(false);
            EndMarquee();
            // 失去焦点后回到桌面层，避免长期压在其他应用之上；
            // 点击收纳盒时会短暂正常浮起，便于使用菜单与对话框。
            SendToBack();
        }
    }

    /// <summary>
    /// 桌面获得焦点（例如按 Win+D）时临时置顶，避免被桌面盖住；
    /// 回到普通窗口时立即恢复桌面层，不长期置顶。
    /// </summary>
    public void SetDesktopMode(bool desktop)
    {
        if (_closed)
        {
            return;
        }

        try
        {
            if (desktop)
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = true;
                }

                ApplyBackdrop();
            }
            else
            {
                ApplyPresenterState();
                SendToBack();
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"切换桌面模式失败：{ex.GetType().Name}");
        }
    }

    private IntPtr _previousForeground = IntPtr.Zero;

    /// <summary>普通窗口激活模式下无需切换焦点样式。</summary>
    public void BeginInteractive()
    {
    }

    public void EndInteractive()
    {
    }

    private IntPtr BoxSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE && _interactiveDepth == 0)
        {
            return (IntPtr)NativeMethods.MA_NOACTIVATE;
        }

        return NativeMethods.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>把窗口压到 z 序最底层，形成“贴着壁纸”的桌面层效果。</summary>
    public void SendToBack()
    {
        if (_closed || _box.IsPinned)
        {
            return;
        }

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // 不能直接用 HWND_BOTTOM：会落到 Progman/桌面图标下面，DWM 会按桌面背景渲染，
            // 圆角、模糊和阴影都会消失。插到“最上层桌面宿主之上、普通窗口之下”的桌面层。
            var insertAfter = NativeMethods.FindDesktopInsertAfter();
            NativeMethods.SetWindowPos(
                hwnd,
                insertAfter,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }
        catch (Exception ex)
        {
            LogService.Warn($"调整收纳盒层级失败：{ex.GetType().Name}");
        }
    }

    private void ApplyInitialPlacement()
    {
        if (_placementApplied || _closed)
        {
            return;
        }

        _placementApplied = true;
        RestorePlacement();
        SendToBack();
    }

    private void OnBoxesChanged()
    {
        var fresh = BoxRepository.Get(_box.Id);
        if (fresh is null)
        {
            CloseForcibly();
            return;
        }

        if (!string.Equals(_box.Name, fresh.Name, StringComparison.Ordinal))
        {
            _box.Name = fresh.Name;
            Title = fresh.Name;
            TitleText.Text = fresh.Name;
        }
    }

    // ---------------- 外观 ----------------

    public void ApplyGlobalAppearance()
    {
        var fresh = BoxRepository.Get(_box.Id);
        if (fresh is null)
        {
            CloseForcibly();
            return;
        }

        _box.Name = fresh.Name;
        _box.Appearance = fresh.Appearance;
        _box.IsPinned = fresh.IsPinned;
        Title = fresh.Name;
        TitleText.Text = fresh.Name;
        TypeIcon.Glyph = fresh.TypeGlyph;

        ApplyTheme();
        ApplyBackdrop();
        ApplyAppearance();
        ApplyPresenterState();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = AccessibilityService.IsHighContrast
            ? ElementTheme.Default
            : ResolveTheme() switch
            {
                ThemeKind.Light => ElementTheme.Light,
                ThemeKind.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
    }

    private ThemeKind ResolveTheme()
    {
        var value = _box.Appearance.Theme ?? AppSettingsService.Current.Theme;
        return Enum.TryParse<ThemeKind>(value, true, out var parsed) ? parsed : ThemeKind.System;
    }

    private void ApplyBackdrop()
    {
        var material = ResolveMaterial();
        try
        {
            if (AccessibilityService.IsHighContrast)
            {
                SystemBackdrop = null;
            }
            else if (material == MaterialKind.Transparent)
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            else if (material == MaterialKind.Mica && MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
            }
            else if (material != MaterialKind.Solid && DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            else
            {
                SystemBackdrop = null;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"设置窗口材质失败：{ex.GetType().Name}");
            SystemBackdrop = null;
        }

        if (SystemBackdrop is null)
        {
            RootGrid.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
            BackgroundLayer.Visibility = Visibility.Collapsed;
            ApplyFrameExtension(1);
        }
        else
        {
            RootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var light = RootGrid.RequestedTheme == ElementTheme.Light ||
                        (RootGrid.RequestedTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Light);
            BackgroundLayer.Background = new SolidColorBrush(light ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black)
            {
                Opacity = 0.35,
            };
            BackgroundLayer.Visibility = Visibility.Visible;
            ApplyFrameExtension(1);
        }

        ApplyWindowBorderColor();
    }

    private void ApplyFrameExtension(int margin)
    {
        try
        {
            var margins = new NativeMethods.MARGINS
            {
                Left = margin,
                Right = margin,
                Top = margin,
                Bottom = margin,
            };
            NativeMethods.DwmExtendFrameIntoClientArea(
                WinRT.Interop.WindowNative.GetWindowHandle(this), ref margins);
        }
        catch
        {
            // 旧系统忽略。
        }
    }

    private MaterialKind ResolveMaterial()
    {
        var value = _box.Appearance.Material ?? AppSettingsService.Current.Material;
        return Enum.TryParse<MaterialKind>(value, true, out var parsed) ? parsed : MaterialKind.Mica;
    }

    private void ApplyPresenterState()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _box.IsPinned;
            presenter.IsResizable = !_isRolledUp;
        }
    }

    public void RefreshVisuals()
    {
        if (_closed)
        {
            return;
        }

        ApplyTheme();
        ApplyBackdrop();
        ApplyAppearance();
    }

    private void ApplyWindowRegion(double radius)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            var r = (int)Math.Round(Math.Clamp(radius, 0, 24) * scale);
            if (r <= 0)
            {
                NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true);
                return;
            }

            var region = NativeMethods.CreateRoundRectRgn(0, 0, AppWindow.Size.Width + 1, AppWindow.Size.Height + 1, r, r);
            if (region != IntPtr.Zero)
            {
                NativeMethods.SetWindowRgn(hwnd, region, true);
            }
        }
        catch
        {
            // 忽略不支持的窗口状态。
        }
    }

    private void ApplyWindowBorderColor()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var color = NativeMethods.DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref color, sizeof(int));
        }
        catch
        {
            // 旧系统忽略。
        }
    }

    private static Windows.UI.Color ParseColor(string? value, Windows.UI.Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var text = value.Trim().TrimStart('#');
        try
        {
            return text.Length switch
            {
                8 => Windows.UI.Color.FromArgb(
                    Convert.ToByte(text[..2], 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16),
                    Convert.ToByte(text.Substring(6, 2), 16)),
                6 => Windows.UI.Color.FromArgb(
                    255,
                    Convert.ToByte(text[..2], 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16)),
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }

    private void ApplyAppearance()
    {
        var appearance = _box.Appearance;
        var radius = new CornerRadius(Math.Clamp(appearance.CornerRadius, 0, 24));
        BackgroundLayer.CornerRadius = radius;
        BorderLayer.CornerRadius = radius;
        BackgroundLayer.Opacity = Math.Clamp(appearance.BackgroundOpacity, 0.1, 1.0);
        var transparent = ResolveMaterial() == MaterialKind.Transparent;
        BorderLayer.Visibility = transparent ? Visibility.Visible : Visibility.Collapsed;
        if (transparent)
        {
            var light = RootGrid.RequestedTheme == ElementTheme.Light ||
                        (RootGrid.RequestedTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Light);
            BorderLayer.BorderThickness = new Thickness(1);
            BorderLayer.BorderBrush = new SolidColorBrush(light ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White)
            {
                Opacity = 0.35,
            };
        }

        ApplyWindowBorderColor();
        ApplyLayout();
        ApplyItemAppearance(reloadIcons: true);
        UpdateStatus();
    }

    private void ApplyItemAppearance(bool reloadIcons)
    {
        var iconSize = Math.Clamp(_box.Appearance.IconSize, 24, 96);
        var sizeChanged = _items.Any(i => Math.Abs(i.IconSize - iconSize) > 0.5);
        BoxService.ApplyAppearance(_items, _box.Appearance);
        if (reloadIcons && sizeChanged)
        {
            foreach (var item in _items)
            {
                item.Icon = null;
            }

            LoadIcons();
        }
    }

    private void ApplyLayout()
    {
        var list = _box.Appearance.ViewMode == BoxViewMode.List;
        ItemsView.ItemTemplate = (DataTemplate)RootGrid.Resources[list ? "ListItemTemplate" : "GridItemTemplate"];
        ItemsView.ItemsPanel = (ItemsPanelTemplate)RootGrid.Resources[list ? "ListPanelTemplate" : "GridPanelTemplate"];

        var style = new Style(typeof(GridViewItem));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(6)));

        if (list)
        {
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 44.0));
        }
        else
        {
            var width = CalculateTileWidth();
            var height = CalculateTileHeight();
            _lastTileWidth = width;
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, width));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, height));
        }

        ItemsView.ItemContainerStyle = style;
    }

    private double CalculateTileWidth()
    {
        var icon = Math.Clamp(_box.Appearance.IconSize, 24, 96);
        var columns = _box.Appearance.Columns;
        if (columns > 0 && ItemsView.ActualWidth > 40)
        {
            var available = ItemsView.ActualWidth - 20;
            return Math.Max(icon + 20, Math.Floor(available / columns) - 6);
        }

        return Math.Max(84, icon + 34);
    }

    private double CalculateTileHeight()
    {
        var icon = Math.Clamp(_box.Appearance.IconSize, 24, 96);
        var lines = Math.Clamp(_box.Appearance.NameLines, 0, 3);
        return icon + lines * 18 + (lines > 0 ? 16 : 8) + 16;
    }

    private void ApplyTileSizeIfNeeded()
    {
        if (_box.Appearance.ViewMode == BoxViewMode.List || _box.Appearance.Columns <= 0)
        {
            return;
        }

        var width = CalculateTileWidth();
        if (Math.Abs(width - _lastTileWidth) > 1)
        {
            ApplyLayout();
        }
    }

    // ---------------- 位置与尺寸 ----------------

    private void RestorePlacement()
    {
        var placement = _box.Placement;
        if (placement is null)
        {
            AppWindow.Resize(new SizeInt32(DpiUtil.Scale(360), DpiUtil.Scale(320)));
            _expandedHeight = DpiUtil.Scale(320);
            return;
        }

        var width = Math.Max(DpiUtil.Scale(300), placement.Width);
        var height = Math.Max(DpiUtil.Scale(240), placement.Height);
        var center = new PointInt32(placement.Left + width / 2, placement.Top + DpiUtil.Scale(24));
        var area = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        width = Math.Min(width, Math.Max(DpiUtil.Scale(300), work.Width));
        height = Math.Min(height, Math.Max(DpiUtil.Scale(240), work.Height));
        var x = Math.Clamp(placement.Left, work.X - width + DpiUtil.Scale(100), work.X + work.Width - DpiUtil.Scale(100));
        var y = Math.Clamp(placement.Top, work.Y, work.Y + work.Height - DpiUtil.Scale(60));

        try
        {
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch (Exception ex)
        {
            LogService.Warn($"恢复窗口位置失败：{ex.GetType().Name}");
        }

        _expandedHeight = height;
        if (placement.IsRolledUp)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isRolledUp)
                {
                    RollUp();
                }
            });
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            if (!_isRolledUp && AppWindow.Size.Height > 80)
            {
                _expandedHeight = AppWindow.Size.Height;
            }

            SchedulePlacementSave();
        }
    }

    private void SchedulePlacementSave()
    {
        if (_closed)
        {
            return;
        }

        _placementTimer.Stop();
        _placementTimer.Start();
    }

    public void PersistPlacement()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            var placement = new WindowPlacement
            {
                Left = AppWindow.Position.X,
                Top = AppWindow.Position.Y,
                Width = Math.Max(DpiUtil.Scale(240), AppWindow.Size.Width),
                Height = Math.Max(_isRolledUp ? _expandedHeight : AppWindow.Size.Height, DpiUtil.Scale(44)),
                MonitorDevice = area.DisplayId.Value.ToString(),
                DpiScale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0,
                State = _isRolledUp ? "RolledUp" : "Normal",
            };
            if (placement.Width <= 0 || placement.Height <= 0)
            {
                return;
            }

            BoxService.SavePlacement(_box, placement);
        }
        catch (Exception ex)
        {
            LogService.Warn($"保存窗口位置失败：{ex.GetType().Name}");
        }
    }

    public void SetPlacement(WindowPlacement placement)
    {
        try
        {
            AppWindow.MoveAndResize(new RectInt32(
                placement.Left,
                placement.Top,
                Math.Max(DpiUtil.Scale(300), placement.Width),
                Math.Max(DpiUtil.Scale(240), placement.Height)));
            _expandedHeight = placement.Height;
            if (_isRolledUp)
            {
                _isRolledUp = false;
                ContentRoot.Visibility = Visibility.Visible;
                StatusBar.Visibility = Visibility.Visible;
                RootGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                RootGrid.RowDefinitions[2].Height = GridLength.Auto;
                ApplyPresenterState();
            }

            PersistPlacement();
        }
        catch (Exception ex)
        {
            LogService.Warn($"设置窗口位置失败：{ex.GetType().Name}");
        }
    }

    // ---------------- 数据加载 ----------------

    public void ReloadItemsFromStorage()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ReloadItemsFromStorage);
            return;
        }

        if (_closed)
        {
            return;
        }

        ReloadItems();
    }

    private void ReloadItems()
    {
        var selected = ItemsView.SelectedItems.OfType<BoxItem>().Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = BoxService.GetItems(_box);
        _items.Clear();
        foreach (var item in fresh)
        {
            _items.Add(item);
        }

        ApplyItemAppearance(reloadIcons: true);
        RestoreSelection(selected);
        LoadIcons();
        _ = PrepareStorageItemsAsync();
        UpdateEmptyState();
        UpdateStatus();
    }

    /// <summary>提前解析拖出所需的 IStorageItem，避免在 DragItemsStarting 中同步等待。</summary>
    private async Task PrepareStorageItemsAsync()
    {
        foreach (var item in _items.ToList())
        {
            if (item.StorageItem is not null || item.Kind == ItemKind.Url ||
                !item.HasRealPath || !PathUtil.Exists(item.RealPath))
            {
                continue;
            }

            try
            {
                item.StorageItem = Directory.Exists(item.RealPath)
                    ? await StorageFolder.GetFolderFromPathAsync(item.RealPath)
                    : await StorageFile.GetFileFromPathAsync(item.RealPath);
            }
            catch (Exception ex)
            {
                LogService.Warn($"预解析拖出文件失败：{Path.GetFileName(item.RealPath)} :: {ex.GetType().Name}");
            }
        }
    }

    private void RestoreSelection(HashSet<string> ids)
    {
        ItemsView.SelectedItems.Clear();
        foreach (var item in _items.Where(i => ids.Contains(i.Id)).Take(200))
        {
            ItemsView.SelectedItems.Add(item);
        }
    }

    private async void LoadIcons()
    {
        var generation = ++_iconGeneration;
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? DpiUtil.SystemScale;
        var tasks = new List<Task>();
        foreach (var item in _items.ToList())
        {
            if (item.Kind == ItemKind.Url && item.IsReference)
            {
                continue;
            }

            if (!item.HasRealPath || (!PathUtil.Exists(item.RealPath) && !item.RealPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            tasks.Add(LoadIconAsync(item, generation, scale));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            LogService.Warn($"批量加载图标失败：{ex.GetType().Name}");
        }
    }

    private async Task LoadIconAsync(BoxItem item, int generation, double scale)
    {
        // Shell 只保证 16/32/48/256 等标准尺寸；96px 对部分文件会退回通用图标。
        // 高 DPI 下按物理像素取 Jumbo（256），低 DPI 取 48。
        var physical = item.IconSize * scale;
        var pixels = physical <= 48 ? 48 : 256;
        var icon = await IconService.LoadAsync(item.RealPath, pixels, item.Kind == ItemKind.Folder);
        if (icon is not null && generation == _iconGeneration && !_closed)
        {
            item.Icon = icon;
        }
    }

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        // Acrylic 在窗口首次显示后才真正挂载；再应用一次，避免启动时先出现纯色底。
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_closed)
            {
                ApplyBackdrop();
            }
        });
        if (RootGrid.XamlRoot is { } root)
        {
            _rasterScale = root.RasterizationScale;
            root.Changed += OnXamlRootChanged;
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        var scale = sender.RasterizationScale;
        if (Math.Abs(scale - _rasterScale) < 0.01)
        {
            return;
        }

        _rasterScale = scale;
        foreach (var item in _items)
        {
            item.Icon = null;
        }

        LoadIcons();
    }

    private void UpdateEmptyState()
    {
        var empty = _items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _box.Type switch
        {
            BoxType.Mapped => "把文件拖到这里（仅记录引用）",
            BoxType.Target => "绑定文件夹中的内容会显示在这里",
            _ => "把文件拖到这里",
        };
    }

    private void UpdateStatus()
    {
        var total = _items.Sum(i => i.Size);
        var selected = ItemsView.SelectedItems.Count;
        StatusText.Text = selected > 0
            ? $"已选择 {selected} / {_items.Count} 项"
            : $"{_items.Count} 项 · {_box.TypeLabel}";
        SizeText.Text = total > 0 ? PathUtil.FormatSize(total) : string.Empty;
    }

    private List<BoxItem> GetSelectedItems() => ItemsView.SelectedItems.OfType<BoxItem>().ToList();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStatus();

    private void OnItemsViewSizeChanged(object sender, SizeChangedEventArgs e) => ApplyTileSizeIfNeeded();

    // ---------------- 标题栏 ----------------

    private void OnDragAreaPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(DragArea).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        NativeMethods.GetCursorPos(out _dragStartCursor);
        _dragStartPosition = AppWindow.Position;
        _draggingWindow = true;
        _dragMoved = false;
        DragArea.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDragAreaPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingWindow)
        {
            return;
        }

        NativeMethods.GetCursorPos(out var cursor);
        var dx = cursor.X - _dragStartCursor.X;
        var dy = cursor.Y - _dragStartCursor.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4)
        {
            return;
        }

        _dragMoved = true;
        AppWindow.Move(new PointInt32(_dragStartPosition.X + dx, _dragStartPosition.Y + dy));
        e.Handled = true;
    }

    private void OnDragAreaPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingWindow)
        {
            return;
        }

        _draggingWindow = false;
        DragArea.ReleasePointerCapture(e.Pointer);
        if (_dragMoved)
        {
            PersistPlacement();
        }
    }

    private async void OnTitleTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        var name = await Dialogs.PromptAsync(
            Root,
            "重命名收纳盒",
            _box.Name,
            "收纳盒名称",
            validate: text => text.Length > 60 ? "名称不能超过 60 个字符。" : null);
        if (name is null)
        {
            return;
        }

        try
        {
            BoxService.RenameBox(_box, name);
            Title = _box.Name;
            TitleText.Text = _box.Name;
        }
        catch (Exception ex)
        {
            ShowInfo("重命名失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnRollClick(object sender, RoutedEventArgs e)
    {
        if (_isRolledUp)
        {
            Expand();
        }
        else
        {
            RollUp();
        }
    }

    private void RollUp()
    {
        if (_isRolledUp)
        {
            return;
        }

        _isRolledUp = true;
        _expandedHeight = Math.Max(AppWindow.Size.Height, DpiUtil.Scale(160));
        ContentRoot.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        RootGrid.RowDefinitions[1].Height = new GridLength(0);
        RootGrid.RowDefinitions[2].Height = new GridLength(0);
        RollGlyph.Glyph = "\uE70D";
        ToolTipService.SetToolTip(RollButton, "展开");
        ApplyPresenterState();
        AppWindow.Resize(new SizeInt32(AppWindow.Size.Width, DpiUtil.Scale(46)));
        PersistPlacement();
    }

    private void Expand()
    {
        if (!_isRolledUp)
        {
            return;
        }

        _isRolledUp = false;
        ContentRoot.Visibility = Visibility.Visible;
        StatusBar.Visibility = Visibility.Visible;
        RootGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        RootGrid.RowDefinitions[2].Height = GridLength.Auto;
        RollGlyph.Glyph = "\uE70E";
        ToolTipService.SetToolTip(RollButton, "卷起");
        ApplyPresenterState();
        AppWindow.Resize(new SizeInt32(AppWindow.Size.Width, Math.Max(_expandedHeight, DpiUtil.Scale(180))));
        PersistPlacement();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (AppSettingsService.Current.CloseToTray)
        {
            HideToTray(userAction: true);
        }
        else
        {
            PersistPlacement();
            AppServices.Shutdown();
        }
    }

    public void HideToTray(bool userAction = false)
    {
        PersistPlacement();
        _box.IsVisible = false;
        BoxRepository.SetVisibility(_box.Id, false);
        AppWindow.Hide();
        BoxService.NotifyBoxesChanged();
        if (userAction && !AppSettingsService.Current.CloseToTray)
        {
            AppServices.Shutdown();
        }
    }

    public void CloseForcibly()
    {
        _forceClose = true;
        Close();
    }

    public void PrepareForShutdown()
    {
        PersistPlacement();
        _forceClose = true;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose)
        {
            PersistPlacement();
            return;
        }

        if (AppSettingsService.Current.CloseToTray)
        {
            args.Cancel = true;
            HideToTray();
        }
        else
        {
            PersistPlacement();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        PersistPlacement();
        _closed = true;
        _forceClose = true;
        _placementTimer.Stop();
        _infoTimer.Stop();
        _undoTimer.Stop();
        _dropOverlayTimer.Stop();
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeMethods.RemoveWindowSubclass(hwnd, _subclassProc, 1);
        }
        catch
        {
            // 窗口可能已销毁。
        }

        BoxService.BoxesChanged -= OnBoxesChanged;
        WindowManager.OnBoxWindowClosed(_box.Id);
    }

    // ---------------- 拖拽 ----------------

    private void OnDragEnter(object sender, DragEventArgs e) => SetDropOverlay(true);

    private void OnDragLeave(object sender, DragEventArgs e) => SetDropOverlay(false);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(InternalDragFormat)
            ? DataPackageOperation.Move
            : DataPackageOperation.Copy;

        if (e.DragUIOverride is { } overrideInfo)
        {
            overrideInfo.Caption = $"收纳到“{_box.Name}”";
            overrideInfo.IsCaptionVisible = true;
            overrideInfo.IsContentVisible = true;
        }

        SetDropOverlay(true);
        e.Handled = true;
    }

    private void SetDropOverlay(bool visible)
    {
        DropOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _dropOverlayTimer.Stop();
        if (visible)
        {
            _dropOverlayTimer.Start();
        }
    }

    private void ReorderWithinBox(IReadOnlyList<string> itemIds, Point dropPoint)
    {
        var moving = itemIds
            .Select(id => _items.FirstOrDefault(i => i.Id == id))
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();
        if (moving.Count == 0 || moving.Count == _items.Count)
        {
            return;
        }

        var target = _items.Count;
        for (var i = 0; i < _items.Count; i++)
        {
            if (ItemsView.ContainerFromIndex(i) is not GridViewItem container)
            {
                continue;
            }

            try
            {
                var origin = container.TransformToVisual(ItemsView).TransformPoint(new Point(0, 0));
                if (dropPoint.Y < origin.Y + container.ActualHeight / 2)
                {
                    target = i;
                    break;
                }
            }
            catch
            {
                // 容器尚未布局时忽略。
            }
        }

        var insertIndex = target;
        foreach (var item in moving)
        {
            var oldIndex = _items.IndexOf(item);
            if (oldIndex >= 0 && oldIndex < target)
            {
                insertIndex--;
            }
        }

        var ordered = _items.ToList();
        ordered.RemoveAll(moving.Contains);
        ordered.InsertRange(Math.Clamp(insertIndex, 0, ordered.Count), moving);
        _items.Clear();
        foreach (var item in ordered)
        {
            _items.Add(item);
        }

        BoxService.SetSort(_box, "custom", false);
        ItemRepository.UpdateSortIndices(_items.ToList());
        ShowInfo("顺序已更新", "已切换为自定义排序，拖拽位置已保存。", InfoBarSeverity.Success, autoCloseMs: 2500);
    }

    private static string? ResolveShellPath(string name) => name switch
    {
        "此电脑" or "This PC" => "shell:MyComputerFolder",
        "控制面板" or "Control Panel" => "shell:ControlPanelFolder",
        "回收站" or "Recycle Bin" => "shell:RecycleBinFolder",
        "网络" or "Network" => "shell:NetworkPlacesFolder",
        _ => null,
    };

    private async void OnDrop(object sender, DragEventArgs e)
    {
        SetDropOverlay(false);
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(InternalDragFormat))
            {
                var raw = await e.DataView.GetDataAsync(InternalDragFormat);
                var payload = Json.Deserialize<InternalDragPayload>(raw as string);
                if (payload is null)
                {
                    return;
                }

                if (payload.BoxId == _box.Id)
                {
                    // 同盒整块拖动由 GridView 原生重排处理。
                    return;
                }

                var sourceBox = BoxService.GetBox(payload.BoxId);
                if (sourceBox is null)
                {
                    return;
                }

                var items = payload.ItemIds.Select(ItemRepository.Get).Where(i => i is not null).Select(i => i!).ToList();
                var (moved, error) = BoxService.MoveItemsToBox(sourceBox, _box, items);
                if (moved > 0)
                {
                    ShowInfo("已移动", $"已将 {moved} 项移动到“{_box.Name}”。", InfoBarSeverity.Success, autoCloseMs: 3000);
                }
                else if (error is not null)
                {
                    ShowInfo("无法移动", error, InfoBarSeverity.Warning);
                }

                return;
            }

            if (e.DataView.Contains(StandardDataFormats.WebLink))
            {
                var uri = await e.DataView.GetWebLinkAsync();
                _interaction.ResetBatch();
                ReportImport(await ImportService.ImportUrlAsync(_box, uri.ToString(), _interaction));
                return;
            }

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var storageItems = await e.DataView.GetStorageItemsAsync();
                var paths = storageItems.Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                var shellAdded = 0;
                if (_box.Type != BoxType.Normal)
                {
                    foreach (var storageItem in storageItems.Where(i => string.IsNullOrWhiteSpace(i.Path)))
                    {
                        var shellPath = ResolveShellPath(storageItem.Name);
                        if (shellPath is not null && BoxService.AddShellReference(_box, shellPath, storageItem.Name) is not null)
                        {
                            shellAdded++;
                        }
                    }
                }

                if (paths.Count == 0)
                {
                    if (shellAdded > 0)
                    {
                        ShowInfo("已添加", $"已添加 {shellAdded} 个系统位置。", InfoBarSeverity.Success, autoCloseMs: 2500);
                    }
                    else
                    {
                        ShowInfo("无法收纳", _box.Type == BoxType.Normal
                            ? "普通收纳盒不能收纳“此电脑/控制面板/回收站”，请使用映射收纳盒。"
                            : "拖入的内容不是本地文件、文件夹或支持的系统位置。", InfoBarSeverity.Warning);
                    }

                    return;
                }

                _interaction.ResetBatch();
                ReportImport(await ImportService.ImportPathsAsync(_box, paths, _interaction));
            }
        }
        catch (Exception ex)
        {
            LogService.Error("拖放收纳失败", ex);
            ShowInfo("收纳失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ReportImport(List<ImportResult> results)
    {
        var failed = results.Where(r => !r.Success && !r.Skipped).ToList();
        var skipped = results.Where(r => r.Skipped).ToList();
        var success = results.Count(r => r.Success);
        if (failed.Count > 0)
        {
            var detail = failed[0].Error ?? "未知错误";
            if (failed.Count > 1)
            {
                detail += $"（另有 {failed.Count - 1} 项失败）";
            }

            ShowInfo("部分文件未能收纳", detail, InfoBarSeverity.Warning, autoCloseMs: 9000);
        }
        else if (skipped.Count > 0 && success == 0)
        {
            ShowInfo("已跳过", skipped[0].Error ?? "没有可收纳的内容。", InfoBarSeverity.Informational, autoCloseMs: 5000);
        }
        else if (success > 0)
        {
            ShowInfo("已收纳", $"已收纳 {success} 项。", InfoBarSeverity.Success, autoCloseMs: 3500);
        }
    }

    private void OnItemsDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        BoxService.SetSort(_box, "custom", false);
        ItemRepository.UpdateSortIndices(_items.ToList());
        ShowInfo("顺序已更新", "已切换为自定义排序，拖拽位置已保存。", InfoBarSeverity.Success, autoCloseMs: 2000);
    }

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // 使用 GridView 原生整块磁贴拖动重排，不注入自定义 DataPackage。
    }

    // ---------------- 菜单 ----------------

    private void OnListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = FindItem(e.OriginalSource as DependencyObject);
        if (item is not null)
        {
            if (!ItemsView.SelectedItems.Contains(item))
            {
                ItemsView.SelectedItems.Clear();
                ItemsView.SelectedItems.Add(item);
            }

            ShowItemMenu(e.GetPosition(ItemsView));
        }
        else
        {
            ShowBoxMenu(e.GetPosition(ItemsView));
        }

        e.Handled = true;
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e) =>
        ShowBoxMenu(new Point(Math.Max(20, StatusBar.ActualWidth - 160), 0));

    private BoxItem? FindItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: BoxItem item })
            {
                return item;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void ShowItemMenu(Point position)
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
        {
            ShowBoxMenu(position);
            return;
        }

        var flyout = new MenuFlyout();
        AddMenuItem(flyout, "打开", "\uE8E5", () => BoxService.OpenItem(_box, selected[0]), selected.Count == 1);
        AddMenuItem(flyout, "打开所在位置", "\uE8B7", () => BoxService.RevealItem(_box, selected[0]), selected.Count == 1);
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "重命名", "\uE8AC", RenameSelectedAsync, selected.Count == 1, "F2");
        if (selected.Count > 1)
        {
            AddMenuItem(flyout, $"批量重命名 {selected.Count} 项…", "\uE8AC", BatchRenameSelectedAsync);
        }
        if (string.Equals(_box.SortField, "custom", StringComparison.OrdinalIgnoreCase) && selected.Count == 1)
        {
            AddMenuItem(flyout, "在自定义顺序中上移", "\uE74A", () =>
            {
                if (BoxService.MoveItemOrder(_box, selected[0], -1))
                {
                    ReloadItems();
                }
            });
            AddMenuItem(flyout, "在自定义顺序中下移", "\uE74B", () =>
            {
                if (BoxService.MoveItemOrder(_box, selected[0], 1))
                {
                    ReloadItems();
                }
            });
        }

        AddMenuItem(flyout, selected.Count > 1 ? $"删除 {selected.Count} 项" : "删除", "\uE74D", DeleteSelectedAsync, true, "Del");
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "复制路径", "\uE8C8", CopyPathsAsync);
        AddMenuItem(flyout, "创建快捷方式", "\uE71B", CreateShortcutAsync, selected.Count == 1);

        var moveMenu = new MenuFlyoutSubItem { Text = "移动到其他盒" };
        var others = BoxService.GetBoxes().Where(b => b.Id != _box.Id && b.Type == _box.Type).ToList();
        if (others.Count == 0)
        {
            moveMenu.Items.Add(new MenuFlyoutItem { Text = "没有同类型收纳盒", IsEnabled = false });
        }
        else
        {
            foreach (var target in others)
            {
                var box = target;
                var entry = new MenuFlyoutItem { Text = box.Name };
                entry.Click += (_, _) => MoveSelectedAsync(box);
                moveMenu.Items.Add(entry);
            }
        }

        flyout.Items.Add(moveMenu);
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "属性", "\uE946", () => ShellService.ShowProperties(
            WinRT.Interop.WindowNative.GetWindowHandle(this), selected[0].RealPath), selected.Count == 1);

        ShowFlyoutInteractive(flyout, ItemsView, position);
    }

    private void ShowBoxMenu(Point position)
    {
        var flyout = new MenuFlyout();

        AddMenuItem(flyout, "收纳文件…", "\uE710", AddFilesAsync);
        AddMenuItem(flyout, "收纳文件夹…", "\uE8B7", AddFolderAsync);
        AddMenuItem(flyout, "粘贴", "\uE77F", PasteAsync);
        AddMenuItem(flyout, "粘贴路径收纳…", "\uE71B", PastePathAsync);
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "新建文件夹", "\uE8F4", CreateFolderAsync, _box.Type != BoxType.Mapped);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var sortMenu = new MenuFlyoutSubItem { Text = "排序" };
        foreach (var (key, label) in new[]
                 {
                     ("name", "名称"), ("type", "类型"), ("modified", "修改时间"),
                     ("added", "添加时间"), ("custom", "自定义"),
                 })
        {
            var check = new ToggleMenuFlyoutItem
            {
                Text = label,
                IsChecked = string.Equals(_box.SortField, key, StringComparison.OrdinalIgnoreCase),
            };
            check.Click += (_, _) =>
            {
                BoxService.SetSort(_box, key, _box.SortDescending);
                ReloadItems();
            };
            sortMenu.Items.Add(check);
        }

        sortMenu.Items.Add(new MenuFlyoutSeparator());
        var ascending = new ToggleMenuFlyoutItem { Text = "升序", IsChecked = !_box.SortDescending };
        ascending.Click += (_, _) =>
        {
            BoxService.SetSort(_box, _box.SortField, false);
            ReloadItems();
        };
        var descending = new ToggleMenuFlyoutItem { Text = "降序", IsChecked = _box.SortDescending };
        descending.Click += (_, _) =>
        {
            BoxService.SetSort(_box, _box.SortField, true);
            ReloadItems();
        };
        sortMenu.Items.Add(ascending);
        sortMenu.Items.Add(descending);
        flyout.Items.Add(sortMenu);

        var viewMenu = new MenuFlyoutSubItem { Text = "视图" };
        var grid = new ToggleMenuFlyoutItem { Text = "网格", IsChecked = _box.Appearance.ViewMode == BoxViewMode.Grid };
        grid.Click += (_, _) => SetView("grid");
        var list = new ToggleMenuFlyoutItem { Text = "列表", IsChecked = _box.Appearance.ViewMode == BoxViewMode.List };
        list.Click += (_, _) => SetView("list");
        viewMenu.Items.Add(grid);
        viewMenu.Items.Add(list);
        flyout.Items.Add(viewMenu);

        AddMenuItem(flyout, "刷新", "\uE72C", () =>
        {
            ReloadItems();
            LoadIcons();
        });
        AddMenuItem(flyout, "全选", "\uE8B3", () =>
        {
            ItemsView.SelectAll();
            UpdateStatus();
        });
        flyout.Items.Add(new MenuFlyoutSeparator());

        var pin = new ToggleMenuFlyoutItem { Text = "始终置顶", IsChecked = _box.IsPinned };
        pin.Click += (_, _) =>
        {
            BoxService.SetPinned(_box, !_box.IsPinned);
            ApplyPresenterState();
            if (!_box.IsPinned)
            {
                SendToBack();
            }
        };
        flyout.Items.Add(pin);
        AddMenuItem(flyout, "卷起 / 展开", "\uE70E", () =>
        {
            if (_isRolledUp)
            {
                Expand();
            }
            else
            {
                RollUp();
            }
        });
        AddMenuItem(flyout, "外观…", "\uE771", OpenAppearanceAsync);
        flyout.Items.Add(BuildOpacityMenu());
        flyout.Items.Add(BuildPresetMenu());
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "新建同类收纳盒", "\uE710", () => WindowManager.OpenManager(_box.Type, createImmediately: true));
        AddMenuItem(flyout, "收纳盒管理器", "\uE8B7", () => WindowManager.OpenManager());
        AddMenuItem(flyout, "设置", "\uE713", () => WindowManager.OpenSettings());
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "删除收纳盒…", "\uE74D", DeleteBoxAsync);

        ShowFlyoutInteractive(flyout, ItemsView, position);
    }

    private static void AddMenuItem(
        MenuFlyout flyout,
        string text,
        string glyph,
        Action handler,
        bool enabled = true,
        string? shortcut = null)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            IsEnabled = enabled,
            Icon = new FontIcon { Glyph = glyph, FontSize = 14 },
        };
        if (!string.IsNullOrEmpty(shortcut))
        {
            item.KeyboardAcceleratorTextOverride = shortcut;
        }

        item.Click += (_, _) =>
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                LogService.Error("菜单动作失败", ex);
            }
        };
        flyout.Items.Add(item);
    }

    private static void AddMenuItem(
        MenuFlyout flyout,
        string text,
        string glyph,
        Func<Task> handler,
        bool enabled = true,
        string? shortcut = null)
    {
        AddMenuItem(flyout, text, glyph, () => _ = RunAsync(handler), enabled, shortcut);
    }

    private static async Task RunAsync(Func<Task> handler)
    {
        try
        {
            await handler();
        }
        catch (Exception ex)
        {
            LogService.Error("菜单动作失败", ex);
        }
    }

    private void ShowFlyoutInteractive(MenuFlyout flyout, FrameworkElement target)
    {
        BeginInteractive();
        flyout.Closed += (_, _) => EndInteractive();
        flyout.ShowAt(target);
    }

    private void ShowFlyoutInteractive(MenuFlyout flyout, FrameworkElement target, Point position)
    {
        BeginInteractive();
        flyout.Closed += (_, _) => EndInteractive();
        flyout.ShowAt(target, position);
    }

    private MenuFlyoutSubItem BuildOpacityMenu()
    {
        var sub = new MenuFlyoutSubItem
        {
            Text = "背景透明度",
            Icon = new FontIcon { Glyph = "\uE7F0", FontSize = 14 },
        };

        foreach (var percent in new[] { 100, 90, 80, 70, 60, 40, 20 })
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = $"{percent}%",
                IsChecked = Math.Abs(_box.Appearance.BackgroundOpacity - percent / 100.0) < 0.01,
            };
            item.Click += (_, _) => SetBackgroundOpacity(percent / 100.0);
            sub.Items.Add(item);
        }

        return sub;
    }

    private void SetBackgroundOpacity(double value)
    {
        _box.Appearance.BackgroundOpacity = Math.Clamp(value, 0.2, 1.0);
        BoxService.SaveAppearance(_box, _box.Appearance);
        ApplyAppearance();
        ShowInfo("背景透明度", $"{_box.Appearance.BackgroundOpacity:P0}", InfoBarSeverity.Informational, autoCloseMs: 2000);
    }

    private MenuFlyoutSubItem BuildPresetMenu()
    {
        var menu = new MenuFlyoutSubItem { Text = "外观预设" };
        var presets = AppearancePresetService.Load();
        foreach (var preset in presets)
        {
            var captured = preset;
            var entry = new MenuFlyoutItem
            {
                Text = captured.Name,
                Icon = new FontIcon { Glyph = "\uE790", FontSize = 14 },
            };
            entry.Click += (_, _) => ApplyPreset(captured);
            menu.Items.Add(entry);
        }

        if (presets.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "暂无预设", IsEnabled = false });
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var save = new MenuFlyoutItem { Text = "另存当前外观为预设…" };
        save.Click += async (_, _) => await SavePresetAsync();
        menu.Items.Add(save);

        if (presets.Count > 0)
        {
            var delete = new MenuFlyoutSubItem { Text = "删除预设" };
            foreach (var preset in presets)
            {
                var captured = preset;
                var entry = new MenuFlyoutItem { Text = captured.Name };
                entry.Click += (_, _) =>
                {
                    AppearancePresetService.Delete(captured.Name);
                    ShowInfo("已删除外观预设", captured.Name, InfoBarSeverity.Informational, autoCloseMs: 2500);
                };
                delete.Items.Add(entry);
            }

            menu.Items.Add(delete);
        }

        return menu;
    }

    private void ApplyPreset(AppearancePreset preset)
    {
        _box.Appearance = preset.Appearance.Clone();
        BoxService.SaveAppearance(_box, _box.Appearance);
        ApplyTheme();
        ApplyBackdrop();
        ApplyAppearance();
        ApplyPresenterState();
        ReloadItems();
        ShowInfo("已应用外观预设", preset.Name, InfoBarSeverity.Success, autoCloseMs: 2500);
    }

    private async Task SavePresetAsync()
    {
        var name = await Dialogs.PromptAsync(
            Root,
            "另存为外观预设",
            "我的预设",
            "预设名称",
            "保存",
            validate: text => text.Length > 40 ? "名称不能超过 40 个字符。" : null);
        if (name is null)
        {
            return;
        }

        AppearancePresetService.Save(name, _box.Appearance);
        ShowInfo("已保存外观预设", name, InfoBarSeverity.Success, autoCloseMs: 2500);
    }

    private void SetView(string view)
    {
        _box.Appearance.View = view;
        BoxService.SaveAppearance(_box, _box.Appearance);
        ApplyAppearance();
        ReloadItems();
    }

    private async void OpenAppearanceAsync()
    {
        BoxAppearance? result;
        BeginInteractive();
        try
        {
            result = await Dialogs.AppearanceAsync(Root, _box.Appearance, AppSettingsService.Current);
        }
        finally
        {
            EndInteractive();
        }

        if (result is null)
        {
            return;
        }

        BoxService.SaveAppearance(_box, result);
        ApplyTheme();
        ApplyBackdrop();
        ApplyAppearance();
        ApplyPresenterState();
        ReloadItems();
    }

    private async void RenameSelectedAsync()
    {
        var selected = GetSelectedItems();
        if (selected.Count != 1)
        {
            return;
        }

        var item = selected[0];
        string? name;
        BeginInteractive();
        try
        {
            name = await Dialogs.PromptAsync(
                Root,
                "重命名",
                item.DisplayName,
                "新名称",
                validate: text =>
                {
                    if (text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        return "名称包含不允许的字符。";
                    }

                    return text.Length > 120 ? "名称过长。" : null;
                });
        }
        finally
        {
            EndInteractive();
        }

        if (name is null)
        {
            return;
        }

        try
        {
            BoxService.RenameItem(_box, item, name);
            ReloadItems();
        }
        catch (Exception ex)
        {
            ShowInfo("重命名失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void BatchRenameSelectedAsync()
    {
        var selected = GetSelectedItems();
        if (selected.Count < 2)
        {
            return;
        }

        var ordered = _items.Where(i => selected.Contains(i)).ToList();
        var options = await Dialogs.BatchRenameAsync(Root, ordered.Select(i => i.DisplayName).ToList());
        if (options is null)
        {
            return;
        }

        var failures = new List<string>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            var numbered = options.Prefix + (options.StartNumber + i).ToString("D" + options.Digits);
            var extension = item.Kind is ItemKind.Folder || (item.Kind == ItemKind.Url && item.IsReference)
                ? string.Empty
                : Path.GetExtension(item.RealPath);
            var newName = options.KeepExtension ? numbered + extension : numbered;
            try
            {
                BoxService.RenameItem(_box, item, newName);
            }
            catch (Exception ex)
            {
                failures.Add($"“{item.DisplayName}”：{ex.Message}");
            }
        }

        ReloadItems();
        if (failures.Count > 0)
        {
            var detail = string.Join("；", failures.Take(3));
            if (failures.Count > 3)
            {
                detail += $"；另有 {failures.Count - 3} 项失败";
            }

            ShowInfo("部分重命名失败", detail, InfoBarSeverity.Warning, autoCloseMs: 9000);
        }
        else
        {
            ShowInfo("已重命名", $"已批量重命名 {ordered.Count} 项。", InfoBarSeverity.Success, autoCloseMs: 3000);
        }
    }

    private async void DeleteSelectedAsync()
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
        {
            return;
        }

        if (AppSettingsService.Current.ConfirmDelete)
        {
            var message = _box.Type switch
            {
                BoxType.Normal => "删除后文件会还原到首次收纳前的原位置（原位置不可用时放到桌面）。",
                BoxType.Mapped => "只会移除引用，源文件不会被修改。",
                _ => "文件会从绑定文件夹移到回收站。",
            };
            message = selected.Count > 1
                ? $"将删除选中的 {selected.Count} 项。{message}"
                : $"确定删除“{selected[0].DisplayName}”吗？\n{message}";

            if (!await Dialogs.ConfirmAsync(Root, L.DeleteConfirmTitle, message, "删除", "取消"))
            {
                return;
            }
        }

        var deleted = BoxService.DeleteItems(_box, selected);
        _lastDeleted = deleted;
        ReloadItems();

        if (deleted.Count == 0)
        {
            ShowInfo("删除失败", "没有项目被删除，请查看诊断日志。", InfoBarSeverity.Error);
            return;
        }

        var restored = deleted.Any(d => !string.IsNullOrEmpty(d.RestoredPath) && PathUtil.Exists(d.RestoredPath));
        var detail = _box.Type switch
        {
            BoxType.Mapped => "源文件未改动。",
            BoxType.Target => "文件已移到回收站。",
            _ when restored => L.RestoredToOriginal,
            _ => "已删除。",
        };
        var canUndo = restored || _box.Type == BoxType.Mapped;
        ShowInfo(
            $"{L.Deleted} {deleted.Count} 项",
            detail,
            InfoBarSeverity.Informational,
            actionText: canUndo ? L.Undo : null,
            action: canUndo ? UndoDelete : null,
            autoCloseMs: Math.Max(5, AppSettingsService.Current.UndoSeconds) * 1000);
    }

    private async void UndoDelete()
    {
        var deleted = _lastDeleted.ToList();
        if (deleted.Count == 0)
        {
            return;
        }

        _lastDeleted.Clear();
        _undoTimer.Stop();
        NotifyBar.IsOpen = false;
        await BoxService.UndoDeleteAsync(_box, deleted);
        ReloadItems();
        ShowInfo("已撤销", $"已恢复 {deleted.Count} 项。", InfoBarSeverity.Success, autoCloseMs: 2500);
    }

    private async void MoveSelectedAsync(Box target)
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
        {
            return;
        }

        var (moved, error) = BoxService.MoveItemsToBox(_box, target, selected);
        if (moved > 0)
        {
            ShowInfo("已移动", $"已将 {moved} 项移动到“{target.Name}”。", InfoBarSeverity.Success, autoCloseMs: 3000);
        }
        else if (error is not null)
        {
            ShowInfo("无法移动", error, InfoBarSeverity.Warning);
        }
    }

    private async void CreateShortcutAsync()
    {
        var selected = GetSelectedItems();
        if (selected.Count != 1)
        {
            return;
        }

        var item = selected[0];
        string? target = null;
        string? url = null;
        if (item.Kind == ItemKind.Url)
        {
            url = item.ShortcutTarget;
            if (string.IsNullOrEmpty(url) && item.HasRealPath && File.Exists(item.RealPath))
            {
                url = ShortcutService.ReadUrl(item.RealPath);
            }
        }
        else if (item.HasRealPath && PathUtil.Exists(item.RealPath))
        {
            target = item.RealPath;
        }

        if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(target))
        {
            ShowInfo("无法创建快捷方式", "收纳项的路径不可用。", InfoBarSeverity.Warning);
            return;
        }

        var created = ShellService.CreateShortcutOnDesktop(item.DisplayName, target, url);
        if (created is null)
        {
            ShowInfo("创建快捷方式失败", "无法在桌面写入快捷方式。", InfoBarSeverity.Error);
            return;
        }

        await Task.CompletedTask;
        ShowInfo(
            "已创建快捷方式",
            Path.GetFileName(created),
            InfoBarSeverity.Success,
            actionText: "打开所在位置",
            action: () => ShellService.RevealInExplorer(created),
            autoCloseMs: 5000);
    }

    private async void CopyPathsAsync()    {
        var paths = GetSelectedItems().Select(i => i.RealPath).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var ok = await ShellService.CopyTextAsync(string.Join(Environment.NewLine, paths));
        ShowInfo(
            ok ? "已复制路径" : "复制失败",
            ok ? $"{paths.Count} 个路径已复制到剪贴板。" : "无法写入剪贴板。",
            ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            autoCloseMs: 2500);
    }

    private async void DeleteBoxAsync()
    {
        var detail = _box.Type switch
        {
            BoxType.Normal => "收纳盒中的文件会还原到各自的原位置。",
            BoxType.Mapped => "只删除引用，源文件不会改动。",
            _ => "只解除绑定，绑定文件夹中的文件会保留。",
        };
        if (!await Dialogs.ConfirmAsync(Root, "删除收纳盒", $"确定删除“{_box.Name}”吗？\n{detail}", "删除收纳盒", "取消"))
        {
            return;
        }

        BoxService.DeleteBox(_box);
        CloseForcibly();
    }

    // ---------------- 添加内容 ----------------

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        AddMenuItem(flyout, "收纳文件…", "\uE710", AddFilesAsync);
        AddMenuItem(flyout, "收纳文件夹…", "\uE8B7", AddFolderAsync);
        AddMenuItem(flyout, "粘贴", "\uE77F", PasteAsync);
        AddMenuItem(flyout, "粘贴路径收纳…", "\uE71B", PastePathAsync);
        ShowFlyoutInteractive(flyout, AddButton);
    }

    private async void AddFilesAsync()
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0)
            {
                return;
            }

            _interaction.ResetBatch();
            ReportImport(await ImportService.ImportPathsAsync(_box, files.Select(f => f.Path).ToList(), _interaction));
        }
        catch (Exception ex)
        {
            LogService.Error("文件选择失败", ex);
            ShowInfo("无法打开文件选择器", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void AddFolderAsync()
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            _interaction.ResetBatch();
            ReportImport(await ImportService.ImportPathsAsync(_box, new[] { folder.Path }, _interaction));
        }
        catch (Exception ex)
        {
            LogService.Error("文件夹选择失败", ex);
            ShowInfo("无法打开文件夹选择器", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void PasteAsync()
    {
        var paths = await ShellService.GetClipboardPathsAsync();
        if (paths.Count == 0)
        {
            ShowInfo("剪贴板为空", "剪贴板中没有可收纳的文件或文件夹。", InfoBarSeverity.Informational, autoCloseMs: 3000);
            return;
        }

        _interaction.ResetBatch();
        ReportImport(await ImportService.ImportPathsAsync(_box, paths, _interaction));
    }

    private async void PastePathAsync()
    {
        var text = await Dialogs.PromptAsync(
            Root,
            "粘贴路径收纳",
            string.Empty,
            "每行一个文件或文件夹路径",
            "收纳",
            multiline: true);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var paths = text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 0)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        _interaction.ResetBatch();
        ReportImport(await ImportService.ImportPathsAsync(_box, paths, _interaction));
    }

    private async void CreateFolderAsync()
    {
        if (_box.Type == BoxType.Mapped)
        {
            ShowInfo("无法新建文件夹", "映射收纳盒只保存引用，请使用资源管理器或改为普通/目标收纳盒。", InfoBarSeverity.Warning);
            return;
        }

        string? name;
        BeginInteractive();
        try
        {
            name = await Dialogs.PromptAsync(Root, "新建文件夹", "新建文件夹", "文件夹名称");
        }
        finally
        {
            EndInteractive();
        }

        if (name is null)
        {
            return;
        }

        var parent = _box.Type == BoxType.Normal ? AppPaths.BoxStorage(_box.Id) : _box.TargetPath;
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            ShowInfo("无法新建", "收纳盒的存储目录不可用。", InfoBarSeverity.Error);
            return;
        }

        try
        {
            var target = PathUtil.UniquePathIn(parent, PathUtil.SanitizeFileName(name));
            Directory.CreateDirectory(target);
            var item = new BoxItem
            {
                BoxId = _box.Id,
                Kind = ItemKind.Folder,
                DisplayName = Path.GetFileName(target),
                SourcePath = _box.Type == BoxType.Normal ? null : target,
                StoredPath = _box.Type == BoxType.Normal ? target : null,
                SortIndex = ItemRepository.MaxSortIndex(_box.Id) + 1,
                ModifiedAt = DateTime.Now,
            };
            ItemRepository.Insert(item);
            BoxService.NotifyBoxContentChanged(_box.Id);
            ReloadItems();
        }
        catch (Exception ex)
        {
            LogService.Error("新建文件夹失败", ex);
            ShowInfo("新建文件夹失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    // ---------------- 列表交互 ----------------

    // ---------------- 框选 ----------------

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is GridViewItem container)
        {
            container.PointerPressed -= OnReorderPointerPressed;
            container.PointerPressed += OnReorderPointerPressed;
        }
    }

    private void OnGripPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BoxItem item)
        {
            return;
        }

        _reorderItem = item;
        _reorderStart = e.GetCurrentPoint(ItemsView).Position;
        _reorderActive = false;
        ItemsView.CapturePointer(e.Pointer);
        e.Handled = true;
        LogService.Info($"拖动把手按下：{item.DisplayName}");
    }

    private void OnReorderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _reorderItem = null;
        _reorderActive = false;
        var point = e.GetCurrentPoint(ItemsView);
        if (!point.Properties.IsLeftButtonPressed || FindItem(e.OriginalSource as DependencyObject) is not { } item)
        {
            return;
        }

        _reorderItem = item;
        _reorderStart = point.Position;
        LogService.Info($"拖拽排序按下：{item.DisplayName}");
    }

    private void OnReorderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_reorderItem is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(ItemsView);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var dx = point.Position.X - _reorderStart.X;
        var dy = point.Position.Y - _reorderStart.Y;
        if (!_reorderActive && Math.Abs(dx) + Math.Abs(dy) > 4)
        {
            _reorderActive = true;
            ItemsView.CapturePointer(e.Pointer);
            if (ItemsView.ContainerFromItem(_reorderItem) is GridViewItem container)
            {
                container.Opacity = 0.55;
            }
        }

        if (!_reorderActive)
        {
            return;
        }

        MoveRebuildingItem(point.Position);
        e.Handled = true;
    }

    private void MoveRebuildingItem(Point point)
    {
        var item = _reorderItem;
        if (item is null)
        {
            return;
        }

        var oldIndex = _items.IndexOf(item);
        if (oldIndex < 0)
        {
            return;
        }

        var target = _items.Count;
        for (var i = 0; i < _items.Count; i++)
        {
            if (ItemsView.ContainerFromIndex(i) is not GridViewItem container)
            {
                continue;
            }

            var origin = container.TransformToVisual(ItemsView).TransformPoint(new Point(0, 0));
            var centerY = origin.Y + container.ActualHeight / 2;
            if (point.Y < centerY)
            {
                target = i;
                break;
            }
        }

        var insert = target;
        if (oldIndex < insert)
        {
            insert--;
        }

        insert = Math.Clamp(insert, 0, _items.Count - 1);
        if (insert != oldIndex)
        {
            _items.Move(oldIndex, insert);
        }
    }

    private void OnReorderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var item = _reorderItem;
        var active = _reorderActive;
        _reorderItem = null;
        _reorderActive = false;
        if (item is null || !active)
        {
            return;
        }

        if (ItemsView.ContainerFromItem(item) is GridViewItem container)
        {
            container.Opacity = 1;
        }

        try
        {
            ItemsView.ReleasePointerCapture(e.Pointer);
        }
        catch
        {
            // 忽略已释放的指针。
        }

        BoxService.SetSort(_box, "custom", false);
        ItemRepository.UpdateSortIndices(_items.ToList());
        ShowInfo("顺序已更新", "已切换为自定义排序，拖拽位置已保存。", InfoBarSeverity.Success, autoCloseMs: 2000);
        LogService.Info($"拖拽排序释放：{item.DisplayName}");
        e.Handled = true;
    }

    private void OnMarqueePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ItemsView);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (FindItem(source) is not null ||
            FindAncestor<ScrollBar>(source) is not null ||
            FindAncestor<Button>(source) is not null)
        {
            return;
        }

        _marqueeActive = true;
        _marqueeAdditive = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
        _marqueeStart = point.Position;
        if (!_marqueeAdditive)
        {
            ItemsView.SelectedItems.Clear();
        }

        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _marqueeStart.X);
        Canvas.SetTop(SelectionRect, _marqueeStart.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        ItemsView.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMarqueePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive)
        {
            return;
        }

        var current = e.GetCurrentPoint(ItemsView).Position;
        var x = Math.Clamp(current.X, 0, Math.Max(1, ItemsView.ActualWidth));
        var y = Math.Clamp(current.Y, 0, Math.Max(1, ItemsView.ActualHeight));
        var left = Math.Min(_marqueeStart.X, x);
        var top = Math.Min(_marqueeStart.Y, y);
        var width = Math.Abs(x - _marqueeStart.X);
        var height = Math.Abs(y - _marqueeStart.Y);

        Canvas.SetLeft(SelectionRect, left);
        Canvas.SetTop(SelectionRect, top);
        SelectionRect.Width = width;
        SelectionRect.Height = height;
        SelectItemsIn(new Rect(left, top, width, height));
        e.Handled = true;
    }

    private void OnMarqueePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive)
        {
            return;
        }

        EndMarquee();
        e.Handled = true;
    }

    private void EndMarquee()
    {
        if (!_marqueeActive)
        {
            return;
        }

        _marqueeActive = false;
        SelectionRect.Visibility = Visibility.Collapsed;
        try
        {
            ItemsView.ReleasePointerCaptures();
        }
        catch
        {
            // ignore
        }
    }

    private void SelectItemsIn(Rect rect)
    {
        foreach (var item in _items)
        {
            if (ItemsView.ContainerFromItem(item) is not FrameworkElement container)
            {
                continue;
            }

            try
            {
                var transform = container.TransformToVisual(ItemsView);
                var origin = transform.TransformPoint(new Point(0, 0));
                var bounds = new Rect(origin.X, origin.Y, container.ActualWidth, container.ActualHeight);
                var intersects = Intersects(bounds, rect);
                if (intersects && !ItemsView.SelectedItems.Contains(item))
                {
                    ItemsView.SelectedItems.Add(item);
                }
                else if (!intersects && !_marqueeAdditive && ItemsView.SelectedItems.Contains(item))
                {
                    ItemsView.SelectedItems.Remove(item);
                }
            }
            catch
            {
                // 容器正在虚拟化回收时忽略。
            }
        }
    }

    private static bool Intersects(Rect a, Rect b) =>
        a.X < b.X + b.Width &&
        a.X + a.Width > b.X &&
        a.Y < b.Y + b.Height &&
        a.Y + a.Height > b.Y;

    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = FindItem(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        e.Handled = true;
        BoxService.OpenItem(_box, item);
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Tab:
                MoveSelectionByTab((NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Delete:
                DeleteSelectedAsync();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.F2:
                RenameSelectedAsync();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Enter:
                var selected = GetSelectedItems();
                if (selected.Count > 0)
                {
                    BoxService.OpenItem(_box, selected[0]);
                    e.Handled = true;
                }

                break;
            case Windows.System.VirtualKey.F5:
                ReloadItems();
                LoadIcons();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                ItemsView.SelectedItems.Clear();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.V when (NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0:
                PasteAsync();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelectionByTab(bool backwards)
    {
        if (_items.Count == 0)
        {
            return;
        }

        var index = ItemsView.SelectedIndex;
        if (index < 0)
        {
            index = backwards ? 0 : -1;
        }

        var next = index + (backwards ? -1 : 1);
        if (next < 0 || next >= _items.Count)
        {
            return;
        }

        ItemsView.SelectedIndex = next;
        var item = _items[next];
        ItemsView.ScrollIntoView(item);
        if (ItemsView.ContainerFromItem(item) is Control container)
        {
            container.Focus(FocusState.Keyboard);
        }
    }

    // ---------------- 提示条 ----------------

    private void ShowInfo(
        string title,
        string message,
        InfoBarSeverity severity,
        string? actionText = null,
        Action? action = null,
        int autoCloseMs = 6000)
    {
        NotifyBar.Title = title;
        NotifyBar.Message = message;
        NotifyBar.Severity = severity;
        NotifyBar.ActionButton = null;

        if (!string.IsNullOrEmpty(actionText) && action is not null)
        {
            var button = new Button { Content = actionText };
            button.Click += (_, _) => action();
            NotifyBar.ActionButton = button;
        }

        NotifyBar.IsOpen = true;
        _infoTimer.Stop();
        if (autoCloseMs > 0)
        {
            _infoTimer.Interval = TimeSpan.FromMilliseconds(autoCloseMs);
            _infoTimer.Start();
        }
    }

    public bool IsHidden => _closed || AppWindow.IsVisible == false;

    /// <summary>导入过程中需要用户决策的交互。</summary>
    private sealed class BoxImportInteraction : IImportInteraction
    {
        private readonly BoxWindow _owner;
        private ConflictChoice? _batch;

        public BoxImportInteraction(BoxWindow owner) => _owner = owner;

        public void ResetBatch() => _batch = null;

        public async Task<ConflictChoice> ResolveConflictAsync(string fileName, string destinationPath)
        {
            if (_batch is { } cached)
            {
                return cached;
            }

            var (choice, applyAll) = await Dialogs.ConflictAsync(_owner.Root, fileName, destinationPath);
            if (applyAll && choice != ConflictChoice.Cancel)
            {
                _batch = choice;
            }

            return choice;
        }

        public Task<bool> ConfirmLargeFileAsync(string fileName, long sizeBytes) =>
            Dialogs.ConfirmAsync(
                _owner.Root,
                "大文件收纳",
                $"“{fileName}”大小为 {PathUtil.FormatSize(sizeBytes)}，移入收纳盒可能需要较长时间，且会占用应用数据目录空间。\n\n是否继续？",
                "继续收纳",
                "跳过");
    }
}

/// <summary>跨盒拖拽负载。</summary>
public sealed class InternalDragPayload
{
    public string BoxId { get; set; } = string.Empty;
    public List<string> ItemIds { get; set; } = new();
}
