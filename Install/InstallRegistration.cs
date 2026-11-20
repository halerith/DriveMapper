using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using DriveMapper;

namespace Install;

internal sealed record InstalledApplication(string? Version, InstallerConfiguration? Config, string[] LegacyTaskNames);

internal static class InstallRegistration
{
    public const string ProductKey = @"SOFTWARE\DriveMapper";
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DriveMapper";

    public static InstalledApplication Read(bool tolerateInvalidConfig = false)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(ProductKey);
        string? version = key?.GetValue("Version") as string;
        string? location = key?.GetValue("InstallLocation") as string;
        var tasks = new List<string>();
        string shortcut = "";
        if (key?.GetValue("InstalledConfig") is string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var config = document.RootElement;
                if (config.TryGetProperty("TargetDirectory", out var target)) location ??= target.GetString();
                if (config.TryGetProperty("ShortcutName", out var name)) shortcut = name.GetString() ?? "";
                // Read legacy task names only for removal, not as configurable new-install features.
                if (config.TryGetProperty("ExeName", out var executable) && executable.GetString() is string oldName)
                { tasks.Add(oldName); tasks.Add(Path.GetFileNameWithoutExtension(oldName)); }
                if (config.TryGetProperty("ScheduledTasks", out var scheduled) && scheduled.ValueKind == JsonValueKind.Array)
                    foreach (var task in scheduled.EnumerateArray())
                        if (task.TryGetProperty("TaskName", out var taskName) && taskName.GetString() is string value) tasks.Add(value);
            }
            catch (Exception error) when (tolerateInvalidConfig && error is JsonException or InvalidOperationException)
            {
                // Forced removal must not depend on a readable legacy JSON blob.
                // The separately stored destination still passes the normal path safety checks.
                shortcut = "";
                tasks.Clear();
            }
        }
        var configuration = location == null ? null : InstallerConfiguration.Validate(new() { TargetDirectory = location, ShortcutName = shortcut });
        return new(version, configuration, tasks.ToArray());
    }

    public static void WithholdDetection()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(ProductKey, true);
        key?.DeleteValue("Version", false);
    }

    public static void WriteLocation(InstallerConfiguration config)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.CreateSubKey(ProductKey);
        key.SetValue("InstallLocation", config.TargetDirectory);
        key.SetValue("InstalledConfig", JsonSerializer.Serialize(config, InstallerConfiguration.JsonOptions));
    }

    public static void Commit(InstallerConfiguration config, string version)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        string installer = Path.Combine(config.TargetDirectory, "Install.exe");
        using (var uninstall = machine.CreateSubKey(UninstallKey))
        {
            uninstall.SetValue("DisplayName", Product.DisplayName);
            uninstall.SetValue("DisplayVersion", version);
            uninstall.SetValue("Publisher", Product.Publisher);
            uninstall.SetValue("InstallLocation", config.TargetDirectory);
            uninstall.SetValue("DisplayIcon", installer);
            uninstall.SetValue("UninstallString", "\"" + installer + "\"");
            uninstall.SetValue("QuietUninstallString", "\"" + installer + "\" -quiet -uninstall");
            uninstall.SetValue("NoModify", 0, RegistryValueKind.DWord);
            uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        using var key = machine.CreateSubKey(ProductKey);
        key.SetValue("InstallTimeUtc", DateTimeOffset.UtcNow.ToString("O"));
        key.SetValue("Version", version); // Detection is committed last.
    }

    public static void Remove()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            machine.DeleteSubKeyTree(ProductKey, false);
            machine.DeleteSubKeyTree(UninstallKey, false);
        }
    }

    public static void EnsureEventSource()
    {
        if (!EventLog.SourceExists("DriveMapper")) EventLog.CreateEventSource(new EventSourceCreationData("DriveMapper", "Application"));
        if (EventLog.LogNameFromSourceName("DriveMapper", ".") != "Application")
            throw new InvalidDataException("DriveMapper Event Log source is registered outside the Application log.");
    }
}
