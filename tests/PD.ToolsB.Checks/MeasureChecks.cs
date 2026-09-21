using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools.MeasureTools;
using PD.PcbTools.OverlayTools;

/// <summary>
/// Lane B T03 offline checks: captured/native measurement math, snap policy,
/// session state machine, ruler lifecycle, recipe publication shape, and
/// export text. No native Allegro is touched: the synthetic board and a fresh
/// Engine session prove zero mutation from measurement paths.
/// </summary>
internal static class MeasureChecks
{
    internal static void Run()
    {
        CheckKnownSpans();
        CheckSamePoint();
        CheckNegativeCoordinates();
        CheckMilMmEquivalence();
        CheckGridSnap();
        CheckNativeEndpointProvenance();
        CheckSessionLifecycle();
        CheckSupersededPickFencing();
        CheckRulerLifecycle();
        CheckRulerRecipeBuild();
        CheckExportText();
        CheckValidation();
    }

    private static void CheckKnownSpans()
    {
        // 3-4-5 triangle in mils.
        MeasuredSpan span = MeasureMath.Compute(
            Capture(0, 0), Capture(300, 400));
        Require(span.DeltaXMils == 300m, "delta X");
        Require(span.DeltaYMils == 400m, "delta Y");
        Require(span.DistanceMils == 500m, "3-4-5 distance");
        Require(span.AngleDegrees is { } angle && Math.Abs(angle - 53.130m) < 0.001m,
            "3-4-5 angle");
        Require(!span.IsDegenerate, "non-degenerate flag");
    }

    private static void CheckSamePoint()
    {
        MeasuredSpan span = MeasureMath.Compute(Capture(10, 20), Capture(10, 20));
        Require(span.IsDegenerate, "same-point degeneracy");
        Require(span.DistanceMils == 0m, "zero distance");
        Require(span.AngleDegrees is null, "undefined angle");
    }

    private static void CheckNegativeCoordinates()
    {
        MeasuredSpan span = MeasureMath.Compute(Capture(-100, -50), Capture(100, 50));
        Require(span.DeltaXMils == 200m && span.DeltaYMils == 100m, "signed deltas");
        decimal expected = (decimal)Math.Sqrt(200.0 * 200.0 + 100.0 * 100.0);
        Require(Math.Abs(span.DistanceMils - expected) < 0.001m, "negative-coordinate distance");
    }

    private static void CheckMilMmEquivalence()
    {
        MeasuredSpan span = MeasureMath.Compute(Capture(0, 0), Capture(1000, 0));
        Require(span.DistanceMillimeters == 25.4m, "1000 mil is 25.4 mm");
        Require(MeasureMath.FormatDistance(span, millimeters: true) == "25.4 mm", "mm display");
        Require(MeasureMath.FormatDistance(span, millimeters: false) == "1000 mil", "mil display");
    }

    private static void CheckGridSnap()
    {
        // Off-grid raw keeps both values: computation uses snapped, audit keeps raw.
        MeasureEndpoint endpoint = MeasureMath.CaptureEndpoint(new DesignPoint(1.6m, 2.3m), 1m);
        Require(endpoint.WasSnapped, "snap moved the point");
        Require(endpoint.Snapped == new DesignPoint(2m, 2m), "snapped position");
        Require(endpoint.Raw == new DesignPoint(1.6m, 2.3m), "raw retained");

        MeasureEndpoint onGrid = MeasureMath.CaptureEndpoint(new DesignPoint(2m, 2m), 1m);
        Require(!onGrid.WasSnapped, "on-grid point unchanged");

        MeasureEndpoint plain = MeasureMath.CaptureEndpoint(new DesignPoint(1.6m, 2.3m), null);
        Require(!plain.WasSnapped && plain.Snapped == plain.Raw, "no-snap identity");
    }

    private static void CheckNativeEndpointProvenance()
    {
        MeasureEndpoint native = MeasureMath.NativeEndpoint(
            new DesignPoint(50m, 75m), "Via", "GND");
        Require(native.Provenance == MeasureEndpointProvenance.NativeObservation, "native provenance");
        Require(native.ObjectKind == "Via" && native.NetName == "GND", "native identity");
        Require(!native.WasSnapped, "native positions are already canonical");

        MeasuredSpan mixed = MeasureMath.Compute(Capture(0, 0), native);
        Require(mixed.Start.Provenance == MeasureEndpointProvenance.Captured, "captured start kept");
        Require(mixed.End.Provenance == MeasureEndpointProvenance.NativeObservation, "native end kept");
    }

    private static void CheckSessionLifecycle()
    {
        var session = new MeasureSession();
        Require(session.State == MeasurePointState.Empty, "initial state");

        session.SetFirst(Capture(0, 0));
        Require(session.State == MeasurePointState.FirstHeld && session.HasFirst, "first held");

        session.SetSecond(Capture(300, 400));
        Require(session.State == MeasurePointState.Complete, "complete");
        Require(session.Completed is { DistanceMils: 500m }, "completed span");

        // A new first point starts a fresh measurement.
        long epoch = session.Epoch;
        session.SetFirst(Capture(1, 1));
        Require(session.State == MeasurePointState.FirstHeld && session.Completed is null, "restart");
        Require(session.Epoch > epoch, "epoch advanced");

        session.ClearFirst();
        Require(session.State == MeasurePointState.Empty && !session.HasFirst, "clear-first");
    }

    private static void CheckSupersededPickFencing()
    {
        var session = new MeasureSession();
        Guid stale = session.StartPickOperation();
        session.SetFirst(Capture(0, 0)); // Epoch advances: the pick is superseded.
        Require(!session.IsCurrentOperation(stale), "superseded pick not current");

        Guid live = session.StartPickOperation();
        Require(session.IsCurrentOperation(live), "current pick");

        session.Cancel();
        Require(!session.IsCurrentOperation(live), "cancel retires the pick");
        Require(session.RetirePickOperation(live), "retire known operation");
        Require(!session.RetirePickOperation(Guid.NewGuid()), "retire unknown operation is false");
    }

    private static void CheckRulerLifecycle()
    {
        var session = new MeasureSession();
        session.SetFirst(Capture(0, 0));
        session.SetSecond(Capture(100, 0));
        MeasureRuler first = session.KeepRuler();
        Require(first.Label == "M1" && session.Rulers.Count == 1, "first ruler");

        session.SetFirst(Capture(0, 0));
        session.SetSecond(Capture(0, 200));
        MeasureRuler second = session.KeepRuler("height");
        Require(second.Label == "height" && second.Revision > first.Revision, "revision chain");

        MeasureRuler moved = session.MoveRuler(
            first.Id, MeasureMath.Compute(Capture(0, 0), Capture(50, 0)));
        Require(moved.Span.DistanceMils == 50m && moved.Revision > second.Revision, "move revision");
        Require(session.Rulers.Count == 2, "move keeps count");

        Require(session.RemoveRuler(second.Id), "remove known ruler");
        Require(session.Rulers.Count == 1 && session.Rulers[0].Id == first.Id, "others untouched");
        Require(!session.RemoveRuler(Guid.NewGuid()), "remove unknown ruler is false");
    }

    private static void CheckRulerRecipeBuild()
    {
        DesignScene scene = EngineExamples.CreateBoard();
        var session = new MeasureSession();
        session.SetFirst(Capture(10, 10));
        session.SetSecond(Capture(210, 10));
        MeasureRuler ruler = session.KeepRuler("ruler-build");

        // Rulers publish through the shared overlay recipe path as dimension
        // drawings: display-only Measurement primitives, never copper.
        var recipe = new OverlayToolRecipe(
            "ruler-build",
            new OverlayToolAnchor.Board(0, 0),
            new OverlayToolShape.Dimension(
                ruler.Span.Start.Snapped.X, ruler.Span.Start.Snapped.Y,
                ruler.Span.End.Snapped.X, ruler.Span.End.Snapped.Y,
                DrawingDimensionKind.Aligned, ruler.Label),
            new OverlayToolStyle(255, 32, 112, 220, 2));
        Require(recipe.Validate() is [], "ruler recipe valid");
        DrawingGroup group = recipe.Build(scene, "tools-b-ruler-build");
        Require(group.Elements.Length == 1, "ruler builds one element");
        var drawings = new DrawingScene(scene.Identity.CaptureId, ruler.Revision, [group]);
        Require(drawings.Groups.Length == 1, "ruler scene holds the group");
    }

    private static void CheckExportText()
    {
        MeasuredSpan span = MeasureMath.Compute(
            Capture(0, 0), MeasureMath.NativeEndpoint(new DesignPoint(300m, 400m), "Pin", "DP_P"),
            Guid.NewGuid(), "session/design.brd");
        string text = MeasureMath.ExportText(span, millimeters: false);
        Require(text.Contains("Captured", StringComparison.Ordinal), "captured provenance exported");
        Require(text.Contains("NativeObservation", StringComparison.Ordinal), "native provenance exported");
        Require(text.Contains("Pin", StringComparison.Ordinal), "native kind exported");
        Require(text.Contains("500", StringComparison.Ordinal), "distance exported");
        Require(text.Contains("no copper changed", StringComparison.Ordinal), "zero-mutation statement");

        string recipe = MeasureMath.ExportCSharp(span);
        Require(recipe.Contains("display-only", StringComparison.Ordinal), "recipe disclaimer");
    }

    private static void CheckValidation()
    {
        Require(MeasureMath.ValidatePoint("P", 1_000_000m, -1_000_000m) is [], "envelope edge valid");
        Require(MeasureMath.ValidatePoint("P", 1_000_001m, 0) is not [], "envelope breach flagged");

        try
        {
            _ = MeasureMath.CaptureEndpoint(new DesignPoint(2_000_000m, 0), null);
            throw new InvalidOperationException("Out-of-envelope coordinate was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        try
        {
            _ = MeasureMath.CaptureEndpoint(new DesignPoint(0, 0), 0m);
            throw new InvalidOperationException("Zero grid step was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        var session = new MeasureSession();
        try
        {
            session.SetSecond(Capture(1, 1));
            throw new InvalidOperationException("Second point without first was accepted.");
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            session.KeepRuler();
            throw new InvalidOperationException("Ruler without completion was accepted.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static MeasureEndpoint Capture(decimal xMils, decimal yMils) =>
        MeasureMath.CaptureEndpoint(new DesignPoint(xMils, yMils), null);

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Measure check failed: " + what);
        }
    }
}
