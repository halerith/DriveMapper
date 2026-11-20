using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.ServiceProcess;

namespace DriveMapper;

internal static class Program
{
    private static int Main()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) return 1; // No console/debug mode that accidentally launches user mapping work.
        ServiceBase.Run(new MappingService());
        return 0;
    }
}

internal sealed class MappingService : ServiceBase
{
    private sealed class Session : IDisposable
    {
        public UserContext User { get; }
        public RecoverySchedule Schedule { get; }
        public CancellationTokenSource Lifetime { get; }
        public Task? Agent { get; set; }
        public Dictionary<string, DriveOutcome> Last { get; } = new();
        public Session(UserContext user, DateTimeOffset now, CancellationToken stop)
        { User = user; Schedule = new(now); Lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop); }
        public void Dispose() { Lifetime.Cancel(); Lifetime.Dispose(); }
    }
    private readonly ConcurrentQueue<(Session Session, IReadOnlyList<DriveOutcome> Outcomes)> completed = new();
    private CancellationTokenSource? stopping;
    private Task? supervisor;
    private int sessionChanged = 1, networkChanged, stopRequested;

    public MappingService()
    {
        ServiceName = Product.ServiceName;
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        CanShutdown = true;
        AutoLog = false;
    }
    protected override void OnStart(string[] args)
    {
        WindowsSessions.EnableLaunchPrivileges();
        stopping = new();
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        supervisor = Task.Run(() => SuperviseAsync(stopping.Token));
    }
    protected override void OnSessionChange(SessionChangeDescription change) => Interlocked.Exchange(ref sessionChanged, 1);
    protected override bool OnPowerEvent(PowerBroadcastStatus status)
    {
        if (status is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeCritical)
            Interlocked.Exchange(ref networkChanged, 1);
        return true;
    }
    private void NetworkChanged(object? sender, EventArgs args) => Interlocked.Exchange(ref networkChanged, 1);
    protected override void OnShutdown() => OnStop();
    protected override void OnStop()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) != 0) return;
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        stopping?.Cancel();
        if (supervisor != null && !supervisor.Wait(TimeSpan.FromSeconds(20)))
            ServiceLog.Write("Supervisor did not stop within 20 seconds.", EventLogEntryType.Error);
        stopping?.Dispose();
    }

    private async Task SuperviseAsync(CancellationToken stop)
    {
        var sessions = new Dictionary<int, Session>();
        var watchers = new Dictionary<string, PolicyWatcher>();
        var agents = new UserAgentTasks();
        DateTimeOffset inventoryAt = DateTimeOffset.MinValue;
        string? lastError = null;
        var detectionErrors = new Dictionary<string, string>();
        try
        {
            ServiceLog.Write("DriveMapper-Service started. Agents are short-lived; policy settling is 60 seconds.");
            while (!stop.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                try
                {
                    agents.RemoveCompleted();
                    if (now >= inventoryAt || Interlocked.Exchange(ref sessionChanged, 0) != 0)
                    {
                        inventoryAt = now.AddSeconds(15);
                        var discovered = WindowsSessions.Discover();
                        foreach (var old in sessions.Values.ToArray())
                            if (!discovered.Contains(old.User)) { old.Dispose(); sessions.Remove(old.User.SessionId); }
                        foreach (var group in discovered.GroupBy(user => user.Sid))
                        {
                            var additions = group.Where(user => !sessions.ContainsKey(user.SessionId)).ToArray();
                            if (agents.IsRunning(group.Key)) continue;
                            // Initialize all same-user sessions before any Agent changes shared remembered ownership.
                            try
                            {
                                using var stateLock = UserStateLock.TryAcquire(group.Key);
                                if (stateLock == null) continue; // A manual Agent may own the same user state.
                                SessionOwnership.Prepare(group.Key, group.Select(user => user.LogonId), WindowsSessions.InventoryComplete);
                                foreach (var user in additions) sessions[user.SessionId] = new Session(user, now, stop);
                                detectionErrors.Remove("state:" + group.Key);
                            }
                            catch (Exception ex) { DetectionError("state:" + group.Key, ex); }
                        }
                        var needed = discovered.Select(user => user.Sid).ToHashSet();
                        foreach (string old in watchers.Keys.Where(key => !needed.Contains(key)).ToArray())
                        { watchers[old].Dispose(); watchers.Remove(old); }
                        foreach (string key in needed)
                            if (!watchers.ContainsKey(key))
                            {
                                try { watchers[key] = new PolicyWatcher(key); detectionErrors.Remove(key); }
                                catch (Exception ex) { DetectionError(key, ex); }
                            }
                    }
                    foreach (var watcher in watchers)
                    {
                        try
                        {
                            if (watcher.Value.Check(now))
                                foreach (var session in sessions.Values.Where(s => s.User.Sid == watcher.Key))
                                    session.Schedule.SignalChange(now);
                            detectionErrors.Remove(watcher.Key);
                        }
                        catch (Exception ex)
                        {
                            // One inaccessible policy tree must not stall other sessions, completions, or retry timers.
                            DetectionError(watcher.Key, ex);
                        }
                    }
                    if (Interlocked.Exchange(ref networkChanged, 0) != 0)
                        foreach (var session in sessions.Values) session.Schedule.SignalChange(now);

                    while (completed.TryDequeue(out var completion))
                    {
                        var session = completion.Session;
                        if (!sessions.TryGetValue(session.User.SessionId, out var current) || current != session) continue;
                        session.Schedule.Complete(completion.Outcomes, now);
                        if (completion.Outcomes.All(outcome => outcome.Letter != "*") && session.Last.Remove("*", out var globalFailure))
                            ServiceLog.Write($"Session {session.User.SessionId}: Agent/global processing recovered from {globalFailure.Kind}.");
                        foreach (var outcome in completion.Outcomes)
                        {
                            if ((!session.Last.TryGetValue(outcome.Letter, out var previous) || previous != outcome) &&
                                (outcome.Kind != OutcomeKind.Success || previous?.Kind != OutcomeKind.Success && previous != null))
                                ServiceLog.Write($"Session {session.User.SessionId}, {outcome.Letter}: {outcome.Kind}. {outcome.Message}",
                                    outcome.Kind == OutcomeKind.Success ? EventLogEntryType.Information : EventLogEntryType.Warning);
                            session.Last[outcome.Letter] = outcome;
                        }
                    }
                    agents.RemoveCompleted();
                    foreach (var session in sessions.Values)
                    {
                        if (agents.Count >= 4) break;
                        if (agents.IsRunning(session.User.Sid) ||
                            sessions.Values.Any(other => other.User.Sid == session.User.Sid && other.Schedule.IsRunning)) continue;
                        var request = session.Schedule.TakeDue(now);
                        if (request == null) continue;
                        var lifetime = session.Lifetime.Token;
                        session.Agent = Task.Run(async () =>
                        {
                            IReadOnlyList<DriveOutcome> outcomes;
                            try { outcomes = await AgentLauncher.RunAsync(session.User, request, lifetime); }
                            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                            { outcomes = new[] { new DriveOutcome("*", OutcomeKind.TransientFailure, "Agent exceeded its 30-second connection or three-minute execution deadline.") }; }
                            catch (Exception ex) { outcomes = new[] { new DriveOutcome("*", OutcomeKind.TransientFailure, ex.Message) }; }
                            completed.Enqueue((session, outcomes));
                        }, CancellationToken.None);
                        agents.Track(session.User.Sid, session.Agent);
                    }
                    lastError = null;
                }
                catch (Exception ex)
                {
                    if (lastError != ex.Message) ServiceLog.Write("Supervisor will retry: " + ex.Message, EventLogEntryType.Error);
                    lastError = ex.Message;
                }
                await Task.Delay(1000, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ServiceLog.Write("Supervisor failed: " + ex, EventLogEntryType.Error);
            Environment.Exit(10); // SCM recovery restarts an unexpectedly failed service.
        }
        finally
        {
            foreach (var session in sessions.Values) session.Dispose();
            foreach (var watcher in watchers.Values) watcher.Dispose();
            await agents.DrainAsync();
        }

        void DetectionError(string key, Exception error)
        {
            if (!detectionErrors.TryGetValue(key, out string? previous) || previous != error.Message)
                ServiceLog.Write($"Detection {key} will be retried: {error.Message}", EventLogEntryType.Warning);
            detectionErrors[key] = error.Message;
        }
    }
}
