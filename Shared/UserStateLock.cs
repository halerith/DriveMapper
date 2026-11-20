using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DriveMapper;

// Agents and the SYSTEM supervisor must use the same lock, including during seeding/pruning.
// The explicit DACL permits the target user to open a mutex first created by SYSTEM.
internal sealed class UserStateLock : IDisposable
{
    private readonly Mutex mutex;
    private UserStateLock(Mutex mutex) => this.mutex = mutex;

    public static UserStateLock? TryAcquire(string sid)
    {
        sid = new SecurityIdentifier(sid).Value;
        string descriptor = $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00100001;;;{sid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(descriptor, 1, out var security, out _))
            throw new Win32Exception();
        SafeWaitHandle handle;
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = security };
            handle = CreateMutexEx(ref attributes, @"Global\DriveMapper-" + sid, 0, 0x00100001);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        }
        finally { LocalFree(security); }
        var mutex = new Mutex(false);
        var anonymous = mutex.SafeWaitHandle;
        mutex.SafeWaitHandle = handle;
        anonymous.Dispose();
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new UserStateLock(mutex);
            mutex.Dispose();
            return null;
        }
        catch { mutex.Dispose(); throw; }
    }

    // Mutex ownership is thread-affine: never await inside this scope.
    public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr security, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateMutexExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexEx(ref SecurityAttributes attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
