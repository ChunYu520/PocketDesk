using System.IO.Compression;

namespace PocketDesk;

/// <summary>开发演示模式（--demo）：在隔离的数据目录中创建演示收纳盒，便于截图与手工验收。</summary>
public static class DemoMode
{
    public static async Task ShowAsync()
    {
        var normal = BoxRepository.GetAll().FirstOrDefault(b => b.Name == "演示 · 普通盒")
                     ?? await CreateNormalDemoAsync();
        var mapped = BoxRepository.GetAll().FirstOrDefault(b => b.Name == "演示 · 映射盒")
                     ?? await CreateMappedDemoAsync();

        var width = 430;
        var height = 400;
        var primary = Microsoft.UI.Windowing.DisplayArea.Primary;
        var work = primary.WorkArea;

        BoxService.SavePlacement(normal, new WindowPlacement
        {
            Left = work.X + DpiUtil.Scale(30),
            Top = work.Y + DpiUtil.Scale(40),
            Width = DpiUtil.Scale(width),
            Height = DpiUtil.Scale(height),
            MonitorDevice = primary.DisplayId.Value.ToString(),
            State = "Normal",
        });
        BoxService.SavePlacement(mapped, new WindowPlacement
        {
            Left = work.X + DpiUtil.Scale(30 + width + 16),
            Top = work.Y + DpiUtil.Scale(40),
            Width = DpiUtil.Scale(360),
            Height = DpiUtil.Scale(320),
            MonitorDevice = primary.DisplayId.Value.ToString(),
            State = "Normal",
        });

        WindowManager.ShowBox(normal, activate: true);
        WindowManager.ShowBox(mapped, activate: false);
    }

    private static async Task<Box> CreateNormalDemoAsync()
    {
        var box = BoxService.CreateBox("演示 · 普通盒", BoxType.Normal, null);
        var source = Path.Combine(Path.GetTempPath(), "PocketDeskDemo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(source);

        var files = new List<string>();
        CopyIfExists(Path.Combine(Environment.SystemDirectory, "notepad.exe"), source, "记事本.exe", files);
        CopyIfExists(Path.Combine(Environment.SystemDirectory, "mspaint.exe"), source, "画图.exe", files);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), source, "应用图标.ico", files);
        CopyIfExists(Path.Combine(AppContext.BaseDirectory, "Assets", "StoreLogo.png"), source, "品牌徽标.png", files);

        var text = Path.Combine(source, "会议纪要.txt");
        File.WriteAllText(text, "PocketDesk 演示文件\n");
        files.Add(text);

        var markdown = Path.Combine(source, "项目计划.md");
        File.WriteAllText(markdown, "# PocketDesk\n\n- 普通收纳盒\n- 映射收纳盒\n");
        files.Add(markdown);

        var url = Path.Combine(source, "DeepSeek.url");
        File.WriteAllText(url, "[InternetShortcut]\r\nURL=https://www.deepseek.com\r\n");
        files.Add(url);

        var archive = Path.Combine(source, "资料包.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("demo");
        }

        files.Add(archive);

        var folder = Path.Combine(source, "设计素材");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "说明.txt"), "demo folder");
        files.Add(folder);

        await ImportService.ImportPathsAsync(box, files, SilentImportInteraction.Instance);
        try
        {
            Directory.Delete(source, recursive: true);
        }
        catch
        {
            // ignore
        }

        return box;
    }

    private static async Task<Box> CreateMappedDemoAsync()
    {
        var box = BoxService.CreateBox("演示 · 映射盒", BoxType.Mapped, null);
        var candidates = new List<string>();
        var wallpaper = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper");
        if (Directory.Exists(wallpaper))
        {
            candidates.Add(wallpaper);
        }

        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (File.Exists(notepad))
        {
            candidates.Add(notepad);
        }

        var drivers = Path.Combine(Environment.SystemDirectory, "drivers");
        if (Directory.Exists(drivers))
        {
            candidates.Add(drivers);
        }

        if (candidates.Count > 0)
        {
            await ImportService.ImportPathsAsync(box, candidates, SilentImportInteraction.Instance);
        }

        return box;
    }

    private static void CopyIfExists(string source, string directory, string name, List<string> files)
    {
        try
        {
            if (!File.Exists(source))
            {
                return;
            }

            var target = Path.Combine(directory, name);
            File.Copy(source, target, overwrite: true);
            files.Add(target);
        }
        catch
        {
            // 演示文件缺失不影响启动。
        }
    }
}
