using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace PocketDesk;

/// <summary>
/// 捕获桌面壁纸层（不含其它窗口），并按窗口矩形裁剪出对应局部，供收纳盒绘制。
/// </summary>
public static class DesktopCaptureService
{
    public sealed record Snapshot(
        byte[] Pixels,
        int Width,
        int Height,
        int OriginX,
        int OriginY,
        int ScreenWidth,
        int ScreenHeight);

    private static Task<Snapshot?>? _captureTask;

    public static async Task<ImageSource?> GetCropAsync(int x, int y, int width, int height)
    {
        var snapshot = await GetAsync();
        if (snapshot is null || width <= 0 || height <= 0)
        {
            return null;
        }

        var scaleX = snapshot.Width / (double)Math.Max(1, snapshot.ScreenWidth);
        var scaleY = snapshot.Height / (double)Math.Max(1, snapshot.ScreenHeight);
        var sourceX = (int)Math.Round((x - snapshot.OriginX) * scaleX);
        var sourceY = (int)Math.Round((y - snapshot.OriginY) * scaleY);
        var bytes = new byte[width * height * 4];

        for (var row = 0; row < height; row++)
        {
            var sy = sourceY + (int)Math.Round(row * scaleY);
            if (sy < 0 || sy >= snapshot.Height)
            {
                continue;
            }

            for (var col = 0; col < width; col++)
            {
                var sx = sourceX + (int)Math.Round(col * scaleX);
                if (sx < 0 || sx >= snapshot.Width)
                {
                    continue;
                }

                var sourceIndex = (sy * snapshot.Width + sx) * 4;
                var targetIndex = (row * width + col) * 4;
                bytes[targetIndex] = snapshot.Pixels[sourceIndex];
                bytes[targetIndex + 1] = snapshot.Pixels[sourceIndex + 1];
                bytes[targetIndex + 2] = snapshot.Pixels[sourceIndex + 2];
                bytes[targetIndex + 3] = 255;
            }
        }

        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            bytes.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Premultiplied);
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        return source;
    }

    private static Task<Snapshot?> GetAsync() => _captureTask ??= Task.Run(CapturePixels);

    private static Snapshot? CapturePixels()
    {
        var desktop = NativeMethods.FindDesktopWindow();
        if (desktop == IntPtr.Zero)
        {
            return null;
        }

        NativeMethods.GetWindowRect(desktop, out var rect);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        var memDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var oldObject = IntPtr.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return null;
            }

            oldObject = NativeMethods.SelectObject(memDc, bitmap);
            if (!NativeMethods.PrintWindow(desktop, memDc, 2))
            {
                return null;
            }

            var info = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                },
            };

            var bytes = new byte[width * height * 4];
            var lines = NativeMethods.GetDIBits(
                screenDc,
                bitmap,
                0,
                (uint)height,
                bytes,
                ref info,
                NativeMethods.DIB_RGB_COLORS);
            if (lines == 0)
            {
                return null;
            }

            for (var i = 3; i < bytes.Length; i += 4)
            {
                bytes[i] = 255;
            }

            // SM_XVIRTUALSCREEN=76, SM_YVIRTUALSCREEN=77
            // SM_CXVIRTUALSCREEN=78, SM_CYVIRTUALSCREEN=79
            return new Snapshot(
                bytes,
                width,
                height,
                NativeMethods.GetSystemMetrics(76),
                NativeMethods.GetSystemMetrics(77),
                Math.Max(1, NativeMethods.GetSystemMetrics(78)),
                Math.Max(1, NativeMethods.GetSystemMetrics(79)));
        }
        finally
        {
            if (oldObject != IntPtr.Zero && memDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memDc, oldObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
