using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace PocketDesk;

/// <summary>收纳盒类型。</summary>
public enum BoxType
{
    /// <summary>文件真实移入应用数据目录。</summary>
    Normal = 0,

    /// <summary>只存绝对路径引用，绝不改动源文件。</summary>
    Mapped = 1,

    /// <summary>绑定现有文件夹，双向同步。</summary>
    Target = 2,
}

/// <summary>收纳项类型。</summary>
public enum ItemKind
{
    File = 0,
    Folder = 1,
    Shortcut = 2,
    Url = 3,
}

/// <summary>排序字段。</summary>
public enum BoxSortField
{
    Name,
    Type,
    ModifiedTime,
    AddedTime,
    Custom,
}

/// <summary>视图模式。</summary>
public enum BoxViewMode
{
    Grid,
    List,
}

/// <summary>材质。</summary>
public enum MaterialKind
{
    Mica,
    Acrylic,
    Solid,
}

/// <summary>主题覆盖。</summary>
public enum ThemeKind
{
    System,
    Light,
    Dark,
}

/// <summary>文件移动 journal 操作类型。</summary>
public enum JournalOpType
{
    MoveIn = 0,
    MoveOut = 1,
    Restore = 2,
    DeleteBox = 3,
    Rename = 4,
}

/// <summary>journal 状态。</summary>
public enum JournalState
{
    Pending = 0,
    Done = 1,
    RolledBack = 2,
}

/// <summary>同名冲突处理策略。</summary>
public enum ConflictChoice
{
    KeepBoth,
    Replace,
    Skip,
    Cancel,
}

/// <summary>收纳盒外观配置（按盒独立持久化）。</summary>
public sealed class BoxAppearance
{
    public double CornerRadius { get; set; } = 12;
    public double BackgroundOpacity { get; set; } = 0.88;
    public double BorderOpacity { get; set; } = 0.30;
    public double IconSize { get; set; } = 48;
    public int Columns { get; set; } // 0 = 自适应
    public int NameLines { get; set; } = 2;
    public bool ShowItemCount { get; set; } = true;
    public string View { get; set; } = "grid";
    public string? Material { get; set; }
    public string? Theme { get; set; }

    public BoxViewMode ViewMode =>
        string.Equals(View, "list", StringComparison.OrdinalIgnoreCase) ? BoxViewMode.List : BoxViewMode.Grid;

    public BoxAppearance Clone() => (BoxAppearance)MemberwiseClone();

    public static BoxAppearance FromSettings(AppSettings s) => new()
    {
        CornerRadius = s.DefaultCornerRadius,
        BackgroundOpacity = s.DefaultBackgroundOpacity,
        BorderOpacity = s.DefaultBorderOpacity,
        IconSize = s.DefaultIconSize,
        Columns = s.DefaultColumns,
        NameLines = s.DefaultNameLines,
        View = "grid",
    };
}

/// <summary>窗口位置与尺寸（像素，来自 AppWindow）。</summary>
public sealed class WindowPlacement
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; } = 360;
    public int Height { get; set; } = 280;
    public string? MonitorDevice { get; set; }
    public double DpiScale { get; set; } = 1.0;
    public string State { get; set; } = "Normal";

    [JsonIgnore]
    public bool IsRolledUp => string.Equals(State, "RolledUp", StringComparison.OrdinalIgnoreCase);
}

/// <summary>收纳盒实体。</summary>
public sealed class Box
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新收纳盒";
    public BoxType Type { get; set; } = BoxType.Normal;
    public string? TargetPath { get; set; }
    public BoxAppearance Appearance { get; set; } = new();
    public WindowPlacement? Placement { get; set; }
    public int ZOrder { get; set; }
    public bool IsPinned { get; set; }
    public bool IsVisible { get; set; } = true;
    public string SortField { get; set; } = "name";
    public bool SortDescending { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string TypeLabel => Type switch
    {
        BoxType.Normal => "普通收纳盒",
        BoxType.Mapped => "映射收纳盒",
        _ => "目标收纳盒",
    };

    [JsonIgnore]
    public string TypeGlyph => Type switch
    {
        BoxType.Normal => "\uE8B7",   // folder
        BoxType.Mapped => "\uE71B",   // link
        _ => "\uE8B7",
    };
}

/// <summary>收纳项。实现 INotifyPropertyChanged 以支持图标异步加载与外观变更。</summary>
public sealed class BoxItem : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private ImageSource? _icon;
    private double _iconSize = 48;
    private int _nameLines = 2;
    private bool _isMissing;
    private string? _missingReason;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BoxId { get; set; } = string.Empty;
    public ItemKind Kind { get; set; }
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetField(ref _displayName, value))
            {
                OnPropertyChanged(nameof(TooltipText));
                OnPropertyChanged(nameof(TypeHint));
            }
        }
    }

    public string? SourcePath { get; set; }
    public string? StoredPath { get; set; }
    public string? IconCacheKey { get; set; }
    public double SortIndex { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public string? OriginalBoxId { get; set; }
    public bool IsLink { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public long Size { get; set; }
    public string? ShortcutTarget { get; set; }

    [JsonIgnore]
    public string RealPath => StoredPath ?? SourcePath ?? string.Empty;

    [JsonIgnore]
    public bool HasRealPath => !string.IsNullOrEmpty(RealPath);

    [JsonIgnore]
    public bool IsReference => string.IsNullOrEmpty(StoredPath);

    /// <summary>拖出时使用的 WinRT 存储对象（异步预缓存，避免拖拽开始时阻塞 UI）。</summary>
    [JsonIgnore]
    public IStorageItem? StorageItem { get; set; }

    [JsonIgnore]
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetField(ref _icon, value))
            {
                OnPropertyChanged(nameof(PlaceholderVisibility));
            }
        }
    }

    /// <summary>图标未加载/加载失败时显示占位字形；加载成功后隐藏，避免叠在真实图标下面。</summary>
    [JsonIgnore]
    public Visibility PlaceholderVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public double IconSize
    {
        get => _iconSize;
        set => SetField(ref _iconSize, value);
    }

    [JsonIgnore]
    public int NameLines
    {
        get => _nameLines;
        set
        {
            if (SetField(ref _nameLines, value))
            {
                OnPropertyChanged(nameof(NameVisibility));
            }
        }
    }

    [JsonIgnore]
    public Visibility NameVisibility => _nameLines <= 0 ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (SetField(ref _isMissing, value))
            {
                OnPropertyChanged(nameof(MissingVisibility));
            }
        }
    }

    [JsonIgnore]
    public string? MissingReason
    {
        get => _missingReason;
        set => SetField(ref _missingReason, value);
    }

    [JsonIgnore]
    public Visibility MissingVisibility => _isMissing ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility LinkVisibility => IsLink ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public string TooltipText
    {
        get
        {
            var lines = new List<string> { DisplayName };
            if (!string.IsNullOrEmpty(RealPath))
            {
                lines.Add(RealPath);
            }

            if (!string.IsNullOrEmpty(ShortcutTarget))
            {
                lines.Add($"目标：{ShortcutTarget}");
            }

            if (Kind == ItemKind.Folder)
            {
                lines.Add("文件夹");
            }

            if (IsLink)
            {
                lines.Add("链接（仅存引用）");
            }

            if (ModifiedAt is { } modified)
            {
                lines.Add($"修改时间：{modified:yyyy-MM-dd HH:mm}");
            }

            if (IsMissing)
            {
                lines.Add($"不可用：{MissingReason ?? "路径不存在"}");
            }

            if (IsReference && !IsLink)
            {
                lines.Add("映射引用（不改动源文件）");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    [JsonIgnore]
    public string KindGlyph => Kind switch
    {
        ItemKind.Folder => "\uE8B7",
        ItemKind.Shortcut => "\uE71B",
        ItemKind.Url => "\uE774",
        _ => "\uE7C3",
    };

    /// <summary>列表视图的第二行提示。</summary>
    [JsonIgnore]
    public string TypeHint
    {
        get
        {
            if (Kind == ItemKind.Url && !string.IsNullOrEmpty(ShortcutTarget))
            {
                return ShortcutTarget!;
            }

            var parent = string.IsNullOrEmpty(RealPath) ? null : Path.GetDirectoryName(RealPath);
            var kind = Kind switch
            {
                ItemKind.Folder => "文件夹",
                ItemKind.Shortcut => "快捷方式",
                ItemKind.Url => "网址",
                _ => Size > 0 ? PathUtil.FormatSize(Size) : "文件",
            };
            return string.IsNullOrEmpty(parent) ? kind : $"{kind} · {parent}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>操作日志条目，用于崩溃恢复。</summary>
public sealed class JournalEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public JournalOpType OpType { get; set; }
    public string? BoxId { get; set; }
    public string? ItemId { get; set; }
    public string? FromPath { get; set; }
    public string? ToPath { get; set; }
    public JournalState State { get; set; } = JournalState.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
}

/// <summary>应用级设置。</summary>
public sealed class AppSettings
{
    public bool LaunchAtStartup { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool RestoreLayoutOnStartup { get; set; } = true;
    public string Language { get; set; } = "zh-CN";

    public string Material { get; set; } = "Mica";
    public string Theme { get; set; } = "System";
    public bool FollowAccentColor { get; set; } = true;
    public double DefaultIconSize { get; set; } = 48;
    public double DefaultCornerRadius { get; set; } = 12;
    public double DefaultBackgroundOpacity { get; set; } = 0.88;
    public double DefaultBorderOpacity { get; set; } = 0.30;
    public int DefaultColumns { get; set; }
    public int DefaultNameLines { get; set; } = 2;

    public bool ConfirmDelete { get; set; } = true;
    public int UndoSeconds { get; set; } = 10;
    public double HoverExpandDelayMs { get; set; } = 600;
    public bool DoubleClickToOpen { get; set; } = true;
    public bool FirstRunCompleted { get; set; }
    public string? DefaultBoxId { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public MaterialKind MaterialKind => Enum.TryParse<MaterialKind>(Material, true, out var m) ? m : MaterialKind.Mica;

    public ThemeKind ThemeKind
    {
        get
        {
            if (Enum.TryParse<ThemeKind>(Theme, true, out var t))
            {
                return t;
            }

            return string.Equals(Theme, "Dark", StringComparison.OrdinalIgnoreCase) ? ThemeKind.Dark
                : string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase) ? ThemeKind.Light
                : ThemeKind.System;
        }
    }
}

/// <summary>批量导入/移动的单项结果。</summary>
public sealed class ImportResult
{
    public required string SourcePath { get; init; }
    public bool Success { get; init; }
    public bool Skipped { get; init; }
    public string? Error { get; init; }
    public string? ItemName { get; init; }
}

/// <summary>界面文案集中定义（后续可迁移到 .resw 资源）。</summary>
public static class L
{
    public const string DeleteConfirmTitle = "删除确认";
    public const string Undo = "撤销";
    public const string Deleted = "已删除";
    public const string RestoredToOriginal = "文件已还原到原位置。";
    public const string ReferenceRemoved = "已移除引用，源文件未改动。";
    public const string LinkRestored = "链接已还原。";
    public const string ImportFailed = "导入失败";
    public const string DropHint = "把文件拖到这里";
    public const string EmptyHint = "拖文件到这里，或右键新建";
    public const string CloudRejected = "OneDrive 云占位文件不受支持：请使用映射收纳盒，或先让文件下载到本地。";
    public const string UncRejected = "普通收纳盒不支持网络路径，请改用映射收纳盒。";
    public const string RecycleBinRejected = "请先从回收站还原文件，再拖入收纳盒。";
    public const string ShortcutResolveFailed = "快捷方式已失效";
    public const string PathMissing = "路径不存在";
    public const string NotSupportedCrossBox = "暂不支持把该类型的收纳项移动到目标收纳盒。";
    public const string SameBoxDrop = "收纳项已经在当前收纳盒中。";
}
