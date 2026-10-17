using Microsoft.Data.Sqlite;

namespace PocketDesk;

/// <summary>卸载流程：还原文件 / 保留数据 / 删除全部数据。由安装包卸载器调用。</summary>
public static class UninstallService
{
    public enum Mode
    {
        Restore,
        Keep,
        Delete,
    }

    public static int Run(string modeText)
    {
        var mode = modeText.ToLowerInvariant() switch
        {
            "restore" => Mode.Restore,
            "delete" => Mode.Delete,
            _ => Mode.Keep,
        };

        try
        {
            LogService.Initialize();
            LogService.Info($"卸载流程开始：{mode}");

            AutostartService.SetEnabled(false);

            if (mode is Mode.Restore)
            {
                var (restored, failed) = RestoreNormalBoxItems();
                LogService.Info($"卸载还原：成功 {restored}，失败 {failed}");
                if (failed > 0)
                {
                    NativeMethods.MessageBox(
                        IntPtr.Zero,
                        $"有 {failed} 个文件未能还原到原位置，数据目录已保留。\n\n" +
                        "请检查原目录权限后，重新运行卸载或手动从数据目录取回文件。",
                        "PocketDesk 卸载",
                        NativeMethods.MB_OK | NativeMethods.MB_ICONWARNING);
                    return 1;
                }
            }

            if (mode is Mode.Restore or Mode.Delete)
            {
                DeleteDataDirectory();
            }

            LogService.Info("卸载流程完成");
            return 0;
        }
        catch (Exception ex)
        {
            LogService.Error("卸载流程失败", ex);
            NativeMethods.MessageBox(
                IntPtr.Zero,
                $"卸载时清理数据失败：{ex.Message}\n\n数据目录未被删除，可在重新安装后继续使用。",
                "PocketDesk 卸载",
                NativeMethods.MB_OK | NativeMethods.MB_ICONWARNING);
            return 1;
        }
    }

    private static (int Restored, int Failed) RestoreNormalBoxItems()
    {
        if (!File.Exists(AppPaths.DatabasePath))
        {
            return (0, 0);
        }

        Database.Initialize();
        AppSettingsService.Load();

        var restored = 0;
        var failed = 0;
        foreach (var box in BoxRepository.GetAll())
        {
            if (box.Type != BoxType.Normal)
            {
                continue;
            }

            foreach (var item in ItemRepository.GetByBox(box.Id))
            {
                if (string.IsNullOrEmpty(item.StoredPath) || !PathUtil.Exists(item.StoredPath))
                {
                    continue;
                }

                try
                {
                    if (item.Kind == ItemKind.Url)
                    {
                        FileSystemService.Recycle(item.StoredPath);
                    }
                    else
                    {
                        FileSystemService.RestoreToOriginal(item.StoredPath, item.SourcePath);
                    }

                    restored++;
                }
                catch (Exception ex)
                {
                    failed++;
                    LogService.Error($"卸载还原失败：{item.DisplayName}", ex);
                }
            }
        }

        return (restored, failed);
    }

    private static void DeleteDataDirectory()
    {
        var root = Path.GetFullPath(AppPaths.Root).TrimEnd(Path.DirectorySeparatorChar);
        if (!root.EndsWith("PocketDesk", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Warn($"数据目录不在预期位置，跳过删除：{root}");
            return;
        }

        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // 至少尝试删除数据库，避免下次启动读到半套数据。
            LogService.Error("删除数据目录失败", ex);
            try
            {
                foreach (var file in new[]
                         {
                             AppPaths.DatabasePath,
                             AppPaths.DatabasePath + "-shm",
                             AppPaths.DatabasePath + "-wal",
                         })
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch
            {
                // ignore
            }

            throw;
        }
    }
}
