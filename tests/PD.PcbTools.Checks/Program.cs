using CircuitHub.AllegroBridge;
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

var endpoints = new AllegroPcbEndpoints(1, "session-test", 7, "pick-test", "mils", "mils", 4,
    new(new(1.00001, 2), AllegroInteractionObjectKind.Pin, "SIGNAL_A"),
    new(new(11, 20), AllegroInteractionObjectKind.Via, "SIGNAL_A"));
var route = HorizontalFirstPlanner.Plan(endpoints, 7, "ETCH/S03");
Check(route.Points.SequenceEqual(new[] { endpoints.First.Position, new AllegroPcbPoint(11, 2), endpoints.Second.Position }),
    "Planner changed clicked endpoints or horizontal-first bend.");
Check(route.Width == 7 && route.Net == "SIGNAL_A" && route.Layer == "ETCH/S03", "Planner changed explicit edit inputs.");
var aligned = endpoints with { Second = endpoints.Second with { Position = new(1.00009, 20) } };
Check(HorizontalFirstPlanner.Plan(aligned, 7, "ETCH/S03").Points.Count == 2,
    "Reference truncation-bucket equality was replaced with coordinate snapping.");
var crossedBucket = aligned with { Second = aligned.Second with { Position = new(1.00011, 20) } };
Check(HorizontalFirstPlanner.Plan(crossedBucket, 7, "ETCH/S03").Points.Count == 3, "Truncation bucket boundary was lost.");
var negative = endpoints with
{
    First = endpoints.First with { Position = new(-1.00001, 2) },
    Second = endpoints.Second with { Position = new(-1.00009, 20) }
};
Check(HorizontalFirstPlanner.Plan(negative, 7, "ETCH/S03").Points.Count == 2,
    "Negative coordinates must truncate toward zero, not floor.");
var metric = endpoints with { NativeUnits = "millimeters" };
Check(HorizontalFirstPlanner.Plan(metric, 7, "ETCH/S03").Points.SequenceEqual(route.Points),
    "Metric planning changed physical clicked coordinates.");
Check(HorizontalFirstPlanner.Plan(endpoints with { Second = endpoints.Second with { Net = "CONFLICT" } }, 7, "ETCH/S03").Net is null,
    "Conflicting nets must remain unassigned, not be silently connected.");
Check(HorizontalFirstPlanner.Plan(endpoints with { First = endpoints.First with { Net = null } }, 7, "ETCH/S03").Net == "SIGNAL_A",
    "One known endpoint net was lost.");
Reject<ArgumentOutOfRangeException>(() => HorizontalFirstPlanner.Plan(endpoints, double.NaN, "ETCH/S03"), "Nonfinite width accepted.");
Reject<InvalidDataException>(() => HorizontalFirstPlanner.Plan(endpoints with { NativeUnits = "unknown" }, 7, "ETCH/S03"), "Unknown units accepted.");

foreach (string suffix in new[] { "PCIE_LINK", "RENAMED_SIGNAL_91" })
{
    var inputs = Fixture(suffix);
    var scan = CorridorAnalyzer.Analyze(inputs, new(0, null, false));
    Check(scan.HasCompleteInputs && scan.PairCount == 1 && scan.CorridorCount == 1 && scan.Findings.Count == 1,
        "Synthetic centerline crossing was not found with complete scalar inputs.");
    var finding = scan.Findings.Single();
    Check(finding.PairName == suffix && finding.AggressorNet == "CLOCK_TEST" && finding.Layer == "ETCH/S03",
        "Finding identifies the wrong pair, aggressor or layer.");
    Check(finding.DistanceMils == 0 && finding.HalfWidthMils == 10 && finding.HalfLengthMils == 50,
        "Measured corridor dimensions are wrong.");
    Check(ReferenceEquals(scan.Inputs, inputs), "The analyzer replaced the immutable source observation.");
    var shifted = CorridorAnalyzer.Analyze(Fixture(suffix, aggressorOffset: 15), new(0, null, false));
    Check(shifted.Findings.Count == 0, "Outside centerline was flagged without margin.");
    Check(CorridorAnalyzer.Analyze(Fixture(suffix, aggressorOffset: 15), new(10, null, false)).Findings.Count == 1,
        "Margin did not expand the screening corridor.");
    var ignored = inputs with { Objects = [inputs.Objects[0], inputs.Objects[1], inputs.Objects[2] with { Net = "DGND_A" }] };
    Check(CorridorAnalyzer.Analyze(ignored, new(0, null, false)).Findings.Count == 0, "Reference ground exclusion was lost.");
    var layerChanged = inputs with { Stackup = [new("ETCH/TOP", false), new("ETCH/S03", true), new("ETCH/BOTTOM", false)] };
    Check(CorridorAnalyzer.Analyze(layerChanged, new(0, null, false)).Findings.Count == 0, "Negative artwork layer became a target.");
    Check(CorridorAnalyzer.Analyze(inputs with { NativeUnits = "millimeters", NativePrecision = 6 }, new(0, null, false)).Findings.Count == 1,
        "Equivalent simple physical geometry differs in millimeter mode.");
    var module = new AllegroPcbModuleInput("module-A", new(new(-60, -5), new(60, 5)));
    Check(CorridorAnalyzer.Analyze(inputs with { Modules = [module] }, new(0, "MODULE-a", false)).Findings.Count == 1,
        "Module scope removed a foreign obstacle or changed case-insensitive module identity.");
    Reject<InvalidOperationException>(() => CorridorAnalyzer.Analyze(inputs, new(0, "absent", false)), "Absent module produced an empty pass.");
    var originalVia = inputs.Objects[0].Via!;
    var unknownVia = inputs.Objects[0] with
    {
        Via = originalVia with { ActiveLayers = null, Backdrill = originalVia.Backdrill with { Status = "unavailable" } }
    };
    var unknown = CorridorAnalyzer.Analyze(inputs with { Objects = [unknownVia, inputs.Objects[1]] }, new(0, null, false));
    Check(!unknown.HasCompleteInputs && unknown.Findings.Count == 0 && unknown.CoverageWarnings.Count > 0,
        "Missing backdrill inputs became a clear result.");
    var absentPad = inputs.Objects[0] with { Via = originalVia with { Pads = originalVia.Pads.Where(item => item.Type != "antipad").ToArray() } };
    Check(!CorridorAnalyzer.Analyze(inputs with { Objects = [absentPad, inputs.Objects[1], inputs.Objects[2]] }, new(0, null, false)).HasCompleteInputs,
        "One missing antipad was hidden by the other via's measurement.");
    Check(!CorridorAnalyzer.Analyze(inputs with { Unavailable = ["scalar_family_missing"] }, new(0, null, false)).HasCompleteInputs,
        "Capture-level missing data became a complete result.");

    var query = CorridorNavigation.CreateQuery(scan, finding);
    var fresh = new AllegroPcbRegionGeometry(1, inputs.SessionId, inputs.BoardGeneration, "fresh-token", "mils",
        inputs.NativeUnits, inputs.NativePrecision, query.Bounds!, query.Layers!, 2048, 16384, false,
        [], inputs.Stackup, inputs.Objects);
    CorridorNavigation.ValidateFreshRead(scan, finding, fresh);
    checks++;
    // A native grid normalization can slightly adjust requested borders while
    // retaining every required witness. The SDK owns request/bounds admission.
    CorridorNavigation.ValidateFreshRead(scan, finding, fresh with
    {
        Bounds = new(new(query.Bounds!.Minimum.X + 0.001, query.Bounds.Minimum.Y + 0.001), query.Bounds.Maximum)
    });
    checks++;
    Reject<InvalidDataException>(() => CorridorNavigation.ValidateFreshRead(scan, finding, fresh with { SessionId = "foreign" }), "Foreign-session navigation accepted.");
    Reject<InvalidDataException>(() => CorridorNavigation.ValidateFreshRead(scan, finding, fresh with { Truncated = true }), "Partial navigation accepted.");
    Reject<InvalidDataException>(() => CorridorNavigation.ValidateFreshRead(scan, finding, fresh with { Unavailable = ["contours_not_requested"] }), "Missing contour data accepted.");
    Reject<InvalidDataException>(() => CorridorNavigation.ValidateFreshRead(scan, finding, fresh with { Layers = ["ETCH/S99"] }), "Wrong navigation layer accepted.");
    var wider = inputs.Objects[2] with { Segment = inputs.Objects[2].Segment! with { Width = 15 } };
    Reject<InvalidDataException>(() => CorridorNavigation.ValidateFreshRead(scan, finding, fresh with { Objects = [inputs.Objects[0], inputs.Objects[1], wider] }),
        "Changed aggressor geometry retained finding authority.");
    Reject<InvalidDataException>(() => CorridorNavigation.ValidateFreshRead(scan, finding, fresh with { Objects = [.. inputs.Objects, inputs.Objects[0]] }),
        "Coincident ambiguous native witnesses were silently deduplicated.");
    Reject<ArgumentException>(() => CorridorNavigation.CreateQuery(scan, finding with { Id = "forged" }), "Foreign finding accepted.");
}

var unused = Fixture("NC_UNUSED");
Check(CorridorAnalyzer.Analyze(unused, new(0, null, false)).PairCount == 0, "Unused pair included by default.");
Check(CorridorAnalyzer.Analyze(unused, new(0, null, true)).PairCount == 1, "Include-unused policy ignored.");
var large = Fixture("LARGE");
large = large with
{
    Objects = [.. large.Objects, .. Enumerable.Range(0, 2500).Select(index => Segment("UNRELATED_" + index, 5000 + index, 100, 101))]
};
Check(CorridorAnalyzer.Analyze(large, new(0, null, false)).Findings.Count == 1,
    "A capture larger than one native response changed the small witnessed crossing.");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    Reject<OperationCanceledException>(() => CorridorAnalyzer.Analyze(large, new(0, null, false), cancelled.Token), "Managed cancellation ignored.");
}
foreach (string ignored in new[] { "GND", "AGND1", "PD_RES", "PU_TEST", "NC_1", "NU_RES", "NC3", "VCC", "+3V" })
{
    Check(SignalClassifier.IsIgnoredAggressor(ignored), "Reference exclusion lost: " + ignored);
}
Check(!SignalClassifier.IsIgnoredAggressor("SIGNAL_GNDRIVE"), "Ground exclusion overmatches signal names.");
Check(SignalClassifier.Default.Classify("PCIE_TX0_P").Category == "PCIE", "Retained ordered PCIe category did not match.");
Check(SignalClassifier.Default.Classify("UNLISTED_XYZ_921") == ("UNKNOWN", "CRITICAL"), "Unknown classification was silently relaxed.");
Console.WriteLine($"PASS: {checks} pure managed routing, corridor, coverage, classification and navigation-witness checks. No Allegro or GUI execution.");
return 0;

static AllegroPcbBoardInputs Fixture(string pair, double aggressorOffset = 0)
{
    return new("session-test", 7, "immutable-capture", 1, "mils", "mils", 2,
        new(new(-100, -100), new(100, 100)),
        [new("ETCH/TOP", false), new("ETCH/S03", false), new("ETCH/BOTTOM", false)],
        [new(pair + "_P", 1), new(pair + "_N", 1), new("CLOCK_TEST", 1)], [], null,
        [Via(pair + "_P", -50), Via(pair + "_N", 50), Segment("CLOCK_TEST", 0, aggressorOffset == 0 ? -20 : aggressorOffset, aggressorOffset == 0 ? 20 : aggressorOffset)],
        ["contours_not_requested"]);
}
static AllegroPcbRegionObject Via(string net, double x)
{
    var position = new AllegroPcbPoint(x, 0);
    var pads = new[]
    {
        new AllegroPcbPadMeasurement("ETCH/S03", "ETCH/S03", "regular", "CIRCLE", 12, 12),
        new AllegroPcbPadMeasurement("ETCH/S03", "ETCH/S03", "antipad", "CIRCLE", 20, 20)
    };
    return new("via", net, null, new(new(x - 6, -6), new(x + 6, 6)), null,
        new(position, "PAD_SAMPLE", true, ["ETCH/TOP", "ETCH/S03", "ETCH/BOTTOM"],
            ["ETCH/TOP", "ETCH/S03", "ETCH/BOTTOM"], new("not_started", null, null, null, null, null), pads),
        null, [], []);
}
static AllegroPcbRegionObject Segment(string net, double x, double startY, double endY)
{
    var first = new AllegroPcbPoint(x, startY);
    var second = startY == endY ? new AllegroPcbPoint(x + 20, endY) : new AllegroPcbPoint(x, endY);
    return new("line", net, "ETCH/S03", new(new(Math.Min(first.X, second.X) - 2, Math.Min(startY, endY) - 2),
        new(Math.Max(first.X, second.X) + 2, Math.Max(startY, endY) + 2)), new(first, second, 4, null), null, null, [], []);
}
