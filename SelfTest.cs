using System.Text;

namespace PocketDesk;

/// <summary>
/// 无界面自测（--selftest）：覆盖收纳、还原、撤销、重命名、同名冲突与 journal 恢复。
/// 仅用于开发验证，不参与正常启动流程。
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var report = new StringBuilder();
        var passed = 0;
        var failed = 0;

        void Check(string name, bool condition, string? detail = null)
        {
            if (condition)
            {
                passed++;
                report.AppendLine($"PASS  {name}");
            }
            else
            {
                failed++;
                report.AppendLine($"FAIL  {name}{(detail is null ? string.Empty : $"  ({detail})")}");
            }
        }

        var source = Path.Combine(Path.GetTempPath(), "PocketDeskSelfTest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            AppPaths.EnsureCreated();
            Database.Initialize();
            AppSettingsService.Load();

            Directory.CreateDirectory(source);
            var fileA = Path.Combine(source, "alpha.txt");
            var fileB = Path.Combine(source, "beta.txt");
            var folderC = Path.Combine(source, "gamma");
            File.WriteAllText(fileA, "alpha");
            File.WriteAllText(fileB, "beta");
            Directory.CreateDirectory(folderC);
            File.WriteAllText(Path.Combine(folderC, "inner.txt"), "inner");

            var box = BoxService.CreateBox("自测普通盒", BoxType.Normal, null);
            var results = await ImportService.ImportPathsAsync(
                box, new[] { fileA, fileB, folderC }, SilentImportInteraction.Instance);
            Check("普通盒导入 3 项成功", results.Count(r => r.Success) == 3,
                string.Join("; ", results.Where(r => !r.Success).Select(r => r.Error)));

            var items = BoxService.GetItems(box);
            Check("普通盒收纳项数量为 3", items.Count == 3, $"实际 {items.Count}");
            Check("普通盒源文件已移走", !File.Exists(fileA) && !File.Exists(fileB) && !Directory.Exists(folderC));
            Check("普通盒存储路径均存在", items.All(i => PathUtil.Exists(i.RealPath)));
            Check("普通盒每项都有原位置记录", items.All(i => !string.IsNullOrEmpty(i.SourcePath)));

            var alpha = items.First(i => i.DisplayName == "alpha.txt");
            var deleted = BoxService.DeleteItems(box, new[] { alpha });
            Check("删除普通盒项目后原位还原", deleted.Count == 1 && File.Exists(fileA));
            Check("删除后收纳项减少", ItemRepository.GetByBox(box.Id).Count == 2);

            await BoxService.UndoDeleteAsync(box, deleted);
            Check("撤销删除后重新收纳", ItemRepository.GetByBox(box.Id).Count == 3 && !File.Exists(fileA));

            var beta = ItemRepository.GetByBox(box.Id).First(i => i.DisplayName == "beta.txt");
            BoxService.RenameItem(box, beta, "beta-renamed");
            Check("普通盒重命名同时改动存储文件",
                File.Exists(Path.Combine(AppPaths.BoxStorage(box.Id), "beta-renamed.txt")));

            var mapped = BoxService.CreateBox("自测映射盒", BoxType.Mapped, null);
            var mappedSource = Path.Combine(source, "mapped.txt");
            File.WriteAllText(mappedSource, "mapped");
            var mappedResult = await ImportService.ImportPathsAsync(mapped, new[] { mappedSource }, SilentImportInteraction.Instance);
            var mappedItems = ItemRepository.GetByBox(mapped.Id);
            Check("映射盒导入成功", mappedResult.Any(r => r.Success) && mappedItems.Count == 1);
            Check("映射盒不移动源文件", File.Exists(mappedSource) && mappedItems[0].StoredPath is null);
            BoxService.RenameItem(mapped, mappedItems[0], "mapped-renamed");
            Check("映射盒重命名同步源文件", File.Exists(Path.Combine(source, "mapped-renamed.txt")));

            var conflictSource = Path.Combine(source, "web.html");
            File.WriteAllText(conflictSource, "first");
            await ImportService.ImportPathsAsync(box, new[] { conflictSource }, SilentImportInteraction.Instance);
            File.WriteAllText(conflictSource, "second");
            await ImportService.ImportPathsAsync(box, new[] { conflictSource }, SilentImportInteraction.Instance);
            var webItems = BoxService.GetItems(box).Where(i => i.DisplayName.StartsWith("web", StringComparison.OrdinalIgnoreCase)).ToList();
            Check("同名冲突保留两者", webItems.Count == 2, $"实际 {webItems.Count}");

            var batch1 = Path.Combine(source, "batch1.txt");
            var batch2 = Path.Combine(source, "batch2.txt");
            File.WriteAllText(batch1, "batch1");
            File.WriteAllText(batch2, "batch2");
            await ImportService.ImportPathsAsync(box, new[] { batch1, batch2 }, SilentImportInteraction.Instance);
            var batchItems = BoxService.GetItems(box)
                .Where(i => i.DisplayName.StartsWith("batch", StringComparison.OrdinalIgnoreCase))
                .ToList();
            for (var i = 0; i < batchItems.Count; i++)
            {
                BoxService.RenameItem(box, batchItems[i], $"批次_{i + 1:D2}.txt");
            }

            var batchNames = ItemRepository.GetByBox(box.Id).Select(i => i.DisplayName).ToList();
            Check("批量重命名生成序号名称",
                batchNames.Contains("批次_01.txt") && batchNames.Contains("批次_02.txt"),
                string.Join(",", batchNames));

            BoxService.SetSort(box, "custom", false);
            var customBefore = BoxService.GetItems(box);
            var firstItem = customBefore[0];
            var secondItem = customBefore[1];
            var moved = BoxService.MoveItemOrder(box, firstItem, 1);
            var customAfter = BoxService.GetItems(box);
            Check("自定义排序上移/下移生效",
                moved && customAfter[0].Id == secondItem.Id && customAfter[1].Id == firstItem.Id);

            var presetAppearance = BoxAppearance.FromSettings(AppSettingsService.Current);
            presetAppearance.IconSize = 64;
            AppearancePresetService.Save("自测预设", presetAppearance);
            var savedPreset = AppearancePresetService.Load().FirstOrDefault(p => p.Name == "自测预设");
            Check("外观预设保存/读取", savedPreset is not null && Math.Abs(savedPreset.Appearance.IconSize - 64) < 0.1);
            AppearancePresetService.Delete("自测预设");
            Check("外观预设删除", AppearancePresetService.Load().All(p => p.Name != "自测预设"));

            var recoverFrom = Path.Combine(source, "recover.txt");
            var recoverTo = Path.Combine(AppPaths.BoxStorage(box.Id), "recover.txt");
            File.WriteAllText(recoverFrom, "recover");
            var entry = new JournalEntry
            {
                OpType = JournalOpType.MoveIn,
                BoxId = box.Id,
                FromPath = recoverFrom,
                ToPath = recoverTo,
            };
            JournalRepository.Add(entry);
            File.Move(recoverFrom, recoverTo);
            var (recovered, rolledBack, recoveryFailed) = FileSystemService.RecoverPending();
            Check("journal 恢复完成半移动",
                recovered >= 1 && !File.Exists(recoverFrom) && File.Exists(recoverTo),
                $"recovered={recovered}, rolledBack={rolledBack}, failed={recoveryFailed}");
            Check("journal 无残留 PENDING", JournalRepository.GetPending().Count == 0);

            var beforeDelete = BoxService.GetItems(box).Count;
            BoxService.DeleteBox(box);
            Check("删除普通盒后数据库记录清除", BoxRepository.Get(box.Id) is null && ItemRepository.GetByBox(box.Id).Count == 0);
            Check("删除普通盒后文件已还原", File.Exists(fileA) || File.Exists(fileB) || File.Exists(Path.Combine(source, "beta-renamed.txt")),
                $"删除前 {beforeDelete} 项");

            BoxService.DeleteBox(mapped);
            Check("删除映射盒不影响源文件", File.Exists(Path.Combine(source, "mapped-renamed.txt")));

            var dbPath = AppPaths.DatabasePath;
            Check("数据库文件存在", File.Exists(dbPath));
        }
        catch (Exception ex)
        {
            failed++;
            report.AppendLine($"FAIL  自测异常: {ex}");
            LogService.Error("自测异常", ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(source))
                {
                    Directory.Delete(source, recursive: true);
                }
            }
            catch
            {
                // ignore
            }
        }

        report.Insert(0, $"PocketDesk 自测：通过 {passed}，失败 {failed}{Environment.NewLine}{Environment.NewLine}");
        var reportPath = Path.Combine(AppPaths.Root, "selftest-report.txt");
        try
        {
            File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
        }
        catch
        {
            // ignore
        }

        LogService.Info($"自测完成：通过 {passed}，失败 {failed}");
        return failed == 0 ? 0 : 1;
    }
}
