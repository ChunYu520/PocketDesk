using Windows.UI.ViewManagement;

namespace PocketDesk;

/// <summary>系统高对比度状态监听。高对比度下强制纯色背景，避免材质影响可读性。</summary>
public static class AccessibilityService
{
    private static AccessibilitySettings? _settings;

    public static bool IsHighContrast { get; private set; }

    public static event Action? Changed;

    public static void Initialize()
    {
        try
        {
            _settings = new AccessibilitySettings();
            IsHighContrast = _settings.HighContrast;
            _settings.HighContrastChanged += (_, _) =>
            {
                IsHighContrast = _settings.HighContrast;
                LogService.Info($"高对比度模式：{(IsHighContrast ? "开启" : "关闭")}");
                Changed?.Invoke();
            };
            LogService.Info($"高对比度初始状态：{(IsHighContrast ? "开启" : "关闭")}");
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取高对比度设置失败：{ex.GetType().Name}");
        }
    }
}
