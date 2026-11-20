using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DriveMapper;

internal sealed class WindowsNetworkMappings : INetworkMappings
{
    private const int UpdateProfile = 1;
    private const int Temporary = 4;
    private const int NotConnected = 2250;
    private const int ConnectionUnavailable = 1201;
    private const int MoreData = 234;
    private const int NoMoreItems = 259;

    private readonly Func<string, MappingSnapshot>? inspectConnection;
    private readonly Func<string, int, bool, int> cancelConnection;

    public WindowsNetworkMappings() => cancelConnection = WNetCancelConnection2;

    // Allows cancellation behavior to be verified without disconnecting a real Windows drive.
    internal WindowsNetworkMappings(Func<string, MappingSnapshot> inspectConnection,
        Func<string, int, bool, int> cancelConnection)
        => (this.inspectConnection, this.cancelConnection) = (inspectConnection, cancelConnection);

    public MappingSnapshot Inspect(string letter)
    {
        if (inspectConnection != null) return inspectConnection(letter);
        string? connected = GetConnection(letter, out bool unavailable);
        string? remembered = GetRememberedConnection(letter);
        if (remembered == null)
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            remembered = ReadRememberedProfile(user, @"Network", letter);
        }
        if (unavailable && remembered == null)
            throw new IOException($"{letter}: is an unavailable persistent connection, but its saved path could not be established.");

        uint type = GetDriveType(letter + @":\");
        if (type == 0) throw new IOException($"Cannot establish the device type for {letter}:.");
        if (type == 4 && connected == null && remembered == null)
            throw new IOException($"{letter}: is a network device whose connection could not be established.");
        bool local = type != 1 && type != 4;
        return new MappingSnapshot(connected, remembered, local);
    }

    public void Remove(string letter, MappingSnapshot expected)
    {
        if (expected.LocalDevice || !Same(Inspect(letter), expected))
            throw new DeferredMappingException("Mapping changed since inspection; cancellation was not attempted.");

        // Enforce removal even with open files, and remove the saved reconnect configuration too.
        int error = cancelConnection(letter + ":", UpdateProfile, true);
        if (error != 0 && error != NotConnected) ThrowOperationError("Disconnect", letter, error);
        if (!Inspect(letter).IsAbsent)
            throw new DeferredMappingException("Cancellation did not leave the letter free of active and remembered mappings; state retained.");
    }

    public void Map(string letter, string path, bool reconnect)
    {
        var current = Inspect(letter);
        bool reconnectExisting = !current.LocalDevice && current.ConnectedPath == null && reconnect &&
            UncPath.Equals(current.RememberedPath, path);
        if (!current.IsAbsent && !reconnectExisting)
            throw new DeferredMappingException("The drive letter was occupied before mapping; it was preserved.");
        var resource = new NativeResource { Type = 1, LocalName = letter + ":", RemoteName = path };
        int error = WNetAddConnection2(ref resource, null, null, reconnect ? UpdateProfile : Temporary);
        if (error != 0) ThrowOperationError("Map", letter, error);
    }

    private static bool Same(MappingSnapshot a, MappingSnapshot b) => a.LocalDevice == b.LocalDevice &&
        SamePath(a.ConnectedPath, b.ConnectedPath) && SamePath(a.RememberedPath, b.RememberedPath);
    private static bool SamePath(string? a, string? b) => a == null ? b == null : UncPath.Equals(a, b);

    private static string? GetConnection(string letter, out bool unavailable)
    {
        unavailable = false;
        int capacity = 1024;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new StringBuilder(capacity);
            int length = buffer.Capacity;
            int error = WNetGetConnection(letter + ":", buffer, ref length);
            if (error == 0) return NormalizeProviderPath(buffer.ToString());
            if (error == NotConnected) return null;
            if (error == ConnectionUnavailable) { unavailable = true; return null; }
            if (error == MoreData && length > capacity && length <= 1024 * 1024)
            {
                capacity = length;
                continue;
            }
            throw new Win32Exception(error, $"Cannot inspect {letter}: connection: {new Win32Exception(error).Message}");
        }
        throw new IOException($"Connection information for {letter}: kept changing size.");
    }

    private static string? GetRememberedConnection(string letter)
    {
        // RESOURCE_REMEMBERED enumerates the user's saved profile, without probing share contents.
        int error = WNetOpenEnum(3, 1, 0, IntPtr.Zero, out IntPtr handle);
        if (error != 0) throw new Win32Exception(error, "Cannot enumerate remembered network mappings.");
        try
        {
            int capacity = 16 * 1024;
            string? path = null;
            while (true)
            {
                IntPtr buffer = Marshal.AllocHGlobal(capacity);
                try
                {
                    int count = -1;
                    int size = capacity;
                    error = WNetEnumResource(handle, ref count, buffer, ref size);
                    if (error == NoMoreItems) return path;
                    if (error == MoreData)
                    {
                        if (size <= capacity || size > 16 * 1024 * 1024)
                            throw new IOException("Invalid remembered mapping buffer size returned by the network provider.");
                        capacity = size;
                        continue;
                    }
                    if (error != 0) throw new Win32Exception(error, "Cannot read remembered network mappings.");
                    int stride = Marshal.SizeOf<NativeResource>();
                    if (count <= 0 || count > capacity / stride)
                        throw new IOException("Invalid remembered mapping count returned by the network provider.");
                    for (int i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<NativeResource>(IntPtr.Add(buffer, i * stride));
                        if (!string.Equals(item.LocalName, letter + ":", StringComparison.OrdinalIgnoreCase)) continue;
                        string found = NormalizeProviderPath(item.RemoteName);
                        if (path != null && !UncPath.Equals(path, found))
                            throw new IOException($"Multiple remembered paths exist for {letter}:.");
                        path = found;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { WNetCloseEnum(handle); }
    }

    private static string NormalizeProviderPath(string? value) =>
        UncPath.TryNormalize(value, out string path) ? path :
            throw new IOException("Network provider returned an invalid UNC mapping path.");

    // A disconnected Microsoft network connection can remain in the Windows user
    // profile while RESOURCE_REMEMBERED omits it. This proves only a saved path,
    // never an active connection. Do not infer the path from our desired policy.
    internal static string? ReadRememberedProfile(RegistryKey user, string root, string letter)
    {
        if (!RegistryStateStore.IsLetter(letter)) throw new ArgumentException("Invalid drive letter.", nameof(letter));
        using var drive = user.OpenSubKey(root + @"\" + letter);
        if (drive == null) return null;
        if (drive.GetValueKind("ProviderType") != RegistryValueKind.DWord ||
            drive.GetValue("ProviderType") is not int provider || provider != 0x20000 ||
            drive.GetValueKind("ConnectionType") != RegistryValueKind.DWord ||
            drive.GetValue("ConnectionType") is not int type || type != 1)
            throw new IOException($"Cannot establish the saved network provider for {letter}:.");
        var kind = drive.GetValueKind("RemotePath");
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new IOException($"Invalid saved RemotePath type for {letter}:.");
        return NormalizeProviderPath(drive.GetValue("RemotePath") as string);
    }

    private static void ThrowOperationError(string operation, string letter, int error)
    {
        string message = $"{operation} {letter}: failed ({error}): {new Win32Exception(error).Message}";
        if (error == 85) throw new DeferredMappingException(message);
        throw new Win32Exception(error, message);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeResource
    {
        public int Scope, Type, DisplayType, Usage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? RemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NativeResource resource, string? password, string? userName, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetOpenEnum(int scope, int type, int usage, IntPtr resource, out IntPtr handle);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetEnumResource(IntPtr handle, ref int count, IntPtr buffer, ref int size);
    [DllImport("mpr.dll")]
    private static extern int WNetCloseEnum(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPath);
}
