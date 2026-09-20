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
        checks += 3;

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
