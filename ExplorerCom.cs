using System.Runtime.InteropServices;

namespace PaneWeaver;

internal sealed class ExplorerEntry : IDisposable
{
    internal ExplorerEntry(object browser, IntPtr identity, IntPtr hwnd, string path, int globalIndex)
    {
        Browser = browser;
        Identity = identity;
        Hwnd = hwnd;
        GlobalIndex = globalIndex;
    }

    internal object Browser { get; }
    internal IntPtr Identity { get; }
    internal IntPtr Hwnd { get; }
    internal string Path => ExplorerCom.ReadPath((IExplorerBrowser)Browser);
    internal string FileSystemPath => ExplorerCom.ReadPath((IExplorerBrowser)Browser, virtualFallback: false);
    internal int GlobalIndex { get; }

    internal IntPtr TabHwnd
    {
        get
        {
            object? service = null;
            try
            {
                var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                var iid = new Guid("000214E2-0000-0000-C000-000000000046");
                ((IComServiceProvider)Browser).QueryService(ref sid, ref iid, out service);
                ((IOleWindow)service).GetWindow(out var hwnd);
                return hwnd;
            }
            catch { return IntPtr.Zero; }
            finally { if (service is not null && Marshal.IsComObject(service)) Marshal.ReleaseComObject(service); }
        }
    }

    internal bool Navigate(string path)
    {
        try
        {
            object target = path, missing = Type.Missing;
            ((IExplorerBrowser)Browser).Navigate2(ref target, ref missing, ref missing, ref missing, ref missing);
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
            ((IExplorerBrowser)Browser).Quit();
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

internal sealed class ExplorerCom : IDisposable
{
    private IShellWindows? windows;

    internal int Count
    {
        get { try { EnsureConnected(); return windows!.Count; } catch { Dispose(); return -1; } }
    }

    internal void WarmUp()
    {
        try { EnsureConnected(); _ = windows!.Count; }
        catch { Dispose(); }
    }

    private void EnsureConnected()
    {
        if (windows is not null) return;
        var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
        windows = (IShellWindows)Activator.CreateInstance(type!)!;
    }

    internal List<ExplorerEntry> Enumerate(IntPtr onlyHwnd = default, IntPtr onlyTab = default, bool firstOnly = false)
    {
        var results = new List<ExplorerEntry>();
        try
        {
            EnsureConnected();
            var count = windows!.Count;
            // New registrations are usually last; correctness is still verified
            // by HWND and COM identity, never inferred from collection position.
            for (var i = count - 1; i >= 0; i--)
            {
                object? browser = null;
                try
                {
                    browser = windows.Item(i);
                    if (browser is null)
                    {
                        continue;
                    }

                    var hwnd = new IntPtr(((IExplorerBrowser)browser).HWND);
                    if (onlyHwnd != IntPtr.Zero && hwnd != onlyHwnd)
                    {
                        Release(browser);
                        browser = null;
                        continue;
                    }

                    var identity = Marshal.GetIUnknownForObject(browser);
                    Marshal.Release(identity);
                    // Paths require extra cross-process calls. Read only the source
                    // and claimed destination, never every existing tab on each poll.
                    var entry = new ExplorerEntry(browser, identity, hwnd, string.Empty, i);
                    browser = null;
                    if (onlyTab != IntPtr.Zero && entry.TabHwnd != onlyTab) { entry.Dispose(); continue; }
                    results.Add(entry);
                    if (firstOnly) break;
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
            Dispose(); // Explorer restarted: reconnect on the next scheduler pass.
        }

        return results;
    }

    public void Dispose()
    {
        if (windows is not null) Release(windows);
        windows = null;
    }

    internal static string ReadPath(IExplorerBrowser browser, bool virtualFallback = true)
    {
        try
        {
            string url = browser.LocationURL ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(url))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                {
                    return uri.LocalPath; // Already decoded; decoding twice corrupts literal %xx names.
                }

                return url;
            }
        }
        catch
        {
            // Virtual folders often expose an empty LocationURL.
        }

        if (!virtualFallback) return string.Empty;

        object? document = null, folder = null, item = null;
        try
        {
            document = browser.Document;
            folder = ((dynamic)document).Folder;
            item = ((dynamic)folder).Self;
            return Convert.ToString(((dynamic)item).Path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (item is not null) Release(item);
            if (folder is not null) Release(folder);
            if (document is not null) Release(document);
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

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComServiceProvider
{
    void QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
}

[ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleWindow
{
    void GetWindow(out IntPtr hwnd);
    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
}

// Dispatch interfaces avoid the dynamic binder, GetIDsOfNames and runtime type
// discovery in the hot path. DISPIDs come from the Windows SDK ExDisp.idl.
[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    int Count { get; }
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object Item([In, Optional, MarshalAs(UnmanagedType.Struct)] object index);
}

[ComImport, Guid("D30C1661-CDAF-11D0-8A3E-00C04FC9E26E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IExplorerBrowser
{
    [DispId(-515)] long HWND { get; }
    [DispId(211)] string LocationURL { [return: MarshalAs(UnmanagedType.BStr)] get; }
    [DispId(203)] object Document { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    [DispId(300)] void Quit();
    [DispId(500)]
    void Navigate2([In, MarshalAs(UnmanagedType.Struct)] ref object target,
        [In, Optional, MarshalAs(UnmanagedType.Struct)] ref object flags,
        [In, Optional, MarshalAs(UnmanagedType.Struct)] ref object frame,
        [In, Optional, MarshalAs(UnmanagedType.Struct)] ref object post,
        [In, Optional, MarshalAs(UnmanagedType.Struct)] ref object headers);
}
