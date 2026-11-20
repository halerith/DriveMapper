using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace Install;

internal static class ForcedCleanup
{
    // These roots contain application settings and ownership only. Never touch HKCU\\Network,
    // Explorer mount points, WNet APIs, user files on shares, or the Application event log itself.
    public static void RegistryAndEventSource()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            machine.DeleteSubKeyTree(@"Software\Policies\DriveMapper", false);
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, view);
            foreach (string sid in users.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-", StringComparison.Ordinal) || sid.EndsWith("_Classes", StringComparison.Ordinal)) continue;
                using var user = users.OpenSubKey(sid, true) ?? throw new IOException("User registry changed during cleanup: " + sid);
                RegistryCleanup.RemoveUserRegistry(user);
            }
        }
        if (EventLog.SourceExists("DriveMapper"))
        {
            if (EventLog.LogNameFromSourceName("DriveMapper", ".") != "Application")
                throw new InvalidDataException("Refusing to remove a DriveMapper source from an unexpected event log.");
            EventLog.DeleteEventSource("DriveMapper");
        }
    }

    public static bool Logs(string target)
    {
        bool reboot = RemoveLogFiles(Path.Combine(target, "Logs"), new[] { "service.log", "service.log.1" });
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profiles = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        foreach (string sid in users.GetSubKeyNames().Where(s => s.StartsWith("S-1-") && !s.EndsWith("_Classes")))
        {
            using var profile = profiles?.OpenSubKey(sid);
            if (profile?.GetValue("ProfileImagePath") is not string path) continue;
            path = Environment.ExpandEnvironmentVariables(path);
            reboot |= RemoveLogFiles(Path.Combine(path, "AppData", "Local", "DriveMapper", "Logs"), new[] { "runtime.log", "runtime.log.1" });
        }
        reboot |= RemoveLogFiles(Path.Combine(InstallLog.Root, "Logs"), new[] { "install.log", "install.log.1", "service.log", "service.log.1" });
        TryRemoveEmpty(target);
        TryRemoveEmpty(InstallLog.Root);
        return reboot;
    }

    internal static bool RemoveLogFiles(string directory, IEnumerable<string> names)
    {
        FileDeployment.EnsureNoLinks(directory);
        if (!Directory.Exists(directory)) return false;
        bool reboot = false;
        foreach (string name in names)
        {
            string path = Path.Combine(directory, name);
            FileDeployment.EnsureNoLinks(path);
            if (!File.Exists(path)) continue;
            try { File.Delete(path); }
            catch (IOException)
            {
                // Rename before scheduling: a later install must not lose a newly created log at reboot.
                string retired = Path.Combine(directory, "DriveMapper.retired-log-" + Guid.NewGuid().ToString("N"));
                File.Move(path, retired);
                RetiredFiles.ScheduleFile(retired);
                reboot = true;
            }
        }
        foreach (string retired in Directory.GetFiles(directory, "DriveMapper.retired-log-*"))
        {
            if (!Guid.TryParseExact(Path.GetFileName(retired)["DriveMapper.retired-log-".Length..], "N", out _)) continue;
            FileDeployment.EnsureNoLinks(retired);
            try { File.Delete(retired); }
            catch (IOException) { RetiredFiles.ScheduleFile(retired); reboot = true; }
        }
        TryRemoveEmpty(directory);
        return reboot;
    }

    internal static void TryRemoveEmpty(string directory)
    {
        FileDeployment.EnsureNoLinks(directory);
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}
