using PD.Simple.Corridor;

namespace PD.Simple;

/// <summary>
/// A3 corridor findings checks. The complete typed in-memory finding set
/// stays reachable: search, risk/layer filtering, paging, selection, and
/// totals all operate on every acquired finding, not a bounded prefix.
/// Runs on plain .NET through the same explorer the workspace view model
/// delegates to. Live WPF rendering still needs GUI acceptance.
/// </summary>
internal static class CorridorFindingsChecks
{
    private const int PageSize = 7;

    public static int Run()
    {
        int checks = 0;
        checks += CheckCompleteSetReachable();
        checks += CheckSmallResultControl();
        Console.WriteLine(
            $"PASS: {checks} corridor findings checks (complete-set search, " +
            "risk/layer filtering, paging, selection, totals, small-result control).");
        return checks;
    }

    private static int CheckCompleteSetReachable()
    {
        int checks = 0;
        const int total = 250;
        const int targetIndex = 210;
        List<DpViaCorridorFinding> findings = BuildFindings(total, targetIndex);
        DpViaCorridorFinding target = findings[targetIndex];
        Require(targetIndex > 200, "The reachability target must sit beyond the retired 200 cap.");
        checks++;

        List<DpViaCorridorFinding> unfiltered = DpViaCorridorFindingExplorer.ApplyFilters(
            findings,
            DpViaCorridorFindingExplorer.AllRisks,
            DpViaCorridorFindingExplorer.AllLayers,
            string.Empty);
        Require(unfiltered.Count == total, "Unfiltered exploration dropped findings.");
        Require(ReferenceEquals(unfiltered[targetIndex], target),
            "The complete set did not preserve finding identity.");
        checks += 2;

        List<DpViaCorridorFinding> searched = DpViaCorridorFindingExplorer.ApplyFilters(
            findings,
            DpViaCorridorFindingExplorer.AllRisks,
            DpViaCorridorFindingExplorer.AllLayers,
            "target_pair_deep");
        Require(searched.Count == 1 && ReferenceEquals(searched[0], target),
            "Search did not reach the finding beyond index 200.");
        checks++;

        List<DpViaCorridorFinding> critical = DpViaCorridorFindingExplorer.ApplyFilters(
            findings,
            "CRITICAL",
            DpViaCorridorFindingExplorer.AllLayers,
            string.Empty);
        Require(critical.Count == 1 && ReferenceEquals(critical[0], target),
            "Risk filtering did not reach the finding beyond index 200.");
        checks++;

        List<DpViaCorridorFinding> layered = DpViaCorridorFindingExplorer.ApplyFilters(
            findings,
            DpViaCorridorFindingExplorer.AllRisks,
            "ETCH/TARGET",
            string.Empty);
        Require(layered.Count == 1 && ReferenceEquals(layered[0], target),
            "Layer filtering did not reach the finding beyond index 200.");
        List<string> layers = DpViaCorridorFindingExplorer.DeriveLayers(findings);
        Require(layers.Contains("ETCH/TARGET", StringComparer.Ordinal),
            "Layer derivation missed the layer that only appears beyond index 200.");
        checks += 2;

        int pages = DpViaCorridorFindingExplorer.PageCount(unfiltered.Count, PageSize);
        Require(pages == 36, $"Paging computed {pages} pages for 250 findings at page size 7.");
        List<DpViaCorridorFinding> targetPage =
            DpViaCorridorFindingExplorer.GetPage(unfiltered, 31, PageSize);
        Require(targetPage.Count == PageSize && targetPage.Contains(target),
            "Paging did not reach the page holding the finding beyond index 200.");
        checks += 2;

        string? kept = DpViaCorridorFindingExplorer.ResolveSelectedId(
            unfiltered, targetPage, target.Id);
        Require(kept == target.Id, "Selection did not resolve the paged target by identity.");
        string? first = DpViaCorridorFindingExplorer.ResolveSelectedId(
            unfiltered, targetPage, "crossing-unknown");
        Require(first == targetPage[0].Id,
            "A missing selection identity did not fall back to the page head.");
        List<DpViaCorridorFinding> firstPage =
            DpViaCorridorFindingExplorer.GetPage(unfiltered, 1, PageSize);
        Require(DpViaCorridorFindingExplorer.ResolveSelectedId(unfiltered, firstPage, target.Id) == target.Id,
            "A selection on another page was not preserved by identity.");
        checks += 3;

        var result = new DpViaCorridorResult(
            DpViaCorridorResult.ManagedSchema,
            "complete",
            7,
            "reachability",
            "mils",
            "mils",
            "unused.rpt",
            null,
            false,
            PairCount: 9,
            CorridorCount: 11,
            AggressorCount: 13,
            FindingCount: findings.Count,
            CriticalCount: findings.Count(item => item.Risk == "CRITICAL"),
            MediumCount: findings.Count(item => item.Risk == "MEDIUM"),
            LowCount: findings.Count(item => item.Risk == "LOW"),
            Findings: findings);
        Require(result.FindingCount == result.Findings.Count,
            "Result totals disagree with the inspectable finding set.");
        Require(result.CriticalCount + result.MediumCount + result.LowCount == result.FindingCount,
            "Severity totals disagree with the finding total.");
        Require(result.CriticalCount == critical.Count &&
                DpViaCorridorFindingExplorer.ApplyFilters(
                    result.Findings, "MEDIUM",
                    DpViaCorridorFindingExplorer.AllLayers, string.Empty).Count == result.MediumCount,
            "Filtered counts disagree with the exported totals.");
        checks += 3;
        return checks;
    }

    private static int CheckSmallResultControl()
    {
        int checks = 0;
        List<DpViaCorridorFinding> findings = BuildFindings(6, -1);
        List<DpViaCorridorFinding> unfiltered = DpViaCorridorFindingExplorer.ApplyFilters(
            findings,
            DpViaCorridorFindingExplorer.AllRisks,
            DpViaCorridorFindingExplorer.AllLayers,
            string.Empty);
        Require(unfiltered.Count == 6, "The small-result control dropped findings.");
        Require(DpViaCorridorFindingExplorer.PageCount(unfiltered.Count, PageSize) == 1,
            "The small-result control paged a six-finding set.");
        List<DpViaCorridorFinding> page =
            DpViaCorridorFindingExplorer.GetPage(unfiltered, 1, PageSize);
        Require(page.Count == 6, "The small-result control did not show every finding.");
        Require(DpViaCorridorFindingExplorer.ResolveSelectedId(unfiltered, page, null) == page[0].Id,
            "The small-result control selected the wrong initial finding.");
        Require(DpViaCorridorFindingExplorer.GetPage(unfiltered, 99, PageSize).Count == 6,
            "Page clamping did not hold a small result on its single page.");
        List<DpViaCorridorFinding> none = DpViaCorridorFindingExplorer.ApplyFilters(
            findings,
            DpViaCorridorFindingExplorer.AllRisks,
            DpViaCorridorFindingExplorer.AllLayers,
            "no-such-pair");
        Require(none.Count == 0, "A non-matching search returned findings.");
        checks += 6;
        return checks;
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
            throw new InvalidOperationException("Corridor findings check failed: " + message);
        }
    }
}
