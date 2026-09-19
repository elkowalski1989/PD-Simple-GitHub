using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.Simple;

/// <summary>
/// Recovery handoff primitives shared by the reconnect window flow.
/// Snapshot-after-teardown ordering and transfer-failure restriction are
/// pure and unit-tested; MainWindow wires them to the Engine session.
/// </summary>
internal static class RecoveryHandoff
{
    /// <summary>
    /// Runs teardown to completion before snapshotting unresolved
    /// operations, so terminal outcomes that arrive during disposal are
    /// included in the carried evidence instead of lost to a pre-close
    /// snapshot.
    /// </summary>
    internal static async Task<System.Collections.Generic.IReadOnlyList<EngineUnresolvedOperation>>
        SnapshotAfterTeardownAsync(
            Func<Task> teardownAsync,
            Func<System.Collections.Generic.IReadOnlyList<EngineUnresolvedOperation>> snapshot)
    {
        ArgumentNullException.ThrowIfNull(teardownAsync);
        ArgumentNullException.ThrowIfNull(snapshot);
        await teardownAsync().ConfigureAwait(false);
        return snapshot();
    }

    /// <summary>
    /// Builds one restriction obligation per affected board when carried
    /// recovery evidence cannot be adopted. Adopting the result keeps the
    /// replacement session explicitly restricted through the Engine
    /// mutation gate and switch fence instead of merely warning.
    /// </summary>
    internal static System.Collections.Generic.IReadOnlyList<EngineUnresolvedOperation>
        TransferFailureObligations(
            System.Collections.Generic.IReadOnlyList<EngineUnresolvedOperation> failed,
            DateTimeOffset observedAt,
            string reason)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string[] documents = failed
            .Select(static operation => operation.Document.Design ?? "\0")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static design => design, StringComparer.Ordinal)
            .ToArray();
        string trimmed = reason.Length > 500 ? reason[..500] : reason;
        var obligations =
            new System.Collections.Generic.List<EngineUnresolvedOperation>(documents.Length);
        for (int index = 0; index < documents.Length; index++)
        {
            WorkspaceDocumentIdentity document = failed
                .First(operation => (operation.Document.Design ?? "\0")
                    .Equals(documents[index], StringComparison.Ordinal))
                .Document;
            obligations.Add(new EngineUnresolvedOperation(
                $"recovery-transfer-failed:{index}",
                EngineOperationState.Uncertain,
                document,
                observedAt,
                [new EngineDiagnostic(
                    "pd_recovery_transfer_failed",
                    $"Carried recovery evidence could not be adopted: {trimmed}. " +
                    "Inspect the affected board in Allegro before mutating.")],
                new EngineRecovery(
                    EngineRecoveryState.Required,
                    "Inspect the affected board in Allegro, then resolve each carried obligation with reviewed evidence.",
                    [])));
        }
        return obligations;
    }
}
