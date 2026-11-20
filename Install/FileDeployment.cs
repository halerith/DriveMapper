using System.Security.Cryptography;
using DriveMapper;

namespace Install;

internal static class FileDeployment
{
    public static readonly string[] PayloadNames = { "Install.exe", Product.AgentExe, Product.ServiceExe, InstallerConfiguration.FileName };
    public static readonly string[] OwnedNames = { "Install.exe", Product.AgentExe, Product.ServiceExe, InstallerConfiguration.FileName, "DriveMapper.exe" };

    public static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            var attributes = new FileInfo(current);
            if (attributes.LinkTarget != null || (File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked installer paths are not supported: " + current);
        }
    }

    public static string NewWorkDirectory(string target, string kind)
        => target + "." + kind + "-" + Guid.NewGuid().ToString("N");

    public static bool IsWorkDirectory(string target, string path)
    {
        string expected = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetDirectoryName(expected), StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string kind in new[] { ".stage-", ".retired-" })
        {
            string prefix = Path.GetFileName(expected) + kind;
            if (Path.GetFileName(full).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(Path.GetFileName(full)[prefix.Length..], "N", out _)) return true;
        }
        return false;
    }

    public static string Stage(string target, string package, string installer, string config, Action<string> secureDirectory, Action<string> secureFile)
    {
        string stage = NewWorkDirectory(target, "stage");
        try
        {
            EnsureNoLinks(stage);
            Directory.CreateDirectory(stage);
            secureDirectory(stage);
            foreach (string name in PayloadNames.Where(name => name != InstallerConfiguration.FileName))
            {
                string source = name == "Install.exe" ? installer : Path.Combine(package, name);
                EnsureNoLinks(source);
                string destination = Path.Combine(stage, name);
                File.Copy(source, destination);
                using var original = File.OpenRead(source);
                using var copied = File.OpenRead(destination);
                if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(copied)))
                    throw new IOException("Staged file verification failed: " + name);
                secureFile(destination);
            }
            File.WriteAllText(Path.Combine(stage, InstallerConfiguration.FileName), config);
            secureFile(Path.Combine(stage, InstallerConfiguration.FileName));
            return stage;
        }
        catch
        {
            // A failed stage contains only known files; preserve the original failure if cleanup is blocked.
            try { DeleteWorkDirectory(target, stage); } catch { }
            throw;
        }
    }

    public static string Replace(string target, string stage, Action<string> secureDirectory)
    {
        if (!IsWorkDirectory(target, stage)) throw new InvalidDataException("Unexpected staging directory.");
        foreach (string name in PayloadNames)
        {
            EnsureNoLinks(Path.Combine(stage, name));
            if (!File.Exists(Path.Combine(stage, name))) throw new FileNotFoundException("Incomplete staged package: " + name);
        }
        EnsureNoLinks(target);
        Directory.CreateDirectory(target);
        secureDirectory(target);
        string retired = NewWorkDirectory(target, "retired");
        Directory.CreateDirectory(retired);
        secureDirectory(retired);
        var movedOld = new List<string>();
        var movedNew = new List<string>();
        try
        {
            foreach (string name in OwnedNames)
            {
                string old = Path.Combine(target, name);
                EnsureNoLinks(old);
                if (!File.Exists(old)) continue;
                File.Move(old, Path.Combine(retired, name));
                movedOld.Add(name);
            }
            foreach (string name in PayloadNames)
            {
                File.Move(Path.Combine(stage, name), Path.Combine(target, name));
                movedNew.Add(name);
            }
            Directory.Delete(stage);
            return retired;
        }
        catch (Exception error)
        {
            // Restore only a failed file replacement. Service-start failure never enters this rollback.
            try
            {
                foreach (string name in movedNew) File.Delete(Path.Combine(target, name));
                foreach (string name in movedOld) File.Move(Path.Combine(retired, name), Path.Combine(target, name));
                Directory.Delete(retired);
            }
            catch (Exception restore) { throw new AggregateException("File replacement and restoration failed; previous files remain in " + retired, error, restore); }
            throw;
        }
    }

    public static string Retire(string target, Action<string> secureDirectory)
    {
        EnsureNoLinks(target);
        string retired = NewWorkDirectory(target, "retired");
        Directory.CreateDirectory(retired);
        secureDirectory(retired);
        foreach (string name in OwnedNames)
        {
            string source = Path.Combine(target, name);
            EnsureNoLinks(source);
            if (File.Exists(source)) File.Move(source, Path.Combine(retired, name));
        }
        return retired;
    }

    public static void DeleteWorkDirectory(string target, string path)
    {
        if (!IsWorkDirectory(target, path)) throw new InvalidDataException("Refusing to clean an unrelated directory.");
        EnsureNoLinks(path);
        if (!Directory.Exists(path)) return;
        // Never recursively delete an arbitrary tree or unrelated contents.
        var entries = Directory.GetFileSystemEntries(path);
        if (entries.Any(entry => !OwnedNames.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase) || Directory.Exists(entry)))
            throw new InvalidDataException("Unexpected contents in installer work directory: " + path);
        foreach (string entry in entries) EnsureNoLinks(entry);
        foreach (string entry in entries) File.Delete(entry);
        Directory.Delete(path);
    }
}

internal static class InstallSequence
{
    // Deliberately independent of startup success: old files are disposed immediately after file commit.
    public static void Run(Action replaceFiles, Action deleteOldFiles, Action startService)
    {
        replaceFiles();
        deleteOldFiles();
        startService();
    }
}
