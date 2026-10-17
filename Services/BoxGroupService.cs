namespace PocketDesk;

/// <summary>收纳盒分组（仅用于管理器/快速面板分区，不改变桌面窗口）。</summary>
public static class BoxGroupService
{
    private const string StorageKey = "boxGroups";

    public static Dictionary<string, string> Load()
    {
        var raw = SettingsRepository.Get(StorageKey);
        var parsed = Json.Deserialize<Dictionary<string, string>>(raw);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parsed is null)
        {
            return result;
        }

        foreach (var (key, value) in parsed)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                result[key] = value.Trim();
            }
        }

        return result;
    }

    public static string? Get(string boxId) =>
        Load().TryGetValue(boxId, out var group) ? group : null;

    public static void Set(string boxId, string? group)
    {
        var map = Load();
        if (string.IsNullOrWhiteSpace(group))
        {
            map.Remove(boxId);
        }
        else
        {
            map[boxId] = group.Trim();
        }

        Persist(map);
    }

    public static void Remove(string boxId)
    {
        var map = Load();
        if (map.Remove(boxId))
        {
            Persist(map);
        }
    }

    public static List<string> DistinctGroups() =>
        Load().Values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static void Persist(Dictionary<string, string> map) =>
        SettingsRepository.Set(StorageKey, Json.Serialize(map));
}
