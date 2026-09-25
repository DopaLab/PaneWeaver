using System.IO.Pipes;

namespace PaneWeaver;

internal sealed class PaneWeaverContext : ApplicationContext
{
    internal const string PipeName = "PaneWeaver.CurrentUser.Broker.v1";
    private readonly ActivityLog log = new();
    private readonly ExplorerRouter router;
    private readonly NotifyIcon tray;
    private readonly Icon appIcon;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ToolStripMenuItem enabledItem;
    private readonly ToolStripMenuItem startupItem;
    private LogForm? logForm;

    internal PaneWeaverContext(bool welcome, string? initialPath)
    {
        appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        router = new ExplorerRouter(log);
        router.Start();

        enabledItem = new ToolStripMenuItem("Routing enabled")
        {
            Checked = true,
            CheckOnClick = true
        };
        enabledItem.CheckedChanged += (_, _) =>
        {
            router.Enabled = enabledItem.Checked;
            enabledItem.Text = enabledItem.Checked ? "Routing enabled" : "Routing paused";
        };

        startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = Installer.StartupEnabled,
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) => Installer.SetStartup(startupItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open a new Explorer tab", null, (_, _) => router.OpenInTab()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(enabledItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripMenuItem("Activity log", null, (_, _) => ShowLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Disable PaneWeaver", null, (_, _) => DisableAndExit()));
        menu.Items.Add(new ToolStripMenuItem("Exit until next sign-in", null, (_, _) => ExitThread()));

        tray = new NotifyIcon
        {
            Icon = appIcon,
            Text = "PaneWeaver — every folder, one native Explorer window",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => router.OpenInTab();

        _ = Task.Run(() => PipeServer(cancellation.Token));

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            router.OpenInTab(initialPath);
        }

        if (welcome)
        {
            tray.ShowBalloonTip(
                5000,
                "PaneWeaver is on",
                "New Explorer windows now dock as native tabs. Hold Shift while opening a folder to allow a separate window.",
                ToolTipIcon.Info);
        }
    }

    private async Task PipeServer(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                using var reader = new StreamReader(pipe);
                var command = await reader.ReadLineAsync(token);
                if (command is null)
                {
                    continue;
                }

                if (command.StartsWith("OPEN\t", StringComparison.Ordinal))
                {
                    router.OpenInTab(command[5..]);
                }
                else if (command == "NEWTAB")
                {
                    router.OpenInTab();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                log.Write($"Broker error: {exception.Message}");
                await Task.Delay(150, token);
            }
        }
    }

    private void ShowLog()
    {
        if (logForm is null || logForm.IsDisposed)
        {
            logForm = new LogForm(log, appIcon);
        }

        logForm.Show();
        logForm.Activate();
    }

    private void DisableAndExit()
    {
        Installer.Disable();
        MessageBox.Show(
            "PaneWeaver has been disabled and removed from Windows startup. The executable was left in your local app folder so you can turn it on again without reinstalling.",
            "PaneWeaver disabled",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        cancellation.Cancel();
        tray.Visible = false;
        tray.Dispose();
        router.Dispose();
        appIcon.Dispose();
        base.ExitThreadCore();
    }
}
