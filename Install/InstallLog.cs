namespace Install;

internal static class InstallLog
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DriveMapper");
    public static string LogPath => Path.Combine(Root, "Logs", "install.log");
    public static void EnsureDirectory(string path)
    {
        FileDeployment.EnsureNoLinks(path);
        Directory.CreateDirectory(path);
        InstallSafety.SecureDirectory(path);
    }
    public static void Write(string message)
    {
        EnsureDirectory(Root);
        EnsureDirectory(Path.GetDirectoryName(LogPath)!);
        if (File.Exists(LogPath))
        {
            FileDeployment.EnsureNoLinks(LogPath);
            InstallSafety.SecureFile(LogPath);
            if (new FileInfo(LogPath).Length > 1024 * 1024)
            {
                string previous = LogPath + ".1";
                FileDeployment.EnsureNoLinks(previous);
                File.Move(LogPath, previous, true);
            }
        }
        File.AppendAllText(LogPath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        InstallSafety.SecureFile(LogPath);
    }
    public static void TryWrite(string message)
    {
        try { Write(message); } catch { }
    }
}
