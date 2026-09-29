using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Reviews;
using PD.PcbTools;

internal static class PhysicalSymbolStageChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Directory.CreateTempSubdirectory("pd-symbol-stage-checks-").FullName;
        try
        {
            string source = Path.Combine(root, "different-source.dra");
            string staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);
            byte[] sourceBytes = [0x44, 0x52, 0x41, 0x2D, 0x01, 0x02, 0x03];
            await File.WriteAllBytesAsync(source, sourceBytes);
            string foreign = Path.Combine(staging, "renamed-symbol.dra");
            await File.WriteAllTextAsync(foreign, "foreign file");
            PhysicalSymbolStageCopy copy = await PhysicalSymbolStageCopy.CreateAsync(
                source, staging, "renamed-symbol", "offline-test");
            check(copy.Area.StagingRoot != staging && copy.Area.SymbolName == "renamed-symbol" &&
                File.Exists(copy.Area.StagedDraPath), "Staging did not create an explicit DRA in a fresh owned directory.");
            check((await File.ReadAllBytesAsync(source)).SequenceEqual(sourceBytes) &&
                (await File.ReadAllBytesAsync(copy.Area.StagedDraPath)).SequenceEqual(sourceBytes) &&
                await File.ReadAllTextAsync(foreign) == "foreign file", "Source or pre-existing destination content was changed.");
            check(copy.SourceSha256 == copy.StagedSha256 && copy.SourceSha256 ==
                Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(), "Stage evidence did not bind exact source and copied bytes.");
            await copy.RequireUnchangedAsync();
            await RejectAsync<IOException>(() => ArchiveFile.SaveNewAsync(copy.Area.StagedDraPath,
                (output, token) => output.WriteAsync(new byte[] { 0xff }, token)).AsTask(), check,
                "A colliding atomic publication overwrote the committed stage.");
            check((await File.ReadAllBytesAsync(copy.Area.StagedDraPath)).SequenceEqual(sourceBytes),
                "Collision cleanup removed or changed the committed stage.");
            using var afterCommit = new CancellationTokenSource();
            PhysicalSymbolStageCopy committed = await PhysicalSymbolStageCopy.CreateAsync(source, staging,
                "committed_stage", "test", afterCommit.Token);
            afterCommit.Cancel();
            await committed.RequireUnchangedAsync();
            check(File.Exists(committed.Area.StagedDraPath),
                "Cancellation after successful staging removed the committed stage.");
            await File.AppendAllTextAsync(copy.Area.StagedDraPath, "replacement");
            await RejectAsync<InvalidDataException>(() => copy.RequireUnchangedAsync().AsTask(), check,
                "A changed staged file retained preparation eligibility.");
            await RejectAsync<ArgumentException>(() => PhysicalSymbolStageCopy.CreateAsync(
                Path.ChangeExtension(source, ".brd"), staging, "other", "test").AsTask(), check,
                "A board file was accepted as a staged DRA.");
            int beforeCancellation = Directory.GetFileSystemEntries(staging).Length;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await RejectAsync<OperationCanceledException>(() => PhysicalSymbolStageCopy.CreateAsync(
                source, staging, "canceled", "test", cancellation.Token).AsTask(), check,
                "Canceled staging created a usable file.");
            check(Directory.GetFileSystemEntries(staging).Length == beforeCancellation,
                "Pre-canceled staging created an owned directory or removed unrelated output.");
            await using AllegroEngineSession session = AllegroEngineSession.Create();
            var runner = new EngineSymbolBindingRunner(session.Workspace);
            await runner.StageExistingAsync(source, staging, "runner_stage", "test");
            check(runner.StageCopy is not null && runner.Preparation is null && runner.Binding is null &&
                runner.CanChangeSession && !runner.CanApply && !runner.CanPublish,
                "Copying a DRA fabricated native PACKAGE, preview, apply or publication evidence.");
            await RejectAsync<InvalidOperationException>(() => runner.ActivateAsync(
                new(EnginePhysicalSymbolExtensionDescriptor.AcceptedExtensionId, "1.0.0", "invented.il", new string('a', 64))).AsTask(),
                check, "A user-entered well-formed hash established trusted descriptor identity.");
            await RejectAsync<InvalidOperationException>(() => runner.ActivatePackagedAsync().AsTask(), check,
                "An offline workspace was treated as the selected staged PACKAGE document.");
            check(!runner.IsBusy && runner.Binding is null && runner.CanChangeSession,
                "A refused document activation retained a busy flag or fabricated a binding.");
            await File.AppendAllTextAsync(runner.StageCopy!.Area.StagedDraPath, "stale stage");
            await RejectAsync<InvalidDataException>(() => runner.ActivatePackagedAsync().AsTask(), check,
                "Changed staged bytes reached extension activation.");
            await RejectAsync<OperationCanceledException>(() => runner.StageExistingAsync(source, staging,
                "canceled_runner", "test", cancellation.Token).AsTask(), check,
                "A pre-canceled runner stage was admitted.");
            check(runner.StageCopy.Area.SymbolName == "runner_stage" && !runner.IsBusy,
                "A canceled stage replaced the admitted stage or lost its idle state.");
            await CheckRequiredRecoveryAsync(root, source, staging, session, check);
            check(!runner.CanApply && runner.Capabilities.All(item => item.InstalledAndMatched != true) &&
                runner.Capabilities.Where(item => item.EnabledForProduction).Select(item => item.Operation)
                    .SequenceEqual([EnginePhysicalSymbolOperation.Generate]),
                "Managed stage evidence fabricated a matched module or executable production preparation.");
        }
        finally
        {
            Directory.Delete(root, recursive: true); // Exact test-owned root only.
        }
    }

    private static async Task CheckRequiredRecoveryAsync(string root, string source, string staging,
        AllegroEngineSession session, Action<bool, string> check)
    {
        // Synthetic apply evidence exercises the real runner's result adoption and public guards.
        // The inert preparation is only a retained record identity; it cannot dispatch native work.
        MethodInfo adopt = typeof(EngineSymbolBindingRunner).GetMethod("AdoptApplyEvidence",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo submitted = typeof(EngineSymbolBindingRunner).GetField("_submittedPreparation",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var required = new EngineRecovery(EngineRecoveryState.Required, "Inspect the original saved DRA.", []);
        foreach ((bool resultRequired, bool operationRequired) in new[] { (true, false), (false, true), (true, true) })
        {
            var runner = new EngineSymbolBindingRunner(session.Workspace);
            await runner.StageExistingAsync(source, staging, "recovery_stage", "synthetic-test");
            PhysicalSymbolStageCopy copy = runner.StageCopy!;
            string record = Path.Combine(copy.Area.StagingRoot, "synthetic-original-operation.json");
            const string recordContent = "Synthetic retained operation; no native execution.";
            await File.WriteAllTextAsync(record, recordContent);
            var preparation = (EnginePhysicalSymbolPreparation)RuntimeHelpers.GetUninitializedObject(
                typeof(EnginePhysicalSymbolPreparation));
            typeof(EnginePhysicalSymbolPreparation).GetProperty(nameof(preparation.OperationRecordPath))!
                .SetValue(preparation, record);
            submitted.SetValue(runner, preparation);
            var document = new WorkspaceDocumentIdentity("synthetic-recovery-session", 3, 9, 719,
                copy.Area.StagedDraPath, "PD_V25");
            var provenance = new EngineEvidence("synthetic-original-operation", document,
                DateTimeOffset.UtcNow, "offline-test", "synthetic failed-persistence evidence", false);
            var before = new EnginePhysicalSymbolSnapshotEvidence(document, "PACKAGE", LengthUnit.Mils,
                [], [], [], new Dictionary<string, string>(), false, new string('b', 64), provenance, []);
            var operation = new EngineOperationSnapshot(provenance.OperationId, EngineOperationState.Complete,
                document, DateTimeOffset.UtcNow, [], operationRequired ? required : EngineRecovery.None);
            var evidence = new EnginePhysicalSymbolApplyEvidence(EnginePhysicalSymbolApplyStatus.Failed,
                before, null, null, ["physical_symbol_saved_dra_unchanged_after_effects"],
                new(EngineBoardDatabaseRecoveryStatus.CommittedBeforeReadback,
                    EngineLibraryFileRecoveryStatus.PartialOutputCannotBeExcluded, "Saved DRA requires inspection."),
                operation, EngineFreshness.Historical, provenance, [], resultRequired ? required : EngineRecovery.None)
            {
                Execution = new(EnginePhysicalSymbolPhaseStatus.Succeeded, "committed",
                    EnginePhysicalSymbolPhaseStatus.Failed, [], [], EnginePhysicalSymbolPhaseStatus.NotStarted,
                    EnginePhysicalSymbolPhaseStatus.Failed, record),
            };
            adopt.Invoke(runner, [evidence]);
            check(ReferenceEquals(runner.LastApply, evidence) && runner.HasUnresolvedOutcome &&
                !runner.CanApply && !runner.CanPublish && !runner.CanChangeSession && runner.CloseBlockReason is not null,
                "Terminal failed persistence discarded explicit result or operation recovery and released the journey.");
            await RejectAsync<InvalidOperationException>(() => runner.ApplyAsync("no-replay").AsTask(), check,
                "Required recovery admitted another Apply.", "Settle the original submitted operation");
            await RejectAsync<InvalidOperationException>(() => runner.PublishDraAsync(
                new(copy.Area.StagedDraPath, Path.Combine(root, "library"), "recovery_stage.dra",
                    EngineLibraryOverwritePolicy.FailIfExists, true, false), "no-publication").AsTask(), check,
                "Required recovery admitted publication.", "Settle the original submitted operation");
            await RejectAsync<InvalidOperationException>(() => runner.StageExistingAsync(
                source, staging, "replacement", "test").AsTask(), check,
                "Required recovery admitted a new staged copy.", "Settle the original submitted operation");
            await RejectAsync<InvalidOperationException>(() =>
            {
                runner.PlanStage(staging, "replacement", "test");
                return Task.CompletedTask;
            }, check, "Required recovery admitted a replacement stage plan.", "Settle the original submitted operation");
            check(ReferenceEquals(submitted.GetValue(runner), preparation) &&
                preparation.OperationRecordPath == record && runner.LastApply?.Execution.OperationRecordPath == record &&
                ReferenceEquals(runner.LastApply, evidence) && ReferenceEquals(runner.StageCopy, copy) &&
                runner.Preparation is null && !runner.IsBusy && runner.HasUnresolvedOutcome &&
                await File.ReadAllTextAsync(record) == recordContent,
                "A refused replay, publication or new stage replaced original recovery evidence or record identity.");
        }

        var resolved = new EngineSymbolBindingRunner(session.Workspace);
        await resolved.StageExistingAsync(source, staging, "resolved_stage", "synthetic-test");
        var resolvedDocument = new WorkspaceDocumentIdentity("resolved-session", 7, 11, 823,
            resolved.StageCopy!.Area.StagedDraPath, "PD_V25");
        var resolvedProvenance = new EngineEvidence("resolved-operation", resolvedDocument, DateTimeOffset.UtcNow,
            "offline-test", "synthetic not-started evidence", false);
        var resolvedBefore = new EnginePhysicalSymbolSnapshotEvidence(resolvedDocument, "PACKAGE", LengthUnit.Mils,
            [], [], [], new Dictionary<string, string>(), false, new string('c', 64), resolvedProvenance, []);
        var resolvedEvidence = new EnginePhysicalSymbolApplyEvidence(EnginePhysicalSymbolApplyStatus.Failed,
            resolvedBefore, null, null, [], new(EngineBoardDatabaseRecoveryStatus.NotNeeded,
                EngineLibraryFileRecoveryStatus.NotNeeded, "No effects started."),
            new("resolved-operation", EngineOperationState.Failed, resolvedDocument, DateTimeOffset.UtcNow, [], EngineRecovery.None),
            EngineFreshness.Historical, resolvedProvenance, [], EngineRecovery.None)
        {
            Execution = new(EnginePhysicalSymbolPhaseStatus.Failed, "not_started", EnginePhysicalSymbolPhaseStatus.NotStarted,
                [], [], EnginePhysicalSymbolPhaseStatus.NotStarted, EnginePhysicalSymbolPhaseStatus.NotStarted, null),
        };
        adopt.Invoke(resolved, [resolvedEvidence]);
        check(!resolved.HasUnresolvedOutcome && resolved.CanChangeSession && resolved.CloseBlockReason is null &&
            ReferenceEquals(resolved.LastApply, resolvedEvidence) && !resolved.CanApply && !resolved.CanPublish,
            "A known resolved, not-started failure incorrectly required recovery or authorized mutation.");
        await resolved.StageExistingAsync(source, staging, "fresh_after_resolved", "test");
        check(resolved.StageCopy!.Area.SymbolName == "fresh_after_resolved" && resolved.LastApply is null,
            "A resolved failure prevented an explicitly new staged journey.");
    }

    private static async Task RejectAsync<T>(Func<Task> action, Action<bool, string> check, string message,
        string? requiredReason = null) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T error)
        {
            check(requiredReason is null || error.Message.Contains(requiredReason, StringComparison.Ordinal), message);
            return;
        }
        check(false, message);
    }
}
