using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace PocketDesk;

/// <summary>统一设置窗口。</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly DispatcherQueueTimer _saveTimer;
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "设置";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) =>
        {
            AppSettingsService.Current.DefaultIconSize = IconSizeSlider.Value;
            AppSettingsService.Current.DefaultCornerRadius = RadiusSlider.Value;
            AppSettingsService.Current.DefaultBackgroundOpacity = OpacitySlider.Value;
            AppSettingsService.Save();
        };

        ApplyTheme();
        LoadSettings();

        Nav.SelectedItem = Nav.MenuItems[0];
        ShowSection("general");
        Activated += OnFirstActivated;
        Closed += (_, _) => _saveTimer.Stop();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        SizeAndCenter();
    }

    private XamlRoot Root => RootGrid.XamlRoot;

    private void SizeAndCenter()
    {
        var work = DisplayArea.Primary.WorkArea;
        var width = Math.Min(DpiUtil.Scale(920), Math.Max(DpiUtil.Scale(680), work.Width - DpiUtil.Scale(160)));
        var height = Math.Min(DpiUtil.Scale(720), Math.Max(DpiUtil.Scale(520), work.Height - DpiUtil.Scale(140)));
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2,
            width,
            height));
    }

    public void ApplyTheme()
    {
        if (AccessibilityService.IsHighContrast)
        {
            SystemBackdrop = null;
            RootGrid.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        }
        else
        {
            RootGrid.Background = null;
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
            }
            else if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
        }

        var theme = AppSettingsService.Current.ThemeKind;
        RootGrid.RequestedTheme = AccessibilityService.IsHighContrast
            ? ElementTheme.Default
            : theme switch
            {
                ThemeKind.Light => ElementTheme.Light,
                ThemeKind.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
    }

    private void LoadSettings()
    {
        var s = AppSettingsService.Current;
        _loading = true;

        StartupToggle.IsOn = AutostartService.IsEnabled();
        CloseBehaviorCombo.SelectedIndex = s.CloseToTray ? 0 : 1;
        RestoreLayoutToggle.IsOn = s.RestoreLayoutOnStartup;

        MaterialCombo.SelectedIndex = s.MaterialKind switch
        {
            MaterialKind.Acrylic => 1,
            MaterialKind.Solid => 2,
            _ => 0,
        };
        ThemeCombo.SelectedIndex = s.ThemeKind switch
        {
            ThemeKind.Light => 1,
            ThemeKind.Dark => 2,
            _ => 0,
        };
        IconSizeSlider.Value = s.DefaultIconSize;
        RadiusSlider.Value = s.DefaultCornerRadius;
        OpacitySlider.Value = s.DefaultBackgroundOpacity;
        UpdateAppearanceLabels();

        ConfirmDeleteToggle.IsOn = s.ConfirmDelete;
        UndoCombo.SelectedIndex = s.UndoSeconds switch
        {
            <= 5 => 0,
            >= 30 => 2,
            _ => 1,
        };

        DataDirText.Text = AppPaths.Root;
        StorageDirText.Text = AppPaths.StorageRoot;
        VersionText.Text = $"版本 {AppInfo.Version} · {RuntimeInformation.ProcessArchitecture} · Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";

        _loading = false;
    }

    private void UpdateAppearanceLabels()
    {
        IconSizeValue.Text = $"{IconSizeSlider.Value:0} px";
        RadiusValue.Text = $"{RadiusSlider.Value:0} px";
        OpacityValue.Text = $"{OpacitySlider.Value:P0}";
    }

    private void Save()
    {
        if (_loading)
        {
            return;
        }

        AppSettingsService.Save();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            ShowSection(tag);
        }
    }

    private void ShowSection(string tag)
    {
        GeneralSection.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        BehaviorSection.Visibility = tag == "behavior" ? Visibility.Visible : Visibility.Collapsed;
        StorageSection.Visibility = tag == "storage" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = StartupToggle.IsOn;
        if (AutostartService.SetEnabled(enabled))
        {
            AppSettingsService.Current.LaunchAtStartup = enabled;
            Save();
        }
        else
        {
            _loading = true;
            StartupToggle.IsOn = !enabled;
            _loading = false;
            _ = Dialogs.ShowAsync(Root, "无法设置开机自启", "无法写入当前用户的启动项（HKCU\\...\\Run），请检查系统权限后重试。");
        }
    }

    private void OnCloseBehaviorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettingsService.Current.CloseToTray = CloseBehaviorCombo.SelectedIndex == 0;
        Save();
    }

    private void OnRestoreLayoutToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettingsService.Current.RestoreLayoutOnStartup = RestoreLayoutToggle.IsOn;
        Save();
    }

    private void OnMaterialChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettingsService.Current.Material = MaterialCombo.SelectedIndex switch
        {
            1 => "Acrylic",
            2 => "Solid",
            _ => "Mica",
        };
        Save();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettingsService.Current.Theme = ThemeCombo.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };
        Save();
        ApplyTheme();
    }

    private void OnAppearanceSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateAppearanceLabels();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnConfirmDeleteToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettingsService.Current.ConfirmDelete = ConfirmDeleteToggle.IsOn;
        Save();
    }

    private void OnUndoChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        AppSettingsService.Current.UndoSeconds = UndoCombo.SelectedIndex switch
        {
            0 => 5,
            2 => 30,
            _ => 10,
        };
        Save();
    }

    private async void OnApplyDefaultsToAll(object sender, RoutedEventArgs e)
    {
        var boxes = BoxRepository.GetAll();
        if (boxes.Count == 0)
        {
            await Dialogs.ShowAsync(Root, "没有收纳盒", "先创建一个收纳盒，再应用默认外观。");
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            Root,
            "应用默认外观",
            $"将默认外观（图标、圆角、不透明度）应用到全部 {boxes.Count} 个收纳盒。\n每个收纳盒的材质与主题覆盖会保留。",
            "应用",
            "取消");
        if (!confirmed)
        {
            return;
        }

        foreach (var box in boxes)
        {
            var appearance = BoxAppearance.FromSettings(AppSettingsService.Current);
            appearance.View = box.Appearance.View;
            appearance.Material = box.Appearance.Material;
            appearance.Theme = box.Appearance.Theme;
            BoxService.SaveAppearance(box, appearance);
            WindowManager.GetWindow(box.Id)?.ApplyGlobalAppearance();
        }

        await Dialogs.ShowAsync(Root, "已应用", $"已更新 {boxes.Count} 个收纳盒的外观。");
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e) => ShellService.Open(AppPaths.Root);

    private void OnOpenStorageDir(object sender, RoutedEventArgs e) => ShellService.Open(AppPaths.StorageRoot);

    private async void OnExportLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = LogService.ExportDiagnostics();
            ExportResultText.Text = $"已导出：{path}";
            ShellService.RevealInExplorer(path);
            await Dialogs.ShowAsync(Root, "诊断日志已导出", $"文件已保存到：\n{path}\n\n日志不包含文件内容，用户目录已脱敏。");
        }
        catch (Exception ex)
        {
            LogService.Error("导出诊断日志失败", ex);
            ExportResultText.Text = $"导出失败：{ex.Message}";
            await Dialogs.ShowAsync(Root, "导出失败", ex.Message);
        }
    }
}
