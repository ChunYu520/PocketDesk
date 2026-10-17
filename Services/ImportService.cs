namespace PocketDesk;

/// <summary>导入过程中需要用户决策的交互（由 UI 层实现）。</summary>
public interface IImportInteraction
{
    Task<ConflictChoice> ResolveConflictAsync(string fileName, string destinationPath);

    Task<bool> ConfirmLargeFileAsync(string fileName, long sizeBytes);
}

/// <summary>无交互的默认策略（用于撤销恢复等后台场景）。</summary>
public sealed class SilentImportInteraction : IImportInteraction
{
    public static readonly SilentImportInteraction Instance = new();

    public Task<ConflictChoice> ResolveConflictAsync(string fileName, string destinationPath) =>
        Task.FromResult(ConflictChoice.KeepBoth);

    public Task<bool> ConfirmLargeFileAsync(string fileName, long sizeBytes) => Task.FromResult(true);
}

/// <summary>把外部文件/文件夹/URL 收纳进收纳盒。所有普通盒文件移动都带 journal。</summary>
public static class ImportService
{
    private const long LargeFileThreshold = 2L * 1024 * 1024 * 1024;

    private static readonly SemaphoreSlim ImportGate = new(1, 1);

    public static event Action<string>? BoxChanged;

    public static void NotifyBoxChanged(string boxId) => BoxChanged?.Invoke(boxId);

    public static async Task<List<ImportResult>> ImportPathsAsync(
        Box box,
        IReadOnlyList<string> rawPaths,
        IImportInteraction interaction)
    {
        var results = new List<ImportResult>();
        var canceled = false;
        await ImportGate.WaitAsync().ConfigureAwait(true);
        try
        {
            foreach (var raw in rawPaths
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (canceled)
                {
                    results.Add(new ImportResult
                    {
                        SourcePath = raw,
                        Success = false,
                        Skipped = true,
                        Error = "用户取消了批量收纳。",
                    });
                    continue;
                }

                try
                {
                    var path = PathUtil.Normalize(raw);
                    var guard = ValidateSource(box, path);
                    if (guard is not null)
                    {
                        results.Add(new ImportResult
                        {
                            SourcePath = raw,
                            Success = false,
                            Skipped = true,
                            Error = guard,
                        });
                        continue;
                    }

                    var outcome = box.Type switch
                    {
                        BoxType.Normal => await ImportIntoNormalAsync(box, path, interaction).ConfigureAwait(true),
                        BoxType.Mapped => ImportReference(box, path),
                        _ => (Result: await ImportIntoTargetAsync(box, path).ConfigureAwait(true), Choice: ConflictChoice.KeepBoth),
                    };

                    if (outcome.Choice == ConflictChoice.Cancel)
                    {
                        canceled = true;
                    }

                    results.Add(outcome.Result);
                }
                catch (Exception ex)
                {
                    LogService.Error($"收纳失败：{Path.GetFileName(raw)}", ex);
                    results.Add(new ImportResult
                    {
                        SourcePath = raw,
                        Success = false,
                        Error = FriendlyError(ex),
                        ItemName = Path.GetFileName(raw),
                    });
                }
            }
        }
        finally
        {
            ImportGate.Release();
        }

        if (results.Any(r => r.Success))
        {
            NotifyBoxChanged(box.Id);
        }

        return results;
    }

    public static async Task<List<ImportResult>> ImportUrlAsync(
        Box box,
        string url,
        IImportInteraction interaction)
    {
        var results = new List<ImportResult>();
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                results.Add(new ImportResult
                {
                    SourcePath = url,
                    Success = false,
                    Error = "只支持 http/https 网址。",
                });
                return results;
            }

            if (box.Type == BoxType.Normal)
            {
                var storage = AppPaths.BoxStorage(box.Id);
                Directory.CreateDirectory(storage);
                var baseName = PathUtil.SanitizeFileNameForUrl(url);
                var destination = Path.GetFullPath(Path.Combine(storage, baseName + ".url"));
                if (PathUtil.Exists(destination))
                {
                    var choice = await interaction.ResolveConflictAsync(Path.GetFileName(destination), destination).ConfigureAwait(true);
                    if (choice is ConflictChoice.Skip)
                    {
                        results.Add(new ImportResult { SourcePath = url, Success = false, Skipped = true, Error = "已跳过。" });
                        return results;
                    }

                    if (choice is ConflictChoice.Cancel)
                    {
                        results.Add(new ImportResult { SourcePath = url, Success = false, Skipped = true, Error = "已取消。" });
                        return results;
                    }

                    destination = PathUtil.UniquePathIn(storage, Path.GetFileName(destination));
                }

                FileSystemService.WriteUrlShortcut(
                    Path.GetDirectoryName(destination)!,
                    url,
                    Path.GetFileNameWithoutExtension(destination),
                    out var created);
                // WriteUrlShortcut 会做唯一化，若结果与预期不同则以实际创建路径为准。
                if (!PathUtil.SamePath(created, destination))
                {
                    destination = created;
                }

                var item = new BoxItem
                {
                    BoxId = box.Id,
                    Kind = ItemKind.Url,
                    DisplayName = Path.GetFileName(destination),
                    SourcePath = url,
                    StoredPath = destination,
                    SortIndex = ItemRepository.MaxSortIndex(box.Id) + 1,
                    ShortcutTarget = url,
                    ModifiedAt = DateTime.Now,
                };
                ItemRepository.Insert(item);
                results.Add(new ImportResult { SourcePath = url, Success = true, ItemName = item.DisplayName });
            }
            else if (box.Type == BoxType.Mapped)
            {
                var existing = ItemRepository.GetByBox(box.Id)
                    .Any(i => i.Kind == ItemKind.Url && string.Equals(i.SourcePath, url, StringComparison.OrdinalIgnoreCase));
                if (existing)
                {
                    results.Add(new ImportResult { SourcePath = url, Success = false, Skipped = true, Error = "该网址已在收纳盒中。" });
                    return results;
                }

                var item = new BoxItem
                {
                    BoxId = box.Id,
                    Kind = ItemKind.Url,
                    DisplayName = url,
                    SourcePath = url,
                    SortIndex = ItemRepository.MaxSortIndex(box.Id) + 1,
                    ShortcutTarget = url,
                    ModifiedAt = DateTime.Now,
                };
                ItemRepository.Insert(item);
                results.Add(new ImportResult { SourcePath = url, Success = true, ItemName = url });
            }
            else
            {
                if (string.IsNullOrEmpty(box.TargetPath) || !Directory.Exists(box.TargetPath))
                {
                    results.Add(new ImportResult { SourcePath = url, Success = false, Error = "目标文件夹不可用。" });
                    return results;
                }

                FileSystemService.WriteUrlShortcut(box.TargetPath, url, null, out var created);
                var item = new BoxItem
                {
                    BoxId = box.Id,
                    Kind = ItemKind.Url,
                    DisplayName = Path.GetFileName(created),
                    SourcePath = created,
                    SortIndex = ItemRepository.MaxSortIndex(box.Id) + 1,
                    ShortcutTarget = url,
                    ModifiedAt = DateTime.Now,
                };
                ItemRepository.Insert(item);
                results.Add(new ImportResult { SourcePath = url, Success = true, ItemName = item.DisplayName });
            }

            NotifyBoxChanged(box.Id);
        }
        catch (Exception ex)
        {
            LogService.Error("网址收纳失败", ex);
            results.Add(new ImportResult { SourcePath = url, Success = false, Error = FriendlyError(ex) });
        }

        return results;
    }

    private static string? ValidateSource(Box box, string path)
    {
        if (!PathUtil.Exists(path))
        {
            return "路径不存在。";
        }

        if (PathUtil.IsRecycleBin(path))
        {
            return L.RecycleBinRejected;
        }

        if (box.Type == BoxType.Normal)
        {
            if (PathUtil.IsUnc(path))
            {
                return L.UncRejected;
            }

            if (PathUtil.IsCloudPlaceholder(path))
            {
                return L.CloudRejected;
            }

            if (path.Length > 259)
            {
                return "路径过长（超过 259 个字符），请缩短路径后再收纳。";
            }
        }

        return null;
    }

    private static async Task<(ImportResult Result, ConflictChoice Choice)> ImportIntoNormalAsync(
        Box box,
        string sourcePath,
        IImportInteraction interaction)
    {
        var fileName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return (new ImportResult { SourcePath = sourcePath, Success = false, Error = "无法识别文件名。" }, ConflictChoice.Skip);
        }

        var storage = AppPaths.BoxStorage(box.Id);
        Directory.CreateDirectory(storage);

        // 链接 / 目录联接：只存引用，不跟随、不复制。
        if (PathUtil.IsSymlink(sourcePath))
        {
            var linkItem = BuildReferenceItem(box, sourcePath, isLink: true);
            ItemRepository.Insert(linkItem);
            return (new ImportResult { SourcePath = sourcePath, Success = true, ItemName = linkItem.DisplayName }, ConflictChoice.KeepBoth);
        }

        if (File.Exists(sourcePath) && new FileInfo(sourcePath).Length > LargeFileThreshold)
        {
            var proceed = await interaction.ConfirmLargeFileAsync(fileName, new FileInfo(sourcePath).Length).ConfigureAwait(true);
            if (!proceed)
            {
                return (new ImportResult { SourcePath = sourcePath, Success = false, Skipped = true, Error = "已跳过大文件。" }, ConflictChoice.Skip);
            }
        }

        var destination = Path.GetFullPath(Path.Combine(storage, fileName));
        if (PathUtil.Exists(destination))
        {
            var choice = await interaction.ResolveConflictAsync(fileName, destination).ConfigureAwait(true);
            switch (choice)
            {
                case ConflictChoice.Skip:
                    return (new ImportResult { SourcePath = sourcePath, Success = false, Skipped = true, Error = "同名冲突，已跳过。" }, ConflictChoice.Skip);
                case ConflictChoice.Cancel:
                    return (new ImportResult { SourcePath = sourcePath, Success = false, Skipped = true, Error = "已取消。" }, ConflictChoice.Cancel);
                case ConflictChoice.KeepBoth:
                    destination = PathUtil.UniquePathIn(storage, fileName);
                    break;
                case ConflictChoice.Replace:
                    RestoreExistingStoredItem(box.Id, destination);
                    break;
            }
        }

        var item = new BoxItem
        {
            BoxId = box.Id,
            Kind = PathUtil.DetectKind(sourcePath),
            DisplayName = Path.GetFileName(destination),
            SourcePath = sourcePath,
            StoredPath = destination,
            SortIndex = ItemRepository.MaxSortIndex(box.Id) + 1,
            IsLink = false,
        };
        FillMetadata(item, sourcePath);
        item.DisplayName = Path.GetFileName(destination);

        try
        {
            FileSystemService.MoveWithJournal(sourcePath, destination, JournalOpType.MoveIn, box.Id, item.Id);
            ItemRepository.Insert(item);
        }
        catch
        {
            // 移动已失败或已回滚；若目标存在但未入库，尽量还原，避免文件被“吞掉”。
            if (PathUtil.Exists(destination) && !PathUtil.Exists(sourcePath))
            {
                try
                {
                    FileSystemService.MoveEntry(destination, sourcePath);
                }
                catch (Exception ex)
                {
                    LogService.Error("导入失败后的回退也失败，文件保留在收纳盒存储目录中", ex);
                }
            }

            throw;
        }

        return (new ImportResult { SourcePath = sourcePath, Success = true, ItemName = item.DisplayName }, ConflictChoice.KeepBoth);
    }

    private static (ImportResult Result, ConflictChoice Choice) ImportReference(Box box, string sourcePath)
    {
        var existing = ItemRepository.GetByBox(box.Id)
            .Any(i => PathUtil.SamePath(i.SourcePath, sourcePath));
        if (existing)
        {
            return (new ImportResult
            {
                SourcePath = sourcePath,
                Success = false,
                Skipped = true,
                Error = "该路径已在收纳盒中。",
            }, ConflictChoice.Skip);
        }

        var item = BuildReferenceItem(box, sourcePath, PathUtil.IsSymlink(sourcePath));
        ItemRepository.Insert(item);
        return (new ImportResult { SourcePath = sourcePath, Success = true, ItemName = item.DisplayName }, ConflictChoice.KeepBoth);
    }

    private static async Task<ImportResult> ImportIntoTargetAsync(Box box, string sourcePath)
    {
        if (string.IsNullOrEmpty(box.TargetPath) || !Directory.Exists(box.TargetPath))
        {
            return new ImportResult { SourcePath = sourcePath, Success = false, Error = "目标文件夹不可用。" };
        }

        var target = PathUtil.Normalize(box.TargetPath);
        var parent = Path.GetDirectoryName(PathUtil.Normalize(sourcePath));
        if (string.Equals(parent, target, StringComparison.OrdinalIgnoreCase))
        {
            var reference = BuildReferenceItem(box, sourcePath, PathUtil.IsSymlink(sourcePath));
            ItemRepository.Insert(reference);
            return new ImportResult { SourcePath = sourcePath, Success = true, ItemName = reference.DisplayName };
        }

        var fileName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destination = Path.Combine(target, fileName);
        if (PathUtil.Exists(destination))
        {
            destination = PathUtil.UniquePathIn(target, fileName);
        }

        var item = BuildReferenceItem(box, destination, PathUtil.IsSymlink(sourcePath));
        item.DisplayName = Path.GetFileName(destination);
        await Task.Run(() => FileSystemService.MoveWithJournal(sourcePath, destination, JournalOpType.MoveIn, box.Id, item.Id))
            .ConfigureAwait(true);
        ItemRepository.Insert(item);
        return new ImportResult { SourcePath = sourcePath, Success = true, ItemName = item.DisplayName };
    }

    private static BoxItem BuildReferenceItem(Box box, string path, bool isLink)
    {
        var kind = PathUtil.DetectKind(path);
        var item = new BoxItem
        {
            BoxId = box.Id,
            Kind = kind,
            DisplayName = PathUtil.GetDisplayName(path),
            SourcePath = path,
            StoredPath = null,
            SortIndex = ItemRepository.MaxSortIndex(box.Id) + 1,
            IsLink = isLink,
        };
        FillMetadata(item, path);
        if (kind == ItemKind.Shortcut)
        {
            var shortcut = ShortcutService.Resolve(path);
            item.ShortcutTarget = shortcut.TargetPath is { Length: > 0 } target
                ? string.IsNullOrEmpty(shortcut.Arguments) ? target : $"{target} {shortcut.Arguments}"
                : null;
        }
        else if (kind == ItemKind.Url)
        {
            item.ShortcutTarget = ShortcutService.Resolve(path).Url;
        }

        return item;
    }

    private static void FillMetadata(BoxItem item, string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                item.ModifiedAt = info.LastWriteTime;
                item.Size = 0;
            }
            else if (File.Exists(path))
            {
                var info = new FileInfo(path);
                item.ModifiedAt = info.LastWriteTime;
                item.Size = info.Length;
            }
        }
        catch
        {
            // 元数据失败不影响收纳。
        }
    }

    /// <summary>替换时先把手上的旧文件还原到它的原位置，避免“替换”直接销毁用户文件。</summary>
    private static void RestoreExistingStoredItem(string boxId, string storedPath)
    {
        var existing = ItemRepository.GetByBox(boxId)
            .FirstOrDefault(i => PathUtil.SamePath(i.StoredPath, storedPath));
        if (existing is null)
        {
            if (PathUtil.Exists(storedPath))
            {
                FileSystemService.Recycle(storedPath);
            }

            return;
        }

        try
        {
            if (existing.Kind == ItemKind.Url)
            {
                FileSystemService.Recycle(storedPath);
            }
            else
            {
                FileSystemService.RestoreToOriginal(storedPath, existing.SourcePath);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"替换前还原旧文件失败：{ex.Message}");
            FileSystemService.Recycle(storedPath);
        }

        ItemRepository.Delete(existing.Id);
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "没有权限访问该文件或文件夹。",
        PathTooLongException => "路径过长，请缩短后重试。",
        IOException io => $"文件操作失败：{io.Message}",
        _ => $"操作失败：{ex.Message}",
    };
}
