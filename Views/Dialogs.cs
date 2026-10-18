using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PocketDesk;

/// <summary>通用对话框。全部使用 WinUI 3 ContentDialog，保证原生样式与键盘可导航。</summary>
public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(
        XamlRoot root,
        string title,
        string message,
        string primaryText = "确定",
        string closeText = "取消")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task ShowAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
            },
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    public static async Task<string?> PromptAsync(
        XamlRoot root,
        string title,
        string initial,
        string placeholder,
        string primaryText = "保存",
        Func<string, string?>? validate = null,
        bool multiline = false,
        bool allowEmpty = false)
    {
        var box = new TextBox
        {
            Text = initial,
            PlaceholderText = placeholder,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height = multiline ? 120 : double.NaN,
            SelectionStart = multiline ? 0 : initial.Length,
        };
        var error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Spacing = 8, Width = 380 };
        panel.Children.Add(box);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = panel,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        string? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var text = box.Text.Trim();
            var problem = string.IsNullOrWhiteSpace(text) && !allowEmpty
                ? "名称不能为空。"
                : validate?.Invoke(text);
            if (problem is not null)
            {
                args.Cancel = true;
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                return;
            }

            result = text;
        };

        await dialog.ShowAsync();
        return result;
    }

    public static async Task<(ConflictChoice Choice, bool ApplyToAll)> ConflictAsync(
        XamlRoot root,
        string fileName,
        string destinationPath)
    {        var choice = ConflictChoice.Skip;
        var applyAll = new CheckBox
        {
            Content = "对后续同名项使用相同选择",
            IsChecked = false,
        };
        var cancel = new HyperlinkButton
        {
            Content = "取消本次批量收纳",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
        };
        var panel = new StackPanel { Spacing = 12, Width = 430 };
        panel.Children.Add(new TextBlock
        {
            Text = $"“{fileName}”已存在于收纳盒中。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "保留两者会生成带序号的新名称；替换会先把旧文件还原到它的原位置，再放入新文件。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75,
        });
        panel.Children.Add(applyAll);
        panel.Children.Add(cancel);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "同名冲突",
            Content = panel,
            PrimaryButtonText = "保留两者",
            SecondaryButtonText = "替换",
            CloseButtonText = "跳过",
            DefaultButton = ContentDialogButton.Primary,
        };

        cancel.Click += (_, _) =>
        {
            choice = ConflictChoice.Cancel;
            dialog.Hide();
        };

        var result = await dialog.ShowAsync();
        if (choice != ConflictChoice.Cancel)
        {
            choice = result switch
            {
                ContentDialogResult.Primary => ConflictChoice.KeepBoth,
                ContentDialogResult.Secondary => ConflictChoice.Replace,
                _ => ConflictChoice.Skip,
            };
        }

        return (choice, applyAll.IsChecked == true && choice != ConflictChoice.Cancel);
    }

    public static async Task<BoxAppearance?> AppearanceAsync(
        XamlRoot root,
        BoxAppearance current,
        AppSettings settings)
    {
        var appearance = current.Clone();

        var material = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "跟随全局", "Mica", "亚克力", "纯色" },
            SelectedIndex = appearance.Material switch
            {
                "Mica" => 1,
                "Acrylic" => 2,
                "Solid" => 3,
                _ => 0,
            },
        };
        var theme = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "跟随系统", "浅色", "深色" },
            SelectedIndex = appearance.Theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0,
            },
        };
        var view = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "网格", "列表" },
            SelectedIndex = appearance.ViewMode == BoxViewMode.List ? 1 : 0,
        };
        var iconSlider = new Slider { Minimum = 24, Maximum = 96, StepFrequency = 4, Value = appearance.IconSize };
        var iconValue = new TextBlock { Text = $"{appearance.IconSize:0} px", FontSize = 12, Opacity = 0.7 };
        iconSlider.ValueChanged += (_, e) => iconValue.Text = $"{e.NewValue:0} px";

        var radiusSlider = new Slider { Minimum = 0, Maximum = 24, StepFrequency = 1, Value = appearance.CornerRadius };
        var radiusValue = new TextBlock { Text = $"{appearance.CornerRadius:0} px", FontSize = 12, Opacity = 0.7 };
        radiusSlider.ValueChanged += (_, e) => radiusValue.Text = $"{e.NewValue:0} px";

        var backgroundSlider = new Slider { Minimum = 0.2, Maximum = 1, StepFrequency = 0.05, Value = appearance.BackgroundOpacity };
        var backgroundValue = new TextBlock { Text = $"{appearance.BackgroundOpacity:P0}", FontSize = 12, Opacity = 0.7 };
        backgroundSlider.ValueChanged += (_, e) => backgroundValue.Text = $"{e.NewValue:P0}";

        var borderSlider = new Slider { Minimum = 0, Maximum = 1, StepFrequency = 0.05, Value = appearance.BorderOpacity };
        var borderValue = new TextBlock { Text = $"{appearance.BorderOpacity:P0}", FontSize = 12, Opacity = 0.7 };
        borderSlider.ValueChanged += (_, e) => borderValue.Text = $"{e.NewValue:P0}";

        var columnsSlider = new Slider { Minimum = 0, Maximum = 9, StepFrequency = 1, Value = appearance.Columns };
        var columnsValue = new TextBlock { Text = appearance.Columns == 0 ? "自适应" : $"{appearance.Columns} 列", FontSize = 12, Opacity = 0.7 };
        columnsSlider.ValueChanged += (_, e) =>
            columnsValue.Text = e.NewValue < 0.5 ? "自适应" : $"{e.NewValue:0} 列";

        var lines = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "隐藏文件名", "一行", "两行", "三行" },
            SelectedIndex = Math.Clamp(appearance.NameLines, 0, 3),
        };

        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(Labeled("材质", material));
        panel.Children.Add(Labeled("主题", theme));
        panel.Children.Add(Labeled("视图", view));
        panel.Children.Add(Labeled("图标大小", new StackPanel { Children = { iconSlider, iconValue } }));
        panel.Children.Add(Labeled("外框圆角", new StackPanel { Children = { radiusSlider, radiusValue } }));
        panel.Children.Add(Labeled("背景不透明度", new StackPanel { Children = { backgroundSlider, backgroundValue } }));
        panel.Children.Add(Labeled("网格列数", new StackPanel { Children = { columnsSlider, columnsValue } }));
        panel.Children.Add(Labeled("文件名显示", lines));

        var hint = new TextBlock
        {
            Text = $"全局默认值：图标 {settings.DefaultIconSize:0} px、圆角 {settings.DefaultCornerRadius:0} px、不透明度 {settings.DefaultBackgroundOpacity:P0}。",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "收纳盒外观",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "保存",
            SecondaryButtonText = "恢复默认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        BoxAppearance? result = null;
        var resetRequested = false;
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            resetRequested = true;
            dialog.Hide();
        };

        dialog.PrimaryButtonClick += (_, _) =>
        {
            appearance.Material = material.SelectedIndex switch
            {
                1 => "Mica",
                2 => "Acrylic",
                3 => "Solid",
                _ => null,
            };
            appearance.Theme = theme.SelectedIndex switch
            {
                1 => "Light",
                2 => "Dark",
                _ => null,
            };
            appearance.View = view.SelectedIndex == 1 ? "list" : "grid";
            appearance.IconSize = iconSlider.Value;
            appearance.CornerRadius = radiusSlider.Value;
            appearance.BackgroundOpacity = backgroundSlider.Value;
            appearance.Columns = (int)Math.Round(columnsSlider.Value);
            appearance.NameLines = lines.SelectedIndex;
            result = appearance;
        };

        await dialog.ShowAsync();
        if (resetRequested)
        {
            return BoxAppearance.FromSettings(settings);
        }

        return result;
    }

    private static Windows.UI.Color ParseHexColor(string? value)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('#');
        try
        {
            if (text.Length == 8)
            {
                return Windows.UI.Color.FromArgb(
                    Convert.ToByte(text[..2], 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16),
                    Convert.ToByte(text.Substring(6, 2), 16));
            }

            if (text.Length == 6)
            {
                return Windows.UI.Color.FromArgb(
                    255,
                    Convert.ToByte(text[..2], 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16));
            }
        }
        catch
        {
            // 使用默认黑色。
        }

        return Microsoft.UI.Colors.Black;
    }

    /// <summary>批量重命名：前缀 + 递增序号，可选保留扩展名。</summary>
    public static async Task<BatchRenameOptions?> BatchRenameAsync(XamlRoot root, IReadOnlyList<string> sampleNames)
    {
        var prefixBox = new TextBox { Text = "文件_", PlaceholderText = "名称前缀" };
        var startBox = new NumberBox
        {
            Value = 1,
            Minimum = 1,
            Maximum = 9999,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var digitsBox = new ComboBox
        {
            ItemsSource = new[] { "1 位", "2 位", "3 位", "4 位" },
            SelectedIndex = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var keepExtension = new CheckBox { Content = "保留原扩展名", IsChecked = true };
        var preview = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            FontSize = 12,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(new TextBlock { Text = "名称前缀", FontSize = 12, Opacity = 0.75 });
        panel.Children.Add(prefixBox);
        panel.Children.Add(new TextBlock { Text = "起始序号", FontSize = 12, Opacity = 0.75 });
        panel.Children.Add(startBox);
        panel.Children.Add(new TextBlock { Text = "序号位数", FontSize = 12, Opacity = 0.75 });
        panel.Children.Add(digitsBox);
        panel.Children.Add(keepExtension);
        panel.Children.Add(new TextBlock
        {
            Text = "按当前显示顺序重命名。普通盒会改动存储文件，映射盒会同步重命名源文件。",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(preview);
        panel.Children.Add(error);

        void UpdatePreview()
        {
            var prefix = prefixBox.Text ?? string.Empty;
            var start = (int)Math.Max(1, startBox.Value);
            var digits = digitsBox.SelectedIndex + 1;
            var keep = keepExtension.IsChecked == true;
            var examples = new List<string>();
            for (var i = 0; i < Math.Min(3, sampleNames.Count); i++)
            {
                var source = sampleNames[i];
                var ext = keep ? Path.GetExtension(source) : string.Empty;
                examples.Add($"{prefix}{(start + i).ToString("D" + digits)}{ext}");
            }

            preview.Text = examples.Count == 0
                ? string.Empty
                : $"预览：{string.Join("、", examples)}{(sampleNames.Count > 3 ? " …" : string.Empty)}";
        }

        prefixBox.TextChanged += (_, _) => UpdatePreview();
        startBox.ValueChanged += (_, _) => UpdatePreview();
        digitsBox.SelectionChanged += (_, _) => UpdatePreview();
        keepExtension.Checked += (_, _) => UpdatePreview();
        keepExtension.Unchecked += (_, _) => UpdatePreview();
        UpdatePreview();

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"批量重命名（{sampleNames.Count} 项）",
            Content = panel,
            PrimaryButtonText = "重命名",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        BatchRenameOptions? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var prefix = prefixBox.Text ?? string.Empty;
            if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                args.Cancel = true;
                error.Text = "前缀包含不允许的字符。";
                error.Visibility = Visibility.Visible;
                return;
            }

            result = new BatchRenameOptions
            {
                Prefix = prefix,
                StartNumber = (int)Math.Max(1, startBox.Value),
                Digits = Math.Clamp(digitsBox.SelectedIndex + 1, 1, 4),
                KeepExtension = keepExtension.IsChecked == true,
            };
        };

        await dialog.ShowAsync();
        return result;
    }

    private static StackPanel Labeled(string label, UIElement control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 });
        panel.Children.Add(control);
        return panel;
    }
}

/// <summary>批量重命名选项。</summary>
public sealed class BatchRenameOptions
{
    public string Prefix { get; init; } = string.Empty;
    public int StartNumber { get; init; } = 1;
    public int Digits { get; init; } = 1;
    public bool KeepExtension { get; init; } = true;
}
