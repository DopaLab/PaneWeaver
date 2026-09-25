using System.Diagnostics;
using PaneWeaver;

internal static class Tests
{
    private static int failures;
    private static void Check(bool condition, string name)
    {
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
        if (!condition) failures++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Check(ExplorerRouter.SameLocation(@"C:\Test\", @"c:\test"), "case and separator normalization");
        Check(!ExplorerRouter.SameLocation(@"C:\100%20", @"C:\100 "), "literal percent path stays distinct");
        Check(ExplorerRouter.SameLocation("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "shell:MyComputerFolder"), "virtual folder aliases");
        Check(!ExplorerRouter.SameLocation(@"C:", @"C:\"), "drive-relative path does not confirm a drive root");
        var observerLog = new ActivityLog();
        var observed = false;
        observerLog.Added += _ => throw new ObjectDisposedException("closed log window");
        observerLog.Added += _ => observed = true;
        observerLog.Write("recovery remains operational");
        Check(observed && observerLog.Snapshot().Contains("recovery remains operational"), "diagnostic failure cannot break routing or other observers");
        if (args.Contains("--cleanup-test-windows"))
        {
            using var cleanupShell = new ExplorerCom();
            var all = cleanupShell.Enumerate();
            var fixturePrefix = Path.Combine(Path.GetTempPath(), "PaneWeaver-tests-");
            foreach (var group in all.GroupBy(e => e.Hwnd))
            {
                var paths = group.Select(e => e.Path).ToArray();
                if (paths.Any(p => p.StartsWith(fixturePrefix, StringComparison.OrdinalIgnoreCase)) &&
                    paths.All(p => p.StartsWith(fixturePrefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith("::{") || p.Length == 0))
                    NativeMethods.PostMessage(group.Key, 0x0112, new IntPtr(0xF060), IntPtr.Zero);
            }
            foreach (var e in all) e.Dispose();
            return 0;
        }
        if (!args.Contains("--live") && !args.Contains("--faults") && !args.Contains("--smoke-installed")) return failures == 0 ? 0 : 1;
        Application.EnableVisualStyles();
        using var control = new Control();
        _ = control.Handle;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        control.BeginInvoke(async () =>
        {
            try
            {
                if (args.Contains("--smoke-installed")) await SmokeInstalled();
                else await Live(args.Contains("--faults"));
            }
            catch (Exception ex) { failures++; Console.WriteLine(ex); }
            finally { Application.ExitThread(); }
        });
        Application.Run();
        return failures == 0 ? 0 : 1;
    }

    private static async Task Live(bool faultsOnly)
    {
        if (Process.GetProcessesByName("PaneWeaver").Length != 0)
            throw new InvalidOperationException("Exit the running PaneWeaver instance before live tests.");
        using var shell = new ExplorerCom();
        var root = Path.Combine(Path.GetTempPath(), "PaneWeaver-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Every close below is restricted to a test-owned path under this unique root.
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        ExplorerEntry? original = null;
        var boot = Stopwatch.StartNew();
        while (original is null && boot.ElapsedMilliseconds < 10000)
        {
            foreach (var entry in shell.Enumerate())
            {
                if (ExplorerRouter.SameLocation(entry.Path, root)) original = entry;
                else entry.Dispose();
            }
            if (original is null) await Task.Delay(30);
        }
        if (original is null) throw new Exception("Test Explorer did not start.");
        using (original)
        {
            var log = new ActivityLog();
            log.Added += Console.WriteLine;
            using var router = new ExplorerRouter(log);
            router.Start(original.Hwnd);
            try
            {
                if (faultsOnly)
                {
                    var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Func<Task> stall = () => { blocked.SetResult(); Thread.Sleep(5500); return Task.CompletedTask; };
                    typeof(ExplorerRouter).GetMethod("Enqueue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .Invoke(router, new object[] { stall });
                    await blocked.Task;
                    var stalledPath = Path.Combine(root, "watchdog");
                    Directory.CreateDirectory(stalledPath);
                    await WaitForLog(log, s => s.Contains("Watchdog"), () =>
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/n,\"{stalledPath}\"") { UseShellExecute = true }));
                    await AssertRestored(shell, stalledPath, "independent watchdog restores during blocked broker");
                    await Task.Delay(2200);
                    await AssertRestored(shell, stalledPath, "late broker continuation does not close restored source");

                    var pausedPath = Path.Combine(root, "pause");
                    Directory.CreateDirectory(pausedPath);
                    void PauseAtCapture(string line) { if (line.Contains("Captured Explorer")) router.Enabled = false; }
                    log.Added += PauseAtCapture;
                    await WaitForLog(log, s => s.Contains("Routing paused"), () =>
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/n,\"{pausedPath}\"") { UseShellExecute = true }));
                    log.Added -= PauseAtCapture;
                    await Task.Delay(300);
                    await AssertRestored(shell, pausedPath, "pause restores in-flight capture");
                    return;
                }
                for (int i = 0; i < 5; i++)
                {
                    var beforeTabs = NativeMethods.GetShellTabHosts(original.Hwnd).Count;
                    var shortcut = Stopwatch.StartNew();
                    await WaitForLog(log, line => line.Contains("Direct tab:"), () => router.OpenInTab());
                    Console.WriteLine($"NEW_TAB_LATENCY_MS {shortcut.ElapsedMilliseconds}");
                    Check(NativeMethods.GetShellTabHosts(original.Hwnd).Count == beforeTabs + 1, "Win+E routing path adds exactly one native tab");
                }
                var samples = new List<long>();
                for (int i = 0; i < 12; i++)
                {
                    var path = Path.Combine(root, i == 3 ? "literal%20name" : $"direct-{i}");
                    Directory.CreateDirectory(path);
                    var watch = Stopwatch.StartNew();
                    await WaitForLog(log, line => line.Contains("Direct tab:"), () => router.OpenInTab(path));
                    samples.Add(watch.ElapsedMilliseconds);
                    var elapsed = watch.ElapsedMilliseconds;
                    var found = await AwaitPaths(shell, new[] { path }, original.Hwnd);
                    Check(found, $"direct {i}: exact path in primary (broker {elapsed} ms)");
                }
                samples.Sort();
                Console.WriteLine($"DIRECT_LATENCY_MS n={samples.Count} median={samples[samples.Count / 2]} p95={samples[(int)Math.Ceiling(samples.Count * .95) - 1]} max={samples[^1]}");

                var paths = Enumerable.Range(0, 3).Select(i => Path.Combine(root, $"burst-{i}")).ToArray();
                foreach (var path in paths) Directory.CreateDirectory(path);
                var browse = Path.Combine(root, "user-browsing");
                Directory.CreateDirectory(browse);
                var burst = Stopwatch.StartNew();
                var completed = 0;
                await WaitForLog(log, line => line.Contains("Direct tab:") && Interlocked.Increment(ref completed) == paths.Length,
                    () => { foreach (var path in paths) router.OpenInTab(path); original.Navigate(browse); });
                Check(await AwaitPaths(shell, paths, original.Hwnd), $"burst arrived ({burst.ElapsedMilliseconds} ms)");
                Check(ExplorerRouter.SameLocation(original.Path, browse), "concurrent browsing preserved original tab");

                for (int i = 0; i < 3; i++)
                {
                    var path = Path.Combine(root, $"capture-{i}");
                    Directory.CreateDirectory(path);
                    var watch = Stopwatch.StartNew();
                    await WaitForLog(log, line => line.Contains("Docked") || line.Contains("restoring window") || line.Contains("Watchdog"),
                        () => Process.Start(new ProcessStartInfo("explorer.exe", $"/n,\"{path}\"") { UseShellExecute = true }));
                    Check(await AwaitPaths(shell, new[] { path }, original.Hwnd), $"external capture {i} ({watch.ElapsedMilliseconds} ms)");
                }
                await Task.Delay(200);
                var entries = shell.Enumerate();
                try
                {
                    Check(entries.Where(e => e.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)).All(e => e.Hwnd == original.Hwnd), "all owned tabs share one Explorer window");
                }
                finally { foreach (var e in entries) e.Dispose(); }
            }
            finally
            {
                router.Enabled = false;
                var cleanup = shell.Enumerate();
                foreach (var group in cleanup.GroupBy(e => e.Hwnd))
                {
                    var paths = group.Select(e => e.Path).ToArray();
                    if (paths.Any(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) &&
                        paths.All(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith("::{") || p.Length == 0))
                        NativeMethods.PostMessage(group.Key, 0x0112, new IntPtr(0xF060), IntPtr.Zero);
                }
                foreach (var entry in cleanup) entry.Dispose();
                Console.WriteLine($"Test fixtures retained at {root}");
            }
        }
    }

    private static async Task<bool> AwaitPaths(ExplorerCom shell, string[] paths, IntPtr target)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 4000)
        {
            var entries = shell.Enumerate(target);
            try
            {
                var actual = entries.Select(e => e.Path).ToArray();
                if (paths.All(path => actual.Count(value => ExplorerRouter.SameLocation(value, path)) == 1)) return true;
            }
            finally { foreach (var e in entries) e.Dispose(); }
            await Task.Delay(15);
        }
        return false;
    }

    private static async Task WaitForLog(ActivityLog log, Func<string, bool> matches, Action trigger)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string line) { if (matches(line)) done.TrySetResult(); }
        log.Added += Handler;
        try { trigger(); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { log.Added -= Handler; }
    }

    private static async Task AssertRestored(ExplorerCom shell, string path, string label)
    {
        var limit = Stopwatch.StartNew();
        while (limit.ElapsedMilliseconds < 3000)
        {
            var entries = shell.Enumerate();
            try
            {
                var entry = entries.FirstOrDefault(e => ExplorerRouter.SameLocation(e.Path, path));
                if (entry is not null)
                {
                    Check(NativeMethods.IsWindowVisible(entry.Hwnd) &&
                        (NativeMethods.GetWindowLongPtr(entry.Hwnd, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExLayered) == 0, label);
                    return;
                }
            }
            finally { foreach (var e in entries) e.Dispose(); }
            await Task.Delay(30);
        }
        Check(false, label);
    }

    private static async Task SmokeInstalled()
    {
        using var shell = new ExplorerCom();
        var root = Path.Combine(Path.GetTempPath(), "PaneWeaver-tests-installed-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "published-build");
        Directory.CreateDirectory(child);
        try
        {
            await SendOpen(root);
            Check(await AwaitPaths(shell, new[] { root }, IntPtr.Zero), "installed executable opens first Explorer window");
            var entries = shell.Enumerate();
            IntPtr target;
            try { target = entries.Single(e => ExplorerRouter.SameLocation(e.Path, root)).Hwnd; }
            finally { foreach (var e in entries) e.Dispose(); }
            await SendOpen(child);
            Check(await AwaitPaths(shell, new[] { root, child }, target), "published executable routes IPC open into the same native window");
        }
        finally
        {
            var entries = shell.Enumerate();
            foreach (var group in entries.GroupBy(e => e.Hwnd))
            {
                var paths = group.Select(e => e.Path).ToArray();
                if (paths.Any(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) &&
                    paths.All(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith("::{") || p.Length == 0))
                    NativeMethods.PostMessage(group.Key, 0x0112, new IntPtr(0xF060), IntPtr.Zero);
            }
            foreach (var e in entries) e.Dispose();
        }
    }

    private static async Task SendOpen(string path)
    {
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PaneWeaverContext.PipeName, System.IO.Pipes.PipeDirection.Out);
        await pipe.ConnectAsync(2000);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync("OPEN\t" + path);
    }
}
