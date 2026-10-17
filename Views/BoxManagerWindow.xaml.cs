using System.Collections.ObjectModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace PocketDesk;

/// <summary>收纳盒管理器：列表、创建、显示/隐藏、排序、外观与删除。</summary>
public sealed partial class BoxManagerWindow : Window
{
    private readonly ObservableCollection<BoxRow> _rows = new();
    private bool _creating;

    public BoxManagerWindow()
    {
        InitializeComponent();
        Title = "收纳盒管理器";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }

        ApplyTheme();
        BoxesList.ItemsSource = _rows;
        BoxesList.SelectionChanged += (_, _) => UpdateButtons();
        RefreshBoxes();
        Activated += OnFirstActivated;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        SizeAndCenter();
    }

    private XamlRoot Root => RootGrid.XamlRoot;

    private BoxRow? SelectedRow => BoxesList.SelectedItem as BoxRow;

    private void SizeAndCenter()
    {
        var work = DisplayArea.Primary.WorkArea;
        var width = Math.Min(DpiUtil.Scale(1000), Math.Max(DpiUtil.Scale(720), work.Width - DpiUtil.Scale(120)));
        var height = Math.Min(DpiUtil.Scale(640), Math.Max(DpiUtil.Scale(480), work.Height - DpiUtil.Scale(120)));
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2,
            width,
            height));
    }

    public void ApplyTheme()
    {
        var theme = AppSettingsService.Current.ThemeKind;
        RootGrid.RequestedTheme = theme switch
        {
            ThemeKind.Light => ElementTheme.Light,
            ThemeKind.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    public void RefreshBoxes()
    {
        var selectedId = SelectedRow?.Id;
        var boxes = BoxRepository.GetAll();
        var counts = ItemRepository.CountAll();

        _rows.Clear();
        foreach (var box in boxes)
        {
            _rows.Add(new BoxRow(box, counts.GetValueOrDefault(box.Id)));
        }

        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BoxesList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SummaryText.Text = _rows.Count == 0
            ? string.Empty
            : $"{_rows.Count} 个收纳盒 · {boxes.Count(b => b.IsVisible)} 个显示中";

        if (selectedId is not null)
        {
            BoxesList.SelectedItem = _rows.FirstOrDefault(r => r.Id == selectedId);
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var row = SelectedRow;
        var has = row is not null;
        OpenButton.IsEnabled = has;
        RenameButton.IsEnabled = has;
        AppearanceButton.IsEnabled = has;
        ShowButton.IsEnabled = has && !row!.Box.IsVisible;
        HideButton.IsEnabled = has && row!.Box.IsVisible;
        UpButton.IsEnabled = has && BoxesList.SelectedIndex > 0;
        DownButton.IsEnabled = has && BoxesList.SelectedIndex < _rows.Count - 1;
        DeleteButton.IsEnabled = has;
    }

    private async void OnCreateNormal(object sender, RoutedEventArgs e) => await CreateAsync(BoxType.Normal);

    private async void OnCreateMapped(object sender, RoutedEventArgs e) => await CreateAsync(BoxType.Mapped);

    private async void OnCreateTarget(object sender, RoutedEventArgs e) => await CreateAsync(BoxType.Target);

    public void BeginCreate(BoxType type)
    {
        if (Root is null)
        {
            DispatcherQueue.TryEnqueue(() => BeginCreate(type));
            return;
        }

        _ = CreateAsync(type);
    }

    private async Task CreateAsync(BoxType type)
    {
        if (_creating)
        {
            return;
        }

        _creating = true;
        try
        {
            string? targetPath = null;
            var typeLabel = type switch
            {
                BoxType.Normal => "普通收纳盒",
                BoxType.Mapped => "映射收纳盒",
                _ => "目标收纳盒",
            };

            if (type == BoxType.Target)
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null)
                {
                    return;
                }

                targetPath = folder.Path;
            }

            var defaultName = type == BoxType.Target && !string.IsNullOrEmpty(targetPath)
                ? Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar))
                : $"收纳盒 {_rows.Count + 1}";

            var name = await Dialogs.PromptAsync(
                Root,
                $"新建{typeLabel}",
                defaultName,
                "收纳盒名称",
                "创建",
                validate: text =>
                {
                    if (text.Length > 60)
                    {
                        return "名称不能超过 60 个字符。";
                    }

                    return _rows.Any(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase))
                        ? "已存在同名收纳盒。"
                        : null;
                });
            if (name is null)
            {
                return;
            }

            var box = BoxService.CreateBox(name, type, targetPath);
            RefreshBoxes();
            BoxesList.SelectedItem = _rows.FirstOrDefault(r => r.Id == box.Id);
            WindowManager.ShowBox(box);
        }
        catch (Exception ex)
        {
            LogService.Error("新建收纳盒失败", ex);
            await Dialogs.ShowAsync(Root, "新建失败", ex.Message);
        }
        finally
        {
            _creating = false;
        }
    }

    private void OnBoxClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BoxRow row)
        {
            WindowManager.ShowBox(row.Box);
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
        {
            WindowManager.ShowBox(row.Box);
        }
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var name = await Dialogs.PromptAsync(
            Root,
            "重命名收纳盒",
            row.Name,
            "收纳盒名称",
            validate: text =>
            {
                if (text.Length > 60)
                {
                    return "名称不能超过 60 个字符。";
                }

                return _rows.Any(r => r.Id != row.Id && string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase))
                    ? "已存在同名收纳盒。"
                    : null;
            });
        if (name is null)
        {
            return;
        }

        BoxService.RenameBox(row.Box, name);
        RefreshBoxes();
    }

    private async void OnAppearance(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var result = await Dialogs.AppearanceAsync(Root, row.Box.Appearance, AppSettingsService.Current);
        if (result is null)
        {
            return;
        }

        BoxService.SaveAppearance(row.Box, result);
        WindowManager.GetWindow(row.Id)?.ApplyGlobalAppearance();
        RefreshBoxes();
    }

    private void OnShow(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        BoxService.SetVisible(row.Box, true);
        WindowManager.ShowBox(row.Box);
        RefreshBoxes();
    }

    private void OnHide(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        BoxService.SetVisible(row.Box, false);
        WindowManager.GetWindow(row.Id)?.HideToTray();
        RefreshBoxes();
    }

    private void OnShowAll(object sender, RoutedEventArgs e)
    {
        WindowManager.ShowAll();
        RefreshBoxes();
    }

    private void OnHideAll(object sender, RoutedEventArgs e)
    {
        WindowManager.HideAll();
        RefreshBoxes();
    }

    private void OnTile(object sender, RoutedEventArgs e)
    {
        WindowManager.TileVisibleBoxes();
        RefreshBoxes();
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var detail = row.Box.Type switch
        {
            BoxType.Normal => "收纳盒中的文件会先还原到各自的原位置，然后删除收纳盒。",
            BoxType.Mapped => "只删除引用，源文件不会被修改。",
            _ => "只解除绑定，绑定文件夹中的文件会保留。",
        };
        var confirmed = await Dialogs.ConfirmAsync(
            Root,
            "删除收纳盒",
            $"确定删除“{row.Name}”吗？\n{detail}",
            "删除",
            "取消");
        if (!confirmed)
        {
            return;
        }

        BoxService.DeleteBox(row.Box);
        WindowManager.CloseBoxWindow(row.Id);
        RefreshBoxes();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelection(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelection(1);

    private void MoveSelection(int delta)
    {
        var index = BoxesList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _rows.Count)
        {
            return;
        }

        var ids = _rows.Select(r => r.Id).ToList();
        (ids[index], ids[target]) = (ids[target], ids[index]);
        BoxRepository.Reorder(ids);
        var selectedId = _rows[index].Id;
        RefreshBoxes();
        BoxesList.SelectedItem = _rows.FirstOrDefault(r => r.Id == selectedId);
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => WindowManager.OpenSettings();
}

/// <summary>管理器列表行。</summary>
public sealed class BoxRow
{
    public BoxRow(Box box, int count)
    {
        Box = box;
        Count = count;
    }

    public Box Box { get; }

    public int Count { get; }

    public string Id => Box.Id;

    public string Name => Box.Name;

    public string TypeLabel => Box.TypeLabel;

    public string TypeGlyph => Box.TypeGlyph;

    public string CountText => $"{Count} 项";

    public string VisibilityText => Box.IsVisible ? "显示中" : "已隐藏";

    public string PathText => Box.Type switch
    {
        BoxType.Target => string.IsNullOrEmpty(Box.TargetPath) ? "（未绑定文件夹）" : Box.TargetPath,
        BoxType.Mapped => "仅保存路径引用",
        _ => AppPaths.BoxStorage(Box.Id),
    };

    public string CreatedText => Box.CreatedAt.ToString("yyyy-MM-dd HH:mm");
}
