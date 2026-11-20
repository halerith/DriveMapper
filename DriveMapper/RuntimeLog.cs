using System;
using System.Diagnostics;
using System.IO;

namespace DriveMapper;

internal sealed class RuntimeLog : IRuntimeLog
{
    public void Info(string message) => Write(EventLogEntryType.Information, message);
    public void Warn(string message) => Write(EventLogEntryType.Warning, message);
    public void Error(string message) => Write(EventLogEntryType.Error, message);

    private static void Write(EventLogEntryType type, string message)
    {
        // Installer owns source creation. Avoid SourceExists' privileged log enumeration in the user agent.
        try { EventLog.WriteEntry("DriveMapper", message, type); }
        catch
        {
            // WinExe has no visible console. Keep a bounded per-user fallback that helpdesk can retrieve.
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveMapper", "Logs");
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "runtime.log");
                if (File.Exists(file) && new FileInfo(file).Length >= 1024 * 1024)
                    File.Move(file, file + ".1", overwrite: true);
                File.AppendAllText(file, $"{DateTimeOffset.UtcNow:O} [{type}] {message}{Environment.NewLine}");
            }
            catch { Console.Error.WriteLine($"[{type}] {message}"); }
        }
    }
}
