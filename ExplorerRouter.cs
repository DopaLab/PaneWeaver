using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PaneWeaver;

internal sealed class ExplorerRouter : IDisposable
{
    private readonly ActivityLog log;
    private readonly ConcurrentQueue<Func<Task>> queue = new();
    private readonly SemaphoreSlim tabGate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly ExplorerCom shell = new(); // Used exclusively on the pumped STA.
    private readonly System.Threading.Timer watchdog;
    private volatile Control? dispatcher;
    private volatile int generation;
    private int outstanding;
    // This is a recovery deadline, not an advertised latency. Explorer owns
    // rendering and cold/network startup; timing out at 850 ms lost valid opens.
    internal const int TransactionBudgetMs = 3000;
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
    private volatile bool disposed;

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
        // Independent of the COM apartment: a stalled shell RPC cannot keep
        // captured Explorer windows invisible indefinitely.
        watchdog = new System.Threading.Timer(_ => RestoreExpired(), null, 50, 50);
    }

    internal bool Enabled
    {
        get => enabled;
        set
        {
            enabled = value;
            if (!value)
            {
                Interlocked.Increment(ref generation);
                foreach (var hwnd in hidden.Keys) Restore(hwnd);
            }
            log.Write(value ? "Routing enabled" : "Routing paused");
        }
    }

    internal void Start(IntPtr preferredWindow = default)
    {
        // Existing windows must never be mistaken for new launches on a later SHOW.
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.IsExplorerWindow(hwnd)) handledWindows.TryAdd(hwnd, 0);
            return true;
        }, IntPtr.Zero);
        lastExplorerWindow = NativeMethods.IsExplorerWindow(preferredWindow)
            ? preferredWindow : NativeMethods.GetForegroundWindow();
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

        var epoch = generation;
        var started = Environment.TickCount64;
        if (!Enqueue(async () =>
        {
            var result = await CreateNativeTab(primary, path, started, epoch, 5000);
            log.Write($"Direct tab: {result}, {Environment.TickCount64 - started} ms");
            // Preserve explicit destinations even if a dispatched tab cannot be
            // confirmed. An occasional duplicate is safer than losing an open.
            if ((result == TabResult.NotStarted || (result != TabResult.Confirmed && !string.IsNullOrWhiteSpace(path))) &&
                enabled && epoch == generation) LaunchExplorer(path);
        })) LaunchExplorer(path);
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
        if (!hidden.ContainsKey(hwnd) && NativeMethods.IsExplorerWindow(hwnd))
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
        if (eventType == NativeMethods.EventObjectDestroy && objectId == NativeMethods.ObjIdWindow)
        {
            handledWindows.TryRemove(hwnd, out _);
            hidden.TryRemove(hwnd, out _);
            return;
        }
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

        var state = Cloak(hwnd);
        if (state is null) return;
        hidden.TryAdd(hwnd, state);

        log.Write($"Captured Explorer window 0x{hwnd.ToInt64():X}");
        var epoch = generation;
        if (!Enqueue(() => RedirectWindow(hwnd, primary, state, epoch))) Restore(hwnd);
    }

    private IntPtr OnKeyboardEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
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
            // Releasing Shift while typing must not disable routing for five seconds.
        }

        if (enabled && key.VkCode == NativeMethods.VkE && isDown)
        {
            var winDown = leftWinDown || rightWinDown ||
                          (NativeMethods.GetAsyncKeyState(NativeMethods.VkLwin) & 0x8000) != 0 ||
                          (NativeMethods.GetAsyncKeyState(NativeMethods.VkRwin) & 0x8000) != 0;
            var shiftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VkShift) & 0x8000) != 0;
            if (winDown && shiftDown)
                Interlocked.Exchange(ref shiftBypassUntil, Environment.TickCount64 + 1500);
            if (winDown && !shiftDown)
            {
                if (suppressingE) return new IntPtr(1); // Ignore auto-repeat.
                var primary = FindPrimaryExplorer(IntPtr.Zero);
                if (primary != IntPtr.Zero)
                {
                    suppressingE = true;
                    OpenInTab();
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

    private async Task RedirectWindow(IntPtr newWindow, IntPtr primary, HiddenWindow state, int epoch)
    {
        try
        {
            var (source, path) = await WaitForPath(newWindow, state.Started, epoch);
            using var sourceLease = source;
            log.Write($"Source resolved after {Environment.TickCount64 - state.Started} ms");
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

            if (!IsActive(state.Started, epoch) || !hidden.ContainsKey(newWindow)) return;
            if (primary == IntPtr.Zero || await CreateNativeTab(primary, path, state.Started, epoch) != TabResult.Confirmed)
            {
                log.Write($"Tab transaction failed safely; restoring window for {path}");
                Restore(newWindow);
                return;
            }

            if (source is null || !SameLocation(source.Path, path) ||
                NativeMethods.GetShellTabHosts(newWindow).Count != 1)
            {
                log.Write("Source changed during routing; leaving it open");
                Restore(newWindow);
                return;
            }

            lock (state)
            {
                // Timeout/pause can win while an RPC is in flight. Never close a
                // source window that has already been returned to the user.
                if (!IsActive(state.Started, epoch) || !hidden.ContainsKey(newWindow)) return;
                if (!NativeMethods.PostMessage(newWindow, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero))
                {
                    Restore(newWindow);
                    return;
                }
                // Keep watchdog ownership until DESTROY, in case WM_CLOSE is ignored.
                lastExplorerWindow = primary;
                log.Write($"Docked in {Environment.TickCount64 - state.Started} ms: {path}");
            }
        }
        catch (Exception exception)
        {
            log.Write($"Redirect error: {exception.Message}");
            Restore(newWindow);
        }
    }

    private async Task<TabResult> CreateNativeTab(IntPtr primary, string? path, long started, int epoch, int budget = TransactionBudgetMs)
    {
        var sent = false;
        var held = false;
        ExplorerEntry? created = null;
        try
        {
            var remaining = budget - (int)(Environment.TickCount64 - started);
            if (remaining <= 0 || !await tabGate.WaitAsync(remaining, stopping.Token)) return TabResult.NotStarted;
            held = true;
            if (!IsActive(started, epoch, budget) || !NativeMethods.IsExplorerWindow(primary)) return TabResult.NotStarted;
            var host = NativeMethods.FindShellTabHost(primary);
            if (host == IntPtr.Zero) return TabResult.NotStarted;
            // Native child-window snapshots avoid an O(all tabs) COM scan before
            // every command. Resolve the new child back to its exact browser via
            // IShellBrowser/IOleWindow before navigation.
            var before = NativeMethods.GetShellTabHosts(primary);
            if (before.Count == 0 || !IsActive(started, epoch, budget)) return TabResult.NotStarted;
            var registrationCount = string.IsNullOrWhiteSpace(path) ? -1 : shell.Count;
            // Deliver the command on Explorer's UI thread before claiming a
            // child. A queued PostMessage could mistake a user's earlier Ctrl+T
            // for our own tab. Timeout is uncertain, so never retry it blindly.
            sent = true;
            if (NativeMethods.SendMessageTimeout(host, NativeMethods.WmCommand,
                new IntPtr(NativeMethods.CmdNewTab), IntPtr.Zero, 0x22, 1000, out _) == IntPtr.Zero)
                return TabResult.Unconfirmed;
            if (NativeMethods.IsIconic(primary)) NativeMethods.ShowWindow(primary, NativeMethods.SwRestore);
            NativeMethods.SetForegroundWindow(primary);

            IntPtr newTab = IntPtr.Zero;
            while (IsActive(started, epoch, budget) && newTab == IntPtr.Zero)
            {
                var candidates = NativeMethods.GetShellTabHosts(primary).Except(before).ToArray();
                if (candidates.Length > 1)
                {
                    log.Write("Concurrent tab creation is ambiguous; preserving the source window");
                    return TabResult.Unconfirmed;
                }
                if (candidates.Length == 1) newTab = candidates[0];
                else await Task.Delay(10, stopping.Token);
            }
            // Once claimed, other requests may create tabs while this exact tab
            // navigates. A slow network folder doesn't serialize every request.
            tabGate.Release();
            held = false;
            if (newTab != IntPtr.Zero && string.IsNullOrWhiteSpace(path) && IsActive(started, epoch, budget))
                return TabResult.Confirmed; // Win+E requires no COM lookup or navigation.
            var nextProbe = Environment.TickCount64 + 120;
            while (newTab != IntPtr.Zero && NativeMethods.IsWindow(newTab) && IsActive(started, epoch, budget) && created is null)
            {
                var count = shell.Count;
                if (count != registrationCount || Environment.TickCount64 >= nextProbe)
                {
                    created = shell.Enumerate(primary, newTab, firstOnly: true).FirstOrDefault();
                    registrationCount = count;
                    nextProbe = Environment.TickCount64 + 120;
                }
                if (created is null) await Task.Delay(10, stopping.Token);
            }
            if (created is null || !IsActive(started, epoch, budget)) return TabResult.Unconfirmed;
            log.Write($"Tab registered after {Environment.TickCount64 - started} ms");
            if (string.IsNullOrWhiteSpace(path)) return TabResult.Confirmed;
            if (!created.Navigate(NormalizeForNavigation(path))) return TabResult.Unconfirmed;
            // Navigate2 is asynchronous: success means accepted, not arrived.
            while (IsActive(started, epoch, budget))
            {
                var actual = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("::{")
                    ? created.Path : created.FileSystemPath;
                if (SameLocation(actual, path)) return TabResult.Confirmed;
                await Task.Delay(10, stopping.Token);
            }
            return TabResult.Unconfirmed;
        }
        catch (OperationCanceledException) { return sent ? TabResult.Unconfirmed : TabResult.NotStarted; }
        finally
        {
            created?.Dispose();
            if (held) tabGate.Release();
        }
    }

    private async Task<(ExplorerEntry? Source, string Path)> WaitForPath(IntPtr hwnd, long started, int epoch)
    {
        var registrationCount = -1;
        var nextProbe = 0L;
        while (IsActive(started, epoch) && NativeMethods.IsWindow(hwnd))
        {
            var count = shell.Count;
            if (count == registrationCount && Environment.TickCount64 < nextProbe)
            {
                await Task.Delay(10, stopping.Token);
                continue;
            }
            registrationCount = count;
            nextProbe = Environment.TickCount64 + 120;
            var entries = shell.Enumerate(hwnd, firstOnly: true);
            ExplorerEntry? selected = null;
            try
            {
                if (entries.Count != 0)
                {
                    var path = entries[0].Path;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        selected = entries[0];
                        return (selected, path);
                    }
                }
            }
            finally
            {
                foreach (var entry in entries)
                {
                    if (!ReferenceEquals(entry, selected)) entry.Dispose();
                }
            }
            await Task.Delay(10, stopping.Token);
        }

        return (null, string.Empty);
    }

    private IntPtr FindPrimaryExplorer(IntPtr exclude)
    {
        var remembered = lastExplorerWindow;
        if (remembered != exclude && !hidden.ContainsKey(remembered) && NativeMethods.IsExplorerWindow(remembered) && NativeMethods.IsWindowVisible(remembered))
        {
            return remembered;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != exclude && !hidden.ContainsKey(foreground) && NativeMethods.IsExplorerWindow(foreground) && NativeMethods.IsWindowVisible(foreground))
        {
            lastExplorerWindow = foreground;
            return foreground;
        }

        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (window == exclude || hidden.ContainsKey(window) || !NativeMethods.IsWindowVisible(window) || !NativeMethods.IsExplorerWindow(window))
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

    private HiddenWindow? Cloak(IntPtr hwnd)
    {
        var cloak = 1;
        var started = Environment.TickCount64;
        if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaCloak, ref cloak, sizeof(int)) == 0)
            return new HiddenWindow(started, null);
        // Windows may reject cross-process cloaking. Preserve the existing style
        // and never take ownership of someone else's layered-window attributes.
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        if ((style & NativeMethods.WsExLayered) != 0) return null;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(style | NativeMethods.WsExLayered));
        if (!NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 0, NativeMethods.LwaAlpha))
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(style));
            return null;
        }
        return new HiddenWindow(started, style);
    }

    private void Restore(IntPtr hwnd)
    {
        if (!hidden.TryGetValue(hwnd, out var state))
        {
            return;
        }

        lock (state)
        {
            if (!hidden.TryRemove(hwnd, out _) || !NativeMethods.IsWindow(hwnd)) return;
            var cloak = 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaCloak, ref cloak, sizeof(int));
            if (state.OriginalStyle is long style)
            {
                NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, NativeMethods.LwaAlpha);
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(style));
            }
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SwRestore);
        }
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
        // COM events and async continuations need a real STA message pump.
        using var control = new Control();
        _ = control.Handle;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        dispatcher = control;
        shell.WarmUp();
        Drain();
        if (!stopping.IsCancellationRequested) Application.Run();
        shell.Dispose();
    }

    private bool Enqueue(Func<Task> action)
    {
        if (disposed) return false;
        if (Interlocked.Increment(ref outstanding) > 64)
        {
            Interlocked.Decrement(ref outstanding);
            return false;
        }
        queue.Enqueue(action);
        try { dispatcher?.BeginInvoke(Drain); }
        catch (InvalidOperationException) { return false; }
        return true;
    }

    private void Drain()
    {
        while (queue.TryDequeue(out var action)) _ = Run(action);
    }

    private async Task Run(Func<Task> action)
    {
        try { if (!stopping.IsCancellationRequested) await action(); }
        catch (Exception exception) { log.Write($"Transaction error: {exception.Message}"); }
        finally { Interlocked.Decrement(ref outstanding); }
    }

    private bool IsActive(long started, int epoch, int budget = TransactionBudgetMs) => enabled && !stopping.IsCancellationRequested &&
        generation == epoch && Environment.TickCount64 - started < budget;

    private void RestoreExpired()
    {
        foreach (var pair in hidden)
        {
            if (Environment.TickCount64 - pair.Value.Started < TransactionBudgetMs) continue;
            Restore(pair.Key);
            log.Write("Watchdog released an expired capture");
        }
    }

    internal static bool SameLocation(string actual, string expected) =>
        CanonicalLocation(actual).Equals(CanonicalLocation(expected), StringComparison.OrdinalIgnoreCase);

    private static string CanonicalLocation(string path)
    {
        var normalized = NormalizeForNavigation(path);
        var trimmed = normalized.TrimEnd('\\', '/');
        // C: is drive-relative; C:\ is the drive root. Do not equate them.
        return trimmed.Length == 2 && trimmed[1] == ':' && trimmed.Length != normalized.Length
            ? trimmed + "\\" : trimmed;
    }

    private enum TabResult { NotStarted, Unconfirmed, Confirmed }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        enabled = false;
        stopping.Cancel();
        watchdog.Dispose();
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

        try { dispatcher?.BeginInvoke(() => Application.ExitThread()); }
        catch (InvalidOperationException) { }
        worker.Join(TimeSpan.FromMilliseconds(100));
    }

    private sealed record HiddenWindow(long Started, long? OriginalStyle);
}
