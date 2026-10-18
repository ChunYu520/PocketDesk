using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace PocketDesk;

/// <summary>
/// 捕获桌面壁纸层（不含其它窗口），用于收纳盒的“局部壁纸”背景。
/// </summary>
public static class DesktopCaptureService
{
    public sealed record Snapshot(SoftwareBitmapSource Source, int PixelWidth, int PixelHeight);

    private static Task<Snapshot?>? _captureTask;

    public static Task<Snapshot?> GetAsync() => _captureTask ??= CaptureAsync();

    private static async Task<Snapshot?> CaptureAsync()
    {
        var pixels = await Task.Run(CapturePixels);
        if (pixels is null)
        {
            return null;
        }

        var (bytes, width, height) = pixels.Value;
        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            bytes.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Premultiplied);
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        return new Snapshot(source, width, height);
    }

    private static (byte[] Bytes, int Width, int Height)? CapturePixels()
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

            return (bytes, width, height);
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
