using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.Simple.Tools.ConstraintsDrc;

internal static class ConstraintsDrcChecks
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;

        RequireThrows<ArgumentNullException>(
            () => new ConstraintsDrcViewModel(null!),
            "A null Engine session was accepted by the Constraints/DRC tool.");
        checks++;

        AllegroEngineSession session = AllegroEngineSession.Create();
        try
        {
            var tool = new ConstraintsDrcViewModel(session);
            try
            {
                Require(!tool.IsConnected, "A disconnected Engine session reported connected.");
                Require(!tool.CanRefreshConstraints, "Constraint refresh was offered while disconnected.");
                Require(tool.RefreshConstraintsReason.Contains("not connected", StringComparison.Ordinal),
                    "The disconnected constraint gate did not name the missing connection.");
                Require(!tool.CanReadMarkers, "DRC marker read was offered while disconnected.");
                Require(tool.ReadMarkersReason.Contains("not connected", StringComparison.Ordinal),
                    "The disconnected marker gate did not name the missing connection.");
                Require(!tool.CanReadEffective && !tool.CanEditConstraints && !tool.CanRunDrc,
                    "An unpackaged Engine operation was offered as available.");
                Require(tool.RunDrcReason.Contains("AllegroWorkspaceDrcRun", StringComparison.Ordinal) &&
                        tool.ReadEffectiveReason.Contains("effective-read", StringComparison.Ordinal) &&
                        tool.EditConstraintsReason.Contains("mutation-preparation", StringComparison.Ordinal),
                    "Pending-package gate reasons did not name the exact missing Engine API.");
                Require(!tool.HasSnapshot && !tool.HasMarkerRead,
                    "A fresh tool reported snapshot or marker state it never acquired.");
                Require(tool.SetRows.Count == 0 && tool.FieldRows.Count == 0 &&
                        tool.AssignmentRows.Count == 0 && tool.ContextAssignmentRows.Count == 0 &&
                        tool.NetClassRows.Count == 0 && tool.ClassClassRows.Count == 0 &&
                        tool.ValueRows.Count == 0 && tool.MarkerRows.Count == 0 &&
                        tool.GroupRows.Count == 0,
                    "A fresh tool listed snapshot or marker rows it never acquired.");
                Require(!tool.CanMarkReviewed, "Review was offered with no marker selected.");
                checks += 9;

                tool.ObserveConstraintSnapshot();
                Require(tool.StatusDetail.Contains("not connected", StringComparison.Ordinal),
                    "Offline constraint observation did not report its gate.");
                await tool.ReadDrcMarkersAsync();
                Require(!tool.HasMarkerRead &&
                        tool.StatusDetail.Contains("not connected", StringComparison.Ordinal),
                    "An offline DRC marker read changed tool state.");
                await tool.RefreshConstraintSnapshotAsync();
                Require(!tool.HasSnapshot, "An offline constraint refresh acquired state.");
                checks += 3;

                tool.MarkSelectedReviewed("note");
                Require(!tool.CanMarkReviewed, "A review note was recorded with no selection.");
                checks++;

                Require(tool.DrcRunSummary.Contains("not been executed", StringComparison.Ordinal),
                    "A fresh tool reported DRC execution it never ran.");
                await tool.RunDrcAsync();
                Require(tool.LastDrcRun is null &&
                        tool.StatusDetail.Contains("not connected", StringComparison.Ordinal) &&
                        tool.StatusDetail.Contains("AllegroWorkspaceDrcRun", StringComparison.Ordinal),
                    "An offline DRC run changed tool state or hid its Engine API.");
                checks += 2;

                var queryRow = new ConstraintsDrcValueRow(
                    "Spacing", "DDR", "ETCH/TOP", "MinLineWidth", "On", "5.0");
                EngineConstraintQuery query = ConstraintsDrcViewModel.BuildEffectiveQuery(
                    queryRow, EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils);
                Require(query.Domain == EngineConstraintDomain.Spacing &&
                        query.Field == "MinLineWidth" &&
                        query.ConstraintSet == "DDR" &&
                        query.Layer == "ETCH/TOP" &&
                        query.TargetKind == EngineConstraintTargetKind.ConstraintSetValue,
                    "A snapshot value row did not map to its exact effective-read query.");
                var nativeRow = new ConstraintsDrcValueRow(
                    "Physical", "DEFAULT", "BOTTOM", "width_min", "On", "7.0");
                EngineConstraintQuery nativeQuery = ConstraintsDrcViewModel.BuildEffectiveQuery(
                    nativeRow, EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils);
                Require(nativeQuery.Layer == "ETCH/BOTTOM" && nativeQuery.ConstraintSet == "DEFAULT" &&
                        nativeQuery.Field == "width_min" && nativeQuery.Domain == EngineConstraintDomain.Physical,
                    "A native snapshot subclass did not become its exact qualified query layer.");
                EngineConstraintQuery renamedQuery = ConstraintsDrcViewModel.BuildEffectiveQuery(
                    nativeRow with { SetName = "RENAMED_SET", Layer = "INNER_SIGNAL_12" },
                    EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils);
                Require(renamedQuery.Layer == "ETCH/INNER_SIGNAL_12" && renamedQuery.ConstraintSet == "RENAMED_SET",
                    "Constraint layer qualification depended on the observed board or outer-layer name.");
                foreach (string unsupportedLayer in new[] { "(all layers)", "", "BOARD GEOMETRY/INNER_SIGNAL_12", "ETCH/" })
                {
                    RequireThrows<ArgumentException>(
                        () => ConstraintsDrcViewModel.BuildEffectiveQuery(
                            nativeRow with { Layer = unsupportedLayer },
                            EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils),
                        "An unsupported or missing exact etch layer was accepted: " + unsupportedLayer);
                    checks++;
                }
                EngineConstraintQuery electrical = ConstraintsDrcViewModel.BuildEffectiveQuery(
                    queryRow with { Domain = "Electrical", Layer = "(all layers)" },
                    EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils);
                Require(electrical.Layer is null,
                    "An electrical CSet query acquired a layer from its display marker.");
                RequireThrows<ArgumentException>(
                    () => ConstraintsDrcViewModel.BuildEffectiveQuery(
                        queryRow with { Domain = "Electrical" },
                        EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils),
                    "An electrical query accepted a non-null layer.");
                checks += 3;
                RequireThrows<ArgumentException>(
                    () => ConstraintsDrcViewModel.BuildEffectiveQuery(
                        queryRow with { Domain = "Nope" },
                        EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils),
                    "An unknown constraint domain was accepted into a query.");
                RequireThrows<ArgumentNullException>(
                    () => ConstraintsDrcViewModel.BuildEffectiveQuery(
                        null!, EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils),
                    "A null snapshot row produced an effective-read query.");
                checks += 4;

                await tool.ReadEffectiveAsync(query);
                Require(tool.EffectiveSummary.Contains("No effective read yet", StringComparison.Ordinal) &&
                        tool.StatusDetail.Contains("not connected", StringComparison.Ordinal) &&
                        tool.StatusDetail.Contains("effective-read", StringComparison.Ordinal),
                    "An offline effective read changed tool state or hid its Engine API.");
                checks++;

                EngineConstraintScalar mils = ConstraintsDrcViewModel.BuildScalar(
                    EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils, "5.0");
                Require(mils.Number == 5.0m && mils.Unit == EngineConstraintUnit.Mils,
                    "A mils scalar did not parse exactly.");
                EngineConstraintScalar unitless = ConstraintsDrcViewModel.BuildScalar(
                    EngineConstraintScalarKind.Number, EngineConstraintUnit.Unitless, "5.0");
                Require(unitless.Unit == EngineConstraintUnit.Unitless,
                    "A unitless scalar lost its unit.");
                Require(ConstraintsDrcViewModel.BuildScalar(
                        EngineConstraintScalarKind.Boolean, EngineConstraintUnit.Unitless, "true").Boolean == true,
                    "A boolean scalar did not parse.");
                Require(ConstraintsDrcViewModel.BuildScalar(
                        EngineConstraintScalarKind.Symbol, EngineConstraintUnit.Unitless, "PLATED").Text == "PLATED" &&
                        ConstraintsDrcViewModel.BuildScalar(
                        EngineConstraintScalarKind.Text, EngineConstraintUnit.Unitless, "note").Text == "note",
                    "Symbol and text scalars did not round-trip.");
                Require(ConstraintsDrcViewModel.DescribeScalar(mils).Contains("Mils", StringComparison.Ordinal),
                    "A scalar summary dropped its unit.");
                RequireThrows<ArgumentException>(
                    () => ConstraintsDrcViewModel.BuildScalar(
                        EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils, "abc"),
                    "A non-numeric scalar was accepted as a number.");
                RequireThrows<ArgumentException>(
                    () => ConstraintsDrcViewModel.BuildScalar(
                        EngineConstraintScalarKind.Boolean, EngineConstraintUnit.Unitless, "yes"),
                    "A non-boolean scalar was accepted as a boolean.");
                RequireThrows<ArgumentException>(
                    () => ConstraintsDrcViewModel.BuildScalar(
                        EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils, "  "),
                    "A blank scalar was accepted.");
                checks += 7;
                checks += CheckEditInputValidation(tool);

                EngineConstraintChange setValue = ConstraintsDrcViewModel.BuildChange(
                    EngineConstraintChangeKind.SetValue, mils);
                Require(setValue.Kind == EngineConstraintChangeKind.SetValue && setValue.Value == mils,
                    "A set-value change dropped its typed scalar.");
                Require(ConstraintsDrcViewModel.BuildChange(
                    EngineConstraintChangeKind.ResetValue).Kind ==
                    EngineConstraintChangeKind.ResetValue,
                    "A reset change was not built.");
                Require(setValue.ConstraintSet is null && setValue.ExpectedEffective is null,
                    "A CSet value change acquired assignment-only fields.");
                RequireThrows<ArgumentException>(
                    () => ConstraintsDrcViewModel.BuildChange(EngineConstraintChangeKind.SetValue, mils, "DDR"),
                    "A set-value change accepted an extraneous destination set.");
                RequireThrows<ArgumentException>(
                    () => ConstraintsDrcViewModel.BuildChange(EngineConstraintChangeKind.ResetValue, constraintSet: "DDR"),
                    "A reset-value change accepted an extraneous destination set.");
                var inherited = new EngineConstraintFact(EngineConstraintEvidenceState.Available,
                    null, mils, "synthetic-effective", "Synthetic inherited electrical evidence.");
                EngineConstraintChange assignment = ConstraintsDrcViewModel.BuildChange(
                    EngineConstraintChangeKind.AssignElectricalSet, constraintSet: "RENAMED_ELECTRICAL",
                    expectedEffective: inherited);
                EngineConstraintChange resetAssignment = ConstraintsDrcViewModel.BuildChange(
                    EngineConstraintChangeKind.ResetElectricalAssignment, expectedEffective: inherited);
                Require(assignment.ConstraintSet == "RENAMED_ELECTRICAL" &&
                        ReferenceEquals(assignment.ExpectedEffective, inherited) &&
                        ReferenceEquals(resetAssignment.ExpectedEffective, inherited),
                    "Electrical construction lost its explicit inherited effective evidence.");
                RequireThrows<ArgumentException>(
                    () => ConstraintsDrcViewModel.BuildChange(EngineConstraintChangeKind.SetValue,
                        mils, expectedEffective: inherited),
                    "A set-value change accepted assignment-only effective evidence.");
                checks += 5;
                RequireThrows<ArgumentException>(
                    () => ConstraintsDrcViewModel.BuildChange(EngineConstraintChangeKind.SetValue),
                    "A set-value change without a scalar was accepted.");
                RequireThrows<ArgumentException>(
                    () => ConstraintsDrcViewModel.BuildChange(EngineConstraintChangeKind.AssignElectricalSet),
                    "An electrical assignment without a set was accepted.");
                RequireThrows<ArgumentNullException>(
                    () => ConstraintsDrcViewModel.DescribeScalar(null!),
                    "A null scalar produced a summary.");
                RequireThrows<ArgumentNullException>(
                    () => ConstraintsDrcViewModel.DescribeEffective(null!),
                    "A null effective read produced a summary.");
                RequireThrows<ArgumentNullException>(
                    () => ConstraintsDrcViewModel.DescribeMutation(null!),
                    "A null mutation result produced a summary.");
                checks += 7;

                await tool.PrepareEditAsync(query, setValue);
                Require(!tool.HasPreparedEdit && !tool.CanExecuteEdit &&
                        tool.StatusDetail.Contains("not connected", StringComparison.Ordinal) &&
                        tool.StatusDetail.Contains("mutation-preparation", StringComparison.Ordinal),
                    "An offline edit preparation held state or hid its Engine API.");
                await tool.ExecuteEditAsync();
                Require(!tool.HasPreparedEdit &&
                        tool.EditSummary.Contains("Prepare a typed change first", StringComparison.Ordinal),
                    "Execution without a preparation did not route back to preparation.");
                await tool.RecoverEditAsync();
                Require(!tool.CanRecoverEdit &&
                        tool.EditSummary.Contains("No recoverable mutation", StringComparison.Ordinal),
                    "Recovery without Engine recovery evidence did not refuse.");
                checks += 3;
            }
            finally
            {
                tool.Dispose();
                tool.Dispose();
            }
        }
        finally
        {
            await session.DisposeAsync();
        }

        checks += CheckDocumentSelections();
        checks += await CheckSharedMutationCallbacksAsync();
        checks += CheckReviewHelpers();
        return checks;
    }

    private static int CheckDocumentSelections()
    {
        var session = AllegroEngineSession.Create();
        EngineSessionSnapshot original = session.State;
        var stateField = typeof(AllegroEngineSession).GetField("_state",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var documentA = new WorkspaceDocumentIdentity("selection-session", 4, 11, 80, "same-name.brd", "fixture");
        var documentB = new WorkspaceDocumentIdentity("selection-session", 4, 12, 80, "same-name.brd", "fixture");
        var stateA = original with { ConnectionState = EngineConnectionState.Ready, Document = documentA };
        var stateB = stateA with { Document = documentB };
        var snapshotA = new EngineConstraintSnapshot(documentA, 8,
            new(true, true, true, true, true, []), [],
            [new(EngineConstraintDomain.Spacing, "RENAMED_SET", "ETCH/INNER", "MinLineWidth", EngineConstraintMode.On, "5")],
            [], [], [], [], [], []) { ConstraintFingerprint = "first-fingerprint" };
        using var tool = new ConstraintsDrcViewModel(session);
        try
        {
            stateField.SetValue(session, stateA);
            tool.ApplySessionState(stateA);
            tool.AcceptSnapshot(snapshotA, wasRefresh: true);
            var oldRow = tool.ValueRows[0];
            tool.SelectedValue = oldRow;
            Require(tool.SelectionIsCurrent, "A selection from the current document was not admitted.");
            long requestRevision = (long)typeof(ConstraintsDrcViewModel).GetField("_requestRevision",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tool)!;
            Require(tool.AcceptsResult(documentA, documentA, requestRevision, CancellationToken.None),
                "Same-document current result was rejected.");
            stateField.SetValue(session, stateB);
            tool.ApplySessionState(stateB);
            Require(tool.SelectedValue is null && !tool.SelectionIsCurrent && !tool.CanPrepareEdit && !tool.CanReadEffective,
                "A board switch retained an actionable old selection.");
            Require(!tool.AcceptsResult(documentA, documentA, requestRevision, CancellationToken.None),
                "A delayed A result was adopted after switching to B.");
            tool.SelectedValue = oldRow;
            Require(!tool.SelectionIsCurrent, "A stale row retargeted to an identical name on B.");
            var snapshotB = snapshotA with { Document = documentB };
            tool.AcceptSnapshot(snapshotB, wasRefresh: true);
            tool.SelectedValue = tool.ValueRows[0];
            Require(tool.SelectionIsCurrent, "A fresh B snapshot and selection were rejected.");
            var priorBRow = tool.SelectedValue;
            tool.AcceptSnapshot(snapshotB with { SnapshotRevision = 9, ConstraintFingerprint = "changed-fingerprint" }, true);
            Require(tool.SelectedValue is null, "A changed constraint fingerprint retained actionable selection.");
            tool.SelectedValue = priorBRow;
            Require(!tool.SelectionIsCurrent, "An old fingerprint row was admitted to a refreshed snapshot.");
            tool.SelectedValue = null;
            tool.SelectedValue = tool.ValueRows[0];
            Require(tool.SelectionIsCurrent, "A fresh same-document selection was rejected after refresh.");
            return 9;
        }
        finally
        {
            stateField.SetValue(session, original);
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task<int> CheckSharedMutationCallbacksAsync()
    {
        var session = AllegroEngineSession.Create();
        EngineSessionSnapshot original = session.State;
        var stateField = typeof(AllegroEngineSession).GetField("_state",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var revisionField = typeof(ConstraintsDrcViewModel).GetField("_requestRevision",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var documentA = new WorkspaceDocumentIdentity("shared-result-session", 7, 41, 123, "identical-name.brd", "fixture");
        var documentB = documentA with { BoardGeneration = 42 };
        var stateA = original with { ConnectionState = EngineConnectionState.Ready, Document = documentA };
        var stateB = stateA with { Document = documentB };
        EngineConstraintMutationResult resultA = CreateMutationResult(session, documentA, "result from A");
        EngineConstraintMutationResult resultB = CreateMutationResult(session, documentB, "result from B");
        int checks = 0;
        try
        {
            foreach (string transition in new[] { "current", "document", "cancel", "draft", "dispose" })
            {
                stateField.SetValue(session, stateA);
                using var model = new ConstraintsDrcViewModel(session);
                using var cancellation = new CancellationTokenSource();
                long revision = (long)revisionField.GetValue(model)!;
                Action<EngineConstraintMutationResult> observer =
                    model.CreateMutationResultObserver(documentA, revision, cancellation.Token);
                Action<Exception> reportFailure = model.CaptureOperationFailureReporter();
                var completion = new TaskCompletionSource<EngineConstraintMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task delivery = DeliverAsync(observer, completion.Task);
                switch (transition)
                {
                    case "document":
                        stateField.SetValue(session, stateB);
                        model.ApplySessionState(stateB);
                        break;
                    case "cancel":
                        cancellation.Cancel();
                        break;
                    case "draft":
                        model.NewValueText = "changed while awaiting native completion";
                        break;
                    case "dispose":
                        model.Dispose();
                        break;
                }
                string heldEdit = model.EditSummary;
                string heldStatus = model.StatusDetail;
                int notifications = 0;
                model.PropertyChanged += (_, _) => notifications++;
                completion.SetResult(resultA);
                await delivery;
                Require(model.MutationHistory.Count == 1 && ReferenceEquals(model.MutationHistory[0], resultA),
                    $"The {transition} callback discarded original native terminal evidence.");
                checks++;
                if (transition == "current")
                {
                    Require(model.EditSummary.Contains("result from A", StringComparison.Ordinal) &&
                        model.StatusDetail.Contains("terminal result", StringComparison.Ordinal),
                        "The current shared callback failed to publish its own document's result.");
                }
                else
                {
                    Require(model.EditSummary == heldEdit && model.StatusDetail == heldStatus,
                        $"The delayed {transition} shared callback replaced current edit presentation.");
                }
                checks++;
                if (transition != "cancel")
                {
                    string currentStatus = model.StatusDetail;
                    reportFailure(new InvalidOperationException("delayed publication failure"));
                    Require(transition == "current"
                            ? model.StatusDetail == "Operation failed: delayed publication failure"
                            : model.StatusDetail == currentStatus,
                        $"The {transition} outer exception reporter ignored its originating request.");
                    checks++;
                }
                if (transition == "dispose")
                {
                    Require(notifications == 0, "A disposed view emitted a shared-result notification.");
                    checks++;
                }
                if (transition == "document")
                {
                    long currentRevision = (long)revisionField.GetValue(model)!;
                    model.CreateMutationResultObserver(documentB, currentRevision, CancellationToken.None)(resultB);
                    Require(model.EditSummary.Contains("result from B", StringComparison.Ordinal) &&
                        model.MutationHistory.Count == 2 && ReferenceEquals(model.MutationHistory[0], resultA),
                        "Fresh B evidence could not be published while A remained historical.");
                    checks++;
                }
            }
            return checks;
        }
        finally
        {
            stateField.SetValue(session, original);
            await session.DisposeAsync();
        }

        static async Task DeliverAsync(Action<EngineConstraintMutationResult> observer,
            Task<EngineConstraintMutationResult> completion)
        {
            observer(await completion);
        }
    }

    private static EngineConstraintMutationResult CreateMutationResult(
        AllegroEngineSession session, WorkspaceDocumentIdentity document, string message)
    {
        // Engine terminal results have no public construction API. Build only this
        // inert receipt fixture through the Engine constructor's declared types;
        // production and the test project retain their Engine-only dependency seam.
        var constructor = typeof(EngineConstraintMutationResult).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        var parameters = constructor.GetParameters();
        Type bindingType = parameters[2].ParameterType;
        object binding = Activator.CreateInstance(bindingType,
            document.SessionId, document.BoardGeneration, 0L, "fixture-snapshot", document.ProtocolVersion)!;
        bindingType.GetProperty("SessionGeneration")!.SetValue(binding, document.SessionGeneration);
        bindingType.GetProperty("ProcessId")!.SetValue(binding, document.ProcessId);
        bindingType.GetProperty("Design")!.SetValue(binding, document.Design);
        Type resultType = parameters[1].ParameterType;
        var resultConstructor = resultType.GetConstructors().Single(item => item.GetParameters().Length == 3);
        Type receiptType = resultConstructor.GetParameters()[0].ParameterType;
        var receiptConstructor = receiptType.GetConstructors().Single(item => item.GetParameters().Length == 7);
        object terminalState = Enum.Parse(receiptConstructor.GetParameters()[3].ParameterType, "Failed");
        object receipt = receiptConstructor.Invoke(
            [Guid.NewGuid(), "fixture-constraint", binding, terminalState, "fixture_terminal", message, DateTimeOffset.UtcNow]);
        object nativeResult = resultConstructor.Invoke([receipt, null, null]);
        return (EngineConstraintMutationResult)constructor.Invoke([session.Workspace.Constraints, nativeResult, binding, document]);
    }

    private static int CheckReviewHelpers()
    {
        int checks = 0;
        var document = new WorkspaceDocumentIdentity(
            "checks-session", 1, 7, 100, "checks.brd", "PD_V25");
        var evidence = new EngineEvidence(
            "checks-operation", document, DateTimeOffset.UtcNow, "pd-checks", "hand-built", true);
        AllegroWorkspaceDrcRead BuildRead(
            string capture,
            params AllegroWorkspaceDrcMarkerEvidence[] markers) =>
            new(
                document,
                EngineAcquisitionState.Complete,
                EngineFreshness.Current,
                new FamilyCoverage(
                    DataFamily.Drc,
                    DataAvailability.Available,
                    DataCompleteness.CompleteForRequestedScope),
                [],
                [.. markers],
                new AllegroWorkspaceDrcEvidence(
                    capture,
                    EngineDrcEvidenceSource.ExistingMarkers,
                    1,
                    markers.Length,
                    markers.Length,
                    EngineDrcEvidenceAvailability.Available,
                    true,
                    false,
                    EngineDrcRunFreshness.Current,
                    EngineDrcEvidenceAvailability.Unavailable,
                    EngineDrcEvidenceAvailability.CountOnly),
                [],
                evidence,
                [],
                EngineRecovery.None);

        var markerA = new AllegroWorkspaceDrcMarkerEvidence(
            new SceneObjectId("marker-a"),
            "Spacing C2C",
            "NET SPACING CONSTRAINTS",
            new LayerId("ETCH/TOP"),
            new DesignPoint(10m, 20m),
            "5.0",
            "3.2",
            "constraint",
            false,
            2);
        var markerB = markerA with
        {
            Id = new SceneObjectId("marker-b"),
            Position = new DesignPoint(30m, 40m),
            Waived = true,
        };
        var markerC = new AllegroWorkspaceDrcMarkerEvidence(
            new SceneObjectId("marker-c"),
            "Line Width",
            "PHYSICAL CONSTRAINTS",
            null,
            new DesignPoint(50m, 60m),
            "8.0",
            "6.0",
            null,
            false,
            1);
        var markerADuplicate = markerA with { Id = new SceneObjectId("marker-a-copy") };

        Require(ConstraintsDrcViewModel.ReviewSubjectKey(markerA) !=
            ConstraintsDrcViewModel.ReviewSubjectKey(markerA with { Expected = "8.0" }) &&
            ConstraintsDrcViewModel.ReviewSubjectKey(markerA) !=
            ConstraintsDrcViewModel.ReviewSubjectKey(markerA with { Actual = "4.0" }),
            "Changed expected/actual evidence inherited the same review-note subject.");
        checks++;

        string keyA = ConstraintsDrcMarkerReview.StableKey(markerA);
        string unitSeparator = new((char)31, 1);
        Require(keyA.Contains(unitSeparator, StringComparison.Ordinal),
            "The PD marker stable key lost its field separator.");
        Require(ConstraintsDrcMarkerReview.StableKey(markerADuplicate) == keyA &&
                ConstraintsDrcMarkerReview.StableKey(markerA with { Waived = true }) == keyA &&
                ConstraintsDrcMarkerReview.StableKey(markerC) != keyA,
            "PD marker stable keys paired duplicates, ignored waiver flips, or collided.");
        var splitA = markerC with { Name = "AB", Type = "C" };
        var splitB = markerC with { Name = "A", Type = "BC" };
        Require(ConstraintsDrcMarkerReview.StableKey(splitA) !=
                ConstraintsDrcMarkerReview.StableKey(splitB),
            "PD marker stable keys conflated adjacent fields without a separator.");
        Require(ConstraintsDrcMarkerReview.StableKey(markerA) ==
                AllegroWorkspaceDrcReview.StableKey(markerA),
            "The PD marker stable key diverged from the Engine review key.");
        checks += 4;

        IReadOnlyList<ConstraintsDrcMarkerReview.MarkerGroup> groups =
            ConstraintsDrcMarkerReview.Group([markerC, markerB, markerA, markerADuplicate]);
        Require(groups.Count == 2 &&
                groups[0].Type == "NET SPACING CONSTRAINTS" &&
                groups[0].Layer == "ETCH/TOP" &&
                groups[0].Count == 3 &&
                groups[0].WaivedCount == 1 &&
                groups[1].Type == "PHYSICAL CONSTRAINTS" &&
                groups[1].Layer is null &&
                groups[1].Count == 1 &&
                groups[1].WaivedCount == 0,
            "PD marker grouping lost counts, waived counts, layer identity, or ordering.");
        Require(ConstraintsDrcMarkerReview.Group([]).Count == 0,
            "Grouping zero PD markers was not empty.");
        checks += 2;

        AllegroWorkspaceDrcRead before = BuildRead("before", markerA, markerB, markerC);
        var reviewSession = AllegroEngineSession.Create();
        EngineSessionSnapshot originalState = reviewSession.State;
        var sessionStateField = typeof(AllegroEngineSession).GetField("_state",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        using (var reviewTool = new ConstraintsDrcViewModel(reviewSession))
        {
            try
            {
                var firstState = originalState with { ConnectionState = EngineConnectionState.Ready, Document = document };
                sessionStateField.SetValue(reviewSession, firstState);
                reviewTool.ApplySessionState(firstState);
                reviewTool.AcceptMarkerRead(before);
                reviewTool.SelectedMarker = reviewTool.MarkerRows[0];
                reviewTool.MarkSelectedReviewed("first board only");
                Require(reviewTool.MarkerRows.Any(row => row.Reviewed), "The source document did not retain its review note.");
                var otherDocument = new WorkspaceDocumentIdentity("other-session", 3, 9, 100, "checks.brd", "PD_V25");
                var secondState = firstState with { Document = otherDocument };
                sessionStateField.SetValue(reviewSession, secondState);
                reviewTool.ApplySessionState(secondState);
                reviewTool.AcceptMarkerRead(before with { Document = otherDocument });
                Require(reviewTool.MarkerRows.All(row => !row.Reviewed), "A same-name marker on another board inherited a review note.");
                sessionStateField.SetValue(reviewSession, firstState);
                reviewTool.ApplySessionState(firstState);
                reviewTool.AcceptMarkerRead(before);
                Require(reviewTool.MarkerRows.Any(row => row.Reviewed), "Returning to the original document lost its local note.");
                checks += 3;
            }
            finally
            {
                sessionStateField.SetValue(reviewSession, originalState);
                reviewSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        AllegroWorkspaceDrcRead reordered = BuildRead(
            "after", markerB, markerC, markerA with { Waived = true });
        ConstraintsDrcCaptureComparison comparison =
            ConstraintsDrcMarkerReview.Compare(before, reordered);
        Require(comparison.BeforeToken == "before" &&
                comparison.AfterToken == "after" &&
                comparison.Added.Count == 0 &&
                comparison.Removed.Count == 0 &&
                comparison.Persistent.Count == 3,
            "Reordered PD markers with a waiver flip were not all persistent.");
        ConstraintsDrcCaptureComparison narrowed = ConstraintsDrcMarkerReview.Compare(
            before, BuildRead("narrower", markerB));
        Require(narrowed.Added.Count == 0 &&
                narrowed.Removed.Count == 2 &&
                narrowed.Persistent.Count == 1 &&
                narrowed.Persistent[0].Waived,
            "PD capture comparison lost removed markers or the persistent one.");
        ConstraintsDrcCaptureComparison duplicates = ConstraintsDrcMarkerReview.Compare(
            BuildRead("dup-before", markerA, markerADuplicate),
            BuildRead("dup-after", markerA));
        Require(duplicates.Added.Count == 0 &&
                duplicates.Removed.Count == 1 &&
                duplicates.Persistent.Count == 1,
            "Duplicate PD markers were not paired by occurrence.");
        checks += 3;

        IReadOnlyList<string> lines = ConstraintsDrcMarkerReview.ExportLines(before);
        Require(lines.Count == 5 &&
                lines[0].Contains("capture=before", StringComparison.Ordinal) &&
                lines[0].Contains("source=ExistingMarkers", StringComparison.Ordinal) &&
                lines[0].Contains("markers=3", StringComparison.Ordinal) &&
                lines[1].StartsWith("# name", StringComparison.Ordinal) &&
                lines[2].StartsWith(
                    "Spacing C2C\tNET SPACING CONSTRAINTS\tETCH/TOP\t", StringComparison.Ordinal) &&
                lines[2].EndsWith("\tfalse\t2", StringComparison.Ordinal) &&
                lines[4].StartsWith(
                    "Line Width\tPHYSICAL CONSTRAINTS\t\t", StringComparison.Ordinal),
            "The PD marker export lost identity, ordering, or violating-object counts.");
        checks++;

        string plainDisplay = new ConstraintsDrcMarkerRow(
            "key", "Spacing C2C", "NET SPACING CONSTRAINTS", "ETCH/TOP",
            "10, 20", "5.0", "3.2", "constraint", false, 2, false).Display;
        string flaggedDisplay = new ConstraintsDrcMarkerRow(
            "key", "Spacing C2C", "NET SPACING CONSTRAINTS", "ETCH/TOP",
            "10, 20", "5.0", "3.2", "constraint", true, 2, true).Display;
        Require(!plainDisplay.Contains("ViolatingObject", StringComparison.Ordinal) &&
                flaggedDisplay.Contains("waived", StringComparison.Ordinal) &&
                flaggedDisplay.Contains("reviewed", StringComparison.Ordinal) &&
                new ConstraintsDrcValueRow(
                    "Spacing", "Set", "ETCH/TOP", "Field", "On", "5.0").Display
                    .Contains("5.0", StringComparison.Ordinal) &&
                new ConstraintsDrcFieldRow("Spacing", "MinLineWidth", "native").Display
                    .Contains("MinLineWidth", StringComparison.Ordinal) &&
                new ConstraintsDrcAssignmentRow(
                    "Net DDR_DQ0", "Spacing", "DDR", "spc", "native").Display
                    .Contains("DDR_DQ0", StringComparison.Ordinal) &&
                new ConstraintsDrcNetClassRow("DDR", "members 8", "native").Display
                    .Contains("members 8", StringComparison.Ordinal) &&
                new ConstraintsDrcClassClassRow("A", "B", "SET", true, "native").Display
                    .Contains("readonly", StringComparison.Ordinal),
            "PD display rows dropped evidence facts or invented object references.");
        checks++;

        RequireThrows<ArgumentNullException>(
            () => ConstraintsDrcMarkerReview.Group(null!),
            "Grouping a null PD marker collection did not throw.");
        RequireThrows<ArgumentNullException>(
            () => ConstraintsDrcMarkerReview.Compare(null!, before),
            "Comparing a null PD before-capture did not throw.");
        RequireThrows<ArgumentNullException>(
            () => ConstraintsDrcMarkerReview.Compare(before, null!),
            "Comparing a null PD after-capture did not throw.");
        RequireThrows<ArgumentNullException>(
            () => ConstraintsDrcMarkerReview.ExportLines(null!),
            "Exporting a null PD read did not throw.");
        RequireThrows<ArgumentNullException>(
            () => ConstraintsDrcMarkerReview.StableKey(null!),
            "A null PD marker produced a stable key.");
        checks += 5;

        return checks;
    }

    private static int CheckEditInputValidation(ConstraintsDrcViewModel tool)
    {
        int checks = 0;

        Require(!ConstraintsDrcViewModel.TryBuildScalar(
                EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils, "  ",
                out EngineConstraintScalar? blank, out string blankError) &&
            blank is null && blankError.Contains("Enter a value", StringComparison.Ordinal),
            "Blank input was accepted or reported without guidance.");
        Require(!ConstraintsDrcViewModel.TryBuildScalar(
                EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils, "abc",
                out _, out string numberError) &&
            numberError.Contains("decimal", StringComparison.Ordinal),
            "Malformed numeric input was accepted or misdiagnosed.");
        Require(!ConstraintsDrcViewModel.TryBuildScalar(
                EngineConstraintScalarKind.Boolean, EngineConstraintUnit.Unitless, "yes",
                out _, out string booleanError) &&
            booleanError.Contains("true or false", StringComparison.Ordinal),
            "Malformed boolean input was accepted or misdiagnosed.");
        Require(ConstraintsDrcViewModel.TryBuildScalar(
                EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils, "5.0",
                out EngineConstraintScalar? mils, out _) &&
            mils is not null && mils.Number == 5.0m && mils.Unit == EngineConstraintUnit.Mils,
            "Valid numeric input was rejected.");
        Require(ConstraintsDrcViewModel.TryBuildScalar(
                EngineConstraintScalarKind.Number, EngineConstraintUnit.Unitless, "5.0",
                out EngineConstraintScalar? unitless, out _) &&
            unitless is not null && unitless.Unit == EngineConstraintUnit.Unitless,
            "A valid unitless scalar lost its unit.");
        Require(ConstraintsDrcViewModel.TryBuildScalar(
                EngineConstraintScalarKind.Boolean, EngineConstraintUnit.Unitless, "true",
                out EngineConstraintScalar? flag, out _) &&
            flag is not null && flag.Boolean == true,
            "Valid boolean input was rejected.");
        Require(ConstraintsDrcViewModel.TryBuildScalar(
                EngineConstraintScalarKind.Symbol, EngineConstraintUnit.Unitless, "PLATED",
                out EngineConstraintScalar? symbol, out _) &&
            symbol is not null && symbol.Text == "PLATED",
            "Valid symbol input was rejected.");
        Require(ConstraintsDrcViewModel.TryBuildScalar(
                EngineConstraintScalarKind.Text, EngineConstraintUnit.Unitless, "note",
                out EngineConstraintScalar? text, out _) &&
            text is not null && text.Text == "note",
            "Valid text input was rejected.");
        checks += 8;

        foreach (string invalid in new[] { "99", "-1", "0", "Number, Boolean", "Unknown" })
        {
            var selected = new ConstraintsDrcValueRow("Spacing", "RENAMED", "ETCH/LOWER", "MinLineWidth", "On", "5");
            foreach (var texts in new[]
            {
                (invalid, "Mils", "SetValue"),
                ("Number", invalid, "SetValue"),
                ("Number", "Mils", invalid),
            })
            {
                Require(!ConstraintsDrcViewModel.ValidateEditInput(selected, texts.Item1, texts.Item2,
                    texts.Item3, "5", out var rejected, out var diagnostic) && rejected is null && diagnostic is not null,
                    "Undefined, numeric, or combined enum input escaped total validation.");
                checks++;
            }
        }
        foreach (var invalid in new[]
        {
            (EngineConstraintScalarKind.Number, (EngineConstraintUnit)42, "1"),
            ((EngineConstraintScalarKind)(-1), EngineConstraintUnit.Unitless, "1"),
            (EngineConstraintScalarKind.Boolean, EngineConstraintUnit.Mils, "true"),
            (EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils, "999999999999999999999999999999999"),
            (EngineConstraintScalarKind.Text, EngineConstraintUnit.Unitless, "line\ncontrol"),
            (EngineConstraintScalarKind.Symbol, EngineConstraintUnit.Unitless, new string('x', 1025)),
        })
        {
            Require(!ConstraintsDrcViewModel.TryBuildScalar(invalid.Item1, invalid.Item2, invalid.Item3,
                out _, out _), "An invalid scalar or unit reached preparation input.");
            checks++;
        }

        var row = new ConstraintsDrcValueRow(
            "Spacing", "DDR", "ETCH/TOP", "MinLineWidth", "On", "5.0");
        tool.SelectedValue = row;
        tool.QueryKindText = "Number";
        tool.QueryUnitText = "Mils";
        tool.EditChangeKindText = "SetValue";

        tool.NewValueText = "abc";
        Require(tool.EditInputError.Contains("decimal", StringComparison.Ordinal),
            "The edit row did not report malformed numeric input.");
        Require(!tool.TryBuildEditInput(out _, out _),
            "Malformed numeric input reached preparation input.");
        Require(!tool.CanPrepareEdit, "Preparation was offered for malformed numeric input.");
        tool.QueryKindText = "Boolean";
        tool.QueryUnitText = "Unitless";
        tool.NewValueText = "yes";
        Require(tool.EditInputError.Contains("true or false", StringComparison.Ordinal),
            "The edit row did not report malformed boolean input.");
        Require(!tool.TryBuildEditInput(out _, out _),
            "Malformed boolean input reached preparation input.");
        tool.NewValueText = "  ";
        Require(tool.EditInputError.Contains("Enter a value", StringComparison.Ordinal),
            "The edit row did not report blank input.");
        Require(!tool.TryBuildEditInput(out _, out _),
            "Blank input reached preparation input.");
        checks += 7;

        tool.QueryKindText = "Number";
        tool.QueryUnitText = "Mils";
        tool.NewValueText = "5.0";
        Require(tool.EditInputError.Length == 0,
            "Valid numeric input was reported as an error: " + tool.EditInputError);
        Require(tool.TryBuildEditInput(
                out EngineConstraintQuery? query, out EngineConstraintChange? change) &&
            query is not null && change is not null &&
            change.Kind == EngineConstraintChangeKind.SetValue,
            "Valid input did not build preparation input.");
        Require(!tool.HasPreparedEdit,
            "Building preparation input prepared an edit as a side effect.");
        checks += 3;

        tool.SelectedValue = new("Physical", "DEFAULT", "BOTTOM", "width_min", "On", "7.0");
        tool.NewValueText = "8";
        Require(tool.TryBuildEditInput(out EngineConstraintQuery? nativeQuery, out EngineConstraintChange? nativeChange) &&
                nativeQuery is { ConstraintSet: "DEFAULT", Layer: "ETCH/BOTTOM", Field: "width_min" } &&
                nativeChange is { Kind: EngineConstraintChangeKind.SetValue, ConstraintSet: null, ExpectedEffective: null } &&
                nativeChange.Value is { Kind: EngineConstraintScalarKind.Number, Unit: EngineConstraintUnit.Mils, Number: 8m },
            "Actual CSet input mixed the query target with assignment-only change fields.");
        foreach (string unsupportedKind in new[] { "ResetValue", "AssignElectricalSet", "ResetElectricalAssignment" })
        {
            tool.EditChangeKindText = unsupportedKind;
            Require(tool.EditInputError.Length > 0 && !tool.TryBuildEditInput(out _, out _) && !tool.CanPrepareEdit,
                "A physical CSet row advertised an unsupported change: " + unsupportedKind);
            checks++;
        }
        tool.EditChangeKindText = "SetValue";
        tool.QueryKindText = "Boolean";
        tool.QueryUnitText = "Unitless";
        tool.NewValueText = "true";
        Require(tool.EditInputError.Length > 0 && !tool.TryBuildEditInput(out _, out _) && !tool.CanPrepareEdit,
            "A boolean set-value input advertised unsupported native mutation.");
        tool.SelectedValue = row;
        tool.QueryKindText = "Number";
        tool.QueryUnitText = "Mils";
        tool.NewValueText = "5.0";
        checks += 2;

        tool.QueryKindText = "Bogus";
        Require(tool.EditInputError.Contains("Scalar kind", StringComparison.Ordinal) &&
                !tool.TryBuildEditInput(out _, out _),
            "An unknown scalar kind was accepted.");
        tool.QueryKindText = "Number";
        tool.QueryUnitText = "Bogus";
        Require(tool.EditInputError.Contains("Scalar unit", StringComparison.Ordinal) &&
                !tool.TryBuildEditInput(out _, out _),
            "An unknown scalar unit was accepted.");
        tool.QueryUnitText = "Mils";
        tool.EditChangeKindText = "Bogus";
        Require(tool.EditInputError.Contains("Change kind", StringComparison.Ordinal) &&
                !tool.TryBuildEditInput(out _, out _),
            "An unknown change kind was accepted.");
        tool.EditChangeKindText = "ResetValue";
        tool.NewValueText = "  ";
        Require(tool.EditInputError.Length > 0 && !tool.TryBuildEditInput(out _, out _),
            "A non-electrical CSet row advertised an unsupported reset.");
        tool.SelectedValue = row with { Domain = "Electrical", Layer = "(all layers)" };
        Require(tool.EditInputError.Length == 0 &&
                tool.TryBuildEditInput(out _, out EngineConstraintChange? reset) &&
                reset is { Kind: EngineConstraintChangeKind.ResetValue, Value: null, ConstraintSet: null, ExpectedEffective: null },
            "A supported electrical value reset required a scalar or carried extraneous fields.");
        tool.SelectedValue = row;
        checks++;
        tool.EditChangeKindText = "SetValue";
        tool.NewValueText = "5.0";
        tool.SelectedValue = row with { Domain = "Nope" };
        Require(tool.EditInputError.Contains("unknown constraint domain", StringComparison.Ordinal) &&
                !tool.TryBuildEditInput(out _, out _),
            "An unknown snapshot domain escaped validation.");
        tool.SelectedValue = null;
        Require(!tool.CanPrepareEdit && !tool.TryBuildEditInput(out _, out _),
            "Preparation input was built with no snapshot value selected.");
        tool.SelectedValue = row;
        checks += 6;

        var raised = new List<string>();
        tool.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                raised.Add(args.PropertyName);
            }
        };
        tool.NewValueText = "abc";
        Require(raised.Contains("EditInputError", StringComparer.Ordinal) &&
                raised.Contains("CanPrepareEdit", StringComparer.Ordinal) &&
                raised.Contains("CanPrepareOrExecuteEdit", StringComparer.Ordinal) &&
                raised.Contains("EditActionReason", StringComparer.Ordinal),
            "Editing the value did not notify the validation gate.");
        checks++;

        tool.SelectedValue = null;
        tool.QueryKindText = "Number";
        tool.QueryUnitText = "Mils";
        tool.NewValueText = string.Empty;
        tool.EditChangeKindText = "SetValue";
        return checks;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
