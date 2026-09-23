using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

/// <summary>
/// C2 pair-policy checks. SuffixCompat retains the historical _P/_N
/// discovery byte-for-byte; DeclaredPairs discovers Engine-declared pairs
/// through Xnet membership, analyzes only pairs with unambiguous polarity
/// evidence, and reports incomplete or ambiguous declarations as
/// review-required instead of skipping them silently or inventing P/N.
/// </summary>
internal static class CorridorPairPolicyChecks
{
    internal static int Run()
    {
        int checks = 0;
        checks += CheckSuffixCompatPreserved();
        checks += CheckDeclaredReady();
        checks += CheckDeclaredAmbiguous();
        checks += CheckDeclaredIncomplete();
        checks += CheckAbsentDeclarations();
        checks += CheckQueries();
        checks += CheckCandidateDiscovery();
        Console.WriteLine(
            $"PASS: {checks} corridor pair policy checks (suffix compat, declared discovery, review-required).");
        return checks;
    }

    private static int CheckSuffixCompatPreserved()
    {
        int checks = 0;
        DesignScene scene = PairScene(
            "DP_SUFFIX_C2_P", "DP_SUFFIX_C2_N",
            CorridorPairPolicy.SuffixCompat, [], []);
        CorridorScan selected = CorridorAnalyzer.Analyze(scene, new(0, null, false));
        CorridorScan explicitPolicy = CorridorAnalyzer.Analyze(
            scene, new(0, null, false, CorridorPairPolicy.SuffixCompat));
        Require(selected.PairCount == 1 && selected.Findings.Count == 1,
            "The suffix-only board lost its pair or finding.");
        Require(selected.Findings[0] is { PairName: "DP_SUFFIX_C2", AggressorNet: "CLOCK_TEST" },
            "Suffix discovery changed pair naming or aggressor attribution.");
        Require(explicitPolicy.Findings.SequenceEqual(selected.Findings) &&
            explicitPolicy.PairCount == selected.PairCount,
            "Explicit SuffixCompat diverges from the default policy.");
        Require(CorridorAnalyzer.Limitations.Contains("SuffixCompat", StringComparison.Ordinal) &&
            CorridorAnalyzer.Limitations.Contains("DeclaredPairs", StringComparison.Ordinal),
            "The limitations statement does not name both pair policies.");
        checks += 4;
        return checks;
    }

    private static int CheckDeclaredReady()
    {
        int checks = 0;
        DesignScene scene = PairScene(
            "USB_C2_P", "USB_C2_N",
            CorridorPairPolicy.DeclaredPairs,
            [new(new("dp:usb"), "DP_DECLARED_C2", "XNET_C2_A", "XNET_C2_B")],
            [
                new(new("xnet:a"), "XNET_C2_A", ["USB_C2_P"]),
                new(new("xnet:b"), "XNET_C2_B", ["USB_C2_N"]),
            ]);
        ImmutableArray<DeclaredCorridorPair> discovered = CorridorAnalyzer.DiscoverDeclaredPairs(scene);
        Require(discovered.Length == 1 && discovered[0] is
            {
                PairName: "DP_DECLARED_C2",
                Status: DeclaredPairStatus.Ready,
                PositiveNet: "USB_C2_P",
                NegativeNet: "USB_C2_N",
            },
            "A declared pair with suffixed members was not discovered ready.");
        CorridorScan scan = CorridorAnalyzer.Analyze(scene, new(0, null, false, CorridorPairPolicy.DeclaredPairs));
        Require(scan.PairCount == 1 && scan.Findings.Count == 1 &&
            scan.Findings[0].PairName == "DP_DECLARED_C2",
            "A ready declared pair was not analyzed under its declared name.");
        checks += 2;

        DesignScene renamed = PairScene(
            "SIGA_X9_P", "SIGA_X9_N",
            CorridorPairPolicy.DeclaredPairs,
            [new(new("dp:renamed"), "DP_RENAMED_X9", "XA_X9", "XB_X9")],
            [
                new(new("xnet:xa"), "XA_X9", ["SIGA_X9_P"]),
                new(new("xnet:xb"), "XB_X9", ["SIGA_X9_N"]),
            ]);
        ImmutableArray<DeclaredCorridorPair> renamedFound = CorridorAnalyzer.DiscoverDeclaredPairs(renamed);
        Require(renamedFound.Length == 1 && renamedFound[0].Status == DeclaredPairStatus.Ready &&
            renamedFound[0].PairName == "DP_RENAMED_X9",
            "Declared discovery depends on specific pair identifiers.");
        CorridorScan renamedScan = CorridorAnalyzer.Analyze(
            renamed, new(0, null, false, CorridorPairPolicy.DeclaredPairs));
        Require(renamedScan.PairCount == 1 && renamedScan.Findings.Count == 1,
            "A renamed declared pair was not analyzed.");
        checks += 2;

        DesignScene lone = PairScene(
            "LONE_C2_A", "LONE_C2_B",
            CorridorPairPolicy.DeclaredPairs, [], []);
        Require(CorridorAnalyzer.DiscoverDeclaredPairs(lone).IsEmpty,
            "Lone nets without declarations were discovered as pairs.");
        checks++;
        return checks;
    }

    private static int CheckDeclaredAmbiguous()
    {
        int checks = 0;
        DesignScene unsuffixed = PairScene(
            "TXA_C2", "TXB_C2",
            CorridorPairPolicy.DeclaredPairs,
            [new(new("dp:nosuffix"), "DP_NOSUFFIX_C2", "XN_C2_A", "XN_C2_B")],
            [
                new(new("xnet:na"), "XN_C2_A", ["TXA_C2"]),
                new(new("xnet:nb"), "XN_C2_B", ["TXB_C2"]),
            ]);
        ImmutableArray<DeclaredCorridorPair> found = CorridorAnalyzer.DiscoverDeclaredPairs(unsuffixed);
        Require(found.Length == 1 && found[0].Status == DeclaredPairStatus.AmbiguousPolarity &&
            found[0].PositiveNet is null && found[0].NegativeNet is null,
            "Side A/B members without polarity evidence were labeled positive/negative.");
        CorridorScan scan = CorridorAnalyzer.Analyze(
            unsuffixed, new(0, null, false, CorridorPairPolicy.DeclaredPairs));
        Require(scan.PairCount == 0 && scan.Findings.Count == 0 &&
            scan.CoverageWarnings.Any(warning => warning.Contains("DP_NOSUFFIX_C2", StringComparison.Ordinal)) &&
            !scan.HasCompleteInputs,
            "A declared pair without _P/_N names was not reported review-required.");
        checks += 2;

        DesignScene bothPositive = PairScene(
            "DUP_C2_P", "DUP2_C2_P",
            CorridorPairPolicy.DeclaredPairs,
            [new(new("dp:dup"), "DP_DUPPOS_C2", "XD_C2_A", "XD_C2_B")],
            [
                new(new("xnet:da"), "XD_C2_A", ["DUP_C2_P"]),
                new(new("xnet:db"), "XD_C2_B", ["DUP2_C2_P"]),
            ]);
        CorridorScan dupScan = CorridorAnalyzer.Analyze(
            bothPositive, new(0, null, false, CorridorPairPolicy.DeclaredPairs));
        Require(dupScan.PairCount == 0 &&
            dupScan.CoverageWarnings.Any(warning => warning.Contains("DP_DUPPOS_C2", StringComparison.Ordinal)),
            "Two positive members were not reported ambiguous.");
        checks++;

        DesignScene threeNets = PairScene(
            "TRI_C2_P", "TRI_C2_N",
            CorridorPairPolicy.DeclaredPairs,
            [new(new("dp:tri"), "DP_TRI_C2", "XT_C2_A", "XT_C2_B")],
            [
                new(new("xnet:ta"), "XT_C2_A", ["TRI_C2_P", "TRI_C2_EXTRA"]),
                new(new("xnet:tb"), "XT_C2_B", ["TRI_C2_N"]),
            ],
            extraNets: ["TRI_C2_EXTRA"]);
        CorridorScan triScan = CorridorAnalyzer.Analyze(
            threeNets, new(0, null, false, CorridorPairPolicy.DeclaredPairs));
        Require(triScan.PairCount == 0 &&
            triScan.CoverageWarnings.Any(warning => warning.Contains("DP_TRI_C2", StringComparison.Ordinal)),
            "A three-net declared pair was not reported ambiguous.");
        checks++;
        return checks;
    }

    private static int CheckDeclaredIncomplete()
    {
        int checks = 0;
        try
        {
            PairScene(
                "USB_C2_P", "USB_C2_N",
                CorridorPairPolicy.DeclaredPairs,
                [new(new("dp:partial"), "DP_PARTIAL_C2", "XNET_C2_A", "XNET_MISSING_C2")],
                [new(new("xnet:a"), "XNET_C2_A", ["USB_C2_P"])]);
            throw new InvalidOperationException(
                "Corridor pair policy check failed: Engine admitted a declared pair whose side Xnet was absent.");
        }
        catch (ArgumentException error)
        {
            Require(error.Message.Contains("distinct Xnets", StringComparison.Ordinal),
                $"Engine rejected an absent declared-pair side for an unexpected reason: '{error.Message}'.");
            checks++;
        }

        DesignScene orphan = PairScene(
            "USB_C2_P", "USB_C2_N",
            CorridorPairPolicy.DeclaredPairs,
            [],
            [
                new(new("xnet:a"), "XNET_C2_A", ["USB_C2_P"], "DP_ORPHAN_C2"),
                new(new("xnet:b"), "XNET_C2_B", ["USB_C2_N"]),
            ]);
        ImmutableArray<DeclaredCorridorPair> orphans = CorridorAnalyzer.DiscoverDeclaredPairs(orphan);
        Require(orphans.Length == 1 && orphans[0].Status == DeclaredPairStatus.IncompleteDeclaration &&
            orphans[0].Detail.Contains("DP_ORPHAN_C2", StringComparison.Ordinal),
            "An orphan Xnet pair claim was skipped silently.");
        checks++;

        DesignScene noConnectivity = PairScene(
            "USB_C2_P", "USB_C2_N",
            CorridorPairPolicy.SuffixCompat,
            [],
            []);
        try
        {
            CorridorAnalyzer.Analyze(noConnectivity, new(0, null, false, CorridorPairPolicy.DeclaredPairs));
            throw new InvalidOperationException(
                "Corridor pair policy check failed: declared analysis ran without Connectivity.");
        }
        catch (InvalidOperationException error)
        {
            Require(error.Message.Contains("Connectivity", StringComparison.Ordinal),
                $"Declared analysis without Connectivity reported '{error.Message}'.");
            checks++;
        }
        return checks;
    }

    private static int CheckAbsentDeclarations()
    {
        int checks = 0;
        DesignScene scene = PairScene(
            "LONE_C2_A", "LONE_C2_B",
            CorridorPairPolicy.DeclaredPairs,
            [],
            [
                new(new("xnet:a"), "XNET_C2_A", ["LONE_C2_A"]),
                new(new("xnet:b"), "XNET_C2_B", ["LONE_C2_B"]),
            ]);
        Require(CorridorAnalyzer.DiscoverDeclaredPairs(scene).IsEmpty,
            "Undeclared Xnets produced pairs.");
        CorridorScan scan = CorridorAnalyzer.Analyze(
            scene, new(0, null, false, CorridorPairPolicy.DeclaredPairs));
        Require(scan.PairCount == 0 && scan.Findings.Count == 0 &&
            scan.CoverageWarnings.All(warning => !warning.Contains("review is required", StringComparison.Ordinal)),
            "Absent declarations produced pair warnings or findings.");
        checks += 2;
        return checks;
    }

    private static int CheckQueries()
    {
        int checks = 0;
        SceneQuery compat = CorridorAnalyzer.CreateSceneQuery();
        SceneQuery declared = CorridorAnalyzer.CreateSceneQuery(pairPolicy: CorridorPairPolicy.DeclaredPairs);
        Require(compat.Kind == SceneReadKind.CompleteBoard && !compat.IncludeContours &&
            !compat.Families.Contains(DataFamily.Connectivity),
            "The suffix query changed shape.");
        Require(declared.Kind == SceneReadKind.CompleteBoard && !declared.IncludeContours &&
            declared.Families.Contains(DataFamily.Connectivity) &&
            declared.Families.Length == compat.Families.Length + 1,
            "The declared query does not add exactly the Connectivity family.");
        Require(CorridorAnalyzer.CreateSceneQuery("module-C2", CorridorPairPolicy.DeclaredPairs).Module == "module-C2",
            "The module filter was lost on the declared query.");
        checks += 3;
        return checks;
    }

    private static int CheckCandidateDiscovery()
    {
        SceneQuery suffixQuery = CorridorAnalyzer.CreateCandidateQuery();
        SceneQuery declaredQuery = CorridorAnalyzer.CreateCandidateQuery(
            CorridorPairPolicy.DeclaredPairs);
        Require(suffixQuery.Kind == SceneReadKind.Metadata &&
                suffixQuery.Families.SequenceEqual([DataFamily.Nets]) &&
                suffixQuery.CopperKinds.IsEmpty &&
                suffixQuery.ViaPadMeasurements.Mode == ViaPadMeasurementSelectionMode.None &&
                declaredQuery.Families.SequenceEqual(
                    [DataFamily.Nets, DataFamily.Connectivity]),
            "Candidate discovery requested copper or omitted declared connectivity.");

        DesignScene source = PairScene(
            "RENAMED_LANE_Q7_P", "RENAMED_LANE_Q7_N",
            CorridorPairPolicy.SuffixCompat, [], []);
        DesignScene metadata = new(
            source.Identity,
            source.Document,
            suffixQuery,
            FullCoverage(suffixQuery),
            source.Data with { Layers = [], Copper = [], CopperScope = new([]) });
        CorridorCandidateDigest suffix = CorridorAnalyzer.DiscoverCandidates(
            metadata, CorridorPairPolicy.SuffixCompat, includeUnused: false);
        Require(suffix.Pairs.Count == 1 && suffix.Pairs[0] is
            {
                PairName: "RENAMED_LANE_Q7",
                PositiveNet: "RENAMED_LANE_Q7_P",
                NegativeNet: "RENAMED_LANE_Q7_N",
            } && suffix.CoverageWarnings.Count == 0,
            "Metadata-only candidate discovery depended on a board fingerprint or copper.");

        try
        {
            CorridorAnalyzer.DiscoverCandidates(
                metadata, CorridorPairPolicy.DeclaredPairs, includeUnused: false);
            throw new InvalidOperationException(
                "Declared candidates were admitted without Connectivity coverage.");
        }
        catch (InvalidOperationException error)
        {
            Require(error.Message.Contains("Connectivity", StringComparison.Ordinal),
                "Missing Connectivity produced an unrelated candidate failure.");
        }
        return 3;
    }

    private static DesignScene PairScene(
        string positiveNet,
        string negativeNet,
        CorridorPairPolicy policy,
        DifferentialPairObject[] declared,
        XnetObject[] xnets,
        string[]? extraNets = null)
    {
        SceneQuery query = CorridorAnalyzer.CreateSceneQuery(pairPolicy: policy);
        var layers = ImmutableArray.Create(
            new LayerObject(new("ETCH/TOP"), 0, false),
            new LayerObject(new("ETCH/S03"), 1, false),
            new LayerObject(new("ETCH/BOTTOM"), 2, false));
        var nets = new List<NetObject>
        {
            new(new("net:p"), positiveNet, 1),
            new(new("net:n"), negativeNet, 1),
            new(new("net:a"), "CLOCK_TEST", 1),
        };
        int extra = 0;
        foreach (string name in extraNets ?? [])
        {
            nets.Add(new(new($"net:extra:{extra++}"), name, 0));
        }
        var data = new SceneData
        {
            Nets = nets.ToImmutableArray(),
            Layers = layers,
            Modules = [],
            Xnets = xnets.ToImmutableArray(),
            DifferentialPairs = declared.ToImmutableArray(),
            Copper =
            [
                Via(positiveNet, -50, 0),
                Via(negativeNet, 50, 1),
                Segment("CLOCK_TEST", 0, -20, 20, 2),
            ],
            CopperScope = new([CopperKind.Trace, CopperKind.Via, CopperKind.Shape]),
        };
        var identity = new SceneIdentity(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new("test-engine", "1", false, "fixture"));
        var document = new DocumentContext(DocumentKind.PcbBoard, "fixture.brd", "mils", 2,
            new(new(-100, -100), new(100, 100)));
        return new DesignScene(identity, document, query, FullCoverage(query), data);
    }

    private static CopperObject Via(string net, decimal x, int id)
    {
        DesignPoint position = new(x, 0);
        ImmutableArray<LayerId> layers = [new("ETCH/TOP"), new("ETCH/S03"), new("ETCH/BOTTOM")];
        var pads = ImmutableArray.Create(
            new ViaPadMeasurement(new("ETCH/S03"), new("ETCH/S03"), "regular", "CIRCLE", new(12), new(12)),
            new ViaPadMeasurement(new("ETCH/S03"), new("ETCH/S03"), "antipad", "CIRCLE", new(20), new(20)));
        var evidence = new ViaAnalysisEvidence(true, null, null, null, null, null, pads);
        var via = new ViaSpan("PAD_SAMPLE", position, layers, layers, "not_started", true, evidence);
        return new(new($"copper:{id}"), CopperKind.Via, net, null,
            new(new(x - 6, -6), new(x + 6, 6)), null, null, via, [], null, []);
    }

    private static CopperObject Segment(string net, decimal x, double startY, double endY, int id)
    {
        DesignPoint first = new(x, checked((decimal)startY));
        DesignPoint second = new(x, checked((decimal)endY));
        var bounds = new DesignBounds(
            new(Math.Min(first.X, second.X) - 2, Math.Min(first.Y, second.Y) - 2),
            new(Math.Max(first.X, second.X) + 2, Math.Max(first.Y, second.Y) + 2));
        return new(new($"copper:{id}"), CopperKind.Trace, net, new("ETCH/S03"), bounds,
            new LineGeometry(first, second), new Length(4), null, [], null, []);
    }

    private static CoverageReport FullCoverage(SceneQuery query) =>
        new(query.Families.Select(family => new FamilyCoverage(
            family, DataAvailability.Available, DataCompleteness.CompleteForRequestedScope, Reasons: [])));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Corridor pair policy check failed: " + message);
        }
    }
}
