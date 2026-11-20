using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Install;

internal static class RetiredFiles
{
    public static bool Cleanup(string target, string retired, string helperSource, Action<string> log)
    {
        try { FileDeployment.DeleteWorkDirectory(target, retired); return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log("Old files are locked: " + retired + ". " + error.Message);
            // Register a reboot fallback first; helper launch/crash cannot strand a retired executable.
            Schedule(target, retired);
            try { StartHelper(target, retired, helperSource); }
            catch (Exception helperError) { log("Cleanup will finish at reboot: " + helperError.Message); }
            return true;
        }
    }

    public static bool CleanupLeftovers(string target, string helperSource, Action<string> log, string? except = null)
    {
        string parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) return false;
        bool reboot = CleanupMaintenance(log);
        foreach (string path in Directory.GetDirectories(parent, Path.GetFileName(target) + ".*"))
            if (FileDeployment.IsWorkDirectory(target, path) && !string.Equals(path, except, StringComparison.OrdinalIgnoreCase))
                reboot |= Cleanup(target, path, helperSource, log);
        return reboot;
    }

    private static bool CleanupMaintenance(Action<string> log)
    {
        string maintenance = Path.Combine(InstallLog.Root, "Maintenance");
        FileDeployment.EnsureNoLinks(maintenance);
        if (!Directory.Exists(maintenance)) return false;
        bool reboot = false;
        foreach (string directory in Directory.GetDirectories(maintenance))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            FileDeployment.EnsureNoLinks(directory);
            var entries = Directory.GetFileSystemEntries(directory);
            if (entries.Any(path => !Path.GetFileName(path).Equals("Install.exe", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path)))
            { log("Preserved unexpected maintenance contents: " + directory); continue; }
            foreach (string file in entries)
            {
                FileDeployment.EnsureNoLinks(file);
                try { File.Delete(file); }
                catch (IOException) { ScheduleFile(file); reboot = true; }
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            else { ScheduleFile(directory); reboot = true; }
        }
        ForcedCleanup.TryRemoveEmpty(maintenance);
        return reboot;
    }

    private static void StartHelper(string target, string retired, string source)
    {
        string maintenance = Path.Combine(InstallLog.Root, "Maintenance");
        InstallLog.EnsureDirectory(InstallLog.Root);
        InstallLog.EnsureDirectory(maintenance);
        string directory = Path.Combine(maintenance, Guid.NewGuid().ToString("N"));
        InstallLog.EnsureDirectory(directory);
        string helper = Path.Combine(directory, "Install.exe");
        FileDeployment.EnsureNoLinks(source);
        File.Copy(source, helper);
        InstallSafety.SecureFile(helper);
        // Only this unique maintenance image is deferred, never the live installation path.
        ScheduleFile(helper);
        ScheduleFile(directory);
        ScheduleFile(maintenance);
        ScheduleFile(InstallLog.Root);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "--cleanup-retired", target, retired, Environment.ProcessId.ToString() }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start retired-file cleanup.");
    }

    public static int RunHelper(string target, string retired, int parentId)
    {
        target = InstallSafety.ValidateDirectory(target);
        if (!FileDeployment.IsWorkDirectory(target, retired)) return 87;
        try
        {
            try
            {
                using var parent = Process.GetProcessById(parentId);
                if (!parent.WaitForExit(60_000)) return 3010;
            }
            catch (ArgumentException) { }
            FileDeployment.DeleteWorkDirectory(target, retired);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 3010; }
    }

    private static void Schedule(string target, string path)
    {
        if (!FileDeployment.IsWorkDirectory(target, path)) throw new InvalidDataException("Unexpected deferred cleanup path.");
        FileDeployment.EnsureNoLinks(path);
        var entries = Directory.GetFileSystemEntries(path);
        foreach (string entry in entries)
        {
            if (!FileDeployment.OwnedNames.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase) || Directory.Exists(entry))
                throw new InvalidDataException("Unexpected deferred cleanup contents.");
            FileDeployment.EnsureNoLinks(entry);
        }
        foreach (string entry in entries) ScheduleFile(entry);
        ScheduleFile(path);
    }

    public static void ScheduleFile(string path)
    {
        FileDeployment.EnsureNoLinks(path);
        if (!MoveFileEx(path, null, 4)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not schedule deletion: " + path);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string? replacement, int flags);
}
