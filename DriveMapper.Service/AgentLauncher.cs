using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DriveMapper;

internal static class AgentLauncher
{
    public static async Task<IReadOnlyList<DriveOutcome>> RunAsync(UserContext user, AgentRequest request, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var cancellation = timeout.Token;
        string name = "DriveMapper-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(user.Sid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (PipeOptions)0x00080000, 4096, 4096, security);
        using var process = AgentProcess.Start(user, name);
        using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        connectionTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            await pipe.WaitForConnectionAsync(connectionTimeout.Token);
            if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) && pid == process.Id) break;
            pipe.Disconnect(); // SID ACL alone does not authenticate a particular Agent process.
        }
        await PipeWire.WriteAsync(pipe, request, cancellation);
        var report = await PipeWire.ReadAsync<AgentReport>(pipe, cancellation);
        AgentReportRules.Validate(report, user);
        while (!process.HasExited) await Task.Delay(100, cancellation);
        if (process.ExitCode is not (0 or 2 or 10)) throw new IOException("Agent exited unexpectedly: " + process.ExitCode);
        return report.Drives;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
