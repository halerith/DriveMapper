using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DriveMapper;

internal sealed class ExplorerLabels : IDriveLabels
{
    public void Apply(string letter, string path, string name)
    {
        string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2\##" +
            path.TrimStart('\\').Replace('\\', '#');
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        bool clear = string.IsNullOrWhiteSpace(name);
        using var key = clear ? root.OpenSubKey(keyPath, writable: true) : root.CreateSubKey(keyPath, writable: true);
        if (key == null) return;
        object? current = key.GetValue("_LabelFromReg");
        if (clear)
        {
            if (current == null) return;
            key.DeleteValue("_LabelFromReg", throwOnMissingValue: false);
        }
        else
        {
            if (string.Equals(current as string, name, StringComparison.Ordinal)) return;
            key.SetValue("_LabelFromReg", name, RegistryValueKind.String);
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
