using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PaneWeaver;

internal sealed class ExplorerRouter : IDisposable
{
    private readonly ActivityLog log;
    private readonly BlockingCollection<Action> queue = new();
    private readonly ConcurrentDictionary<IntPtr, HiddenWindow> hidden = new();
    private readonly ConcurrentDictionary<IntPtr, byte> handledWindows = new();
    private readonly Thread worker;
    private readonly NativeMethods.WinEventDelegate windowEventDelegate;
    private readonly NativeMethods.WinEventDelegate foregroundEventDelegate;
    private readonly NativeMethods.LowLevelKeyboardProc keyboardDelegate;
    private IntPtr windowEventHook;
    private IntPtr foregroundEventHook;
    private IntPtr keyboardHook;
    private volatile IntPtr lastExplorerWindow;
    private volatile bool enabled = true;
    private volatile bool suppressingE;
    private volatile bool leftWinDown;
    private volatile bool rightWinDown;
    private volatile bool shiftDown;
    private long shiftBypassUntil;
    private bool disposed;

    internal ExplorerRouter(ActivityLog log)
    {
        this.log = log;
        windowEventDelegate = OnWindowEvent;
        foregroundEventDelegate = OnForegroundEvent;
        keyboardDelegate = OnKeyboardEvent;
        worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "PaneWeaver tab transaction broker"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    internal bool Enabled
    {
        get => enabled;
        set
        {
            enabled = value;
            log.Write(value ? "Routing enabled" : "Routing paused");
        }
    }

    internal void Start()
    {
        lastExplorerWindow = NativeMethods.GetForegroundWindow();
        if (!NativeMethods.IsExplorerWindow(lastExplorerWindow))
        {
            lastExplorerWindow = FindPrimaryExplorer(IntPtr.Zero);
        }

        windowEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventObjectCreate,
            NativeMethods.EventObjectShow,
            IntPtr.Zero,
            windowEventDelegate,
            0,
            0,
            NativeMethods.WineventOutOfContext | NativeMethods.WineventSkipOwnProcess);

        foregroundEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground,
            NativeMethods.EventSystemForeground,
            IntPtr.Zero,
            foregroundEventDelegate,
            0,
            0,
            NativeMethods.WineventOutOfContext | NativeMethods.WineventSkipOwnProcess);

        keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            keyboardDelegate,
            NativeMethods.GetModuleHandle(null),
            0);

        log.Write($"Hooks ready (window={windowEventHook != IntPtr.Zero}, Win+E={keyboardHook != IntPtr.Zero})");
    }

    internal void OpenInTab(string? path = null)
    {
        if (!enabled)
        {
            return;
        }

        var primary = FindPrimaryExplorer(IntPtr.Zero);
        if (primary == IntPtr.Zero)
        {
            LaunchExplorer(path);
            return;
        }

        queue.Add(() => CreateNativeTab(primary, path, bringToFront: true));
    }

    private void OnForegroundEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (NativeMethods.IsExplorerWindow(hwnd))
        {
            lastExplorerWindow = hwnd;
        }
    }

    private void OnWindowEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (!enabled || objectId != NativeMethods.ObjIdWindow || hwnd == IntPtr.Zero || !NativeMethods.IsExplorerWindow(hwnd))
        {
            return;
        }

        // CREATE and SHOW are both observed for the same HWND. Claim it before
        // any decision so a Shift bypass or first-window allowance cannot be
        // reconsidered by the later event.
        if (!handledWindows.TryAdd(hwnd, 0))
        {
            return;
        }

        if (handledWindows.Count > 64)
        {
            foreach (var oldWindow in handledWindows.Keys.Where(window => !NativeMethods.IsWindow(window)).ToArray())
            {
                handledWindows.TryRemove(oldWindow, out _);
            }
        }

        var shiftBypass = shiftDown ||
                          (NativeMethods.GetAsyncKeyState(NativeMethods.VkShift) & 0x8000) != 0 ||
                          Environment.TickCount64 <= Interlocked.Read(ref shiftBypassUntil);
        if (shiftBypass)
        {
            Interlocked.Exchange(ref shiftBypassUntil, 0);
            log.Write("Shift bypass: leaving the new Explorer window alone");
            return;
        }

        var primary = FindPrimaryExplorer(hwnd);
        if (primary == IntPtr.Zero || primary == hwnd)
        {
            lastExplorerWindow = hwnd;
            return;
        }

        hidden.TryAdd(hwnd, Cloak(hwnd));

        log.Write($"Captured Explorer window 0x{hwnd.ToInt64():X} before display");
        queue.Add(() => RedirectWindow(hwnd, primary));
    }

    private IntPtr OnKeyboardEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !enabled)
        {
            return NativeMethods.CallNextHookEx(keyboardHook, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var key = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
        var isDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
        var isUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;

        if (key.VkCode == NativeMethods.VkLwin)
        {
            leftWinDown = isDown || (leftWinDown && !isUp);
        }
        else if (key.VkCode == NativeMethods.VkRwin)
        {
            rightWinDown = isDown || (rightWinDown && !isUp);
        }
        else if (key.VkCode is NativeMethods.VkShift or NativeMethods.VkLshift or NativeMethods.VkRshift)
        {
            shiftDown = isDown || (shiftDown && !isUp);
            if (isUp)
            {
                // Explorer can defer a shell launch for several seconds. Arm one
                // bypass transaction after Shift is released so the user's intent
                // survives that delay; the first Explorer window consumes it.
                Interlocked.Exchange(ref shiftBypassUntil, Environment.TickCount64 + 5000);
            }
        }

        if (key.VkCode == NativeMethods.VkE && isDown)
        {
            var winDown = leftWinDown || rightWinDown ||
                          (NativeMethods.GetAsyncKeyState(NativeMethods.VkLwin) & 0x8000) != 0 ||
                          (NativeMethods.GetAsyncKeyState(NativeMethods.VkRwin) & 0x8000) != 0;
            var shiftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VkShift) & 0x8000) != 0;
            if (winDown && !shiftDown)
            {
                var primary = FindPrimaryExplorer(IntPtr.Zero);
                if (primary != IntPtr.Zero)
                {
                    suppressingE = true;
                    queue.Add(() => CreateNativeTab(primary, null, bringToFront: true));
                    log.Write("Win+E routed directly to a native tab");
                    return new IntPtr(1);
                }
            }
        }

        if (key.VkCode == NativeMethods.VkE && isUp && suppressingE)
        {
            suppressingE = false;
            return new IntPtr(1);
        }

        return NativeMethods.CallNextHookEx(keyboardHook, code, wParam, lParam);
    }

    private void RedirectWindow(IntPtr newWindow, IntPtr primary)
    {
        try
        {
            var path = WaitForPath(newWindow, TimeSpan.FromSeconds(2));
            if (!NativeMethods.IsWindow(newWindow))
            {
                hidden.TryRemove(newWindow, out _);
                return;
            }

            if (string.IsNullOrWhiteSpace(path) || !IsRedirectable(path))
            {
                log.Write($"Allowing unsupported shell location: {path}");
                Restore(newWindow);
                return;
            }

            if (!NativeMethods.IsExplorerWindow(primary))
            {
                primary = FindPrimaryExplorer(newWindow);
            }

            if (primary == IntPtr.Zero || !CreateNativeTab(primary, path, bringToFront: true))
            {
                log.Write($"Tab transaction failed safely; restoring window for {path}");
                Restore(newWindow);
                return;
            }

            NativeMethods.PostMessage(newWindow, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
            hidden.TryRemove(newWindow, out _);
            lastExplorerWindow = primary;
            log.Write($"Docked {path}");
        }
        catch (Exception exception)
        {
            log.Write($"Redirect error: {exception.Message}");
            Restore(newWindow);
        }
    }

    private bool CreateNativeTab(IntPtr primary, string? path, bool bringToFront)
    {
        if (!NativeMethods.IsExplorerWindow(primary))
        {
            return false;
        }

        var host = NativeMethods.FindShellTabHost(primary);
        if (host == IntPtr.Zero)
        {
            log.Write("Explorer tab host was not found");
            return false;
        }

        var globalCountBefore = ExplorerCom.Count();

        if (!NativeMethods.PostMessage(host, NativeMethods.WmCommand, new IntPtr(NativeMethods.CmdNewTab), IntPtr.Zero))
        {
            return false;
        }

        ExplorerEntry? created = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && created is null)
        {
            Thread.Sleep(20);
            var entries = ExplorerCom.Enumerate(primary);
            foreach (var entry in entries)
            {
                if (created is null && entry.GlobalIndex >= globalCountBefore)
                {
                    created = entry;
                }
                else
                {
                    entry.Dispose();
                }
            }
        }

        if (created is null)
        {
            log.Write("Explorer did not register the new tab in time");
            return false;
        }

        using (created)
        {
            if (!string.IsNullOrWhiteSpace(path) && !created.Navigate(NormalizeForNavigation(path)))
            {
                created.Quit();
                log.Write($"Explorer rejected tab navigation to {path}");
                return false;
            }
        }

        if (bringToFront)
        {
            if (NativeMethods.IsIconic(primary))
            {
                NativeMethods.ShowWindow(primary, NativeMethods.SwRestore);
            }

            NativeMethods.SetForegroundWindow(primary);
        }

        return true;
    }

    private static string WaitForPath(IntPtr hwnd, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && NativeMethods.IsWindow(hwnd))
        {
            Thread.Sleep(20);
            var entries = ExplorerCom.Enumerate(hwnd);
            try
            {
                var path = entries.Select(entry => entry.Path).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
            finally
            {
                foreach (var entry in entries)
                {
                    entry.Dispose();
                }
            }
        }

        return string.Empty;
    }

    private IntPtr FindPrimaryExplorer(IntPtr exclude)
    {
        var remembered = lastExplorerWindow;
        if (remembered != exclude && NativeMethods.IsExplorerWindow(remembered) && NativeMethods.IsWindowVisible(remembered))
        {
            return remembered;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != exclude && NativeMethods.IsExplorerWindow(foreground) && NativeMethods.IsWindowVisible(foreground))
        {
            lastExplorerWindow = foreground;
            return foreground;
        }

        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (window == exclude || !NativeMethods.IsWindowVisible(window) || !NativeMethods.IsExplorerWindow(window))
            {
                return true;
            }

            found = window;
            return false;
        }, IntPtr.Zero);
        if (found != IntPtr.Zero)
        {
            lastExplorerWindow = found;
        }

        return found;
    }

    private HiddenWindow Cloak(IntPtr hwnd)
    {
        var originalStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(originalStyle | NativeMethods.WsExLayered));
        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 0, NativeMethods.LwaAlpha);
        var cloak = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaCloak, ref cloak, sizeof(int));
        return new HiddenWindow(originalStyle);
    }

    private void Restore(IntPtr hwnd)
    {
        if (!hidden.TryRemove(hwnd, out var state) || !NativeMethods.IsWindow(hwnd))
        {
            return;
        }

        var cloak = 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaCloak, ref cloak, sizeof(int));
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(state.OriginalExStyle));
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpFrameChanged);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
    }

    private static bool IsRedirectable(string path)
    {
        return !path.Contains("{26EE0668-A00A-44D7-9371-BEB064C98683}", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForNavigation(string path)
    {
        if (path.Equals("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", StringComparison.OrdinalIgnoreCase))
        {
            return "shell:MyComputerFolder";
        }

        if (path.Equals("::{645FF040-5081-101B-9F08-00AA002F954E}", StringComparison.OrdinalIgnoreCase))
        {
            return "shell:RecycleBinFolder";
        }

        return path;
    }

    private static void LaunchExplorer(string? path)
    {
        var arguments = string.IsNullOrWhiteSpace(path) ? string.Empty : $"\"{path}\"";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            Arguments = arguments,
            UseShellExecute = true
        });
    }

    private void WorkerMain()
    {
        foreach (var action in queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                log.Write($"Transaction error: {exception.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        enabled = false;
        if (windowEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(windowEventHook);
        }

        if (foregroundEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(foregroundEventHook);
        }

        if (keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(keyboardHook);
        }

        foreach (var hwnd in hidden.Keys.ToArray())
        {
            Restore(hwnd);
        }

        queue.CompleteAdding();
        worker.Join(TimeSpan.FromSeconds(2));
    }

    private sealed record HiddenWindow(long OriginalExStyle);
}
