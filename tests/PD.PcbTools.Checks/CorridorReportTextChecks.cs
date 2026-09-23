using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

/// <summary>
/// A3 report-export checks. The managed .rpt text renders the complete
/// acquired finding set: the totals header and every finding line describe
/// the same findings the explorer pages, so exported totals always match
/// what search, filtering, paging, and selection can reach.
/// </summary>
internal static class CorridorReportTextChecks
{
    public static int Run()
    {
        int checks = 0;
        checks += CheckCompleteSetRendered();
        checks += CheckSmallResultControl();
        Console.WriteLine(
            $"PASS: {checks} corridor report-text checks (complete-set export, totals, small-result control).");
        return checks;
    }

    private static int CheckCompleteSetRendered()
    {
        int checks = 0;
        const int total = 250;
        const int targetIndex = 210;
        List<CorridorFinding> findings = BuildFindings(total, targetIndex);
        CorridorFinding target = findings[targetIndex];
        Require(targetIndex > 200, "The export target must sit beyond the retired 200 cap.");
        checks++;

        var scan = new CorridorScan(
            ReportScene(),
            new CorridorOptions(10, null, false),
            PairCount: 9,
            CorridorCount: 11,
            Findings: findings,
            CoverageWarnings: []);
        string text = CorridorReportText.Build(scan, "export-proof.brd");
        Require(text.Contains("findings: 250", StringComparison.Ordinal),
            "The exported totals omitted findings beyond index 200.");
        Require(text.Contains("Pairs: 9; via corridors: 11", StringComparison.Ordinal),
            "The exported header lost the pair/corridor totals.");
        Require(text.Contains(target.Id, StringComparison.Ordinal) &&
                text.Contains("TARGET_PAIR_DEEP", StringComparison.Ordinal),
            "The exported report did not reach the finding beyond index 200.");
        Require(CountLines(text, "crossing-") == total,
            "The exported report rendered a bounded prefix instead of the complete set.");
        checks += 4;
        return checks;
    }

    private static int CheckSmallResultControl()
    {
        int checks = 0;
        var scan = new CorridorScan(
            ReportScene(),
            new CorridorOptions(10, null, false),
            PairCount: 1,
            CorridorCount: 1,
            Findings: BuildFindings(6, -1),
            CoverageWarnings: []);
        string text = CorridorReportText.Build(scan, "control.brd");
        Require(text.Contains("findings: 6", StringComparison.Ordinal),
            "The small-result control changed its exported total.");
        Require(CountLines(text, "crossing-") == 6,
            "The small-result control did not render every finding.");
        checks += 2;
        return checks;
    }

    private static DesignScene ReportScene()
    {
        SceneQuery query = CorridorAnalyzer.CreateSceneQuery();
        var data = new SceneData
        {
            Nets = [],
            Layers = [],
            Modules = [],
            Copper = [],
            CopperScope = new([]),
        };
        var identity = new SceneIdentity(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new("test-engine", "1", false, "fixture"));
        var document = new DocumentContext(DocumentKind.PcbBoard, "fixture.brd", "mils", 2,
            new(new(-100, -100), new(100, 100)));
        return new DesignScene(
            identity,
            document,
            query,
            new CoverageReport(query.Families.Select(family => new FamilyCoverage(
                family, DataAvailability.Available, DataCompleteness.CompleteForRequestedScope, Reasons: []))),
            data);
    }

    private static List<CorridorFinding> BuildFindings(int count, int targetIndex)
    {
        string[] layers = ["ETCH/S01", "ETCH/S02", "ETCH/S03", "ETCH/S04"];
        var findings = new List<CorridorFinding>(count);
        for (int index = 0; index < count; index++)
        {
            if (index == targetIndex)
            {
                findings.Add(new CorridorFinding(
                    $"crossing-{index}",
                    "TARGET_PAIR_DEEP",
                    "TARGET_NET_DEEP",
                    "via",
                    "ETCH/TARGET",
                    "Target",
                    "CRITICAL",
                    new DesignPoint(100 + index, 100),
                    new DesignPoint(140 + index, 100),
                    new DesignPoint(120 + index, 100),
                    5 + index,
                    12,
                    30,
                    0,
                    1,
                    2,
                    "ETCH/TARGET"));
                continue;
            }
            findings.Add(new CorridorFinding(
                $"crossing-{index}",
                $"PAIR_{index}",
                $"NET_{index}",
                index % 2 == 0 ? "cline_segment" : "via",
                layers[index % layers.Length],
                "Signal",
                index % 5 == 0 ? "MEDIUM" : "LOW",
                new DesignPoint(100 + index, 100),
                new DesignPoint(140 + index, 100),
                new DesignPoint(120 + index, 100),
                5 + index,
                12,
                30,
                0,
                1,
                2,
                layers[index % layers.Length]));
        }
        return findings;
    }

    private static int CountLines(string text, string marker)
    {
        int count = 0;
        foreach (string line in text.Split('\n'))
        {
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Corridor report-text check failed: " + message);
        }
    }
}
