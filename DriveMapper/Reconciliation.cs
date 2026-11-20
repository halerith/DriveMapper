using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.IO;

namespace DriveMapper;

internal sealed record DrivePolicy(bool Enabled, string Path, string Name, bool Reconnect, string? Error = null);

internal sealed record MappingSnapshot(string? ConnectedPath, string? RememberedPath, bool LocalDevice = false)
{
    public bool IsAbsent => ConnectedPath == null && RememberedPath == null && !LocalDevice;
    public bool HasNetworkMapping => ConnectedPath != null || RememberedPath != null;
    public bool Matches(string path, bool reconnect) => !LocalDevice &&
        UncPath.Equals(ConnectedPath, path) &&
        (reconnect ? UncPath.Equals(RememberedPath, path) : RememberedPath == null);
}

internal interface IPolicySource { IReadOnlyDictionary<string, DrivePolicy> Load(); }
internal interface IAppliedState
{
    IReadOnlyDictionary<string, string> Load();
    void Save(string letter, string path, bool reconnect = false);
    void Delete(string letter);
}
internal interface INetworkMappings
{
    MappingSnapshot Inspect(string letter);
    void Remove(string letter, MappingSnapshot expected);
    void Map(string letter, string path, bool reconnect);
}
internal interface IDriveLabels { void Apply(string letter, string path, string name); }
internal interface IRuntimeLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

internal sealed class DeferredMappingException : Exception
{
    public DeferredMappingException(string message) : base(message) { }
}

internal sealed class ReconciliationResult
{
    public List<DriveOutcome> Drives { get; } = new();
    public int Succeeded { get; set; }
    public int Deferred { get; set; }
    public int Failed { get; set; }
    public int ExitCode => Failed > 0 ? 10 : Deferred > 0 ? 2 : 0;
}

internal sealed class DriveReconciler
{
    private readonly IPolicySource policies;
    private readonly IAppliedState state;
    private readonly INetworkMappings network;
    private readonly IDriveLabels labels;
    private readonly IRuntimeLog log;

    public DriveReconciler(IPolicySource policies, IAppliedState state, INetworkMappings network,
        IDriveLabels labels, IRuntimeLog log)
        => (this.policies, this.state, this.network, this.labels, this.log) = (policies, state, network, labels, log);

    public ReconciliationResult Run(ISet<string>? letters = null)
    {
        var result = new ReconciliationResult();
        IReadOnlyDictionary<string, DrivePolicy> effective;
        IReadOnlyDictionary<string, string> applied;
        try
        {
            // No mutations unless BOTH complete inputs were read successfully.
            effective = policies.Load();
            applied = state.Load();
        }
        catch (Exception ex)
        {
            log.Error($"Policy/state could not be read reliably. All mappings preserved: {ex.Message}");
            result.Failed++;
            result.Drives.Add(Failure("*", ex));
            return result;
        }

        // Do not probe unrelated local drives, removable media, or network paths.
        foreach (string letter in effective.Keys.Concat(applied.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            if (letters != null && !letters.Contains(letter)) continue;
            try
            {
                effective.TryGetValue(letter, out var policy);
                applied.TryGetValue(letter, out var previous);
                if (policy?.Error != null)
                    throw new InvalidDataException($"Invalid policy; mapping preserved: {policy.Error}");

                if (policy?.Enabled == true)
                {
                    // Validate before inspection or disconnection. Partial policy is not a removal request.
                    if (!UncPath.TryNormalize(policy.Path, out string path))
                        throw new InvalidDataException("Enabled policy requires a valid UNC server/share path; mapping preserved.");
                    ReconcileEnabled(letter, policy, path, network.Inspect(letter));
                }
                else if (previous != null)
                {
                    Cleanup(letter, previous, network.Inspect(letter));
                }
                result.Succeeded++;
                result.Drives.Add(new(letter, OutcomeKind.Success));
            }
            catch (DeferredMappingException ex)
            {
                log.Warn($"{letter}: deferred until another execution: {ex.Message}");
                result.Deferred++;
                result.Drives.Add(new(letter, OutcomeKind.Deferred, ex.Message));
            }
            catch (Exception ex)
            {
                log.Error($"{letter}: reconciliation failed: {ex.Message}");
                result.Failed++;
                result.Drives.Add(Failure(letter, ex));
            }
        }
        return result;
    }

    private static DriveOutcome Failure(string letter, Exception ex)
    {
        int? code = (ex as Win32Exception)?.NativeErrorCode;
        var kind = ex is InvalidDataException ? OutcomeKind.ConfigurationError :
            ex is UnauthorizedAccessException || code is 5 or 86 or 1326 or 1219
                ? OutcomeKind.AccessError : OutcomeKind.TransientFailure;
        return new(letter, kind, ex.Message, code);
    }

    private void Cleanup(string letter, string previous, MappingSnapshot current)
    {
        bool connectedOwned = UncPath.Equals(current.ConnectedPath, previous);
        bool rememberedOwned = UncPath.Equals(current.RememberedPath, previous);
        bool connectedForeign = current.ConnectedPath != null && !connectedOwned;
        bool rememberedForeign = current.RememberedPath != null && !rememberedOwned;

        if (connectedOwned || rememberedOwned)
        {
            // Cancellation by letter can affect both records. Never destroy a foreign replacement.
            if (current.LocalDevice || connectedForeign || rememberedForeign)
                throw new DeferredMappingException("Active and remembered ownership conflict; mapping and cleanup state preserved.");
            network.Remove(letter, current);
            log.Info($"Removed owned mapping and reconnect configuration for {letter}: ({previous}).");
        }
        else if (!current.IsAbsent)
        {
            log.Info($"{letter}: no longer matches the previously applied path; current device/mapping preserved.");
        }

        // Remove verifies the postcondition before returning. Exceptions retain the legacy path-only state.
        state.Delete(letter);
    }

    private void ReconcileEnabled(string letter, DrivePolicy policy, string path, MappingSnapshot current)
    {
        if (current.LocalDevice)
            throw new DeferredMappingException("The letter is occupied by a local device; it will not be disconnected.");

        if (!current.Matches(path, policy.Reconnect))
        {
            bool reconnectExisting = current.ConnectedPath == null && policy.Reconnect &&
                UncPath.Equals(current.RememberedPath, path);
            if (current.HasNetworkMapping && !reconnectExisting)
            {
                // Retire the old path AND its saved reconnect entry. Never restore a revoked destination.
                network.Remove(letter, current);
                state.Delete(letter);
                log.Info($"Retired previous mapping for {letter}: before applying {path}.");
            }

            network.Map(letter, path, policy.Reconnect);
            if (!network.Inspect(letter).Matches(path, policy.Reconnect))
                throw new InvalidOperationException("Windows did not confirm the requested path and persistence; new ownership was not saved.");
            log.Info($"Mapped {letter}: -> {path} (Reconnect={policy.Reconnect}).");
        }

        // Track confirmed mapping even if updating its display label subsequently fails.
        state.Save(letter, path, policy.Reconnect);
        labels.Apply(letter, path, policy.Name);
    }
}
