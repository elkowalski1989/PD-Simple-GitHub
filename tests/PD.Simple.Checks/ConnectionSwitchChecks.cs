using PcbEngine.Advanced;
using PcbEngine.Scenes;
using PD.Simple;

internal static class ConnectionSwitchChecks
{
    internal static int Run()
    {
        int checks = 0;
        PcbSession owner = PcbSession.Create();
        try
        {
            SessionSnapshot disconnected = owner.State;
            Require(
                ConnectionSwitchPolicy.SelectAction(
                    disconnected,
                    SessionTargetKind.LaunchContext,
                    SessionTargetAvailability.Available,
                    []) == EngineConnectionAction.ConnectLaunchTarget,
                "A launch target did not select initial Engine connection.");
            Require(
                ConnectionSwitchPolicy.SelectAction(
                    disconnected,
                    SessionTargetKind.RunningInstance,
                    SessionTargetAvailability.Available,
                    []) == EngineConnectionAction.AttachRunningTarget,
                "A running target did not select initial Engine attachment.");
            checks += 2;

            SessionSnapshot ready = disconnected with
            {
                ConnectionState = ConnectionState.Ready,
            };
            foreach (SessionTargetKind kind in Enum.GetValues<SessionTargetKind>())
            {
                Require(
                    ConnectionSwitchPolicy.SelectAction(
                        ready,
                        kind,
                        SessionTargetAvailability.Available,
                        []) == EngineConnectionAction.SwitchTarget,
                    "A ready Engine session did not preserve one-session switching.");
                checks++;
            }

            foreach (ConnectionState state in Enum.GetValues<ConnectionState>()
                .Where(state => state is not (
                    ConnectionState.Disconnected or
                    ConnectionState.Ready or
                    ConnectionState.Faulted)))
            {
                RequireThrows<InvalidOperationException>(() =>
                    ConnectionSwitchPolicy.SelectAction(
                        disconnected with { ConnectionState = state },
                        SessionTargetKind.RunningInstance,
                        SessionTargetAvailability.Available,
                        []));
                checks++;
            }

            SessionSnapshot faulted = disconnected with
            {
                ConnectionState = ConnectionState.Faulted,
            };
            foreach (SessionTargetKind kind in Enum.GetValues<SessionTargetKind>())
            {
                Require(
                    ConnectionSwitchPolicy.SelectAction(
                        faulted,
                        kind,
                        SessionTargetAvailability.Available,
                        []) == EngineConnectionAction.RecoverWithNewWindow,
                    "A faulted Engine session did not select new-window recovery.");
                checks++;
            }

            const string mismatch =
                "The selected package does not match the running Engine host.";
            RequireThrows<InvalidOperationException>(
                () => ConnectionSwitchPolicy.SelectAction(
                    disconnected,
                    SessionTargetKind.RunningInstance,
                    SessionTargetAvailability.Unavailable,
                    [new Diagnostic("engine_package_mismatch", mismatch)]),
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
            foreach (ResultOperationState state in new[]
            {
                ResultOperationState.Preparing,
                ResultOperationState.Running,
                ResultOperationState.AwaitingUserInput,
                ResultOperationState.WaitingForApplication,
            })
            {
                SessionSnapshot active = WithOperation(
                    ready,
                    document,
                    state,
                    Recovery.None);
                Require(
                    ConnectionSwitchPolicy.HasActiveOperation(active) &&
                    !ConnectionSwitchPolicy.CanChooseConnection(active, []),
                    $"Engine operation state {state} did not keep connection choice busy.");
                checks++;
            }

            SessionSnapshot uncertain = WithOperation(
                ready,
                document,
                ResultOperationState.Uncertain,
                new Recovery(RecoveryState.Required, "recover", []));
            Require(
                ConnectionSwitchPolicy.HasUncertainOutcome(uncertain, []) &&
                !ConnectionSwitchPolicy.CanChooseConnection(uncertain, []),
                "An uncertain Engine outcome did not retain its connection fence.");
            checks++;

            var unresolved = new UnresolvedOperation(
                "unresolved-operation",
                ResultOperationState.Uncertain,
                document,
                DateTimeOffset.UtcNow,
                [],
                new Recovery(RecoveryState.Required, "recover", []));
            Require(
                ConnectionSwitchPolicy.HasUncertainOutcome(ready, [unresolved]) &&
                !ConnectionSwitchPolicy.CanChooseConnection(ready, [unresolved]),
                "Retained unresolved Engine work did not keep the session selected.");
            checks++;

            foreach (RecoveryState recoveryState in new[]
            {
                RecoveryState.Required,
                RecoveryState.InProgress,
                RecoveryState.Failed,
                RecoveryState.Uncertain,
            })
            {
                SessionSnapshot recovery = WithOperation(
                    ready,
                    document,
                    ResultOperationState.Failed,
                    new Recovery(recoveryState, "operation-specific", []));
                Require(
                    ConnectionSwitchPolicy.HasRequiredRecovery(recovery) &&
                    !ConnectionSwitchPolicy.CanChooseConnection(recovery, []),
                    $"Engine recovery state {recoveryState} did not retain its connection fence.");
                checks++;
            }

            SessionSnapshot availableRecovery = WithOperation(
                ready,
                document,
                ResultOperationState.Complete,
                new Recovery(RecoveryState.Available, "undo", []));
            Require(
                !ConnectionSwitchPolicy.HasRequiredRecovery(availableRecovery) &&
                ConnectionSwitchPolicy.CanChooseConnection(availableRecovery, []),
                "Optional completed Engine recovery was promoted to a required fence.");
            checks++;

            foreach (ResultOperationState state in new[]
            {
                ResultOperationState.Complete,
                ResultOperationState.Superseded,
                ResultOperationState.Failed,
                ResultOperationState.Cancelled,
            })
            {
                SessionSnapshot terminal = WithOperation(
                    ready,
                    document,
                    state,
                    Recovery.None);
                Require(
                    !ConnectionSwitchPolicy.HasActiveOperation(terminal),
                    $"Terminal Engine operation state {state} remained busy.");
                checks++;
            }

            foreach (ConnectionState state in new[]
            {
                ConnectionState.Connecting,
                ConnectionState.Switching,
                ConnectionState.Recovering,
                ConnectionState.Disposing,
                ConnectionState.Disposed,
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
            SessionSnapshot faultedUncertain = WithOperation(
                faulted,
                document,
                ResultOperationState.Uncertain,
                new Recovery(RecoveryState.Required, "recover", []));
            Require(
                ConnectionSwitchPolicy.CanChooseConnection(faultedUncertain, [unresolved]),
                "A faulted Engine session with unresolvable fenced work could not open recovery choice.");
            checks++;

            string described = ConnectionSwitchPolicy.DescribeDiagnostics(
                [new Diagnostic(
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

    private static SessionSnapshot WithOperation(
        SessionSnapshot state,
        WorkspaceDocumentIdentity document,
        ResultOperationState operationState,
        Recovery recovery) =>
        state with
        {
            Operations =
            [
                new OperationSnapshot(
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
