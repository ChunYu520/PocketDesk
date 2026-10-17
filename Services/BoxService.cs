using System.Diagnostics;

namespace PocketDesk;

/// <summary>收纳盒与收纳项的业务操作（创建、删除、重命名、打开、排序、状态校验）。</summary>
public static class BoxService
{
    public static event Action? BoxesChanged;
    public static event Action<string>? BoxContentChanged;

    public static void NotifyBoxesChanged() => BoxesChanged?.Invoke();

    public static void NotifyBoxContentChanged(string boxId)
    {
        BoxContentChanged?.Invoke(boxId);
        ImportService.NotifyBoxChanged(boxId);
    }

    public static List<Box> GetBoxes() => BoxRepository.GetAll();

    public static Box? GetBox(string id) => BoxRepository.Get(id);

    public static List<BoxItem> GetItems(Box box, bool refreshMetadata = true)
    {
        var items = ItemRepository.GetByBox(box.Id);
        if (box.Type == BoxType.Target)
        {
            SyncTargetBox(box, items);
            items = ItemRepository.GetByBox(box.Id);
        }

        if (refreshMetadata)
        {
            foreach (var item in items)
            {
                RefreshState(item);
            }
        }

        var sorted = Sort(items, box);
        ApplyAppearance(sorted, box.Appearance);
        return sorted;
    }

    public static Box CreateBox(string name, BoxType type, string? targetPath)
    {
        if (type == BoxType.Target && (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath)))
        {
            throw new InvalidOperationException("目标收纳盒需要选择一个已存在的文件夹。");
        }

        var boxes = BoxRepository.GetAll();
        var finalName = string.IsNullOrWhiteSpace(name) ? NextDefaultName(boxes) : name.Trim();
        var appearance = BoxAppearance.FromSettings(AppSettingsService.Current);
        var index = boxes.Count;
        var placement = DefaultPlacement(index);
        var box = new Box
        {
            Name = finalName,
            Type = type,
            TargetPath = type == BoxType.Target ? PathUtil.Normalize(targetPath!) : null,
            Appearance = appearance,
            Placement = placement,
            ZOrder = BoxRepository.NextZOrder(),
            IsPinned = false,
            IsVisible = true,
            SortField = "name",
            CreatedAt = DateTime.Now,
        };
        BoxRepository.Insert(box);
        LogService.Info($"已创建{box.TypeLabel}：{box.Name}");
        BoxesChanged?.Invoke();
        return box;
    }

    private static string NextDefaultName(List<Box> boxes)
    {
        var index = boxes.Count + 1;
        while (boxes.Any(b => string.Equals(b.Name, $"收纳盒 {index}", StringComparison.OrdinalIgnoreCase)))
        {
            index++;
        }

        return $"收纳盒 {index}";
    }

    private static WindowPlacement DefaultPlacement(int index)
    {
        var display = Microsoft.UI.Windowing.DisplayArea.Primary;
        var work = display.WorkArea;
        var width = DpiUtil.Scale(360);
        var height = DpiUtil.Scale(320);
        var offset = (index % 5) * DpiUtil.Scale(28);
        return new WindowPlacement
        {
            Width = width,
            Height = height,
            Left = work.X + (work.Width - width) / 2 + offset,
            Top = work.Y + (work.Height - height) / 2 - DpiUtil.Scale(40) + offset,
            MonitorDevice = display.DisplayId.Value.ToString(),
            State = "Normal",
        };
    }

    /// <summary>复制收纳盒：只复制外观/类型/排序等配置，不复制收纳项与文件。</summary>
    public static Box DuplicateBox(Box source)
    {
        var boxes = BoxRepository.GetAll();
        var baseName = $"{source.Name} 副本";
        var name = baseName;
        var index = 2;
        while (boxes.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} {index++}";
        }

        var copy = new Box
        {
            Name = name,
            Type = source.Type,
            TargetPath = source.TargetPath,
            Appearance = source.Appearance.Clone(),
            Placement = null,
            ZOrder = BoxRepository.NextZOrder(),
            IsPinned = source.IsPinned,
            IsVisible = true,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            CreatedAt = DateTime.Now,
        };
        BoxRepository.Insert(copy);
        LogService.Info($"已复制收纳盒：{source.Name} -> {copy.Name}");
        BoxesChanged?.Invoke();
        return copy;
    }

    public static void RenameBox(Box box, string newName)    {
        var trimmed = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("收纳盒名称不能为空。");
        }

        box.Name = trimmed;
        BoxRepository.SetName(box.Id, trimmed);
        BoxesChanged?.Invoke();
    }

    public static void SetPinned(Box box, bool pinned)
    {
        box.IsPinned = pinned;
        BoxRepository.SetPinned(box.Id, pinned);
        BoxesChanged?.Invoke();
    }

    public static void SetVisible(Box box, bool visible)
    {
        box.IsVisible = visible;
        BoxRepository.SetVisibility(box.Id, visible);
        BoxesChanged?.Invoke();
    }

    public static void SetSort(Box box, string field, bool descending)
    {
        box.SortField = field;
        box.SortDescending = descending;
        BoxRepository.SetSort(box.Id, field, descending);
        BoxContentChanged?.Invoke(box.Id);
    }

    public static void SaveAppearance(Box box, BoxAppearance appearance)
    {
        box.Appearance = appearance;
        BoxRepository.SetAppearance(box.Id, appearance);
        BoxContentChanged?.Invoke(box.Id);
    }

    public static void SavePlacement(Box box, WindowPlacement? placement)
    {
        box.Placement = placement;
        BoxRepository.SetPlacement(box.Id, placement);
    }

    /// <summary>删除收纳盒：普通盒文件先原位还原；映射/目标盒只移除引用。</summary>
    public static void DeleteBox(Box box)
    {
        var items = ItemRepository.GetByBox(box.Id);        foreach (var item in items)
        {
            try
            {
                if (box.Type == BoxType.Normal && !string.IsNullOrEmpty(item.StoredPath))
                {
                    if (item.Kind == ItemKind.Url)
                    {
                        FileSystemService.Recycle(item.StoredPath);
                    }
                    else if (PathUtil.Exists(item.StoredPath))
                    {
                        FileSystemService.RestoreToOriginal(item.StoredPath, item.SourcePath);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"删除收纳盒时还原失败：{item.DisplayName}", ex);
            }
        }

        ItemRepository.DeleteByBox(box.Id);
        BoxRepository.Delete(box.Id);
        BoxGroupService.Remove(box.Id);

        try
        {
            var storage = AppPaths.BoxStorage(box.Id);
            if (Directory.Exists(storage))
            {
                Directory.Delete(storage, recursive: true);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"清理收纳盒存储目录失败：{ex.Message}");
        }

        LogService.Info($"已删除收纳盒：{box.Name}");
        BoxesChanged?.Invoke();
    }

    public static void OpenItem(Box box, BoxItem item)
    {
        if (item.Kind == ItemKind.Url)
        {
            var target = item.ShortcutTarget ?? item.SourcePath;
            if (!string.IsNullOrEmpty(target) &&
                (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                ShellService.OpenUrl(target);
                return;
            }
        }

        if (!item.HasRealPath || !PathUtil.Exists(item.RealPath))
        {
            RefreshState(item);
            return;
        }

        ShellService.Open(item.RealPath);
    }

    public static void RevealItem(Box box, BoxItem item)
    {
        if (item.Kind == ItemKind.Url && item.IsReference)
        {
            return;
        }

        ShellService.RevealInExplorer(item.RealPath);
    }

    /// <summary>删除收纳项。返回可用于撤销的信息。</summary>
    public static List<DeletedItemInfo> DeleteItems(Box box, IReadOnlyList<BoxItem> items)
    {
        var deleted = new List<DeletedItemInfo>();
        foreach (var item in items)
        {
            try
            {
                string? restorePath = null;
                if (box.Type == BoxType.Normal && !string.IsNullOrEmpty(item.StoredPath))
                {
                    if (item.Kind == ItemKind.Url)
                    {
                        if (PathUtil.Exists(item.StoredPath))
                        {
                            FileSystemService.Recycle(item.StoredPath);
                        }
                    }
                    else if (PathUtil.Exists(item.StoredPath))
                    {
                        restorePath = FileSystemService.RestoreToOriginal(item.StoredPath, item.SourcePath);
                    }
                }
                else if (box.Type == BoxType.Target && item.Kind != ItemKind.Url && PathUtil.Exists(item.RealPath))
                {
                    // 目标盒绑定真实文件夹：删除即移动到回收站，与资源管理器一致。
                    FileSystemService.Recycle(item.RealPath);
                }

                ItemRepository.Delete(item.Id);
                deleted.Add(new DeletedItemInfo
                {
                    BoxId = box.Id,
                    DisplayName = item.DisplayName,
                    Kind = item.Kind,
                    WasReference = item.IsReference,
                    OriginalSource = item.SourcePath,
                    RestoredPath = restorePath ?? (item.IsReference ? item.SourcePath : null),
                    DeletedAt = DateTime.Now,
                });
            }
            catch (Exception ex)
            {
                LogService.Error($"删除收纳项失败：{item.DisplayName}", ex);
            }
        }

        if (deleted.Count > 0)
        {
            NotifyBoxContentChanged(box.Id);
        }

        return deleted;
    }

    public static async Task UndoDeleteAsync(Box box, IReadOnlyList<DeletedItemInfo> deleted)
    {
        var iconPaths = new List<string>();
        foreach (var info in deleted)
        {
            if (info.Kind == ItemKind.Url && info.WasReference && !string.IsNullOrEmpty(info.OriginalSource))
            {
                await ImportService.ImportUrlAsync(box, info.OriginalSource, SilentImportInteraction.Instance).ConfigureAwait(true);
                continue;
            }

            if (!string.IsNullOrEmpty(info.RestoredPath) && PathUtil.Exists(info.RestoredPath))
            {
                iconPaths.Add(info.RestoredPath);
            }
        }

        if (iconPaths.Count > 0)
        {
            await ImportService.ImportPathsAsync(box, iconPaths, SilentImportInteraction.Instance).ConfigureAwait(true);
        }

        NotifyBoxContentChanged(box.Id);
    }

    /// <summary>重命名收纳项。普通盒改存储文件；映射/目标盒改源文件（AC-02）。</summary>
    public static void RenameItem(Box box, BoxItem item, string newName)
    {
        var trimmed = PathUtil.SanitizeFileName(newName.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("名称不能为空。");
        }

        if (!item.HasRealPath)
        {
            item.DisplayName = trimmed;
            ItemRepository.Update(item);
            NotifyBoxContentChanged(box.Id);
            return;
        }

        if (item.Kind == ItemKind.Url && item.IsReference)
        {
            item.DisplayName = trimmed;
            ItemRepository.Update(item);
            NotifyBoxContentChanged(box.Id);
            return;
        }

        var currentPath = item.RealPath;
        var isDirectory = Directory.Exists(currentPath);
        var extension = Path.GetExtension(currentPath);
        var requestedHasExtension = !string.IsNullOrEmpty(Path.GetExtension(trimmed));
        var fileName = isDirectory || requestedHasExtension ? trimmed : trimmed + extension;
        if (string.Equals(fileName, Path.GetFileName(currentPath), StringComparison.Ordinal))
        {
            item.DisplayName = PathUtil.GetDisplayName(fileName);
            ItemRepository.Update(item);
            NotifyBoxContentChanged(box.Id);
            return;
        }

        var directory = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("无法确定文件所在目录。");
        }

        var destination = Path.Combine(directory, fileName);
        if (PathUtil.Exists(destination) && !PathUtil.SamePath(destination, currentPath))
        {
            throw new IOException($"该位置已存在同名项：{fileName}");
        }

        if (!string.IsNullOrEmpty(item.StoredPath))
        {
            FileSystemService.MoveWithJournal(currentPath, destination, JournalOpType.Rename, box.Id, item.Id);
            item.StoredPath = destination;
        }
        else
        {
            FileSystemService.MoveEntry(currentPath, destination);
            item.SourcePath = destination;
        }

        item.DisplayName = PathUtil.GetDisplayName(destination);
        if (item.Kind == ItemKind.Shortcut)
        {
            var shortcut = ShortcutService.Resolve(destination);
            item.ShortcutTarget = shortcut.TargetPath is { Length: > 0 } target
                ? string.IsNullOrEmpty(shortcut.Arguments) ? target : $"{target} {shortcut.Arguments}"
                : null;
        }

        ItemRepository.Update(item);
        IconService.Invalidate(destination);
        IconService.Invalidate(currentPath);
        NotifyBoxContentChanged(box.Id);
        LogService.Info($"已重命名收纳项：{Path.GetFileName(currentPath)} -> {fileName}");
    }

    /// <summary>跨盒移动收纳项（普通→普通或映射→映射）。</summary>
    public static (int Moved, string? Error) MoveItemsToBox(Box source, Box target, IReadOnlyList<BoxItem> items)
    {
        if (source.Id == target.Id)
        {
            return (0, L.SameBoxDrop);
        }

        if (source.Type != target.Type)
        {
            return (0, "暂不支持在普通盒与映射/目标盒之间移动收纳项，请重新拖入。");
        }

        if (source.Type == BoxType.Target)
        {
            return (0, "目标收纳盒的内容由绑定文件夹决定，暂不支持跨盒移动。");
        }

        var moved = 0;
        foreach (var item in items)
        {
            try
            {
                if (source.Type == BoxType.Normal)
                {
                    var storage = AppPaths.BoxStorage(target.Id);
                    Directory.CreateDirectory(storage);
                    var fileName = Path.GetFileName(item.RealPath);
                    var destination = PathUtil.UniquePathIn(storage, fileName);
                    FileSystemService.MoveWithJournal(item.RealPath, destination, JournalOpType.MoveIn, target.Id, item.Id);
                    item.StoredPath = destination;
                    item.DisplayName = Path.GetFileName(destination);
                }

                var previousBox = item.BoxId;
                item.BoxId = target.Id;
                item.OriginalBoxId = previousBox;
                item.SortIndex = ItemRepository.MaxSortIndex(target.Id) + 1;
                ItemRepository.Update(item);
                moved++;
            }
            catch (Exception ex)
            {
                LogService.Error($"跨盒移动失败：{item.DisplayName}", ex);
            }
        }

        if (moved > 0)
        {
            NotifyBoxContentChanged(source.Id);
            NotifyBoxContentChanged(target.Id);
        }

        return (moved, moved == 0 ? "没有收纳项被移动。" : null);
    }

    /// <summary>自定义排序：把某一项上移/下移一格。</summary>
    public static bool MoveItemOrder(Box box, BoxItem item, int delta)
    {
        var ordered = ItemRepository.GetByBox(box.Id)
            .OrderBy(i => i.SortIndex)
            .ThenBy(i => i.AddedAt)
            .ToList();
        var index = ordered.FindIndex(i => i.Id == item.Id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Count || delta == 0)
        {
            return false;
        }

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortIndex = i;
        }

        ItemRepository.UpdateSortIndices(ordered);
        NotifyBoxContentChanged(box.Id);
        return true;
    }

    public static void RefreshState(BoxItem item)
    {
        item.IsMissing = false;
        item.MissingReason = null;

        if (item.Kind == ItemKind.Url && item.IsReference)
        {
            var target = item.ShortcutTarget ?? item.SourcePath;
            item.IsMissing = string.IsNullOrWhiteSpace(target);
            item.MissingReason = item.IsMissing ? "网址为空" : null;
            return;
        }

        if (string.IsNullOrEmpty(item.RealPath))
        {
            item.IsMissing = true;
            item.MissingReason = L.PathMissing;
            return;
        }

        if (!PathUtil.Exists(item.RealPath))
        {
            item.IsMissing = true;
            item.MissingReason = PathUtil.IsUnc(item.RealPath) ? "网络路径不可达" : L.PathMissing;
            return;
        }

        try
        {
            if (Directory.Exists(item.RealPath))
            {
                item.ModifiedAt = Directory.GetLastWriteTime(item.RealPath);
            }
            else
            {
                var info = new FileInfo(item.RealPath);
                item.ModifiedAt = info.LastWriteTime;
                item.Size = info.Length;
            }
        }
        catch
        {
            // 元数据失败不影响可用性判断。
        }
    }

    public static void ApplyAppearance(IReadOnlyList<BoxItem> items, BoxAppearance appearance)
    {
        foreach (var item in items)
        {
            item.IconSize = Math.Clamp(appearance.IconSize, 24, 128);
            item.NameLines = Math.Clamp(appearance.NameLines, 0, 3);
        }
    }

    public static List<BoxItem> Sort(IReadOnlyList<BoxItem> items, Box box)
    {
        var field = Enum.TryParse<BoxSortField>(box.SortField, true, out var parsed) ? parsed : BoxSortField.Name;
        IEnumerable<BoxItem> ordered = field switch
        {
            BoxSortField.Type => items.OrderBy(i => i.Kind).ThenBy(i => i.DisplayName, NaturalComparer.Instance),
            BoxSortField.ModifiedTime => items.OrderByDescending(i => i.ModifiedAt ?? DateTime.MinValue),
            BoxSortField.AddedTime => items.OrderBy(i => i.AddedAt),
            BoxSortField.Custom => items.OrderBy(i => i.SortIndex).ThenBy(i => i.AddedAt),
            _ => items.OrderBy(i => i.DisplayName, NaturalComparer.Instance),
        };

        if (box.SortDescending && field is not (BoxSortField.ModifiedTime or BoxSortField.AddedTime))
        {
            ordered = ordered.Reverse();
        }

        return ordered.ToList();
    }

    /// <summary>目标盒与绑定文件夹做轻量同步（P1 能力，只处理顶层项）。</summary>
    private static void SyncTargetBox(Box box, List<BoxItem> existing)
    {
        if (string.IsNullOrEmpty(box.TargetPath) || !Directory.Exists(box.TargetPath))
        {
            return;
        }

        try
        {
            var known = existing
                .Where(i => !string.IsNullOrEmpty(i.SourcePath))
                .Select(i => PathUtil.Normalize(i.SourcePath!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = new List<string>();
            candidates.AddRange(Directory.EnumerateFiles(box.TargetPath));
            candidates.AddRange(Directory.EnumerateDirectories(box.TargetPath));

            foreach (var path in candidates)
            {
                var name = Path.GetFileName(path);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var normalized = PathUtil.Normalize(path);
                if (known.Contains(normalized))
                {
                    continue;
                }

                var item = new BoxItem
                {
                    BoxId = box.Id,
                    Kind = PathUtil.DetectKind(path),
                    DisplayName = PathUtil.GetDisplayName(path),
                    SourcePath = normalized,
                    SortIndex = ItemRepository.MaxSortIndex(box.Id) + 1,
                };
                if (item.Kind == ItemKind.Shortcut)
                {
                    var shortcut = ShortcutService.Resolve(path);
                    item.ShortcutTarget = shortcut.TargetPath;
                }

                RefreshState(item);
                ItemRepository.Insert(item);
                known.Add(normalized);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("目标收纳盒同步失败", ex);
        }
    }

    public static long GetTotalSize(IEnumerable<BoxItem> items) => items.Sum(i => i.Size);

    public static void ShowItemProperties(Box box, BoxItem item, nint hwnd)
    {
        ShellService.ShowProperties(hwnd, item.HasRealPath ? item.RealPath : null);
    }

    /// <summary>打开数据目录（设置页使用）。</summary>
    public static void OpenDataDirectory() => ShellService.Open(AppPaths.Root);

    public static Process? CurrentProcess => Process.GetCurrentProcess();
}

/// <summary>已删除收纳项，供撤销使用。</summary>
public sealed class DeletedItemInfo
{
    public required string BoxId { get; init; }
    public required string DisplayName { get; init; }
    public ItemKind Kind { get; init; }
    public bool WasReference { get; init; }
    public string? OriginalSource { get; init; }
    public string? RestoredPath { get; init; }
    public DateTime DeletedAt { get; init; }
}

internal sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        try
        {
            return NativeMethods.StrCmpLogicalW(x, y);
        }
        catch
        {
            return string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
