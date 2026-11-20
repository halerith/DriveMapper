using Microsoft.Win32;

namespace Install;

internal static class RegistryCleanup
{
    // Application-owned roots only: never delete HKCU\Network or Explorer mount points.
    public static void RemoveUserRegistry(RegistryKey user)
    {
        user.DeleteSubKeyTree(@"Software\DriveMapper", false);
        user.DeleteSubKeyTree(@"Software\Policies\DriveMapper", false);
    }
}
