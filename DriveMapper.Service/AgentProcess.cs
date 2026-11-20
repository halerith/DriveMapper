using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DriveMapper;

internal sealed class AgentProcess : IDisposable
{
    private readonly SafeProcessHandle process;
    private readonly SafeFileHandle job;
    public int Id { get; }

    private AgentProcess(SafeProcessHandle process, SafeFileHandle job, int id)
        => (this.process, this.job, Id) = (process, job, id);

    public static AgentProcess Start(UserContext user, string pipe)
    {
        using var token = WindowsSessions.QueryToken(user.SessionId);
        if (UserContext.FromToken(token, user.SessionId) != user)
            throw new InvalidOperationException("The target logon session has changed.");
        string executable = Path.Combine(AppContext.BaseDirectory, Product.AgentExe);
        if (!File.Exists(executable)) throw new FileNotFoundException("Installed Agent is missing.", executable);
        if (!CreateEnvironmentBlock(out IntPtr environment, token, false)) throw new Win32Exception();
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            var arguments = new StringBuilder($"\"{executable}\" --pipe {pipe}");
            if (!CreateProcessAsUser(token, executable, arguments, IntPtr.Zero, IntPtr.Zero, false,
                0x08000404, environment, AppContext.BaseDirectory, ref startup, out var info)) throw new Win32Exception();
            var process = new SafeProcessHandle(info.Process, true);
            using var thread = new SafeWaitHandle(info.Thread, true);
            var job = CreateJobObject(IntPtr.Zero, null);
            try
            {
                if (job.IsInvalid) throw new Win32Exception();
                var limits = new JobLimits();
                limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, including service crash/restart.
                if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobLimits>()) ||
                    !AssignProcessToJobObject(job, process)) throw new Win32Exception();
                if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
                return new AgentProcess(process, job, info.ProcessId);
            }
            catch { TerminateProcess(process, 10); process.Dispose(); job.Dispose(); throw; }
        }
        finally { DestroyEnvironmentBlock(environment); }
    }

    public bool HasExited => GetExitCodeProcess(process, out int code) ? code != 259 : throw new Win32Exception();
    public int ExitCode => GetExitCodeProcess(process, out int code) ? code : throw new Win32Exception();
    public async Task StopAsync()
    {
        job.Dispose();
        // Closing a kill-on-close job requests termination; wait for actual exit before the
        // supervisor treats the user as idle and starts seeding/pruning ownership again.
        while (!HasExited) await Task.Delay(100);
    }
    public void Dispose() { job.Dispose(); process.Dispose(); }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public short ShowWindow, ReservedSize; public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, StringBuilder command,
        IntPtr processSecurity, IntPtr threadSecurity, bool inherit, int flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out int code);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(SafeProcessHandle process, int code);
}
