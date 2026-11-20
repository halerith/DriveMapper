#nullable enable
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Install;

internal static class InstallSafety
{
    public static string ValidateDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\"))
            throw new InvalidDataException("The service requires a dedicated absolute local install directory.");
        string path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        string root = Path.GetPathRoot(path)!.TrimEnd(Path.DirectorySeparatorChar);
        bool programFiles = false;
        foreach (string basePath in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (!string.IsNullOrEmpty(basePath) && path.StartsWith(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                programFiles = true;
        if (!programFiles) throw new InvalidDataException("The privileged service must be installed in a dedicated Program Files subdirectory.");
        foreach (string protectedPath in new[] { root, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) })
            if (!string.IsNullOrEmpty(protectedPath) && path.Equals(protectedPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a dedicated application subdirectory.");
        for (DirectoryInfo? current = new(path); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Service installation cannot traverse junctions or symbolic links.");
        return path;
    }

    public static void SecureDirectory(string directory)
    {
        if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Refusing to secure a linked directory.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var entry in new[] { (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl), (WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute) })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(entry.Item1, null), entry.Item2,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    public static void SecureFile(string file)
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Payload cannot be a symbolic link.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var entry in new[] { (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl), (WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute) })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(entry.Item1, null), entry.Item2, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }
}
