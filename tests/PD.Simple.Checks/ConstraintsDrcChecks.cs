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
                EngineConstraintQuery allLayers = ConstraintsDrcViewModel.BuildEffectiveQuery(
                    queryRow with { Layer = "(all layers)" },
                    EngineConstraintScalarKind.Number, EngineConstraintUnit.Mils);
                Require(allLayers.Layer is null,
                    "The all-layers display marker leaked into an effective-read query.");
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
                    EngineConstraintChangeKind.SetValue, mils, "DDR");
                Require(setValue.Kind == EngineConstraintChangeKind.SetValue && setValue.Value == mils,
                    "A set-value change dropped its typed scalar.");
                Require(ConstraintsDrcViewModel.BuildChange(
                    EngineConstraintChangeKind.ResetValue, constraintSet: "DDR").Kind ==
                    EngineConstraintChangeKind.ResetValue,
                    "A reset change was not built.");
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

        checks += CheckReviewHelpers();
        return checks;
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
        Require(tool.EditInputError.Length == 0 &&
                tool.TryBuildEditInput(out _, out EngineConstraintChange? reset) &&
                reset is not null && reset.Kind == EngineConstraintChangeKind.ResetValue,
            "A value-less change wrongly required a value.");
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
