using System.Linq;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.Simple;

internal static class RecoveryHandoffChecks
{
    internal static async Task RunAsync()
    {
        var journal = new System.Collections.Generic.List<string> { "op-uncertain" };
        bool teardownFinished = false;
        System.Collections.Generic.IReadOnlyList<EngineUnresolvedOperation> snapshot =
            await RecoveryHandoff.SnapshotAfterTeardownAsync(
                () =>
                {
                    journal.Add("op-terminal-during-disposal");
                    teardownFinished = true;
                    return Task.CompletedTask;
                },
                () => journal
                    .Select(id => new EngineUnresolvedOperation(
                        id,
                        EngineOperationState.Uncertain,
                        new WorkspaceDocumentIdentity("s", 1, 1, null, "board.brd", "v1"),
                        DateTimeOffset.UtcNow,
                        [],
                        EngineRecovery.None))
                    .ToArray());
        if (!teardownFinished ||
            snapshot.Count != 2 ||
            snapshot.All(operation => operation.OperationId != "op-terminal-during-disposal"))
        {
            throw new InvalidOperationException(
                "The recovery snapshot did not run after teardown completed.");
        }
        await ExpectArgumentNullAsync(() =>
            RecoveryHandoff.SnapshotAfterTeardownAsync(null!, () => []));
        await ExpectArgumentNullAsync(() =>
            RecoveryHandoff.SnapshotAfterTeardownAsync(() => Task.CompletedTask, null!));

        WorkspaceDocumentIdentity Board(string design) =>
            new("prior", 1, 1, null, design, "v1");
        EngineUnresolvedOperation Failed(string id, string design) =>
            new(id, EngineOperationState.Uncertain, Board(design), DateTimeOffset.UtcNow, [], EngineRecovery.None);
        System.Collections.Generic.IReadOnlyList<EngineUnresolvedOperation> obligations =
            RecoveryHandoff.TransferFailureObligations(
                [Failed("a", "b.brd"), Failed("b", "a.brd"), Failed("c", "b.brd")],
                DateTimeOffset.UtcNow,
                "adopt failed: duplicate id");
        if (obligations.Count != 2 ||
            obligations[0].OperationId != "recovery-transfer-failed:0" ||
            obligations[0].Document.Design != "a.brd" ||
            obligations[1].OperationId != "recovery-transfer-failed:1" ||
            obligations[1].Document.Design != "b.brd")
        {
            throw new InvalidOperationException(
                "Transfer failure did not produce one deterministic obligation per affected board.");
        }
        if (obligations.Any(operation =>
                operation.LastState != EngineOperationState.Uncertain ||
                operation.Recovery.State != EngineRecoveryState.Required ||
                operation.Recovery.Action is null ||
                !operation.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == "pd_recovery_transfer_failed" &&
                    diagnostic.Message.Contains("adopt failed", StringComparison.Ordinal))))
        {
            throw new InvalidOperationException(
                "Transfer-failure obligations lack uncertain state, required recovery, or cause.");
        }

        string longReason = new('r', 600);
        EngineUnresolvedOperation truncated = RecoveryHandoff.TransferFailureObligations(
            [Failed("a", "b.brd")], DateTimeOffset.UtcNow, longReason).Single();
        string message = truncated.Diagnostics.Single().Message;
        if (message.Length > 700 || !message.Contains(new string('r', 500), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The transfer-failure reason was not bounded.");
        }
        if (RecoveryHandoff.TransferFailureObligations([], DateTimeOffset.UtcNow, "reason").Count != 0)
        {
            throw new InvalidOperationException("Empty carried evidence produced obligations.");
        }
        ExpectArgumentNull(() =>
            RecoveryHandoff.TransferFailureObligations(null!, DateTimeOffset.UtcNow, "reason"));
        ExpectArgument(() =>
            RecoveryHandoff.TransferFailureObligations([Failed("a", "b.brd")], DateTimeOffset.UtcNow, "  "));
    }

    private static void ExpectArgument(Action attempt)
    {
        try
        {
            attempt();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid recovery handoff input was accepted.");
    }

    private static void ExpectArgumentNull(Action attempt)
    {
        try
        {
            attempt();
        }
        catch (ArgumentNullException)
        {
            return;
        }
        throw new InvalidOperationException("Null recovery handoff input was accepted.");
    }

    private static async Task ExpectArgumentNullAsync(Func<Task> attempt)
    {
        try
        {
            await attempt().ConfigureAwait(false);
        }
        catch (ArgumentNullException)
        {
            return;
        }
        throw new InvalidOperationException("Null recovery handoff input was accepted.");
    }
}
