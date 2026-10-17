namespace PocketDesk;

/// <summary>应用设置的单例访问与变更通知。</summary>
public static class AppSettingsService
{
    private const string StorageKey = "app";

    private static AppSettings _current = new();

    public static AppSettings Current => _current;

    public static event Action? Changed;

    public static void Load()
    {
        var json = SettingsRepository.Get(StorageKey);
        _current = Json.Deserialize<AppSettings>(json) ?? new AppSettings();
        _current.DefaultIconSize = Math.Clamp(_current.DefaultIconSize, 24, 128);
        _current.DefaultCornerRadius = Math.Clamp(_current.DefaultCornerRadius, 0, 24);
        _current.DefaultBackgroundOpacity = Math.Clamp(_current.DefaultBackgroundOpacity, 0.2, 1.0);
        _current.DefaultBorderOpacity = Math.Clamp(_current.DefaultBorderOpacity, 0, 1.0);
        _current.DefaultColumns = Math.Clamp(_current.DefaultColumns, 0, 9);
        _current.DefaultNameLines = Math.Clamp(_current.DefaultNameLines, 0, 3);
        _current.UndoSeconds = _current.UndoSeconds <= 0 ? 10 : _current.UndoSeconds;
    }

    public static void Save()
    {
        SettingsRepository.Set(StorageKey, Json.Serialize(_current));
        Changed?.Invoke();
    }

    public static void Update(Action<AppSettings> mutate)
    {
        mutate(_current);
        Save();
    }
}
