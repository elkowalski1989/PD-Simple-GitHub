using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.Simple.Corridor;

/// <summary>
/// A3 VM-level corridor findings checks. Same reachability contract as the
/// Linux explorer checks, exercised through the workspace view model that
/// the crossings panel binds to: paging, search, risk/layer filtering,
/// selection identity, and totals. Selections must perform zero native
/// work: the injected analysis carries no live scene, and the navigation
/// and capture overrides throw if anything dispatches.
/// </summary>
internal static class CorridorFindingsVmChecks
{
    internal static int Run()
    {
        int checks = 0;
        AllegroEngineSession session = AllegroEngineSession.Create();
        try
        {
            EngineWpfPresentation presentation = EngineWpfPresentation.Attach(
                session,
                Dispatcher.CurrentDispatcher);
            try
            {
                checks += CheckCompleteSetReachable(session, presentation);
                checks += CheckSmallResultControl(session, presentation);
            }
            finally
            {
                presentation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        Console.WriteLine(
            $"PASS: {checks} VM-level corridor findings checks (paging, " +
            "search, filtering, selection identity, totals, small-result control).");
        return checks;
    }

    private static int CheckCompleteSetReachable(
        AllegroEngineSession session,
        EngineWpfPresentation presentation)
    {
        int checks = 0;
        const int total = 250;
        const int targetIndex = 210;
        List<DpViaCorridorFinding> findings = BuildFindings(total, targetIndex);
        DpViaCorridorFinding target = findings[targetIndex];
        var workspace = new DpViaCorridorWorkspaceViewModel(session, presentation);
        try
        {
            int navigateCalls = 0;
            int captureCalls = 0;
            workspace.NavigateOverride = (analysis, finding, request, token) =>
            {
                navigateCalls++;
                throw new InvalidOperationException(
                    "A VM findings selection dispatched native navigation.");
            };
            workspace.CaptureReviewOverride = (source, drawings, token) =>
            {
                captureCalls++;
                throw new InvalidOperationException(
                    "A VM findings selection dispatched a review capture.");
            };
            workspace.FollowSelection = false;
            workspace.AdoptResultForTest(CreateAnalysis(findings));

            Require(workspace.PageCount == 36,
                $"The workspace paged 250 findings into {workspace.PageCount} pages instead of 36.");
            Require(workspace.CurrentPage == 1, "The workspace did not start on page 1.");
            Require(workspace.VisibleFindings.Count == 7,
                "The first workspace page did not show exactly 7 findings.");
            checks += 3;

            IReadOnlyList<DpViaCorridorWorkspaceViewModel.CorridorPageEntry> entries =
                workspace.PageEntries;
            Require(entries.Count == 36, "The workspace page entries dropped pages.");
            Require(entries[30].Number == 31, "Page entry 31 is misnumbered.");
            entries[30].GoTo.Execute(null);
            Require(workspace.CurrentPage == 31, "The page-31 entry did not navigate to page 31.");
            Require(workspace.VisibleFindings.Any(item => ReferenceEquals(item, target)),
                "Workspace paging did not reach the finding beyond index 200.");
            checks += 4;

            workspace.SelectedFinding = target;
            Require(ReferenceEquals(workspace.SelectedFinding, target),
                "The workspace did not keep the paged target selected by identity.");
            Require(workspace.FindingCountDisplay == "250",
                $"The workspace total shows '{workspace.FindingCountDisplay}' instead of '250'.");
            Require(workspace.CriticalCount == 1,
                "The workspace critical total missed the single critical finding.");
            Require(workspace.MediumCount == 49 && workspace.LowCount == 200,
                "The workspace severity totals disagree with the finding set.");
            checks += 4;

            workspace.SearchText = "target_pair_deep";
            Require(workspace.VisibleFindings.Count == 1 &&
                ReferenceEquals(workspace.VisibleFindings[0], target),
                "Workspace search did not reach the finding beyond index 200.");
            Require(workspace.PageCount == 1,
                "A single-hit workspace search did not collapse to one page.");
            Require(ReferenceEquals(workspace.SelectedFinding, target),
                "A narrowing workspace search did not keep the matching selection.");
            checks += 3;

            workspace.SearchText = string.Empty;
            workspace.RiskFilter = "CRITICAL";
            Require(workspace.VisibleFindings.Count == 1 &&
                ReferenceEquals(workspace.VisibleFindings[0], target),
                "Workspace risk filtering did not reach the finding beyond index 200.");
            workspace.RiskFilter = DpViaCorridorFindingExplorer.AllRisks;
            checks += 1;

            Require(workspace.LayerOptions.Contains("ETCH/TARGET", StringComparer.Ordinal),
                "Workspace layer options missed the layer that only appears beyond index 200.");
            workspace.LayerFilter = "ETCH/TARGET";
            Require(workspace.VisibleFindings.Count == 1 &&
                ReferenceEquals(workspace.VisibleFindings[0], target),
                "Workspace layer filtering did not reach the finding beyond index 200.");
            workspace.LayerFilter = DpViaCorridorFindingExplorer.AllLayers;
            DpViaCorridorWorkspaceViewModel.CorridorLayerTile? tile =
                workspace.LayerTiles.FirstOrDefault(item => item.Layer == "ETCH/TARGET");
            Require(tile is { Crossings: 1 },
                "The workspace layer tile missed the crossing beyond index 200.");
            checks += 3;

            workspace.SearchText = "no-such-pair";
            Require(workspace.VisibleFindings.Count == 0,
                "A non-matching workspace search returned findings.");
            Require(workspace.SelectedFinding is null,
                "An empty workspace result kept a stale selection.");
            Require(workspace.PageDisplay == "0 of 0",
                $"An empty workspace result shows '{workspace.PageDisplay}' instead of '0 of 0'.");
            workspace.SearchText = string.Empty;
            Require(workspace.VisibleFindings.Count == 7,
                "Clearing the workspace search did not restore the first page.");
            Require(ReferenceEquals(workspace.SelectedFinding, workspace.VisibleFindings[0]),
                "A cleared workspace search did not select the page head.");
            checks += 5;

            if (navigateCalls != 0 || captureCalls != 0 ||
                workspace.CapturedReview is not null ||
                workspace.VerifiedZoom is not null ||
                workspace.NavigationError.Length != 0)
            {
                throw new InvalidOperationException(
                    "VM findings checks failed: offline exploration performed native work or published.");
            }
            checks++;
        }
        finally
        {
            workspace.Dispose();
        }
        return checks;
    }

    private static int CheckSmallResultControl(
        AllegroEngineSession session,
        EngineWpfPresentation presentation)
    {
        int checks = 0;
        List<DpViaCorridorFinding> findings = BuildFindings(6, -1);
        var workspace = new DpViaCorridorWorkspaceViewModel(session, presentation);
        try
        {
            workspace.FollowSelection = false;
            workspace.AdoptResultForTest(CreateAnalysis(findings));
            Require(workspace.PageCount == 1,
                "The small-result control paged a six-finding set.");
            Require(workspace.VisibleFindings.Count == 6,
                "The small-result control did not show every finding.");
            Require(workspace.PageDisplay == "1\u20136 of 6",
                $"The small-result control shows '{workspace.PageDisplay}' instead of '1\u20136 of 6'.");
            Require(ReferenceEquals(workspace.SelectedFinding, findings[0]),
                "The small-result control selected the wrong initial finding.");
            workspace.PreviousPageCommand.Execute(null);
            Require(workspace.CurrentPage == 1,
                "The small-result control left its single page.");
            workspace.SearchText = "no-such-pair";
            Require(workspace.VisibleFindings.Count == 0 && workspace.SelectedFinding is null,
                "A non-matching small-result search kept rows or a selection.");
            checks += 6;
        }
        finally
        {
            workspace.Dispose();
        }
        return checks;
    }

    private static DpViaCorridorAnalysis CreateAnalysis(List<DpViaCorridorFinding> findings)
    {
        var document = new WorkspaceDocumentIdentity(
            "reachability-session",
            SessionGeneration: 1,
            BoardGeneration: 7,
            ProcessId: null,
            Design: @"C:\disposable\reachability.brd",
            ProtocolVersion: "25");
        var result = new DpViaCorridorResult(
            DpViaCorridorResult.ManagedSchema,
            "complete",
            7,
            "reachability",
            "mils",
            "mils",
            "unused.rpt",
            null,
            true,
            PairCount: 9,
            CorridorCount: 11,
            AggressorCount: 13,
            FindingCount: findings.Count,
            CriticalCount: findings.Count(item => item.Risk == "CRITICAL"),
            MediumCount: findings.Count(item => item.Risk == "MEDIUM"),
            LowCount: findings.Count(item => item.Risk == "LOW"),
            Findings: findings);
        return new(document, result);
    }

    private static List<DpViaCorridorFinding> BuildFindings(int count, int targetIndex)
    {
        string[] layers = ["ETCH/S01", "ETCH/S02", "ETCH/S03", "ETCH/S04"];
        var findings = new List<DpViaCorridorFinding>(count);
        for (int index = 0; index < count; index++)
        {
            if (index == targetIndex)
            {
                findings.Add(new DpViaCorridorFinding(
                    $"crossing-{index}",
                    "TARGET_PAIR_DEEP",
                    "TARGET_NET_DEEP",
                    "via",
                    "ETCH/TARGET",
                    "Target",
                    "CRITICAL",
                    new(100 + index, 100),
                    new(140 + index, 100),
                    null,
                    5 + index,
                    12,
                    30));
                continue;
            }
            findings.Add(new DpViaCorridorFinding(
                $"crossing-{index}",
                $"PAIR_{index}",
                $"NET_{index}",
                index % 2 == 0 ? "cline_segment" : "via",
                layers[index % layers.Length],
                "Signal",
                index % 5 == 0 ? "MEDIUM" : "LOW",
                new(100 + index, 100),
                new(140 + index, 100),
                null,
                5 + index,
                12,
                30));
        }
        return findings;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("VM findings check failed: " + message);
        }
    }
}
