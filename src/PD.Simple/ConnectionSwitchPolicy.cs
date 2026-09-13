using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.Simple;

internal enum EngineConnectionAction
{
    ConnectLaunchTarget,
    AttachRunningTarget,
    SwitchTarget,
}

/// <summary>
/// Projects Engine state into PD connection-chooser behavior. Engine remains
/// authoritative for connection admission and every operation/recovery fence.
/// </summary>
internal static class ConnectionSwitchPolicy
{
    internal static EngineConnectionAction SelectAction(
        EngineSessionSnapshot state,
        EngineSessionTarget target)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(target);
        return SelectAction(
            state,
            target.Kind,
            target.Availability,
            target.Diagnostics);
    }

    internal static EngineConnectionAction SelectAction(
        EngineSessionSnapshot state,
        EngineSessionTargetKind targetKind,
        EngineSessionTargetAvailability availability,
        IEnumerable<EngineDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (availability != EngineSessionTargetAvailability.Available)
        {
            throw new InvalidOperationException(
                DescribeDiagnostics(
                    diagnostics,
                    "The selected Engine target is unavailable."));
        }

        return state.ConnectionState switch
        {
            EngineConnectionState.Disconnected
                when targetKind == EngineSessionTargetKind.LaunchContext =>
                    EngineConnectionAction.ConnectLaunchTarget,
            EngineConnectionState.Disconnected
                when targetKind == EngineSessionTargetKind.RunningInstance =>
                    EngineConnectionAction.AttachRunningTarget,
            EngineConnectionState.Ready => EngineConnectionAction.SwitchTarget,
            _ => throw new InvalidOperationException(
                $"The Engine session cannot change connections while it is {state.ConnectionState}.")
        };
    }

    internal static bool HasActiveOperation(EngineSessionSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Operations.Any(static operation => !IsTerminal(operation.State));
    }

    internal static bool HasUncertainOutcome(
        EngineSessionSnapshot state,
        IReadOnlyList<EngineUnresolvedOperation> unresolvedOperations)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(unresolvedOperations);
        return unresolvedOperations.Count > 0 ||
            state.Operations.Any(static operation =>
                operation.State == EngineOperationState.Uncertain);
    }

    internal static bool HasRequiredRecovery(EngineSessionSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Operations.Any(static operation => operation.Recovery.State is
            EngineRecoveryState.Required or
            EngineRecoveryState.InProgress or
            EngineRecoveryState.Failed or
            EngineRecoveryState.Uncertain);
    }

    internal static bool CanChooseConnection(
        EngineSessionSnapshot state,
        IReadOnlyList<EngineUnresolvedOperation> unresolvedOperations)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(unresolvedOperations);
        if (state.ConnectionState == EngineConnectionState.Disconnected)
        {
            return true;
        }
        return state.ConnectionState == EngineConnectionState.Ready &&
            !HasActiveOperation(state) &&
            !HasUncertainOutcome(state, unresolvedOperations) &&
            !HasRequiredRecovery(state);
    }

    internal static string DescribeDiagnostics(
        IEnumerable<EngineDiagnostic> diagnostics,
        string fallback)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        string[] messages = diagnostics
            .Where(static diagnostic => diagnostic is not null)
            .Select(static diagnostic => string.IsNullOrWhiteSpace(diagnostic.CorrectiveAction)
                ? diagnostic.Message
                : $"{diagnostic.Message} {diagnostic.CorrectiveAction}")
            .Where(static message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return messages.Length == 0 ? fallback : string.Join(" ", messages);
    }

    private static bool IsTerminal(EngineOperationState state) => state is
        EngineOperationState.Complete or
        EngineOperationState.Superseded or
        EngineOperationState.Failed or
        EngineOperationState.Cancelled;
}
