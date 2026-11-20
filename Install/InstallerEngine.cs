using System.Diagnostics;
using System.Text.Json;
using DriveMapper;

namespace Install;

internal sealed record InstallResult(int ExitCode, string Message);

internal static class InstallerEngine
{
    public static InstallResult Execute(string command, bool force, Action<string>? progress = null)
    {
        if (command is not ("install" or "uninstall") || force && command != "uninstall") return new(87, InstallerArguments.Usage);
        if (!Program.IsAdministrator()) return new(740, "Administrator rights or SYSTEM context are required.");
        using var mutex = new Mutex(false, @"Global\DriveMapper.Install");
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return new(1618, "Another Halerith DriveMapper installation is running.");
        string? stage = null;
        string? target = null;
        try
        {
            InstallLog.Write("Starting " + command + (force ? " (forced)." : "."));
            var installed = InstallRegistration.Read(tolerateInvalidConfig: force);
            var config = command == "install" ? InstallerConfiguration.ReadPackage(AppContext.BaseDirectory)
                : installed.Config ?? new InstallerConfiguration();
            if (installed.Config != null) config = config with { TargetDirectory = installed.Config.TargetDirectory };
            config = InstallerConfiguration.Validate(config);
            target = config.TargetDirectory;
            string self = Environment.ProcessPath ?? throw new IOException("Installer path is unavailable.");
            bool reboot = false;
            if (command == "install")
            {
                progress?.Invoke("Validating and staging the package…");
                stage = FileDeployment.Stage(target, AppContext.BaseDirectory, self,
                    JsonSerializer.Serialize(config, InstallerConfiguration.JsonOptions), InstallSafety.SecureDirectory, InstallSafety.SecureFile);
                string version = ValidatePayload(stage);
                string? oldVersion = installed.Version;
                string oldAgent = Path.Combine(target, Product.AgentExe);
                if (oldVersion == null && File.Exists(oldAgent)) oldVersion = FileVersionInfo.GetVersionInfo(oldAgent).FileVersion;
                if (oldVersion != null && !Version.TryParse(oldVersion, out _)) throw new InvalidDataException("Installed version is invalid; refusing an unchecked replacement.");
                if (oldVersion != null && Version.Parse(oldVersion) > Version.Parse(version))
                    return new(1638, "A newer Halerith DriveMapper version is already installed.");
                InstallLog.Write($"Package {version}; installed {oldVersion ?? "none"}; destination {target}.");
                reboot |= RetiredFiles.CleanupLeftovers(target, self, InstallLog.Write, stage);
                progress?.Invoke("Stopping Halerith DriveMapper…");
                ServiceRegistration.Stop();
                LegacyTasks.Remove(target, installed.LegacyTaskNames, InstallLog.Write);
                InstallRegistration.WithholdDetection();
                string retired = "";
                InstallSequence.Run(
                    () =>
                    {
                        progress?.Invoke("Installing application files…");
                        retired = FileDeployment.Replace(target, stage, InstallSafety.SecureDirectory);
                        stage = null;
                    },
                    () =>
                    {
                        progress?.Invoke("Deleting previous application files…");
                        reboot |= RetiredFiles.Cleanup(target, retired, Path.Combine(target, "Install.exe"), InstallLog.Write);
                    },
                    () =>
                    {
                        // Record the location even if startup fails, so a subsequent uninstall can find it.
                        InstallRegistration.WriteLocation(config);
                        InstallRegistration.EnsureEventSource();
                        string logs = Path.Combine(target, "Logs");
                        InstallLog.EnsureDirectory(logs);
                        foreach (string name in new[] { "service.log", "service.log.1" })
                        {
                            string file = Path.Combine(logs, name);
                            FileDeployment.EnsureNoLinks(file);
                            if (File.Exists(file)) InstallSafety.SecureFile(file);
                        }
                        progress?.Invoke("Starting Halerith DriveMapper…");
                        ServiceRegistration.InstallAndStart(target);
                    });
                if (installed.Config != null) Shortcuts.Remove(installed.Config.ShortcutName);
                Shortcuts.Create(config);
                InstallRegistration.Commit(config, version);
                InstallLog.TryWrite("Installation completed.");
                return Completed(reboot, Product.DisplayName + " " + version + " installed.");
            }

            progress?.Invoke("Stopping and removing Halerith DriveMapper…");
            reboot |= RetiredFiles.CleanupLeftovers(target, self, InstallLog.Write);
            ServiceRegistration.Remove();
            LegacyTasks.Remove(target, installed.LegacyTaskNames, InstallLog.Write);
            // Prepare the helper image from the retired installer when uninstalling itself.
            string removed = FileDeployment.Retire(target, InstallSafety.SecureDirectory);
            string helperSource = File.Exists(self) ? self : Path.Combine(removed, "Install.exe");
            reboot |= RetiredFiles.Cleanup(target, removed, helperSource, InstallLog.Write);
            Shortcuts.Remove(installed.Config?.ShortcutName ?? config.ShortcutName);
            InstallRegistration.Remove();
            ForcedCleanup.TryRemoveEmpty(target);
            if (force)
            {
                progress?.Invoke("Removing Halerith DriveMapper settings and logging data…");
                ForcedCleanup.RegistryAndEventSource();
                InstallLog.Write("Forced uninstall completed; removing file logs. Drive mappings were not changed.");
                reboot |= ForcedCleanup.Logs(target); // Last log mutation; do not recreate deleted logs afterward.
            }
            else InstallLog.TryWrite("Uninstall completed. User ownership and logs retained; drive mappings were not changed.");
            return Completed(reboot, force ? "Halerith DriveMapper and its settings/logging data removed. Drive mappings were left unchanged."
                : "Halerith DriveMapper removed. Logs and user ownership records retained. Drive mappings were left unchanged.");
        }
        catch (Exception error)
        {
            InstallLog.TryWrite("FAILED " + command + ": " + error);
            return new(1603, error.Message + "\n\nInstaller log: " + InstallLog.LogPath);
        }
        finally
        {
            if (stage != null && target != null)
            {
                try { FileDeployment.DeleteWorkDirectory(target, stage); }
                catch (Exception error) { InstallLog.TryWrite("Staging cleanup deferred: " + error.Message); }
            }
            mutex.ReleaseMutex();
        }
    }

    private static InstallResult Completed(bool reboot, string message)
        => new(reboot ? 3010 : 0, message + (reboot ? "\n\nRestart required to finish file cleanup." : ""));

    internal static string ValidatePayload(string stage)
    {
        foreach (string name in FileDeployment.PayloadNames.Where(name => name.EndsWith(".exe")))
        {
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(stage, name)));
            if (reader.BaseStream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Invalid executable: " + name);
            reader.BaseStream.Position = 0x3C;
            int offset = reader.ReadInt32();
            if (offset < 64 || offset > reader.BaseStream.Length - 6) throw new InvalidDataException("Invalid PE header: " + name);
            reader.BaseStream.Position = offset;
            if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664) throw new InvalidDataException("A win-x64 executable is required: " + name);
        }
        string? agent = FileVersionInfo.GetVersionInfo(Path.Combine(stage, Product.AgentExe)).FileVersion;
        string? service = FileVersionInfo.GetVersionInfo(Path.Combine(stage, Product.ServiceExe)).FileVersion;
        if (!Version.TryParse(agent, out var agentVersion) || !Version.TryParse(service, out var serviceVersion) || agentVersion != serviceVersion)
            throw new InvalidDataException("Agent and Service must have matching valid versions.");
        return agent!;
    }
}
