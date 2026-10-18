using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace PocketDesk;

/// <summary>
/// 系统原生图标提取。使用 IShellItemImageFactory 获取任意尺寸的 Shell 图标，
/// 转成 SoftwareBitmap 后交给 XAML；带内存缓存与并发限制，避免阻塞 UI。
/// </summary>
public static class IconService
{
    private const int MaxCacheEntries = 256;

    private static readonly ConcurrentDictionary<string, SoftwareBitmapSource> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim Gate = new(4, 4);
    private static int _cacheHits;

    public static async Task<ImageSource?> LoadAsync(string path, int size, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var iconSize = Math.Clamp((int)Math.Round(size / 8.0) * 8, 16, 256);
        var key = $"{path.ToLowerInvariant()}|{iconSize}";
        if (Cache.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        await Gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (Cache.TryGetValue(key, out cached))
            {
                return cached;
            }

            var pixels = await Task.Run(() => ExtractPixels(path, iconSize, isDirectory)).ConfigureAwait(true);
            if (pixels is null)
            {
                LogService.Warn($"图标像素为空：{Path.GetFileName(path)} size={iconSize}");
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

            if (Cache.Count >= MaxCacheEntries)
            {
                Cache.Clear();
            }

            Cache[key] = source;
            return source;
        }
        catch (Exception ex)
        {
            LogService.Warn($"图标加载失败：{Path.GetFileName(path)} :: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Invalidate(string path)
    {
        var prefix = path.ToLowerInvariant() + "|";
        foreach (var key in Cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            Cache.TryRemove(key, out _);
        }
    }

    public static int CacheHits => _cacheHits;

    private static (byte[] Bytes, int Width, int Height)? ExtractPixels(string path, int size, bool isDirectory)
    {
        if (!isDirectory && !PathUtil.Exists(path))
        {
            return null;
        }

        try
        {
            var iid = typeof(NativeMethods.IShellItemImageFactory).GUID;
            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            var hr = factory.GetImage(
                new NativeMethods.SIZE(size, size),
                NativeMethods.SIIGBF.IconOnly | NativeMethods.SIIGBF.BiggerSizeOk,
                out var hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                LogService.Warn($"图标提取失败：{Path.GetFileName(path)} size={size} hr=0x{hr:X8}");
                return null;
            }

            try
            {
                return BitmapToPixels(hBitmap);
            }
            finally
            {
                NativeMethods.DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            // 部分虚拟项（例如已失效的网络路径）会失败，回退到通用图标由 XAML 处理。
            LogService.Warn($"图标提取失败：{ex.GetType().Name}");
            return null;
        }
    }

    private static (byte[] Bytes, int Width, int Height)? BitmapToPixels(IntPtr hBitmap)
    {
        var bitmap = new NativeMethods.BITMAP();
        if (NativeMethods.GetObject(hBitmap, Marshal.SizeOf<NativeMethods.BITMAP>(), ref bitmap) == 0)
        {
            return null;
        }

        var width = bitmap.bmWidth;
        var height = bitmap.bmHeight;
        if (width <= 0 || height <= 0 || width > 512 || height > 512)
        {
            return null;
        }

        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // 负数 = top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            },
        };

        var bytes = new byte[width * height * 4];
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var lines = NativeMethods.GetDIBits(
                hdc,
                hBitmap,
                0,
                (uint)height,
                bytes,
                ref info,
                NativeMethods.DIB_RGB_COLORS);
            if (lines == 0)
            {
                return null;
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }

        EnsureAlpha(bytes);
        return (bytes, width, height);
    }

    private static void EnsureAlpha(byte[] bgra)
    {
        var hasAlpha = false;
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        if (hasAlpha)
        {
            return;
        }

        for (var i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;
        }
    }
}
