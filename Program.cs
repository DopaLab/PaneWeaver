using System.IO.Pipes;

namespace PaneWeaver;

internal static class Program
{
    private const string MutexName = "Local\\PaneWeaver.CurrentUser.Instance.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Installer.InstallAndLaunch();
                MessageBox.Show(
                    "PaneWeaver is installed and running.\n\nNew File Explorer windows will dock as native tabs. Hold Shift while opening a folder whenever you intentionally want a separate window.",
                    "PaneWeaver",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "PaneWeaver setup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        var path = ReadPathArgument(args);
        var welcome = args.Contains("--welcome", StringComparer.OrdinalIgnoreCase);
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            return SendToRunningInstance(path);
        }

        using var context = new PaneWeaverContext(welcome, path);
        Application.Run(context);
        return 0;
    }

    private static string? ReadPathArgument(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--open", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
    }

    private static int SendToRunningInstance(string? path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PaneWeaverContext.PipeName, PipeDirection.Out);
            pipe.Connect(1000);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(string.IsNullOrWhiteSpace(path) ? "NEWTAB" : $"OPEN\t{path}");
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "PaneWeaver is not responding", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }
    }
}
