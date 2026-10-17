using System.Text;

namespace PocketDesk;

/// <summary>文件移动与 journal。所有会改变用户文件位置的操作都必须经过这里。</summary>
public static class FileSystemService
{
    public const string TempSuffix = ".pocketdesk.tmp";

    /// <summary>带 journal 的移动：先写 PENDING，再执行移动，成功后标记 DONE。</summary>
    public static void MoveWithJournal(
        string from,
        string to,
        JournalOpType opType,
        string? boxId = null,
        string? itemId = null)
    {
        if (!PathUtil.Exists(from))
        {
            throw new FileNotFoundException("源文件不存在。", from);
        }

        if (!PathUtil.SamePath(from, to) && PathUtil.Exists(to))
        {
            throw new IOException($"目标位置已存在同名项：{Path.GetFileName(to)}");
        }

        var entry = new JournalEntry
        {
            OpType = opType,
            BoxId = boxId,
            ItemId = itemId,
            FromPath = from,
            ToPath = to,
        };
        JournalRepository.Add(entry);

        try
        {
            MoveEntry(from, to);
            JournalRepository.SetState(entry.Id, JournalState.Done);
        }
        catch (Exception ex)
        {
            LogService.Error($"移动失败：{opType} {Path.GetFileName(from)} -> {Path.GetFileName(to)}", ex);
            try
            {
                // 依据磁盘真实状态收敛，避免留下“半移动”。
                Resolve(entry, isStartupRecovery: false);
            }
            catch (Exception recoveryError)
            {
                LogService.Error("失败后的状态收敛也失败了，保留 journal 待下次启动恢复", recoveryError);
            }

            throw;
        }
    }

    public static void MoveEntry(string from, string to)
    {
        var sameVolume = PathUtil.IsSameVolume(from, to);
        if (Directory.Exists(from))
        {
            if (sameVolume)
            {
                Directory.Move(from, to);
            }
            else
            {
                StagedMoveDirectory(from, to);
            }

            return;
        }

        if (sameVolume)
        {
            File.Move(from, to);
        }
        else
        {
            StagedMoveFile(from, to);
        }
    }

    private static void StagedMoveFile(string from, string to)
    {
        var temp = to + TempSuffix;
        TryDelete(temp);
        File.Copy(from, temp, overwrite: true);
        var sourceLength = new FileInfo(from).Length;
        var copiedLength = new FileInfo(temp).Length;
        if (sourceLength != copiedLength)
        {
            TryDelete(temp);
            throw new IOException("跨卷复制校验失败，源文件未被删除。");
        }

        File.Move(temp, to);
        File.Delete(from);
    }

    private static void StagedMoveDirectory(string from, string to)
    {
        var temp = to + TempSuffix;
        TryDeleteDirectory(temp);
        CopyDirectory(from, temp);
        if (PathUtil.GetSizeOrCount(from) != PathUtil.GetSizeOrCount(temp))
        {
            TryDeleteDirectory(temp);
            throw new IOException("跨卷目录复制校验失败，源目录未被删除。");
        }

        Directory.Move(temp, to);
        Directory.Delete(from, recursive: true);
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>把文件恢复到首次收纳前的原位置；原位置被占用或不存在时回退桌面并加 restored 后缀。</summary>
    public static string RestoreToOriginal(string storedPath, string? originalPath)
    {
        if (!PathUtil.Exists(storedPath))
        {
            throw new FileNotFoundException("收纳盒中的文件已不存在，无法还原。", storedPath);
        }

        var name = Path.GetFileName(storedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(originalPath))
        {
            return MoveToDesktopFallback(storedPath, name);
        }

        var directory = Path.GetDirectoryName(originalPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return MoveToDesktopFallback(storedPath, name);
        }

        var target = originalPath;
        if (PathUtil.Exists(target))
        {
            var ext = Path.GetExtension(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            target = PathUtil.UniquePathIn(directory, $"{stem} (restored){ext}");
        }

        if (!PathUtil.SamePath(storedPath, target))
        {
            MoveWithJournal(storedPath, target, JournalOpType.Restore);
        }

        return target;
    }

    private static string MoveToDesktopFallback(string storedPath, string name)
    {
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var target = PathUtil.UniquePathIn(AppPaths.DesktopPath, $"{stem} (restored){ext}");
        MoveWithJournal(storedPath, target, JournalOpType.Restore);
        LogService.Warn($"原位置不可用，已还原到桌面：{name}");
        return target;
    }

    public static int Recycle(params string[] paths)
    {
        var valid = paths.Where(PathUtil.Exists).ToArray();
        if (valid.Length == 0)
        {
            return 0;
        }

        var from = string.Join('\0', valid) + '\0';
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            wFunc = NativeMethods.FO_DELETE,
            pFrom = from,
            fFlags = NativeMethods.FOF_ALLOWUNDO |
                     NativeMethods.FOF_NOCONFIRMATION |
                     NativeMethods.FOF_SILENT |
                     NativeMethods.FOF_NOERRORUI,
        };
        var result = NativeMethods.SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
        {
            LogService.Warn($"回收站删除未完全成功，返回码 {result}");
        }

        return valid.Count(p => !PathUtil.Exists(p));
    }

    public static bool WriteUrlShortcut(string directory, string url, string? fileName, out string createdPath)
    {
        Directory.CreateDirectory(directory);
        var name = PathUtil.SanitizeFileNameForUrl(fileName ?? url);
        var target = PathUtil.UniquePathIn(directory, $"{name}.url");
        var builder = new StringBuilder()
            .AppendLine("[InternetShortcut]")
            .AppendLine($"URL={url}")
            .AppendLine();
        File.WriteAllText(target, builder.ToString(), Encoding.UTF8);
        createdPath = target;
        return true;
    }

    /// <summary>
    /// 启动时恢复未完成的移动。返回 (恢复数, 回滚数, 失败数)。
    /// </summary>
    public static (int Recovered, int RolledBack, int Failed) RecoverPending()
    {
        var pending = JournalRepository.GetPending();
        int recovered = 0, rolledBack = 0, failed = 0;
        foreach (var entry in pending)
        {
            try
            {
                var state = Resolve(entry, isStartupRecovery: true);
                if (state == JournalState.Done)
                {
                    recovered++;
                }
                else
                {
                    rolledBack++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                LogService.Error($"journal 恢复失败（{entry.OpType}）：{ex.GetType().Name}", ex);
            }
        }

        CleanupTempFiles();
        JournalRepository.Cleanup(TimeSpan.FromDays(7));
        return (recovered, rolledBack, failed);
    }

    private static JournalState Resolve(JournalEntry entry, bool isStartupRecovery)
    {
        var source = entry.FromPath;
        var destination = entry.ToPath;
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination))
        {
            JournalRepository.SetState(entry.Id, JournalState.RolledBack);
            return JournalState.RolledBack;
        }

        var sourceExists = PathUtil.Exists(source);
        var destinationExists = PathUtil.Exists(destination);

        if (!sourceExists && destinationExists)
        {
            JournalRepository.SetState(entry.Id, JournalState.Done);
            return JournalState.Done;
        }

        if (!destinationExists)
        {
            // 目标不存在：移动没有发生（或已被清理），源仍在则视为回滚。
            JournalRepository.SetState(entry.Id, JournalState.RolledBack);
            return JournalState.RolledBack;
        }

        // 源与目标同时存在：比较内容规模，决定完成还是清理。
        var sourceSize = PathUtil.GetSizeOrCount(source);
        var destinationSize = PathUtil.GetSizeOrCount(destination);
        var identical = sourceSize >= 0 && sourceSize == destinationSize;

        if (entry.OpType == JournalOpType.MoveIn || entry.OpType == JournalOpType.Rename)
        {
            if (identical)
            {
                // 复制已完成，只差删除源。
                if (TryDeleteEntry(source))
                {
                    JournalRepository.SetState(entry.Id, JournalState.Done);
                    return JournalState.Done;
                }
            }
            else
            {
                // 目标是不完整副本，丢弃它，保留源。
                TryDeleteEntry(destination);
            }
        }
        else
        {
            // MoveOut / Restore：目标是用户可见位置，保留目标，删除收纳盒内的副本。
            if (identical && TryDeleteEntry(source))
            {
                JournalRepository.SetState(entry.Id, JournalState.Done);
                return JournalState.Done;
            }
        }

        JournalRepository.SetState(entry.Id, JournalState.RolledBack);
        if (isStartupRecovery && !identical)
        {
            LogService.Warn($"journal 目标内容不完整，已保留源并清理目标：{entry.OpType}");
        }

        return JournalState.RolledBack;
    }

    private static bool TryDeleteEntry(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("清理路径失败", ex);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void CleanupTempFiles()
    {
        foreach (var root in new[] { AppPaths.StorageRoot, AppPaths.DesktopPath })
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var temp in Directory.EnumerateFiles(root, "*" + TempSuffix, SearchOption.AllDirectories))
                {
                    TryDelete(temp);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"清理临时文件失败：{ex.Message}");
            }
        }
    }
}
