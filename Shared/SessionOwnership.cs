using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace DriveMapper;

internal static class SessionOwnership
{
    public const string Root = @"Software\DriveMapper\State";
    public static string SessionKey(string logonId) => Root + @"\Sessions\" + logonId;
    public const string Remembered = Root + @"\RememberedDrives";

    // Called for ALL discovered sessions of a user before any Agent starts. Each gets its own cleanup snapshot.
    public static void Prepare(string sid, IEnumerable<string> logonIds, bool pruneInactive = false)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using var user = users.OpenSubKey(sid, writable: true) ?? throw new IOException("User profile is not loaded yet.");
        PrepareAt(user, logonIds, Root, pruneInactive);
    }

    internal static void PrepareAt(RegistryKey user, IEnumerable<string> logonIds, string root, bool pruneInactive = false)
    {
        using var persistent = user.CreateSubKey(root + @"\RememberedDrives");
        var seed = Read(persistent);
        var active = new HashSet<string>(logonIds, StringComparer.OrdinalIgnoreCase);
        foreach (string logonId in active)
        {
            using var session = user.CreateSubKey(root + @"\Sessions\" + logonId);
            if (session.GetValue("Initialized") is int initialized && initialized == 1) continue;
            using var applied = session.CreateSubKey("AppliedDrives");
            foreach (var entry in seed) applied.SetValue(entry.Key, entry.Value, RegistryValueKind.String);
            session.SetValue("Initialized", 1, RegistryValueKind.DWord);
        }
        // Only the service may supply a complete inventory. A manual Agent knows only its own logon.
        if (pruneInactive)
        {
            using var sessions = user.OpenSubKey(root + @"\Sessions", writable: true);
            if (sessions != null)
                foreach (string old in sessions.GetSubKeyNames())
                    if (!active.Contains(old)) sessions.DeleteSubKeyTree(old, false);
        }
    }

    private static Dictionary<string, string> Read(RegistryKey key)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in key.GetValueNames())
        {
            if (name.Length != 1 || char.ToUpperInvariant(name[0]) is < 'A' or > 'Z') continue;
            if (key.GetValue(name) is not string value || !UncPath.TryNormalize(value, out string path))
                throw new InvalidDataException("Invalid ownership record for " + name);
            result[name.ToUpperInvariant()] = path;
        }
        return result;
    }
}
