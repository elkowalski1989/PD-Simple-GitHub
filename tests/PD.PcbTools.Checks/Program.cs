using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
    checks++;
}
void Reject<T>(Action action, string message) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        checks++;
        return;
    }
    throw new InvalidOperationException(message);
}
T Capture<T>(Action action, string message) where T : Exception
{
    try
    {
        action();
    }
    catch (T error)
    {
        checks++;
        return error;
    }
    throw new InvalidOperationException(message);
}

SceneQuery corridorQuery = CorridorAnalyzer.CreateSceneQuery();
Check(
    corridorQuery.Kind == SceneReadKind.CompleteBoard &&
    !corridorQuery.IncludeContours &&
    corridorQuery.CopperKinds.SequenceEqual(
        [CopperKind.Trace, CopperKind.Via, CopperKind.Shape]) &&
    corridorQuery.ViaPadMeasurements.Mode ==
        ViaPadMeasurementSelectionMode.MatchingNetNameSuffixes &&
    corridorQuery.ViaPadMeasurements.NetNameSuffixes.SequenceEqual(
        ["_P", "_N"]) &&
    corridorQuery.Module is null &&
    corridorQuery.Families.SequenceEqual(
        new[]
        {
            DataFamily.Nets,
            DataFamily.Modules,
            DataFamily.Layers,
            DataFamily.Copper,
        }),
    "The corridor acquisition query requested unrelated Engine families.");
SceneQuery moduleCorridorQuery = CorridorAnalyzer.CreateSceneQuery("module-A");
Check(moduleCorridorQuery.Module == "module-A" &&
    moduleCorridorQuery.Families.SequenceEqual(corridorQuery.Families),
    "Module-scoped corridor acquisition changed its required family set.");
Reject<ArgumentException>(
    () => CorridorAnalyzer.CreateSceneQuery(" "),
    "An empty module name was admitted into a native corridor query.");

var routeLayer = new EngineRoutingLayer(new("ETCH/S03"), false, true, true);
var endpoints = new EngineTraceEndpoints(
    new(new(1.00001m, 2), "SIGNAL_A"),
    new(new(11, 20), "SIGNAL_A"),
    "mils", 4);
EngineTracePlan route = EngineHorizontalFirstRoutePolicy.Plan(endpoints, 7, routeLayer);
Check(route.Points.SequenceEqual(new[] { endpoints.First.Position, new DesignPoint(11, 2), endpoints.Second.Position }),
    "Engine planner changed clicked endpoints or horizontal-first bend.");
Check(route.Width.Mils == 7 && route.NetName == "SIGNAL_A" && route.Layer == new LayerId("ETCH/S03"),
    "Engine planner changed explicit edit inputs.");
var aligned = endpoints with { Second = endpoints.Second with { Position = new(1.00009m, 20) } };
Check(EngineHorizontalFirstRoutePolicy.Plan(aligned, 7, routeLayer).Points.Count == 2,
    "Reference truncation-bucket equality was replaced with coordinate snapping.");
var crossedBucket = aligned with { Second = aligned.Second with { Position = new(1.00011m, 20) } };
Check(EngineHorizontalFirstRoutePolicy.Plan(crossedBucket, 7, routeLayer).Points.Count == 3,
    "Truncation bucket boundary was lost.");
var negative = endpoints with
{
    First = endpoints.First with { Position = new(-1.00001m, 2) },
    Second = endpoints.Second with { Position = new(-1.00009m, 20) }
};
Check(EngineHorizontalFirstRoutePolicy.Plan(negative, 7, routeLayer).Points.Count == 2,
    "Negative coordinates must truncate toward zero, not floor.");
var metricEndpoints = endpoints with { NativeUnits = "millimeters" };
Check(EngineHorizontalFirstRoutePolicy.Plan(metricEndpoints, 7, routeLayer).Points.SequenceEqual(route.Points),
    "Metric planning changed physical clicked coordinates.");
Check(EngineHorizontalFirstRoutePolicy.Plan(endpoints with
{
    Second = endpoints.Second with { NetName = "CONFLICT" }
}, 7, routeLayer).NetName is null, "Conflicting nets must remain unassigned, not be silently connected.");
Check(EngineHorizontalFirstRoutePolicy.Plan(endpoints with
{
    First = endpoints.First with { NetName = null }
}, 7, routeLayer).NetName == "SIGNAL_A", "One known endpoint net was lost.");
Reject<ArgumentOutOfRangeException>(() => EngineHorizontalFirstRoutePolicy.Plan(endpoints, 0.01m, routeLayer),
    "Out-of-range width accepted.");
Reject<InvalidDataException>(() => EngineHorizontalFirstRoutePolicy.Plan(endpoints with { NativeUnits = "unknown" }, 7, routeLayer),
    "Unknown units accepted.");

foreach (string suffix in new[] { "PCIE_LINK", "RENAMED_SIGNAL_91" })
{
    DesignScene inputs = Fixture(suffix);
    ImmutableArray<CopperObject> copper = inputs.Data.Copper;
    CorridorScan scan = CorridorAnalyzer.Analyze(inputs, new(0, null, false));
    Check(scan.HasCompleteInputs && scan.PairCount == 1 && scan.CorridorCount == 1 && scan.Findings.Count == 1,
        "Synthetic centerline crossing was not found with complete Engine inputs.");
    SceneQuery selectiveQuery = CorridorAnalyzer.CreateSceneQuery();
    DesignScene selectiveInputs = Rebuild(
        inputs,
        query: selectiveQuery,
        coverage: CoverageForQuery(selectiveQuery, copperComplete: true));
    CorridorScan selectiveScan = CorridorAnalyzer.Analyze(
        selectiveInputs,
        new(0, null, false));
    Check(selectiveScan.PairCount == scan.PairCount &&
        selectiveScan.CorridorCount == scan.CorridorCount &&
        selectiveScan.Findings.SequenceEqual(scan.Findings) &&
        selectiveScan.CoverageWarnings.SequenceEqual(scan.CoverageWarnings),
        "Selective Engine acquisition changed corridor findings, dimensions, or warnings.");

    CopperObject foreignVia = Via("CLOCK_TEST", 0, 3);
    DesignScene fullViaAggressorScene = WithCopper(
        inputs,
        [copper[0], copper[1], foreignVia]);
    CorridorScan fullViaAggressorScan = CorridorAnalyzer.Analyze(
        fullViaAggressorScene,
        new(0, null, false));
    CopperObject selectiveForeignVia = foreignVia with
    {
        Via = foreignVia.Via! with
        {
            Analysis = foreignVia.Via.Analysis! with
            {
                Pads = [],
                PadMeasurementsRequested = false,
            },
        },
    };
    DesignScene selectiveViaAggressorScene = Rebuild(
        WithCopper(inputs, [copper[0], copper[1], selectiveForeignVia]),
        query: selectiveQuery,
        coverage: CoverageForQuery(selectiveQuery, copperComplete: true));
    CorridorScan selectiveViaAggressorScan = CorridorAnalyzer.Analyze(
        selectiveViaAggressorScene,
        new(0, null, false));
    Check(selectiveViaAggressorScene.Data.Copper.Contains(selectiveForeignVia) &&
        selectiveViaAggressorScan.Findings.SequenceEqual(
            fullViaAggressorScan.Findings) &&
        selectiveViaAggressorScan.CoverageWarnings.SequenceEqual(
            fullViaAggressorScan.CoverageWarnings),
        "Selective via-pad acquisition removed a foreign via or changed the result.");
    CorridorFinding finding = scan.Findings.Single();
    Check(finding.PairName == suffix && finding.AggressorNet == "CLOCK_TEST" && finding.Layer == "ETCH/S03",
        "Finding identifies the wrong pair, aggressor or layer.");
    Check(finding.DistanceMils == 0 && finding.HalfWidthMils == 10 && finding.HalfLengthMils == 50,
        "Measured corridor dimensions are wrong.");
    Check(ReferenceEquals(scan.Scene, inputs), "The analyzer replaced the immutable Engine source scene.");
    Check(CorridorAnalyzer.Analyze(Fixture(suffix, aggressorOffset: 15), new(0, null, false)).Findings.Count == 0,
        "Outside centerline was flagged without margin.");
    Check(CorridorAnalyzer.Analyze(Fixture(suffix, aggressorOffset: 15), new(10, null, false)).Findings.Count == 1,
        "Margin did not expand the screening corridor.");

    DesignScene ignored = WithCopper(inputs, [copper[0], copper[1], copper[2] with { NetName = "DGND_A" }]);
    Check(CorridorAnalyzer.Analyze(ignored, new(0, null, false)).Findings.Count == 0,
        "Reference ground exclusion was lost.");

    DesignScene layerChanged = Rebuild(inputs, data: inputs.Data with
    {
        Layers = [new(new("ETCH/TOP"), 0, false), new(new("ETCH/S03"), 1, true), new(new("ETCH/BOTTOM"), 2, false)]
    });
    Check(CorridorAnalyzer.Analyze(layerChanged, new(0, null, false)).Findings.Count == 0,
        "Negative artwork layer became a target.");
    Check(CorridorAnalyzer.Analyze(Rebuild(inputs,
            document: inputs.Document with { NativeUnits = "millimeters", NativePrecision = 6 }),
        new(0, null, false)).Findings.Count == 1,
        "Equivalent simple physical geometry differs in millimeter mode.");

    var module = new ModuleObject(new("module:fixture"), "module-A", new(new(-60, -5), new(60, 5)));
    DesignScene moduleScene = Rebuild(inputs, data: inputs.Data with { Modules = [module] });
    Check(CorridorAnalyzer.Analyze(moduleScene, new(0, "MODULE-a", false)).Findings.Count == 1,
        "Module scope removed a foreign obstacle or changed case-insensitive module identity.");
    SceneQuery selectiveModuleQuery = CorridorAnalyzer.CreateSceneQuery("MODULE-a");
    DesignScene selectiveModuleScene = Rebuild(
        moduleScene,
        query: selectiveModuleQuery,
        coverage: CoverageForQuery(selectiveModuleQuery, copperComplete: true));
    CorridorScan fullModuleScan = CorridorAnalyzer.Analyze(
        moduleScene,
        new(0, "MODULE-a", false));
    CorridorScan selectiveModuleScan = CorridorAnalyzer.Analyze(
        selectiveModuleScene,
        new(0, "MODULE-a", false));
    Check(selectiveModuleScan.Findings.SequenceEqual(fullModuleScan.Findings) &&
        selectiveModuleScan.CoverageWarnings.SequenceEqual(fullModuleScan.CoverageWarnings),
        "Selective module-scoped acquisition changed corridor behavior.");
    Reject<InvalidOperationException>(() => CorridorAnalyzer.Analyze(inputs, new(0, "absent", false)),
        "Absent module produced an empty pass.");

    ViaSpan originalVia = copper[0].Via!;
    ViaAnalysisEvidence originalEvidence = originalVia.Analysis!;
    CopperObject unknownVia = copper[0] with
    {
        Via = originalVia with
        {
            ActiveLayers = [],
            BackdrillStatus = "unavailable",
            Analysis = originalEvidence with { ActiveLayersAvailable = false }
        }
    };
    CorridorScan unknown = CorridorAnalyzer.Analyze(WithCopper(inputs, [unknownVia, copper[1]]), new(0, null, false));
    Check(!unknown.HasCompleteInputs && unknown.Findings.Count == 0 && unknown.CoverageWarnings.Count > 0,
        "Missing backdrill inputs became a clear result.");

    CopperObject absentPad = copper[0] with
    {
        Via = originalVia with
        {
            Analysis = originalEvidence with
            {
                Pads = originalEvidence.Pads.Where(item => item.Type != "antipad").ToImmutableArray()
            }
        }
    };
    Check(!CorridorAnalyzer.Analyze(WithCopper(inputs, [absentPad, copper[1], copper[2]]), new(0, null, false)).HasCompleteInputs,
        "One missing antipad was hidden by the other via's measurement.");

    CopperObject unrequestedSubjectPad = copper[0] with
    {
        Via = originalVia with
        {
            Analysis = originalEvidence with
            {
                Pads = [],
                PadMeasurementsRequested = false,
            },
        },
    };
    CorridorScan unrequestedSubject = CorridorAnalyzer.Analyze(
        WithCopper(inputs, [unrequestedSubjectPad, copper[1], copper[2]]),
        new(0, null, false));
    Check(!unrequestedSubject.HasCompleteInputs &&
        unrequestedSubject.CoverageWarnings.Any(warning =>
            warning.Contains("were not requested", StringComparison.Ordinal)),
        "An unrequested subject-via pad measurement became a complete result.");
    Reject<ArgumentException>(() => WithCopper(
        inputs,
        [
            copper[0] with
            {
                Via = originalVia with
                {
                    Analysis = originalEvidence with
                    {
                        PadMeasurementsRequested = false,
                    },
                },
            },
            copper[1],
            copper[2],
        ]),
        "A scene retained pad data while declaring that measurements were not requested.");

    DesignScene partial = Rebuild(inputs, coverage: Coverage(inputs, copperComplete: false, "scalar_family_missing"));
    Check(!CorridorAnalyzer.Analyze(partial, new(0, null, false)).HasCompleteInputs,
        "Capture-level missing data became a complete result.");

    const string pinPadReason = "pin:pad:ETCH/TOP:antipad";
    var pinOnlyPartialScope = new CopperReadScope(
        [CopperKind.Trace, CopperKind.Via, CopperKind.Shape],
        [
            new(CopperKind.Trace, DataAvailability.Available,
                DataCompleteness.CompleteForRequestedScope, GeometryFidelity.AnalyticPrimitive, []),
            new(CopperKind.Via, DataAvailability.Available,
                DataCompleteness.CompleteForRequestedScope, GeometryFidelity.BoundsOnly, []),
            new(CopperKind.Shape, DataAvailability.Available,
                DataCompleteness.CompleteForRequestedScope, GeometryFidelity.BoundsOnly, []),
            new(CopperKind.Pin, DataAvailability.Available,
                DataCompleteness.Partial, GeometryFidelity.BoundsOnly, [pinPadReason]),
        ]);
    DesignScene pinOnlyPartial = Rebuild(
        inputs,
        coverage: Coverage(inputs, copperComplete: false, pinPadReason),
        data: inputs.Data with { CopperScope = pinOnlyPartialScope });
    Check(CorridorAnalyzer.Analyze(pinOnlyPartial, new(0, null, false)).HasCompleteInputs,
        "Component-pin pad absence incorrectly made a trace/via/shape corridor check partial.");

    const string traceReason = "cline:centerline_unavailable";
    var tracePartialScope = pinOnlyPartialScope with
    {
        Kinds = [CopperKind.Via, CopperKind.Shape],
        Coverage = pinOnlyPartialScope.Details
            .Select(item => item.Kind == CopperKind.Trace
                ? item with
                {
                    Completeness = DataCompleteness.Partial,
                    Reasons = [traceReason],
                }
                : item)
            .ToImmutableArray(),
    };
    DesignScene tracePartial = Rebuild(
        inputs,
        coverage: Coverage(inputs, copperComplete: false, traceReason),
        data: inputs.Data with { CopperScope = tracePartialScope });
    CorridorScan tracePartialScan = CorridorAnalyzer.Analyze(
        tracePartial,
        new(0, null, false));
    Check(!tracePartialScan.HasCompleteInputs &&
        tracePartialScan.CoverageWarnings.Any(warning =>
            warning.Contains(traceReason, StringComparison.Ordinal)),
        "A corridor-required trace coverage defect was hidden by kind-scoped admission.");

    SceneQuery query = CorridorNavigation.CreateQuery(scan, finding);
    WorkspaceDocumentIdentity document = new("session-test", 1, 7, 1234, "fixture.brd", "25");
    DesignScene fresh = FreshRegion(inputs, query);
    CorridorNavigation.ValidateFreshScene(scan, finding, fresh, document, document);
    checks++;
    // Native bounds can be slightly normalized while retaining every witness.
    DesignBounds shiftedBounds = new(
        new(fresh.Document.Bounds.Minimum.X + 0.001m, fresh.Document.Bounds.Minimum.Y + 0.001m),
        fresh.Document.Bounds.Maximum);
    CorridorNavigation.ValidateFreshScene(scan, finding,
        Rebuild(fresh, document: fresh.Document with { Bounds = shiftedBounds }), document, document);
    checks++;
    Reject<InvalidDataException>(() => CorridorNavigation.ValidateFreshScene(scan, finding, fresh,
        document with { SessionId = "foreign" }, document), "Foreign-session navigation accepted.");
    Reject<InvalidDataException>(() => CorridorNavigation.ValidateFreshScene(scan, finding,
        Rebuild(fresh, coverage: Coverage(fresh, copperComplete: false, "truncated")), document, document),
        "Partial navigation accepted.");
    const string surfaceReason =
        "surface_conversion_unavailable:ETCH/S03:resolved_copper:below_qualified_resolution:" +
        "source=surface/test:contour=0:vertex=2";
    var partialKinds = new CopperReadScope(
        [CopperKind.Trace, CopperKind.Via, CopperKind.Pin],
        [
            new(CopperKind.Trace, DataAvailability.Available,
                DataCompleteness.CompleteForRequestedScope, GeometryFidelity.AnalyticPrimitive, []),
            new(CopperKind.Via, DataAvailability.Available,
                DataCompleteness.CompleteForRequestedScope, GeometryFidelity.NativeContour, []),
            new(CopperKind.Shape, DataAvailability.Available,
                DataCompleteness.Partial, GeometryFidelity.NativeContour, [surfaceReason]),
            new(CopperKind.Pin, DataAvailability.Available,
                DataCompleteness.CompleteForRequestedScope, GeometryFidelity.NativeContour, []),
        ]);
    DesignScene surfacePartial = Rebuild(
        fresh,
        coverage: Coverage(fresh, copperComplete: false),
        data: fresh.Data with { CopperScope = partialKinds });
    InvalidDataException surfaceError = Capture<InvalidDataException>(
        () => CorridorNavigation.ValidateFreshScene(
            scan,
            finding,
            surfacePartial,
            document,
            document),
        "Partial surface conversion unexpectedly authorized navigation.");
    Check(surfaceError.Message.Contains("below_qualified_resolution", StringComparison.Ordinal) &&
        surfaceError.Message.Contains("surface/test", StringComparison.Ordinal) &&
        surfaceError.Message.Contains("Shape", StringComparison.Ordinal),
        "Navigation hid the exact incomplete copper kind and conversion evidence.");
    DesignScene wrongLayerScope = Rebuild(fresh, query: fresh.Query with { Layers = [new("ETCH/S99")] });
    Reject<InvalidDataException>(() => CorridorNavigation.ValidateFreshScene(scan, finding, wrongLayerScope, document, document),
        "Wrong navigation layer scope accepted.");
    CopperObject wider = copper[2] with { Width = new Length(15) };
    Reject<InvalidDataException>(() => CorridorNavigation.ValidateFreshScene(scan, finding,
        WithCopper(fresh, [copper[0], copper[1], wider]), document, document),
        "Changed aggressor geometry retained finding authority.");
    CopperObject duplicateWitness = copper[0] with { Id = new("copper:duplicate") };
    Reject<InvalidDataException>(() => CorridorNavigation.ValidateFreshScene(scan, finding,
        WithCopper(fresh, [.. copper, duplicateWitness]), document, document),
        "Coincident ambiguous Engine witnesses were silently deduplicated.");
    Reject<ArgumentException>(() => CorridorNavigation.CreateQuery(scan, finding with { Id = "forged" }),
        "Foreign finding accepted.");
}

DesignScene unused = Fixture("NC_UNUSED");
Check(CorridorAnalyzer.Analyze(unused, new(0, null, false)).PairCount == 0, "Unused pair included by default.");
Check(CorridorAnalyzer.Analyze(unused, new(0, null, true)).PairCount == 1, "Include-unused policy ignored.");
DesignScene large = Fixture("LARGE");
var largeCopper = large.Data.Copper.ToBuilder();
for (int index = 0; index < 2500; index++)
{
    largeCopper.Add(Segment("UNRELATED_" + index, 5000 + index, 100, 101, 100 + index));
}
large = WithCopper(large, largeCopper.ToImmutable());
Check(CorridorAnalyzer.Analyze(large, new(0, null, false)).Findings.Count == 1,
    "A capture larger than one native response changed the small witnessed crossing.");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    Reject<OperationCanceledException>(() => CorridorAnalyzer.Analyze(large, new(0, null, false), cancelled.Token),
        "Managed cancellation ignored.");
}
foreach (string ignoredNet in new[] { "GND", "AGND1", "PD_RES", "PU_TEST", "NC_1", "NU_RES", "NC3", "VCC", "+3V" })
{
    Check(SignalClassifier.IsIgnoredAggressor(ignoredNet), "Reference exclusion lost: " + ignoredNet);
}
Check(!SignalClassifier.IsIgnoredAggressor("SIGNAL_GNDRIVE"), "Ground exclusion overmatches signal names.");
Check(SignalClassifier.Default.Classify("PCIE_TX0_P").Category == "PCIE", "Retained ordered PCIe category did not match.");
Check(SignalClassifier.Default.Classify("UNLISTED_XYZ_921") == ("UNKNOWN", "CRITICAL"),
    "Unknown classification was silently relaxed.");
Console.WriteLine($"PASS: {checks} Engine routing policy, corridor, coverage, classification and navigation-witness checks. No Allegro or GUI execution.");
return 0;

static DesignScene Fixture(string pair, double aggressorOffset = 0)
{
    SceneQuery query = SceneQuery.BoardGeometry();
    var layers = ImmutableArray.Create(
        new LayerObject(new("ETCH/TOP"), 0, false),
        new LayerObject(new("ETCH/S03"), 1, false),
        new LayerObject(new("ETCH/BOTTOM"), 2, false));
    var data = new SceneData
    {
        Nets = [new(new("net:p"), pair + "_P", 1), new(new("net:n"), pair + "_N", 1), new(new("net:a"), "CLOCK_TEST", 1)],
        Layers = layers,
        Modules = [],
        Copper = [Via(pair + "_P", -50, 0), Via(pair + "_N", 50, 1),
            Segment("CLOCK_TEST", 0, aggressorOffset == 0 ? -20 : aggressorOffset,
                aggressorOffset == 0 ? 20 : aggressorOffset, 2)],
        CopperScope = new([CopperKind.Trace, CopperKind.Via, CopperKind.Shape])
    };
    var identity = new SceneIdentity(Guid.NewGuid(), DateTimeOffset.UtcNow,
        new("test-engine", "1", false, "fixture"));
    var document = new DocumentContext(DocumentKind.PcbBoard, "fixture.brd", "mils", 2,
        new(new(-100, -100), new(100, 100)));
    return new DesignScene(identity, document, query, CoverageForQuery(query, true), data);
}

static CopperObject Via(string net, decimal x, int id)
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

static CopperObject Segment(string net, decimal x, double startY, double endY, int id)
{
    DesignPoint first = new(x, checked((decimal)startY));
    DesignPoint second = startY == endY ? new(x + 20, checked((decimal)endY)) : new(x, checked((decimal)endY));
    var bounds = new DesignBounds(
        new(Math.Min(first.X, second.X) - 2, Math.Min(first.Y, second.Y) - 2),
        new(Math.Max(first.X, second.X) + 2, Math.Max(first.Y, second.Y) + 2));
    return new(new($"copper:{id}"), CopperKind.Trace, net, new("ETCH/S03"), bounds,
        new LineGeometry(first, second), new Length(4), null, [], null, []);
}

static DesignScene FreshRegion(DesignScene source, SceneQuery query)
{
    DesignBounds bounds = query.Region ?? throw new InvalidOperationException("Region query has no bounds.");
    var document = source.Document with { Bounds = bounds };
    var data = new SceneData
    {
        Layers = source.Data.Layers,
        Copper = source.Data.Copper,
        CopperScope = source.Data.CopperScope
    };
    return new DesignScene(new(Guid.NewGuid(), DateTimeOffset.UtcNow, new("test-engine", "1", false, "fresh-region")),
        document, query, CoverageForQuery(query, true), data);
}

static DesignScene WithCopper(DesignScene source, IEnumerable<CopperObject> copper)
{
    ImmutableArray<CopperObject> values = copper.ToImmutableArray();
    SceneData data = source.Data with { Copper = values };
    if (source.Query.Families.Contains(DataFamily.Nets))
    {
        var nets = source.Data.Nets.ToBuilder();
        var known = source.Data.Nets.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        int extra = 0;
        foreach (string name in values.Select(item => item.NetName).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (known.Add(name))
            {
                nets.Add(new(new($"net:fixture-extra:{extra++}"), name, 0));
            }
        }
        data = data with { Nets = nets.ToImmutable() };
    }
    return Rebuild(source, data: data);
}

static DesignScene Rebuild(DesignScene source, DocumentContext? document = null,
    CoverageReport? coverage = null, SceneData? data = null, SceneQuery? query = null) =>
    new(source.Identity, document ?? source.Document, query ?? source.Query, coverage ?? source.Coverage, data ?? source.Data);

static CoverageReport Coverage(DesignScene scene, bool copperComplete, params string[] reasons) =>
    CoverageForQuery(scene.Query, copperComplete, reasons);

static CoverageReport CoverageForQuery(SceneQuery query, bool copperComplete, params string[] reasons)
{
    var families = new List<FamilyCoverage>();
    foreach (DataFamily family in query.Families)
    {
        if (family == DataFamily.Copper)
        {
            families.Add(new(family, DataAvailability.Available,
                copperComplete ? DataCompleteness.CompleteForRequestedScope : DataCompleteness.Partial,
                Reasons: reasons.ToImmutableArray()));
        }
        else if (IsSyntheticFixtureUnavailableFamily(family))
        {
            families.Add(new(
                family,
                DataAvailability.Unavailable,
                DataCompleteness.Partial,
                Reasons: ["synthetic_fixture_family_unavailable"]));
        }
        else
        {
            families.Add(new(family, DataAvailability.Available, DataCompleteness.CompleteForRequestedScope, Reasons: []));
        }
    }
    return new CoverageReport(families);
}

static bool IsSyntheticFixtureUnavailableFamily(DataFamily family) =>
    family.ToString() is "BoardGeometry" or "Stackup" or "Routes" or "Contacts";
