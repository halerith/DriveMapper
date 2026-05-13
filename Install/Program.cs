using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace Install
{
    class Program
    {
        private const string AppName = "DriveMapper";
        private const string ConfigFileName = "install.config.json";
        private const string EmbeddedConfigResourceName = "install.config.json";
        private const string EventLogName = "Application";
        private const string EventLogSourceName = "DriveMapper";
        private const string BuiltinUsersSid = "S-1-5-32-545";

        private const int ExitOk = 0;
        private const int ExitBadArgs = 1;
        private const int ExitConfigError = 2;
        private const int ExitBlockedDowngrade = 3;
        private const int ExitFailed = 10;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                var app = new Application();
                app.Run(new InstallerWindow());
                return ExitOk;
            }

            if (args.Length != 1 && args.Length != 2)
                return ExitBadArgs;

            string mode = (args.Length == 1 ? args[0] : args[1] ?? "").Trim().ToLowerInvariant();
            string configPath = args.Length == 2 ? args[0] : null;

            if (!IsSupportedMode(mode))
                return ExitBadArgs;

            if (!IsAdministrator() && !IsRunningAsSystem())
                return RelaunchElevatedAndWait(configPath, mode);

            try
            {
                if (mode == "uninstall")
                    return UninstallFromRegistry();

                var context = LoadConfigContext(configPath);
                var config = context.Config;
                string exeKey = GetExeKey(config);
                string packageDirectory = context.PackageDirectory;

                switch (mode)
                {
                    case "install":
                        return InstallOrUpgrade(context, exeKey, allowDowngrade: false, reconcileInstalledConfig: false);
                    case "upgrade":
                        return InstallOrUpgrade(context, exeKey, allowDowngrade: false, reconcileInstalledConfig: true);
                    case "repair":
                        DoRepair(context, exeKey);
                        return ExitOk;
                    default:
                        return ExitBadArgs;
                }
            }
            catch
            {
                return ExitFailed;
            }
        }

        private static bool IsSupportedMode(string mode)
            => mode == "install" || mode == "uninstall" || mode == "upgrade" || mode == "repair";

        private static int InstallOrUpgrade(ConfigContext context, string exeKey, bool allowDowngrade, bool reconcileInstalledConfig)
        {
            Config config = reconcileInstalledConfig
                ? ReconcileWithInstalledConfig(context.Config, exeKey)
                : context.Config;
            string requestedVersion = GetPackagedDriveMapperVersion(config, context.PackageDirectory);
            if (!allowDowngrade && IsDowngradeAttempt(exeKey, requestedVersion, out _, out _))
                return ExitBlockedDowngrade;

            var oldConfig = GetInstalledConfig(exeKey);
            if (oldConfig != null)
                DeleteScheduledTasks(oldConfig);

            string configJson = reconcileInstalledConfig ? SerializeConfig(config) : context.ConfigJson;
            DoInstall(config, configJson, exeKey, context.PackageDirectory);
            return ExitOk;
        }

        private static void DoInstall(Config config, string configJson, string exeKey, string packageDirectory)
        {
            Directory.CreateDirectory(config.TargetDirectory);
            CopyPackageFiles(packageDirectory, config.TargetDirectory, config);
            DeployConfig(config, configJson);

            string exePath = Path.Combine(config.TargetDirectory, config.ExeName);
            EnsureEventLogSource();
            CreateConfiguredTasks(config, exePath);

            if (!string.IsNullOrWhiteSpace(config.ShortcutName))
                CreateStartMenuShortcut(config.ShortcutName, exePath);

            string mapperVersion = GetInstalledDriveMapperVersion(config);
            WriteInstallRegistry(config, exeKey, mapperVersion);
            WriteAddRemoveProgramsEntry(config, mapperVersion);
        }

        private static void DoRepair(ConfigContext context, string exeKey)
        {
            Config config = context.Config;
            string packageDirectory = context.PackageDirectory;
            string targetDirectory = Path.GetFullPath(config.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string sourceDirectory = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar);

            if (!targetDirectory.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase))
            {
                DeleteScheduledTasks(GetInstalledConfig(exeKey) ?? config);
                DoInstall(config, context.ConfigJson, exeKey, packageDirectory);
                return;
            }

            DeleteScheduledTasks(config);
            EnsureEventLogSource();
            DeployConfig(config, context.ConfigJson);
            CreateConfiguredTasks(config, Path.Combine(config.TargetDirectory, config.ExeName));
            WriteInstallRegistry(config, exeKey, GetInstalledDriveMapperVersion(config));
            WriteAddRemoveProgramsEntry(config, GetInstalledDriveMapperVersion(config));
        }

        private static void DoUninstall(Config config, string exeKey)
        {
            DeleteScheduledTasks(config);
            RemoveShortcut(config);
            DeleteInstalledConfig(config);
            DeleteInstallRegistry(exeKey);
            DeleteAddRemoveProgramsEntry();
            DeleteInstallDirectory(config.TargetDirectory);
        }

        private static int UninstallFromRegistry()
        {
            var installedConfig = GetInstalledConfig(AppName);
            if (installedConfig == null)
                return ExitConfigError;

            DoUninstall(installedConfig, AppName);
            return ExitOk;
        }

        private static void CopyPackageFiles(string sourceDirectory, string targetDirectory, Config config)
        {
            foreach (string fileName in GetInstallPayloadFileNames(config))
            {
                string file = Path.Combine(sourceDirectory, fileName);
                if (!File.Exists(file))
                    continue;

                string destPath = Path.Combine(targetDirectory, fileName);

                if (Path.GetFullPath(file).Equals(Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Copy(file, destPath, true);
            }
        }

        private static IEnumerable<string> GetInstallPayloadFileNames(Config config)
        {
            string installerName = Path.GetFileName(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "Install.exe");
            yield return installerName;

            if (!installerName.Equals("Install.exe", StringComparison.OrdinalIgnoreCase))
                yield return "Install.exe";

            yield return config.ExeName;
        }

        private static void DeployConfig(Config config, string configJson)
        {
            File.WriteAllText(Path.Combine(config.TargetDirectory, ConfigFileName), configJson, Encoding.UTF8);
        }

        private static void DeleteInstalledConfig(Config config)
        {
            try
            {
                string configPath = Path.Combine(config.TargetDirectory, ConfigFileName);
                if (File.Exists(configPath))
                    File.Delete(configPath);
            }
            catch { }
        }

        private static void CreateConfiguredTasks(Config config, string exePath)
        {
            foreach (var task in config.ScheduledTasks ?? new List<ScheduledTaskConfig>())
            {
                string baseName = string.IsNullOrWhiteSpace(task.TaskName) ? Path.GetFileNameWithoutExtension(config.ExeName) : task.TaskName;

                if (task.CreateLogonTask)
                    CreateScheduledTask(baseName, exePath, task.Arguments ?? "", TaskTriggerType.Logon);

                if (task.CreateNetworkTask)
                    CreateScheduledTask(baseName + "_NetworkChange", exePath, task.Arguments ?? "", TaskTriggerType.NetworkProfile);

                if (task.CreateBootTask)
                    CreateScheduledTask(baseName + "_Boot", exePath, task.Arguments ?? "", TaskTriggerType.Boot);
            }
        }

        private static void DeleteScheduledTasks(Config config)
        {
            using (TaskService ts = new TaskService())
            {
                foreach (var task in config.ScheduledTasks ?? new List<ScheduledTaskConfig>())
                {
                    string baseName = string.IsNullOrWhiteSpace(task.TaskName) ? config.ExeName : task.TaskName;
                    SafeDeleteTask(ts, baseName);
                    SafeDeleteTask(ts, baseName + "_NetworkChange");
                    SafeDeleteTask(ts, baseName + "_Boot");
                }

                string canonical = Path.GetFileNameWithoutExtension(config.ExeName) ?? config.ExeName;
                SafeDeleteTask(ts, canonical);
                SafeDeleteTask(ts, canonical + "_NetworkChange");
                SafeDeleteTask(ts, canonical + "_Boot");
                SafeDeleteTask(ts, config.ExeName);
                SafeDeleteTask(ts, config.ExeName + "_NetworkChange");
                SafeDeleteTask(ts, config.ExeName + "_Boot");
            }
        }

        private static void SafeDeleteTask(TaskService ts, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            try { ts.RootFolder.DeleteTask(name, false); }
            catch { }
        }

        private static void DeleteInstallDirectory(string targetDirectory)
        {
            if (!Directory.Exists(targetDirectory))
                return;

            string currentExe = Environment.ProcessPath ?? "";
            bool runningFromTarget = !string.IsNullOrWhiteSpace(currentExe) &&
                                     Path.GetFullPath(currentExe).StartsWith(
                                         Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                         StringComparison.OrdinalIgnoreCase);

            if (!runningFromTarget)
            {
                Directory.Delete(targetDirectory, true);
                return;
            }

            string command = $"/c timeout /t 2 /nobreak > nul & rmdir /s /q \"{targetDirectory}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", command)
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            });
        }

        private static ConfigContext LoadConfigContext(string configPath)
        {
            string packageDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string adjacentConfigPath = Path.Combine(packageDirectory, ConfigFileName);
            string json;
            string sourcePath;

            if (!string.IsNullOrWhiteSpace(configPath))
            {
                if (!File.Exists(configPath))
                    throw new FileNotFoundException("Config file not found.", configPath);

                json = File.ReadAllText(configPath);
                sourcePath = Path.GetFullPath(configPath);
            }
            else if (File.Exists(adjacentConfigPath))
            {
                json = File.ReadAllText(adjacentConfigPath);
                sourcePath = adjacentConfigPath;
            }
            else
            {
                json = ReadEmbeddedConfigJson();
                sourcePath = "embedded";
            }

            var config = JsonConvert.DeserializeObject<Config>(json);
            if (config == null)
                throw new InvalidOperationException("Config is empty or invalid.");

            if (string.IsNullOrWhiteSpace(config.TargetDirectory) || string.IsNullOrWhiteSpace(config.ExeName))
                throw new InvalidOperationException("Config must include TargetDirectory and ExeName.");

            config.ScheduledTasks ??= new List<ScheduledTaskConfig>();
            return new ConfigContext(config, NormalizeConfigJson(json), sourcePath, packageDirectory);
        }

        private static string ReadEmbeddedConfigJson()
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedConfigResourceName);
            if (stream == null)
                throw new InvalidOperationException("Embedded install.config.json was not found.");

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static string NormalizeConfigJson(string json)
            => JToken.Parse(json).ToString(Formatting.Indented);

        private static string SerializeConfig(Config config)
            => JsonConvert.SerializeObject(config, Formatting.Indented);

        private static string GetExeKey(Config config)
            => Path.GetFileNameWithoutExtension(config.ExeName) ?? AppName;

        private static Config ReconcileWithInstalledConfig(Config packageConfig, string exeKey)
        {
            var installedConfig = GetInstalledConfig(exeKey);
            if (installedConfig == null)
                return packageConfig;

            var packageObject = JObject.FromObject(packageConfig);
            var installedObject = JObject.FromObject(installedConfig);
            var reconciled = ReconcileObject(packageObject, installedObject);
            return reconciled.ToObject<Config>() ?? packageConfig;
        }

        private static JObject ReconcileObject(JObject packageObject, JObject installedObject)
        {
            var result = new JObject();

            foreach (var packageProperty in packageObject.Properties())
            {
                if (!installedObject.TryGetValue(packageProperty.Name, StringComparison.OrdinalIgnoreCase, out var installedValue))
                {
                    result[packageProperty.Name] = packageProperty.Value.DeepClone();
                    continue;
                }

                if (packageProperty.Value is JObject packageChild && installedValue is JObject installedChild)
                {
                    result[packageProperty.Name] = ReconcileObject(packageChild, installedChild);
                }
                else if (packageProperty.Value is JArray packageArray && installedValue is JArray installedArray)
                {
                    result[packageProperty.Name] = packageProperty.Name.Equals(nameof(Config.ScheduledTasks), StringComparison.OrdinalIgnoreCase)
                        ? ReconcileScheduledTasks(packageArray, installedArray)
                        : ReconcileArray(packageArray, installedArray);
                }
                else
                {
                    result[packageProperty.Name] = installedValue.DeepClone();
                }
            }

            return result;
        }

        private static JArray ReconcileScheduledTasks(JArray packageArray, JArray installedArray)
        {
            var installedByName = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

            foreach (var installedItem in installedArray.OfType<JObject>())
            {
                string taskName = installedItem.Value<string>(nameof(ScheduledTaskConfig.TaskName));
                if (!string.IsNullOrWhiteSpace(taskName))
                    installedByName[taskName] = installedItem;
            }

            var result = new JArray();

            foreach (var packageItem in packageArray)
            {
                if (packageItem is not JObject packageObject)
                {
                    result.Add(packageItem.DeepClone());
                    continue;
                }

                string taskName = packageObject.Value<string>(nameof(ScheduledTaskConfig.TaskName));
                if (!string.IsNullOrWhiteSpace(taskName) &&
                    installedByName.TryGetValue(taskName, out var installedObject))
                {
                    result.Add(ReconcileObject(packageObject, installedObject));
                }
                else
                {
                    result.Add(packageObject.DeepClone());
                }
            }

            return result;
        }

        private static JArray ReconcileArray(JArray packageArray, JArray installedArray)
        {
            var result = new JArray();

            for (int i = 0; i < packageArray.Count; i++)
            {
                var packageItem = packageArray[i];
                var installedItem = i < installedArray.Count ? installedArray[i] : null;

                if (packageItem is JObject packageObject && installedItem is JObject installedObject)
                    result.Add(ReconcileObject(packageObject, installedObject));
                else if (installedItem != null)
                    result.Add(installedItem.DeepClone());
                else
                    result.Add(packageItem.DeepClone());
            }

            return result;
        }

        private static bool IsInstalled(string exeKey, Config config)
            => !string.IsNullOrWhiteSpace(GetInstalledVersion(exeKey)) ||
               File.Exists(Path.Combine(config.TargetDirectory, config.ExeName));

        private static string GetInstalledVersion(string exeKey)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\{exeKey}");
                return key?.GetValue("Version") as string;
            }
            catch { return null; }
        }

        private static Config GetInstalledConfig(string exeKey)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\{exeKey}");
                var json = key?.GetValue("InstalledConfig") as string;
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonConvert.DeserializeObject<Config>(json);
            }
            catch { return null; }
        }

        private static void WriteInstallRegistry(Config config, string exeKey, string mapperVersion)
        {
            using var key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\{exeKey}");
            key.SetValue("Version", mapperVersion, RegistryValueKind.String);
            key.SetValue("InstalledConfig", JsonConvert.SerializeObject(config), RegistryValueKind.String);
            key.SetValue("InstallLocation", config.TargetDirectory, RegistryValueKind.String);
            key.SetValue("InstallTimeUtc", DateTime.UtcNow.ToString("o"), RegistryValueKind.String);
        }

        private static void DeleteInstallRegistry(string exeKey)
        {
            try { Registry.LocalMachine.DeleteSubKeyTree($@"SOFTWARE\{exeKey}", false); }
            catch { }
        }

        private static string GetInstalledDriveMapperVersion(Config config)
        {
            string exePath = Path.Combine(config.TargetDirectory, config.ExeName);
            return GetFileVersion(exePath) ?? "Unknown";
        }

        private static string GetPackagedDriveMapperVersion(Config config, string packageDirectory)
        {
            string exePath = Path.Combine(packageDirectory, config.ExeName);
            return GetFileVersion(exePath) ?? "Unknown";
        }

        private static string GetFileVersion(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var info = FileVersionInfo.GetVersionInfo(path);
                return !string.IsNullOrWhiteSpace(info.FileVersion) ? info.FileVersion : info.ProductVersion;
            }
            catch { return null; }
        }

        private static bool IsDowngradeAttempt(string exeKey, string requestedVersionRaw, out string installedRaw, out string requestedRaw)
        {
            installedRaw = GetInstalledVersion(exeKey);
            requestedRaw = requestedVersionRaw ?? "Unknown";

            if (string.IsNullOrWhiteSpace(installedRaw))
                return false;

            if (!TryParseVersion(requestedRaw, out var requested))
                return false;

            if (!TryParseVersion(installedRaw, out var installed))
                return false;

            return installed > requested;
        }

        private static bool TryParseVersion(string s, out Version v)
        {
            v = null;
            return !string.IsNullOrWhiteSpace(s) && Version.TryParse(s.Trim(), out v);
        }

        private static void WriteAddRemoveProgramsEntry(Config config, string mapperVersion)
        {
            using var key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}");
            string installExe = Path.Combine(config.TargetDirectory, Path.GetFileName(Environment.ProcessPath ?? "Install.exe"));
            string configPath = Path.Combine(config.TargetDirectory, "install.config.json");

            key.SetValue("DisplayName", AppName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", mapperVersion, RegistryValueKind.String);
            key.SetValue("Publisher", "DriveMapper", RegistryValueKind.String);
            key.SetValue("InstallLocation", config.TargetDirectory, RegistryValueKind.String);
            key.SetValue("DisplayIcon", installExe, RegistryValueKind.String);
            key.SetValue("UninstallString", Quote(installExe), RegistryValueKind.String);
            key.SetValue("QuietUninstallString", Quote(installExe) + " uninstall", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        private static void DeleteAddRemoveProgramsEntry()
        {
            try { Registry.LocalMachine.DeleteSubKeyTree($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}", false); }
            catch { }
        }

        private static string Quote(string value) => "\"" + value + "\"";

        private static void EnsureEventLogSource()
        {
            try
            {
                if (!EventLog.SourceExists(EventLogSourceName))
                {
                    var data = new EventSourceCreationData(EventLogSourceName, EventLogName);
                    EventLog.CreateEventSource(data);
                }

                EventLog.WriteEntry(EventLogSourceName, "DriveMapper event log source created/verified by installer.", EventLogEntryType.Information);
            }
            catch { }
        }

        static void CreateScheduledTask(string taskName, string exePath, string arguments, TaskTriggerType triggerType)
        {
            using (TaskService ts = new TaskService())
            {
                TaskDefinition td = ts.NewTask();
                td.RegistrationInfo.Description = $"Run {Path.GetFileName(exePath)} on {triggerType}";
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.StartWhenAvailable = true;
                td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
                td.Principal.GroupId = BuiltinUsersSid;
                td.Principal.LogonType = TaskLogonType.Group;
                td.Principal.RunLevel = TaskRunLevel.LUA;

                Microsoft.Win32.TaskScheduler.Trigger trigger = triggerType switch
                {
                    TaskTriggerType.Logon => new LogonTrigger { Delay = TimeSpan.FromSeconds(10) },
                    TaskTriggerType.NetworkProfile => new Microsoft.Win32.TaskScheduler.EventTrigger
                    {
                        Subscription =
                            @"<QueryList><Query Id='0' Path='Microsoft-Windows-NetworkProfile/Operational'>
                                <Select Path='Microsoft-Windows-NetworkProfile/Operational'>
                                  *[System[Provider[@Name='Microsoft-Windows-NetworkProfile'] and (EventID=10000 or EventID=10001)]]
                                </Select>
                              </Query></QueryList>"
                    },
                    TaskTriggerType.Boot => new BootTrigger { Delay = TimeSpan.FromSeconds(10) },
                    _ => throw new ArgumentException("Unsupported trigger type")
                };

                td.Triggers.Add(trigger);
                td.Actions.Add(new ExecAction(exePath, arguments, null));
                ts.RootFolder.RegisterTaskDefinition(taskName, td, TaskCreation.CreateOrUpdate, null, null, TaskLogonType.Group);
            }
        }

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool IsRunningAsSystem()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value == "S-1-5-18";
        }

        private static int RelaunchElevatedAndWait(string configPath, string mode)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "Install.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.IsNullOrWhiteSpace(configPath)
                    ? mode
                    : Quote(Path.GetFullPath(configPath)) + " " + mode,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            process.WaitForExit();
            return process.ExitCode;
        }

        private static void RemoveShortcut(Config config)
        {
            if (string.IsNullOrWhiteSpace(config.ShortcutName))
                return;

            string startMenuPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs",
                config.ShortcutName + ".lnk"
            );

            try
            {
                if (File.Exists(startMenuPath))
                    File.Delete(startMenuPath);
            }
            catch { }
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLink
        {
            void GetPath([Out] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        static void CreateStartMenuShortcut(string shortcutName, string targetPath)
        {
            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs",
                shortcutName + ".lnk"
            );

            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
            IShellLink link = (IShellLink)new ShellLink();
            link.SetDescription(shortcutName);
            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath));
            ((IPersistFile)link).Save(shortcutPath, false);
        }

        private sealed class InstallerWindow : Window
        {
            private readonly string _configPath;
            private Config _config;
            private string _exeKey;
            private TextBlock _statusText;
            private TextBlock _versionText;
            private TextBlock _locationText;
            private StackPanel _actionPanel;

            public InstallerWindow()
            {
                _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "install.config.json");
                Title = "DriveMapper Setup";
                Width = 680;
                Height = 430;
                MinWidth = 620;
                MinHeight = 390;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
                FontFamily = new FontFamily("Segoe UI");

                BuildLayout();
                LoadState();
            }

            private void BuildLayout()
            {
                var root = new Grid { Margin = new Thickness(28) };
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var titleBlock = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
                titleBlock.Children.Add(new TextBlock
                {
                    Text = "DriveMapper Setup",
                    FontSize = 28,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
                });
                titleBlock.Children.Add(new TextBlock
                {
                    Text = "Install, repair, upgrade, or remove the scheduled drive mapping agent.",
                    FontSize = 14,
                    Margin = new Thickness(0, 6, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
                });
                Grid.SetRow(titleBlock, 0);
                root.Children.Add(titleBlock);

                var card = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(22)
                };
                Grid.SetRow(card, 1);

                var details = new StackPanel();
                _statusText = CreateDetailText(18, FontWeights.SemiBold, Color.FromRgb(15, 23, 42));
                _versionText = CreateDetailText(14, FontWeights.Normal, Color.FromRgb(51, 65, 85));
                _locationText = CreateDetailText(14, FontWeights.Normal, Color.FromRgb(51, 65, 85));
                _versionText.Margin = new Thickness(0, 18, 0, 0);
                _locationText.Margin = new Thickness(0, 8, 0, 0);

                details.Children.Add(_statusText);
                details.Children.Add(_versionText);
                details.Children.Add(_locationText);
                card.Child = details;
                root.Children.Add(card);

                _actionPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 22, 0, 0)
                };
                Grid.SetRow(_actionPanel, 2);
                root.Children.Add(_actionPanel);

                Content = root;
            }

            private static TextBlock CreateDetailText(double size, FontWeight weight, Color color)
            {
                return new TextBlock
                {
                    FontSize = size,
                    FontWeight = weight,
                    Foreground = new SolidColorBrush(color),
                    TextWrapping = TextWrapping.Wrap
                };
            }

            private void LoadState()
            {
                _actionPanel.Children.Clear();

                try
                {
                    var context = LoadConfigContext(null);
                    _config = context.Config;
                    _exeKey = GetExeKey(_config);

                    string installedVersion = GetInstalledVersion(_exeKey);
                    string packageVersion = GetPackagedDriveMapperVersion(_config, context.PackageDirectory);
                    bool installed = IsInstalled(_exeKey, _config);

                    _statusText.Text = installed ? "DriveMapper is installed" : "DriveMapper is not installed";
                    _versionText.Text = $"Package version: {packageVersion}\nInstalled version: {(string.IsNullOrWhiteSpace(installedVersion) ? "Not installed" : installedVersion)}";
                    _locationText.Text = $"Install location: {_config.TargetDirectory}";

                    AddCloseButton();

                    if (!installed)
                    {
                        AddActionButton("Install", "install", primary: true);
                    }
                    else if (IsOlder(installedVersion, packageVersion))
                    {
                        AddActionButton("Uninstall", "uninstall", primary: false);
                        AddActionButton("Repair", "repair", primary: false);
                        AddActionButton("Upgrade", "upgrade", primary: true);
                    }
                    else
                    {
                        AddActionButton("Uninstall", "uninstall", primary: false);
                        AddActionButton("Repair", "repair", primary: true);
                    }
                }
                catch (Exception ex)
                {
                    _statusText.Text = "Setup could not load install.config.json";
                    _versionText.Text = ex.Message;
                    _locationText.Text = "";
                    AddCloseButton();
                }
            }

            private static bool IsOlder(string installedVersion, string packageVersion)
            {
                if (!TryParseVersion(installedVersion, out var installed)) return false;
                if (!TryParseVersion(packageVersion, out var package)) return false;
                return installed < package;
            }

            private void AddCloseButton()
            {
                var close = CreateButton("Close", primary: false);
                close.Click += (_, __) => Close();
                _actionPanel.Children.Add(close);
            }

            private void AddActionButton(string text, string mode, bool primary)
            {
                var button = CreateButton(text, primary);
                button.Click += (_, __) => RunAction(mode);
                _actionPanel.Children.Add(button);
            }

            private static Button CreateButton(string text, bool primary)
            {
                var button = new Button
                {
                    Content = text,
                    MinWidth = 108,
                    Height = 38,
                    Margin = new Thickness(10, 0, 0, 0),
                    Padding = new Thickness(18, 0, 18, 0),
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                if (primary)
                {
                    button.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
                    button.Foreground = Brushes.White;
                    button.BorderBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235));
                }
                else
                {
                    button.Background = Brushes.White;
                    button.Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                    button.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225));
                }

                return button;
            }

            private void RunAction(string mode)
            {
                try
                {
                    SetButtonsEnabled(false);
                    _statusText.Text = $"Running {mode}...";
                    _versionText.Text = "Windows may ask for administrator approval before continuing.";
                    _locationText.Text = "";

                    int exitCode = (IsAdministrator() || IsRunningAsSystem())
                        ? Main(new[] { mode })
                        : RelaunchElevatedAndWait(null, mode);

                    _statusText.Text = exitCode == ExitOk
                        ? $"{ToTitle(mode)} completed"
                        : $"{ToTitle(mode)} failed";
                    _versionText.Text = exitCode == ExitOk ? "The installer state has been refreshed." : $"Exit code: {exitCode}";
                }
                catch (Exception ex)
                {
                    _statusText.Text = $"{ToTitle(mode)} failed";
                    _versionText.Text = ex.Message;
                }
                finally
                {
                    SetButtonsEnabled(true);
                    LoadState();
                }
            }

            private void SetButtonsEnabled(bool enabled)
            {
                foreach (UIElement child in _actionPanel.Children)
                    child.IsEnabled = enabled;
            }

            private static string ToTitle(string value)
                => string.IsNullOrWhiteSpace(value) ? value : char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        enum TaskTriggerType
        {
            Logon,
            NetworkProfile,
            Boot
        }

        class Config
        {
            public string TargetDirectory { get; set; }
            public string ExeName { get; set; }
            public string ShortcutName { get; set; }
            public List<ScheduledTaskConfig> ScheduledTasks { get; set; }
        }

        class ConfigContext
        {
            public ConfigContext(Config config, string configJson, string sourcePath, string packageDirectory)
            {
                Config = config;
                ConfigJson = configJson;
                SourcePath = sourcePath;
                PackageDirectory = packageDirectory;
            }

            public Config Config { get; }
            public string ConfigJson { get; }
            public string SourcePath { get; }
            public string PackageDirectory { get; }
        }

        class ScheduledTaskConfig
        {
            public string TaskName { get; set; }
            public string Arguments { get; set; }
            public bool CreateLogonTask { get; set; }
            public bool CreateNetworkTask { get; set; }
            public bool CreateBootTask { get; set; }
        }
    }
}
