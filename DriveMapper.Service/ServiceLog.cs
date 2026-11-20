using System.Diagnostics;

namespace DriveMapper;

internal static class ServiceLog
{
    private static readonly object Gate = new();
    public static void Write(string message, EventLogEntryType type = EventLogEntryType.Information)
    {
        try { EventLog.WriteEntry("DriveMapper", message, type); }
        catch
        {
            try
            {
                lock (Gate)
                {
                    string directory = Path.Combine(AppContext.BaseDirectory, "Logs");
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "service.log");
                    if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024) File.Move(path, path + ".1", true);
                    File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} [{type}] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
