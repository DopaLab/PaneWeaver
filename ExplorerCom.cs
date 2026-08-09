using System.Runtime.InteropServices;

namespace PaneWeaver;

internal sealed class ExplorerEntry : IDisposable
{
    internal ExplorerEntry(object browser, IntPtr identity, IntPtr hwnd, string path, int globalIndex)
    {
        Browser = browser;
        Identity = identity;
        Hwnd = hwnd;
        Path = path;
        GlobalIndex = globalIndex;
    }

    internal object Browser { get; }
    internal IntPtr Identity { get; }
    internal IntPtr Hwnd { get; }
    internal string Path { get; }
    internal int GlobalIndex { get; }

    internal bool Navigate(string path)
    {
        try
        {
            dynamic browser = Browser;
            browser.Navigate2(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal void Quit()
    {
        try
        {
            dynamic browser = Browser;
            browser.Quit();
        }
        catch
        {
            // Explorer may already have removed the tab.
        }
    }

    public void Dispose()
    {
        try
        {
            if (Marshal.IsComObject(Browser))
            {
                Marshal.ReleaseComObject(Browser);
            }
        }
        catch
        {
            // RCW lifetime is best effort; the owning Explorer process remains authoritative.
        }
    }
}

internal static class ExplorerCom
{
    internal static List<ExplorerEntry> Enumerate(IntPtr onlyHwnd = default)
    {
        var results = new List<ExplorerEntry>();
        object? shell = null;
        object? windows = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                return results;
            }

            shell = Activator.CreateInstance(type);
            if (shell is null)
            {
                return results;
            }

            dynamic dynamicShell = shell;
            windows = dynamicShell.Windows();
            dynamic dynamicWindows = windows;
            var count = (int)dynamicWindows.Count;
            for (var i = 0; i < count; i++)
            {
                object? browser = null;
                try
                {
                    browser = dynamicWindows.Item(i);
                    if (browser is null)
                    {
                        continue;
                    }

                    dynamic dynamicBrowser = browser;
                    var hwnd = new IntPtr(Convert.ToInt64(dynamicBrowser.HWND));
                    if (onlyHwnd != IntPtr.Zero && hwnd != onlyHwnd)
                    {
                        Release(browser);
                        browser = null;
                        continue;
                    }

                    var identity = Marshal.GetIUnknownForObject(browser);
                    Marshal.Release(identity);
                    var path = ReadPath(dynamicBrowser);
                    results.Add(new ExplorerEntry(browser, identity, hwnd, path, i));
                    browser = null;
                }
                catch
                {
                    if (browser is not null)
                    {
                        Release(browser);
                    }
                }
            }
        }
        catch
        {
            foreach (var item in results)
            {
                item.Dispose();
            }

            results.Clear();
        }
        finally
        {
            if (windows is not null)
            {
                Release(windows);
            }

            if (shell is not null)
            {
                Release(shell);
            }
        }

        return results;
    }

    internal static int Count()
    {
        object? shell = null;
        object? windows = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            shell = type is null ? null : Activator.CreateInstance(type);
            if (shell is null)
            {
                return 0;
            }

            dynamic dynamicShell = shell;
            windows = dynamicShell.Windows();
            dynamic dynamicWindows = windows;
            return (int)dynamicWindows.Count;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (windows is not null)
            {
                Release(windows);
            }

            if (shell is not null)
            {
                Release(shell);
            }
        }
    }

    private static string ReadPath(dynamic browser)
    {
        try
        {
            string url = Convert.ToString(browser.LocationURL) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(url))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                {
                    return Uri.UnescapeDataString(uri.LocalPath);
                }

                return url;
            }
        }
        catch
        {
            // Virtual folders often expose an empty LocationURL.
        }

        try
        {
            return Convert.ToString(browser.Document.Folder.Self.Path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void Release(object value)
    {
        try
        {
            if (Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
