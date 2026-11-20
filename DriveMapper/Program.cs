using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace DriveMapper;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var log = new RuntimeLog();
        bool serviceMode = args.Length == 2 && args[0] == "--pipe" &&
            args[1].StartsWith("DriveMapper-", StringComparison.Ordinal) &&
            Guid.TryParseExact(args[1].Substring("DriveMapper-".Length), "N", out _);
        if (args.Length != 0 && !serviceMode)
        {
            log.Error("Use DriveMapper-Agent without arguments for a manual run. Exit code 1.");
            return 1;
        }
        try
        {
            using var executionDeadline = new ExecutionDeadline(TimeSpan.FromMinutes(3));
            var user = UserContext.Current();
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Run the Agent in the normal unelevated user session.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var pipe = serviceMode ? new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous) : null;
            AgentRequest request = new();
            if (pipe != null)
            {
                await pipe.ConnectAsync(15000, deadline.Token);
                request = await PipeWire.ReadAsync<AgentRequest>(pipe, deadline.Token);
            }
            if (request.Letters != null && (request.Letters.Length > 26 ||
                Array.Exists(request.Letters, letter => letter == null || !RegistryStateStore.IsLetter(letter))))
                throw new InvalidDataException("Invalid requested drive letters.");

            ReconciliationResult result;
            // Same-user sessions share the remembered profile. Serialize their writes, while storing active ownership separately.
            {
                using var stateLock = UserStateLock.TryAcquire(user.Sid);
                if (stateLock == null)
                {
                    result = new ReconciliationResult { Deferred = 1 };
                    result.Drives.Add(new("*", OutcomeKind.Deferred, "Another Agent for this user is running."));
                }
                else
                {
                    try
                    {
                        SessionOwnership.Prepare(user.Sid, new[] { user.LogonId });
                        result = new DriveReconciler(new RegistryPolicySource(), new RegistryStateStore(user.LogonId),
                            new WindowsNetworkMappings(), new ExplorerLabels(), serviceMode ? new AgentLog(log) : log)
                            .Run(request.Letters == null ? null : new HashSet<string>(request.Letters, StringComparer.OrdinalIgnoreCase));
                    }
                    catch (Exception ex)
                    {
                        result = new ReconciliationResult { Failed = 1 };
                        result.Drives.Add(new("*", OutcomeKind.TransientFailure, ex.Message));
                    }
                }
            }
            if (pipe != null)
                await PipeWire.WriteAsync(pipe, new AgentReport(user.SessionId, user.Sid, user.LogonId, result.Drives), deadline.Token);
            else
                log.Info($"Manual reconciliation: {result.Succeeded} succeeded, {result.Deferred} deferred, {result.Failed} failed. Exit code {result.ExitCode}.");
            return result.ExitCode;
        }
        catch (Exception ex)
        {
            log.Error($"DriveMapper-Agent failed: {ex}");
            return 10;
        }
    }

    // The service logs failure/recovery transitions once. Agents still log actual mapping changes.
    private sealed class AgentLog : IRuntimeLog
    {
        private readonly IRuntimeLog inner;
        public AgentLog(IRuntimeLog inner) => this.inner = inner;
        public void Info(string message) => inner.Info(message);
        public void Warn(string message) { }
        public void Error(string message) { }
    }
}
