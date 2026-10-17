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
    private bool _updatingFilter;
    private string? _groupFilter;

    public BoxManagerWindow()
    {
        InitializeComponent();
        Title = "收纳盒管理器";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        ApplyTheme();
        BoxesList.ItemsSource = _rows;
        BoxesList.SelectionChanged += (_, _) => UpdateButtons();
        RefreshGroupFilter();
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

    public void RefreshBoxes()
    {
        var selectedId = SelectedRow?.Id;
        var boxes = BoxRepository.GetAll();
        var counts = ItemRepository.CountAll();
        var groups = BoxGroupService.Load();

        _rows.Clear();
        foreach (var box in boxes.Where(b => MatchesGroupFilter(groups.GetValueOrDefault(b.Id))))
        {
            _rows.Add(new BoxRow(box, counts.GetValueOrDefault(box.Id), groups.GetValueOrDefault(box.Id)));
        }

        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BoxesList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SummaryText.Text = boxes.Count == 0
            ? string.Empty
            : $"{_rows.Count}/{boxes.Count} 个收纳盒 · {boxes.Count(b => b.IsVisible)} 个显示中";

        if (selectedId is not null)
        {
            BoxesList.SelectedItem = _rows.FirstOrDefault(r => r.Id == selectedId);
        }

        UpdateButtons();
    }

    private bool MatchesGroupFilter(string? group)
    {
        if (_groupFilter is null)
        {
            return true;
        }

        if (_groupFilter.Length == 0)
        {
            return string.IsNullOrEmpty(group);
        }

        return string.Equals(group, _groupFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshGroupFilter()
    {
        _updatingFilter = true;
        try
        {
            var previous = GroupFilter.SelectedIndex > 0 ? GroupFilter.SelectedItem as string : null;
            GroupFilter.Items.Clear();
            GroupFilter.Items.Add("全部分组");
            GroupFilter.Items.Add("未分组");
            foreach (var group in BoxGroupService.DistinctGroups())
            {
                GroupFilter.Items.Add(group);
            }

            var index = previous is null ? 0 : GroupFilter.Items.IndexOf(previous);
            GroupFilter.SelectedIndex = index >= 0 ? index : 0;
            _groupFilter = GroupFilter.SelectedIndex <= 0
                ? null
                : GroupFilter.SelectedIndex == 1
                    ? string.Empty
                    : GroupFilter.SelectedItem as string;
        }
        finally
        {
            _updatingFilter = false;
        }
    }

    private void OnGroupFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFilter)
        {
            return;
        }

        _groupFilter = GroupFilter.SelectedIndex <= 0
            ? null
            : GroupFilter.SelectedIndex == 1
                ? string.Empty
                : GroupFilter.SelectedItem as string;
        RefreshBoxes();
    }

    private void UpdateButtons()
    {
        var row = SelectedRow;
        var has = row is not null;
        OpenButton.IsEnabled = has;
        RenameButton.IsEnabled = has;
        AppearanceButton.IsEnabled = has;
        DuplicateButton.IsEnabled = has;
        GroupButton.IsEnabled = has;
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
            _updatingFilter = true;
            GroupFilter.SelectedIndex = 0;
            _groupFilter = null;
            _updatingFilter = false;
            RefreshGroupFilter();
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

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var copy = BoxService.DuplicateBox(row.Box);
        var group = BoxGroupService.Get(row.Id);
        if (!string.IsNullOrEmpty(group))
        {
            BoxGroupService.Set(copy.Id, group);
        }

        RefreshGroupFilter();
        RefreshBoxes();
        BoxesList.SelectedItem = _rows.FirstOrDefault(r => r.Id == copy.Id);
        WindowManager.ShowBox(copy);
    }

    private async void OnSetGroup(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var current = BoxGroupService.Get(row.Id) ?? string.Empty;
        var group = await Dialogs.PromptAsync(
            Root,
            "设置分组",
            current,
            "例如：工作 / 设计 / 临时（留空表示取消分组）",
            "保存",
            allowEmpty: true,
            validate: text => text.Length > 40 ? "分组名称不能超过 40 个字符。" : null);
        if (group is null)
        {
            return;
        }

        BoxGroupService.Set(row.Id, group);
        RefreshGroupFilter();
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
        RefreshGroupFilter();
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
    public BoxRow(Box box, int count, string? group)
    {
        Box = box;
        Count = count;
        GroupName = group ?? string.Empty;
    }

    public Box Box { get; }

    public int Count { get; }

    public string GroupName { get; }

    public string GroupText => string.IsNullOrEmpty(GroupName) ? "未分组" : GroupName;

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
