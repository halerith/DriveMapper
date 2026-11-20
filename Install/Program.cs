using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using DriveMapper;

namespace Install;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--cleanup-retired")
        {
            if (!IsAdministrator()) return 740;
            try { return RetiredFiles.RunHelper(args[1], args[2], int.Parse(args[3])); }
            catch { return 1603; }
        }
        InstallerArguments options;
        try { options = InstallerArguments.Parse(args); }
        catch (FormatException error) { Console.Error.WriteLine(error.Message); return 87; }
        if (options.Command == null)
        {
            var app = new Application();
            ThemeManager.Apply(app);
            app.Run(new InstallerWindow());
            return Environment.ExitCode;
        }
        // Quiet mode never displays UAC or dialogs. Intune supplies SYSTEM context.
        var result = options.Quiet && !IsAdministrator()
            ? new InstallResult(740, "Administrator rights or SYSTEM context are required.")
            : Execute(options);
        if (!options.Quiet)
        {
            var app = new Application();
            ThemeManager.Apply(app);
            MessageBox.Show(result.Message, Product.SetupTitle, MessageBoxButton.OK,
                result.ExitCode is 0 or 3010 ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        return result.ExitCode;
    }

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static InstallResult Execute(InstallerArguments options, Action<string>? progress = null)
    {
        if (IsAdministrator()) return InstallerEngine.Execute(options.Command!, options.Force, progress);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "-quiet -" + options.Command + (options.Force ? " -force" : "")
            };
            using var elevated = Process.Start(start) ?? throw new IOException("Could not start elevated setup.");
            elevated.WaitForExit();
            return new InstallResult(elevated.ExitCode, elevated.ExitCode switch
            {
                0 => "Operation completed.",
                3010 => "Operation completed. Restart required to finish file cleanup.",
                _ => $"Operation failed (exit {elevated.ExitCode}). See the installer log."
            });
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { return new InstallResult(1223, "Administrator approval was cancelled."); }
        catch (Exception error)
        {
            InstallLog.TryWrite("Setup could not complete: " + error);
            return new InstallResult(1603, error.Message + "\n\nInstaller log: " + InstallLog.LogPath);
        }
    }
}
