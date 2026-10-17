namespace PocketDesk;

/// <summary>
/// AppWindow 使用物理像素，而 XAML 使用 DIP。所有跨这两个体系的尺寸都必须经过这里换算。
/// </summary>
public static class DpiUtil
{
    public static double SystemScale
    {
        get
        {
            try
            {
                var dpi = NativeMethods.GetDpiForSystem();
                return dpi <= 0 ? 1.0 : dpi / 96.0;
            }
            catch
            {
                return 1.0;
            }
        }
    }

    public static int Scale(int dip) => (int)Math.Round(dip * SystemScale);

    public static int Scale(double dip) => (int)Math.Round(dip * SystemScale);

    public static int Unscale(double pixels) => (int)Math.Round(pixels / SystemScale);

    public static uint WindowDpi(nint hwnd)
    {
        try
        {
            var dpi = NativeMethods.GetDpiForWindow(hwnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch
        {
            return 96;
        }
    }
}
