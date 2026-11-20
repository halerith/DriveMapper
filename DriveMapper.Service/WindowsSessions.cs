using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DriveMapper;

internal static class WindowsSessions
{
    private static readonly Dictionary<int, string> DiscoveryErrors = new();
    // A skipped token could belong to any user. Never prune ownership from an incomplete inventory.
    public static bool InventoryComplete { get; private set; }

    public static void EnableLaunchPrivileges()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var token)) throw new Win32Exception();
        using (token)
            foreach (string name in new[] { "SeTcbPrivilege", "SeAssignPrimaryTokenPrivilege", "SeIncreaseQuotaPrivilege" })
            {
                if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception();
                var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                int error = Marshal.GetLastWin32Error();
                if (error != 0) throw new Win32Exception(error, "Cannot enable service privilege " + name);
            }
    }
    public static IReadOnlyList<UserContext> Discover()
    {
        InventoryComplete = false;
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr sessions, out int count)) throw new Win32Exception();
        try
        {
            var result = new List<UserContext>();
            bool complete = true;
            int stride = Marshal.SizeOf<SessionInfo>();
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<SessionInfo>(IntPtr.Add(sessions, i * stride));
                if (session.Id == 0 || session.State is not (0 or 1 or 4)) continue;
                try
                {
                    using var token = QueryToken(session.Id);
                    result.Add(UserContext.FromToken(token, session.Id));
                    DiscoveryErrors.Remove(session.Id);
                }
                catch (NoUserTokenException)
                {
                    // WTS can enumerate a session before sign-in or during sign-out.
                    // Retry on the next discovery without warning about an expected condition.
                    // Its unknown identity still makes ownership pruning unsafe.
                    complete = false;
                    DiscoveryErrors.Remove(session.Id);
                }
                catch (Win32Exception ex) { complete = false; RecordError(session.Id, ex.Message); }
                catch (InvalidOperationException ex) { complete = false; RecordError(session.Id, ex.Message); }
            }
            InventoryComplete = complete;
            return result;
        }
        finally { WTSFreeMemory(sessions); }
    }

    private static void RecordError(int sessionId, string message)
    {
        if (!DiscoveryErrors.TryGetValue(sessionId, out string? previous) || previous != message)
            ServiceLog.Write($"Session {sessionId} is not ready/eligible for an Agent: {message}", System.Diagnostics.EventLogEntryType.Warning);
        DiscoveryErrors[sessionId] = message;
    }

    public static SafeAccessTokenHandle QueryToken(int sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out var token))
        {
            int error = Marshal.GetLastWin32Error();
            token?.Dispose();
            if (error == 1008) throw new NoUserTokenException(); // ERROR_NO_TOKEN, scoped to WTS discovery only.
            throw new Win32Exception(error);
        }
        try
        {
            if (TokenInformation.ElevationType(token) == 2) // TokenElevationTypeFull: use the linked normal Explorer token.
            {
                var limited = TokenInformation.LinkedToken(token);
                token.Dispose();
                token = limited;
            }
            if (TokenInformation.IsElevated(token))
                throw new InvalidOperationException("Windows returned an elevated user token; no normal unelevated session token was selected.");
            return token;
        }
        catch { token.Dispose(); throw; }
    }

    private sealed class NoUserTokenException : Win32Exception
    {
        public NoUserTokenException() : base(1008) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo { public int Id; public IntPtr StationName; public int State; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public int Count; public Luid Luid; public int Attributes; }
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges privileges,
        int size, IntPtr previous, IntPtr required);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out SafeAccessTokenHandle token);
}
