using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DriveMapper;

internal sealed class PolicyWatcher : IDisposable
{
    private readonly RegistryHive hive;
    private readonly string path;
    private RegistryKey? watched;
    private readonly EventWaitHandle signal = new(false, EventResetMode.AutoReset);
    private string? fingerprint;
    private bool armed;
    private DateTimeOffset verifyAt;

    public PolicyWatcher(string sid) : this(RegistryHive.Users, UserPolicyPath(sid)) { }

    private static string UserPolicyPath(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) throw new ArgumentException("A user SID is required.", nameof(sid));
        return sid + "\\" + PolicyContract.DrivesKey;
    }

    internal PolicyWatcher(RegistryHive hive, string path)
    {
        this.hive = hive;
        this.path = path;
        // Establish the notification before reading the baseline so there is no read/register gap.
        try
        {
            Arm();
            fingerprint = Fingerprint();
        }
        catch { Dispose(); throw; }
    }

    public bool Check(DateTimeOffset now)
    {
        bool notified = signal.WaitOne(0);
        if (!notified && armed && now < verifyAt) return false;
        verifyAt = now.AddSeconds(30); // Also recover from missed notifications/key deletion/temporarily unloaded hives.
        if (notified || !armed) Arm();
        string current = Fingerprint();
        bool changed = current != fingerprint;
        fingerprint = current;
        return changed;
    }

    private void Arm()
    {
        armed = false;
        watched?.Dispose();
        watched = null;
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        string candidate = path;
        while (true)
        {
            watched = candidate.Length == 0 ? RegistryKey.OpenBaseKey(hive, RegistryView.Registry64) : root.OpenSubKey(candidate);
            if (watched != null) break;
            int separator = candidate.LastIndexOf('\\');
            candidate = separator < 0 ? "" : candidate.Substring(0, separator);
        }
        int error = RegNotifyChangeKeyValue(watched.Handle, true, 0x10000005,
            signal.SafeWaitHandle, true);
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
        armed = true;
    }

    private string Fingerprint()
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(path);
        if (key == null) return "absent";
        var value = new StringBuilder("present;");
        foreach (string letter in key.GetSubKeyNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (letter.Length != 1 || char.ToUpperInvariant(letter[0]) is < 'A' or > 'Z') continue;
            using var drive = key.OpenSubKey(letter) ?? throw new IOException("Policy changed during notification inspection.");
            value.Append(letter.ToUpperInvariant()).Append(';');
            foreach (string name in PolicyContract.ValueNames)
            {
                object? item = drive.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                value.Append(name).Append('=').Append(item?.GetType().FullName).Append(':');
                if (item != null) value.Append(drive.GetValueKind(name)).Append(':');
                value.Append(item is byte[] bytes ? Convert.ToHexString(bytes) : item is string[] strings ? string.Join("\0", strings) : item);
                value.Append('\0');
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }

    public void Dispose() { watched?.Dispose(); signal.Dispose(); }
    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool subtree, int filter,
        SafeWaitHandle signal, bool asynchronous);
}
