using System.Text;

namespace PocketDesk;

/// <summary>快捷方式（.lnk / .url）解析。</summary>
public static class ShortcutService
{
    public sealed record ShortcutInfo(string? TargetPath, string? Arguments, string? WorkingDirectory, string? Url);

    public static ShortcutInfo Resolve(string path)
    {
        var extension = Path.GetExtension(path);
        try
        {
            if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                var url = ReadUrl(path);
                return new ShortcutInfo(null, null, null, url);
            }

            if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return new ShortcutInfo(null, null, null, null);
            }

            var clsid = NativeMethods.CLSID_ShellLink;
            var iid = NativeMethods.IID_IPersistFile;
            var hr = NativeMethods.CoCreateInstance(
                ref clsid,
                IntPtr.Zero,
                NativeMethods.CLSCTX_INPROC_SERVER,
                ref iid,
                out var comObject);
            if (hr != 0 || comObject is null)
            {
                return new ShortcutInfo(null, null, null, null);
            }

            var persist = (NativeMethods.IPersistFile)comObject;
            persist.Load(path, NativeMethods.STGM_READ);

            var link = (NativeMethods.IShellLinkW)comObject;
            link.Resolve(IntPtr.Zero, NativeMethods.SLR_NO_UI);

            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var arguments = new StringBuilder(1024);
            link.GetArguments(arguments, arguments.Capacity);
            var workingDirectory = new StringBuilder(1024);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);

            return new ShortcutInfo(
                target.Length > 0 ? target.ToString() : null,
                arguments.Length > 0 ? arguments.ToString() : null,
                workingDirectory.Length > 0 ? workingDirectory.ToString() : null,
                null);
        }
        catch (Exception ex)
        {
            LogService.Warn($"解析快捷方式失败：{Path.GetFileName(path)} :: {ex.GetType().Name}");
            return new ShortcutInfo(null, null, null, null);
        }
    }

    public static string? ReadUrl(string urlFilePath)
    {
        try
        {
            foreach (var line in File.ReadLines(urlFilePath))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    return line[4..].Trim();
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
