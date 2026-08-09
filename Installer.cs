using Microsoft.Win32;
using System.Diagnostics;

namespace PaneWeaver;

internal static class Installer
{
    internal const string RunValueName = "PaneWeaver";
    internal static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PaneWeaver");
    internal static string InstalledExecutable => Path.Combine(InstallDirectory, "PaneWeaver.exe");

    internal static void InstallAndLaunch()
    {
        Directory.CreateDirectory(InstallDirectory);
        var source = Application.ExecutablePath;
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(InstalledExecutable), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, InstalledExecutable, true);
        }

        SetStartup(true);
        RegisterContextVerb("Folder");
        RegisterContextVerb("Directory");
        RegisterContextVerb("Drive");

        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(InstalledExecutable), StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = InstalledExecutable,
                Arguments = "--background --welcome",
                UseShellExecute = true
            });
        }
    }

    internal static bool IsInstalled => File.Exists(InstalledExecutable);

    internal static bool StartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(RunValueName) is string value &&
                   value.Contains("PaneWeaver.exe", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var executable = File.Exists(InstalledExecutable) ? InstalledExecutable : Application.ExecutablePath;
            key.SetValue(RunValueName, $"\"{executable}\" --background");
        }
        else
        {
            key.DeleteValue(RunValueName, false);
        }
    }

    internal static void Disable()
    {
        SetStartup(false);
        UnregisterContextVerb("Folder");
        UnregisterContextVerb("Directory");
        UnregisterContextVerb("Drive");
    }

    private static void RegisterContextVerb(string shellClass)
    {
        using var verb = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{shellClass}\shell\PaneWeaver");
        verb.SetValue("", "Open in PaneWeaver tab");
        verb.SetValue("Icon", $"{InstalledExecutable},0");
        using var command = verb.CreateSubKey("command");
        command.SetValue("", $"\"{InstalledExecutable}\" --open \"%1\"");
    }

    private static void UnregisterContextVerb(string shellClass)
    {
        using var shell = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{shellClass}\shell", writable: true);
        shell?.DeleteSubKeyTree("PaneWeaver", false);
    }
}
