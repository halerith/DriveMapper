using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace DriveMapper;

internal sealed class RegistryPolicySource : IPolicySource
{
    private readonly string drivesKey;

    public RegistryPolicySource(string drivesKey = PolicyContract.DrivesKey) => this.drivesKey = drivesKey;

    public IReadOnlyDictionary<string, DrivePolicy> Load()
    {
        var first = ReadEffective();
        var second = ReadEffective();
        if (first.Count != second.Count)
            throw new IOException("Drive policy changed while being read; preserve mappings until the next run.");
        foreach (var entry in first)
            if (!second.TryGetValue(entry.Key, out var value) || entry.Value != value)
                throw new IOException("Drive policy changed while being read; preserve mappings until the next run.");
        return second;
    }

    private Dictionary<string, DrivePolicy> ReadEffective()
    {
        // An inaccessible view is uncertainty, not evidence of absent policy. Propagate read failures.
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var drives = root.OpenSubKey(drivesKey);
        var entries = new Dictionary<string, DrivePolicy>(StringComparer.OrdinalIgnoreCase);
        if (drives == null) return entries;
        foreach (string subkey in drives.GetSubKeyNames())
        {
            if (!RegistryStateStore.IsLetter(subkey)) continue;
            using var drive = drives.OpenSubKey(subkey) ??
                throw new IOException("User policy changed during reading; retry on the next run.");
            // GetValue alone expands REG_EXPAND_SZ and hides the actual registry type.
            entries[subkey.ToUpperInvariant()] = ParseEntry(name =>
            {
                object? value = drive.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) return null;
                var expected = name == PolicyContract.Enabled || name == PolicyContract.Reconnect
                    ? RegistryValueKind.DWord : RegistryValueKind.String;
                if (drive.GetValueKind(name) != expected)
                    throw new InvalidDataException(name + " has the wrong registry type.");
                return value;
            });
        }
        return entries;
    }

    internal static DrivePolicy ParseEntry(Func<string, object?> getValue)
    {
        try
        {
            object? enabledValue = getValue(PolicyContract.Enabled);
            // Windows may leave an empty key after withdrawal. Partially delivered configuration is uncertainty.
            if (enabledValue == null)
            {
                foreach (string name in new[] { PolicyContract.Path, PolicyContract.Name, PolicyContract.Reconnect })
                    if (getValue(name) != null) throw new InvalidDataException("Enabled is missing from a populated drive policy.");
                return new DrivePolicy(false, "", "", false);
            }
            bool enabled = ReadBool(enabledValue, PolicyContract.Enabled);
            // Disabled policy can be cleaned up even if its unused path/name is malformed.
            return enabled
                ? new DrivePolicy(true, ReadString(getValue(PolicyContract.Path), PolicyContract.Path), ReadString(getValue(PolicyContract.Name), PolicyContract.Name),
                    ReadBool(getValue(PolicyContract.Reconnect), PolicyContract.Reconnect))
                : new DrivePolicy(false, "", "", false);
        }
        catch (InvalidDataException ex)
        {
            return new DrivePolicy(false, "", "", false, ex.Message);
        }
    }

    private static string ReadString(object? value, string name) => value switch
    {
        null => "",
        string text => text,
        _ => throw new InvalidDataException($"{name} must be a string.")
    };

    private static bool ReadBool(object? value, string name)
    {
        return value switch
        {
            null => false,
            0 => false,
            1 => true,
            _ => throw new InvalidDataException($"{name} must be DWORD 0 or 1.")
        };
    }
}

internal sealed class RegistryStateStore : IAppliedState
{
    private readonly string stateKey;
    private readonly string persistentKey;
    public RegistryStateStore(string logonId, string root = SessionOwnership.Root)
    {
        stateKey = root + @"\Sessions\" + logonId + @"\AppliedDrives";
        persistentKey = root + @"\RememberedDrives";
    }

    public IReadOnlyDictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(stateKey);
        if (key == null) return result;
        foreach (string name in key.GetValueNames())
        {
            if (!IsLetter(name)) continue;
            if (key.GetValue(name) is not string value || !UncPath.TryNormalize(value, out string path))
                throw new InvalidDataException($"Applied state for {name} is invalid; cannot safely infer ownership.");
            result[name.ToUpperInvariant()] = path;
        }
        return result;
    }

    public void Save(string letter, string path, bool reconnect = false)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.CreateSubKey(stateKey, writable: true);
        key.SetValue(letter, path, RegistryValueKind.String);
        using var persistent = root.CreateSubKey(persistentKey);
        if (reconnect) persistent.SetValue(letter, path, RegistryValueKind.String);
        else persistent.DeleteValue(letter, false); // Windows confirmed no saved reconnect entry.
    }

    public void Delete(string letter)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(stateKey, writable: true);
        string? previous = key?.GetValue(letter) as string;
        key?.DeleteValue(letter, throwOnMissingValue: false);
        using var persistent = root.OpenSubKey(persistentKey, writable: true);
        if (UncPath.Equals(previous, persistent?.GetValue(letter) as string)) persistent?.DeleteValue(letter, false);
    }

    internal static bool IsLetter(string name) => name.Length == 1 && char.ToUpperInvariant(name[0]) is >= 'A' and <= 'Z';
}
