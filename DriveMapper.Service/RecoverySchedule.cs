using System;
using System.Collections.Generic;
using System.Linq;

namespace DriveMapper;

internal sealed class RecoverySchedule
{
    private sealed record Retry(int Attempts, DateTimeOffset Due);
    private readonly Dictionary<string, Retry> retries = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset periodic;
    private DateTimeOffset? changed;
    private AgentRequest? running;
    public bool IsRunning => running != null;
    public DateTimeOffset? SettlingUntil => changed;

    public RecoverySchedule(DateTimeOffset now) { periodic = now; SignalChange(now); }

    public void SignalChange(DateTimeOffset now)
    {
        changed = now.AddSeconds(60);
        // Policy/network changes supersede historical failures. Do not retain obsolete retry delays.
        retries.Clear();
    }

    public AgentRequest? TakeDue(DateTimeOffset now)
    {
        if (IsRunning || changed > now) return null;
        if (changed != null || now >= periodic || retries.TryGetValue("*", out var global) && global.Due <= now)
        {
            changed = null;
            periodic = now.AddMinutes(5);
            return running = new AgentRequest();
        }
        var letters = retries.Where(pair => pair.Value.Due <= now).Select(pair => pair.Key).ToArray();
        return letters.Length == 0 ? null : running = new AgentRequest(letters);
    }

    public void Complete(IReadOnlyList<DriveOutcome> outcomes, DateTimeOffset now)
    {
        var request = running ?? throw new InvalidOperationException("No Agent is running.");
        running = null;
        var previous = new Dictionary<string, Retry>(retries, StringComparer.OrdinalIgnoreCase);
        if (request.Letters == null) retries.Clear();
        else foreach (string letter in request.Letters) retries.Remove(letter);
        if (changed != null) return; // A newer change already queued a full pass, including events during execution.
        foreach (var outcome in outcomes)
        {
            if (outcome.Kind == OutcomeKind.Success) { retries.Remove(outcome.Letter); continue; }
            int attempt = previous.TryGetValue(outcome.Letter, out var old) ? old.Attempts + 1 : 1;
            int seconds = outcome.Kind is OutcomeKind.AccessError or OutcomeKind.ConfigurationError ? 300 :
                attempt switch { 1 => 15, 2 => 30, 3 => 60, 4 => 120, _ => 300 };
            retries[outcome.Letter] = new(attempt, now.AddSeconds(seconds));
        }
    }
}
