namespace PocketDesk;

/// <summary>外观预设（命名后可在任意收纳盒上应用）。</summary>
public sealed class AppearancePreset
{
    public string Name { get; set; } = string.Empty;
    public BoxAppearance Appearance { get; set; } = new();
}

/// <summary>外观预设的持久化（存放于 settings 表的 JSON）。</summary>
public static class AppearancePresetService
{
    private const string StorageKey = "appearancePresets";

    public static List<AppearancePreset> Load()
    {
        try
        {
            var presets = Json.Deserialize<List<AppearancePreset>>(SettingsRepository.Get(StorageKey))
                          ?? new List<AppearancePreset>();
            presets.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Appearance is null);
            return presets;
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取外观预设失败：{ex.GetType().Name}");
            return new List<AppearancePreset>();
        }
    }

    public static void Save(string name, BoxAppearance appearance)
    {
        var presets = Load();
        presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        presets.Add(new AppearancePreset { Name = name, Appearance = appearance.Clone() });
        Persist(presets);
    }

    public static void Delete(string name)
    {
        var presets = Load();
        if (presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Persist(presets);
        }
    }

    private static void Persist(List<AppearancePreset> presets)
    {
        SettingsRepository.Set(StorageKey, Json.Serialize(presets));
        LogService.Info($"外观预设已更新，共 {presets.Count} 个");
    }
}
