using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.ToolsAChecks;

/// <summary>
/// Native gate fixture definitions for T01/T02/T08. Each definition names
/// the disposable board, the explicit queries, the budgets, and the live
/// operations the gate needs. Without a licensed Allegro session (env
/// <c>PD_LIVE_ALLEGRO=1</c>, scheduled by the coordinator on the single
/// native slot) every gate reports NOT_EXECUTED with its cause and never
/// claims a pass. Synthetic fixtures never close these gates.
/// </summary>
internal static class NativeGateFixtures
{
    public sealed record NativeGateFixture(
        string GateId,
        string ToolId,
        string Board,
        string Description,
        SceneQuery Query,
        int MaximumFindings,
        ImmutableArray<string> LiveOperations);

    public static ImmutableArray<NativeGateFixture> All { get; } = ImmutableArray.Create(
        new NativeGateFixture(
            "T01-NATIVE-01",
            "T01",
            "lane-a-crossing.brd (disposable copy)",
            "Two crossing trace diagonals on ETCH/TOP, one parallel on ETCH/BOT, one collinear duplicate; " +
            "known complete crossing at the diagonal intersection plus a deliberate no-crossing BOT region.",
            new SceneQuery
            {
                Families = ImmutableArray.Create(DataFamily.Copper, DataFamily.Nets),
                Layers = ImmutableArray.Create(new LayerId("ETCH/TOP"), new LayerId("ETCH/BOT")),
                IncludeContours = true,
                MaximumObjects = 10_000,
            },
            200,
            ImmutableArray.Create(
                "AllegroWorkspace.ReadAsync",
                "CrossingAnalyzer.Analyze (live scene)",
                "AllegroWorkspaceDisplay.ZoomWitnessesAsync",
                "SceneArchive.SaveAsync/LoadAsync same-path close/reopen/Rerun")),
        new NativeGateFixture(
            "T02-NATIVE-01",
            "T02",
            "lane-a-geometry.brd (disposable copy)",
            "Arc/loop/region-with-hole/island copper, mirrored and rotated components, negative coordinates, " +
            "per-layer via geometry with backdrill exclusions, and one dense contour object for paging.",
            new SceneQuery
            {
                Families = ImmutableArray.Create(
                    DataFamily.Copper, DataFamily.Components, DataFamily.Pins,
                    DataFamily.Layers, DataFamily.BoardGeometry, DataFamily.Stackup),
                IncludeContours = true,
                MaximumObjects = 10_000,
            },
            200,
            ImmutableArray.Create(
                "AllegroWorkspace.ReadAsync (contours)",
                "SceneObjectBrowser.Search/Inspect",
                "AllegroWorkspaceDisplay.HighlightAsync/ZoomAsync",
                "GeometryKernel pairwise distance/intersection readback")),
        new NativeGateFixture(
            "T08-NATIVE-01",
            "T08",
            "lane-a-bulk.brd (disposable copy)",
            "Dense contours and holes for bulk budgets; save/open/close/replay/export flows; same-pathname " +
            "close/reopen with a known saved edit between runs; identical unchanged board still issues a new request.",
            new SceneQuery
            {
                Families = ImmutableArray.Create(DataFamily.Copper, DataFamily.Nets, DataFamily.Components),
                IncludeContours = true,
                MaximumObjects = 50_000,
            },
            200,
            ImmutableArray.Create(
                "AllegroWorkspace.CaptureBulkSceneAsync",
                "EngineBulkCapture.ReplayAsync/ExportAsync/OpenAsync",
                "SceneArchive.SaveAsync/LoadAsync",
                "LiveDesignScene.RefreshAsync same-path freshness",
                "Cancellation and early reader disposal")));

    /// <summary>
    /// Verifies the definitions are well-formed offline, then reports every
    /// live gate as NOT_EXECUTED unless the coordinator scheduled this run
    /// on the licensed native slot.
    /// </summary>
    public static int Run(out int notExecuted)
    {
        int checks = 0;
        notExecuted = 0;
        foreach (NativeGateFixture fixture in All)
        {
            LaneACheck.Require(!string.IsNullOrWhiteSpace(fixture.Board), "A fixture has no board.");
            LaneACheck.Require(
                fixture.Query.Families.Contains(DataFamily.Copper),
                $"Fixture {fixture.GateId} lost its Copper family.");
            LaneACheck.Require(
                fixture.LiveOperations.Length > 0,
                $"Fixture {fixture.GateId} names no live operations.");
            checks++;
        }
        bool liveScheduled = string.Equals(
            Environment.GetEnvironmentVariable("PD_LIVE_ALLEGRO"), "1",
            StringComparison.Ordinal);
        foreach (NativeGateFixture fixture in All)
        {
            if (!liveScheduled)
            {
                Console.WriteLine(
                    $"NOT_EXECUTED {fixture.GateId} ({fixture.ToolId} on {fixture.Board}): " +
                    "no licensed Allegro session in this run; single-license slot is scheduled by the coordinator.");
                notExecuted++;
            }
        }
        if (liveScheduled)
        {
            Console.WriteLine("LIVE: native slot claimed; live gates are implemented by the scheduled native run.");
        }
        return checks;
    }
}
