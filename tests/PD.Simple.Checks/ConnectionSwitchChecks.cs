using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.Simple;

internal static class ConnectionSwitchChecks
{
    internal static int Run()
    {
        int checks = 0;
        AllegroEngineSession owner = AllegroEngineSession.Create();
        try
        {
            EngineSessionSnapshot disconnected = owner.State;
            Require(
                ConnectionSwitchPolicy.SelectAction(
                    disconnected,
                    EngineSessionTargetKind.LaunchContext,
                    EngineSessionTargetAvailability.Available,
                    []) == EngineConnectionAction.ConnectLaunchTarget,
                "A launch target did not select initial Engine connection.");
            Require(
                ConnectionSwitchPolicy.SelectAction(
                    disconnected,
                    EngineSessionTargetKind.RunningInstance,
                    EngineSessionTargetAvailability.Available,
                    []) == EngineConnectionAction.AttachRunningTarget,
                "A running target did not select initial Engine attachment.");
            checks += 2;

            EngineSessionSnapshot ready = disconnected with
            {
                ConnectionState = EngineConnectionState.Ready,
            };
            foreach (EngineSessionTargetKind kind in Enum.GetValues<EngineSessionTargetKind>())
            {
                Require(
                    ConnectionSwitchPolicy.SelectAction(
                        ready,
                        kind,
                        EngineSessionTargetAvailability.Available,
                        []) == EngineConnectionAction.SwitchTarget,
                    "A ready Engine session did not preserve one-session switching.");
                checks++;
            }

            foreach (EngineConnectionState state in Enum.GetValues<EngineConnectionState>()
                .Where(state => state is not (
                    EngineConnectionState.Disconnected or
                    EngineConnectionState.Ready or
                    EngineConnectionState.Faulted)))
            {
                RequireThrows<InvalidOperationException>(() =>
                    ConnectionSwitchPolicy.SelectAction(
                        disconnected with { ConnectionState = state },
                        EngineSessionTargetKind.RunningInstance,
                        EngineSessionTargetAvailability.Available,
                        []));
                checks++;
            }

            EngineSessionSnapshot faulted = disconnected with
            {
                ConnectionState = EngineConnectionState.Faulted,
            };
            foreach (EngineSessionTargetKind kind in Enum.GetValues<EngineSessionTargetKind>())
            {
                Require(
                    ConnectionSwitchPolicy.SelectAction(
                        faulted,
                        kind,
                        EngineSessionTargetAvailability.Available,
                        []) == EngineConnectionAction.RecoverWithNewWindow,
                    "A faulted Engine session did not select new-window recovery.");
                checks++;
            }

            const string mismatch =
                "The selected package does not match the running Engine host.";
            RequireThrows<InvalidOperationException>(
                () => ConnectionSwitchPolicy.SelectAction(
                    disconnected,
                    EngineSessionTargetKind.RunningInstance,
                    EngineSessionTargetAvailability.Unavailable,
                    [new EngineDiagnostic("engine_package_mismatch", mismatch)]),
                mismatch);
            checks++;

            Require(
                ConnectionSwitchPolicy.CanChooseConnection(disconnected, []),
                "A disconnected Engine session could not open an explicit target.");
            Require(
                ConnectionSwitchPolicy.CanChooseConnection(ready, []),
                "An idle ready Engine session could not open the chooser.");
            checks += 2;

            WorkspaceDocumentIdentity document = new(
                "changed-session",
                2,
                17,
                197,
                "changed-board.brd",
                "PD_V25");
            foreach (EngineOperationState state in new[]
            {
                EngineOperationState.Preparing,
                EngineOperationState.Running,
                EngineOperationState.AwaitingUserInput,
                EngineOperationState.WaitingForApplication,
            })
            {
                EngineSessionSnapshot active = WithOperation(
                    ready,
                    document,
                    state,
                    EngineRecovery.None);
                Require(
                    ConnectionSwitchPolicy.HasActiveOperation(active) &&
                    !ConnectionSwitchPolicy.CanChooseConnection(active, []),
                    $"Engine operation state {state} did not keep connection choice busy.");
                checks++;
            }

            EngineSessionSnapshot uncertain = WithOperation(
                ready,
                document,
                EngineOperationState.Uncertain,
                new EngineRecovery(EngineRecoveryState.Required, "recover", []));
            Require(
                ConnectionSwitchPolicy.HasUncertainOutcome(uncertain, []) &&
                !ConnectionSwitchPolicy.CanChooseConnection(uncertain, []),
                "An uncertain Engine outcome did not retain its connection fence.");
            checks++;

            var unresolved = new EngineUnresolvedOperation(
                "unresolved-operation",
                EngineOperationState.Uncertain,
                document,
                DateTimeOffset.UtcNow,
                [],
                new EngineRecovery(EngineRecoveryState.Required, "recover", []));
            Require(
                ConnectionSwitchPolicy.HasUncertainOutcome(ready, [unresolved]) &&
                !ConnectionSwitchPolicy.CanChooseConnection(ready, [unresolved]),
                "Retained unresolved Engine work did not keep the session selected.");
            checks++;

            foreach (EngineRecoveryState recoveryState in new[]
            {
                EngineRecoveryState.Required,
                EngineRecoveryState.InProgress,
                EngineRecoveryState.Failed,
                EngineRecoveryState.Uncertain,
            })
            {
                EngineSessionSnapshot recovery = WithOperation(
                    ready,
                    document,
                    EngineOperationState.Failed,
                    new EngineRecovery(recoveryState, "operation-specific", []));
                Require(
                    ConnectionSwitchPolicy.HasRequiredRecovery(recovery) &&
                    !ConnectionSwitchPolicy.CanChooseConnection(recovery, []),
                    $"Engine recovery state {recoveryState} did not retain its connection fence.");
                checks++;
            }

            EngineSessionSnapshot availableRecovery = WithOperation(
                ready,
                document,
                EngineOperationState.Complete,
                new EngineRecovery(EngineRecoveryState.Available, "undo", []));
            Require(
                !ConnectionSwitchPolicy.HasRequiredRecovery(availableRecovery) &&
                ConnectionSwitchPolicy.CanChooseConnection(availableRecovery, []),
                "Optional completed Engine recovery was promoted to a required fence.");
            checks++;

            foreach (EngineOperationState state in new[]
            {
                EngineOperationState.Complete,
                EngineOperationState.Superseded,
                EngineOperationState.Failed,
                EngineOperationState.Cancelled,
            })
            {
                EngineSessionSnapshot terminal = WithOperation(
                    ready,
                    document,
                    state,
                    EngineRecovery.None);
                Require(
                    !ConnectionSwitchPolicy.HasActiveOperation(terminal),
                    $"Terminal Engine operation state {state} remained busy.");
                checks++;
            }

            foreach (EngineConnectionState state in new[]
            {
                EngineConnectionState.Connecting,
                EngineConnectionState.Switching,
                EngineConnectionState.Recovering,
                EngineConnectionState.Disposing,
                EngineConnectionState.Disposed,
            })
            {
                Require(
                    !ConnectionSwitchPolicy.CanChooseConnection(
                        disconnected with { ConnectionState = state },
                        []),
                    $"Connection choice was enabled while Engine was {state}.");
                checks++;
            }

            Require(
                ConnectionSwitchPolicy.CanChooseConnection(faulted, []),
                "A faulted Engine session could not open recovery choice; Reconnect stranded the user.");
            checks++;
            EngineSessionSnapshot faultedUncertain = WithOperation(
                faulted,
                document,
                EngineOperationState.Uncertain,
                new EngineRecovery(EngineRecoveryState.Required, "recover", []));
            Require(
                ConnectionSwitchPolicy.CanChooseConnection(faultedUncertain, [unresolved]),
                "A faulted Engine session with unresolvable fenced work could not open recovery choice.");
            checks++;

            string described = ConnectionSwitchPolicy.DescribeDiagnostics(
                [new EngineDiagnostic(
                    "engine_package_mismatch",
                    "Package mismatch.",
                    CorrectiveAction: "Install the matching bundle.")],
                "fallback");
            Require(
                described == "Package mismatch. Install the matching bundle.",
                "Engine diagnostics or corrective action were reinterpreted by PD.");
            checks++;
        }
        finally
        {
            owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return checks;
    }

    private static EngineSessionSnapshot WithOperation(
        EngineSessionSnapshot state,
        WorkspaceDocumentIdentity document,
        EngineOperationState operationState,
        EngineRecovery recovery) =>
        state with
        {
            Operations =
            [
                new EngineOperationSnapshot(
                    "operation",
                    operationState,
                    document,
                    DateTimeOffset.UtcNow,
                    [],
                    recovery),
            ],
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireThrows<TException>(
        Action action,
        string? requiredMessage = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            if (requiredMessage is not null &&
                !exception.Message.Contains(requiredMessage, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The expected Engine diagnostic was not preserved.",
                    exception);
            }
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
