using PcbEngine.Advanced;
using PcbEngine.Scenes;

namespace PD.Simple;

internal enum EngineConnectionAction
{
    ConnectLaunchTarget,
    AttachRunningTarget,
    SwitchTarget,
    RecoverWithNewWindow,
}

/// <summary>
/// Projects Engine state into PD connection-chooser behavior. Engine remains
/// authoritative for connection admission and every operation/recovery fence.
/// </summary>
internal static class ConnectionSwitchPolicy
{
    internal static EngineConnectionAction SelectAction(
        SessionSnapshot state,
        SessionTarget target)
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
        SessionSnapshot state,
        SessionTargetKind targetKind,
        SessionTargetAvailability availability,
        IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (availability != SessionTargetAvailability.Available)
        {
            throw new InvalidOperationException(
                DescribeDiagnostics(
                    diagnostics,
                    "The selected Engine target is unavailable."));
        }

        return state.ConnectionState switch
        {
            ConnectionState.Disconnected
                when targetKind == SessionTargetKind.LaunchContext =>
                    EngineConnectionAction.ConnectLaunchTarget,
            ConnectionState.Disconnected
                when targetKind == SessionTargetKind.RunningInstance =>
                    EngineConnectionAction.AttachRunningTarget,
            ConnectionState.Ready => EngineConnectionAction.SwitchTarget,
            // A faulted Engine session cannot switch: the lower document is
            // gone and its unresolved work can never resolve. Recovery opens a
            // new window with a fresh Engine session and attaches there; the
            // caller reports abandoned unresolved operations explicitly.
            ConnectionState.Faulted => EngineConnectionAction.RecoverWithNewWindow,
            _ => throw new InvalidOperationException(
                $"The Engine session cannot change connections while it is {state.ConnectionState}.")
        };
    }

    internal static bool HasActiveOperation(SessionSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Operations.Any(static operation => !IsTerminal(operation.State));
    }

    internal static bool HasUncertainOutcome(
        SessionSnapshot state,
        IReadOnlyList<UnresolvedOperation> unresolvedOperations)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(unresolvedOperations);
        return unresolvedOperations.Count > 0 ||
            state.Operations.Any(static operation =>
                operation.State == ResultOperationState.Uncertain);
    }

    internal static bool HasRequiredRecovery(SessionSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Operations.Any(static operation => operation.Recovery.State is
            RecoveryState.Required or
            RecoveryState.InProgress or
            RecoveryState.Failed or
            RecoveryState.Uncertain);
    }

    internal static bool CanChooseConnection(
        SessionSnapshot state,
        IReadOnlyList<UnresolvedOperation> unresolvedOperations)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(unresolvedOperations);
        if (state.ConnectionState == ConnectionState.Disconnected)
        {
            return true;
        }
        if (state.ConnectionState == ConnectionState.Faulted)
        {
            // Explicit recovery out of a dead session. Fences on the dead
            // document cannot resolve; the recovery path abandons them openly
            // and Engine still admits the fresh attach.
            return true;
        }
        return state.ConnectionState == ConnectionState.Ready &&
            !HasActiveOperation(state) &&
            !HasUncertainOutcome(state, unresolvedOperations) &&
            !HasRequiredRecovery(state);
    }

    internal static string DescribeDiagnostics(
        IEnumerable<Diagnostic> diagnostics,
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

    private static bool IsTerminal(ResultOperationState state) => state is
        ResultOperationState.Complete or
        ResultOperationState.Superseded or
        ResultOperationState.Failed or
        ResultOperationState.Cancelled;
}
