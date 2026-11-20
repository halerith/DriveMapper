using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DriveMapper;

internal static class TokenInformation
{
    public static int ElevationType(SafeAccessTokenHandle token) => ReadInt32(token, 18, "TokenElevationType");
    public static bool IsElevated(SafeAccessTokenHandle token) => ReadInt32(token, 20, "TokenElevation") != 0;

    public static SafeAccessTokenHandle LinkedToken(SafeAccessTokenHandle token)
    {
        IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Query(token, 19, "TokenLinkedToken", buffer, IntPtr.Size);
            var linked = new SafeAccessTokenHandle(Marshal.ReadIntPtr(buffer));
            if (linked.IsInvalid) { linked.Dispose(); throw new InvalidDataException("Windows returned an invalid linked token."); }
            return linked;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int ReadInt32(SafeAccessTokenHandle token, int informationClass, string name)
    {
        // TokenElevation requires exactly sizeof(TOKEN_ELEVATION), a single DWORD.
        // A larger reusable buffer fails with ERROR_BAD_LENGTH on Windows.
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Query(token, informationClass, name, buffer, sizeof(int));
            return Marshal.ReadInt32(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void Query(SafeAccessTokenHandle token, int informationClass, string name, IntPtr buffer, int size)
    {
        if (!UserContext.GetTokenInformation(token, informationClass, buffer, size, out int required))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"GetTokenInformation({name}) failed: Win32 {error}; buffer {size}, required {required} bytes.");
        }
    }
}
