using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DriveMapper;

internal sealed record UserContext(int SessionId, string Sid, string LogonId)
{
    public static UserContext Current()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return FromToken(identity.AccessToken, Process.GetCurrentProcess().SessionId);
    }

    public static UserContext FromToken(SafeAccessTokenHandle token, int sessionId)
    {
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        if (identity.IsSystem || sessionId == 0) throw new InvalidOperationException("An interactive user session is required.");
        IntPtr data = Marshal.AllocHGlobal(128);
        try
        {
            if (!GetTokenInformation(token, 10, data, 128, out _)) throw new Win32Exception();
            // TOKEN_STATISTICS.AuthenticationId follows TokenId (two DWORDs each).
            string logonId = unchecked((ulong)Marshal.ReadInt64(data, 8)).ToString("X16");
            return new(sessionId, identity.User!.Value, logonId);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int length, out int required);
}
