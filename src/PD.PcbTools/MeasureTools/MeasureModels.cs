using System.Globalization;
using System.Text;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Interactions;

namespace PD.PcbTools.MeasureTools;

/// <summary>Where one measured endpoint came from. Captured points and native
/// click observations are never conflated in results.</summary>
public enum MeasureEndpointProvenance
{
    Captured,
    NativeObservation,
}

/// <summary>One measured endpoint in canonical board mils. Snapped holds the
/// value used for computation; Raw is retained so the distinction stays
/// visible whenever snapping moved the point.</summary>
public sealed record MeasureEndpoint(
    DesignPoint Raw,
    DesignPoint Snapped,
    bool WasSnapped,
    string SnapDescription,
    MeasureEndpointProvenance Provenance,
    string? ObjectKind,
    string? NetName)
{
    public override string ToString() =>
        WasSnapped
            ? $"{Format(Snapped)} mils (snapped from {Format(Raw)}; {SnapDescription})"
            : $"{Format(Raw)} mils ({SnapDescription})";

    private static string Format(DesignPoint point) =>
        string.Create(CultureInfo.InvariantCulture, $"({point.X:0.###}, {point.Y:0.###})");
}

/// <summary>One computed two-point span. All values derive from the snapped
/// endpoints; the raw endpoints stay on the record for audit.</summary>
public sealed record MeasuredSpan(
    MeasureEndpoint Start,
    MeasureEndpoint End,
    Guid? CaptureId,
    string? Document,
    decimal DeltaXMils,
    decimal DeltaYMils,
    decimal DistanceMils,
    decimal DistanceMillimeters,
    decimal? AngleDegrees,
    bool IsDegenerate);

/// <summary>Typed validation failure for one measurement field, with the
/// corrective step the PD tool page displays beside the blocked action.</summary>
public sealed record MeasureError(string Field, string Message, string NextStep)
{
    public override string ToString() => $"{Field}: {Message} Next: {NextStep}";
}

/// <summary>
/// PD-owned captured/native measurement math. Pure computation over canonical
/// Engine board points: no native I/O, no copper mutation, no screen-pixel
/// arithmetic. Grid snapping reuses Engine <see cref="DesignSnapping"/>; the
/// same grid policy the captured interaction session applies to gestures.
/// </summary>
public static class MeasureMath
{
    public const decimal CoordinateLimitMils = 1_000_000m;
    public const decimal MaximumGridStepMils = 100_000_000m;

    /// <summary>Builds a captured endpoint from a raw board point.</summary>
    public static MeasureEndpoint CaptureEndpoint(
        DesignPoint raw,
        decimal? gridStepMils,
        string? objectId = null)
    {
        RequireCoordinate(nameof(raw), raw.X);
        RequireCoordinate(nameof(raw), raw.Y);

        DesignPoint snapped = raw;
        bool wasSnapped = false;
        string description = "no snap";

        if (gridStepMils is { } step)
        {
            RequireGridStep(step);
            SnapCandidate candidate = DesignSnapping.Grid(raw, new Length(step));
            snapped = candidate.Position;
            wasSnapped = snapped != raw;
            description = wasSnapped
                ? $"grid {Format(step)} mil"
                : $"grid {Format(step)} mil (already on grid)";
        }

        return new MeasureEndpoint(
            raw, snapped, wasSnapped, description,
            MeasureEndpointProvenance.Captured, objectId, null);
    }

    /// <summary>Builds a native click-observation endpoint. Positions are the
    /// canonical Engine board mils the native pick normalized; the object
    /// kind/net are native authority, never visual inference.</summary>
    public static MeasureEndpoint NativeEndpoint(
        DesignPoint position,
        string objectKind,
        string? netName)
    {
        RequireCoordinate(nameof(position), position.X);
        RequireCoordinate(nameof(position), position.Y);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKind);

        return new MeasureEndpoint(
            position, position, false,
            "native object " + objectKind.Trim(),
            MeasureEndpointProvenance.NativeObservation,
            objectKind.Trim(), string.IsNullOrWhiteSpace(netName) ? null : netName.Trim());
    }

    /// <summary>Computes deltas, distance, and angle for two endpoints.</summary>
    public static MeasuredSpan Compute(
        MeasureEndpoint start,
        MeasureEndpoint end,
        Guid? captureId = null,
        string? document = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);

        decimal dx = end.Snapped.X - start.Snapped.X;
        decimal dy = end.Snapped.Y - start.Snapped.Y;
        Length distance = start.Snapped.DistanceTo(end.Snapped);
        bool degenerate = distance.Mils == 0;

        decimal? angle = degenerate
            ? null
            : (decimal)(Math.Atan2((double)dy, (double)dx) * 180.0 / Math.PI);

        return new MeasuredSpan(
            start, end, captureId, document,
            dx, dy, distance.Mils, distance.In(LengthUnit.Millimeters),
            angle, degenerate);
    }

    /// <summary>Validates raw coordinate inputs without touching a scene.</summary>
    public static System.Collections.Immutable.ImmutableArray<MeasureError> ValidatePoint(
        string field, decimal xMils, decimal yMils)
    {
        var errors = new List<MeasureError>();
        if (!IsInEnvelope(xMils) || !IsInEnvelope(yMils))
        {
            errors.Add(new(field,
                "The coordinate is outside the plausible board envelope (±1,000,000 mils).",
                "Enter board coordinates in mils."));
        }

        return [.. errors];
    }

    /// <summary>Deterministic human-readable measurement text for copy/export.</summary>
    public static string ExportText(MeasuredSpan span, bool millimeters)
    {
        ArgumentNullException.ThrowIfNull(span);
        var text = new StringBuilder();
        text.AppendLine("PD pick/measure/ruler — display-only measurement, no copper changed.");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"start: {span.Start} [{span.Start.Provenance}{Detail(span.Start)}]"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"end: {span.End} [{span.End.Provenance}{Detail(span.End)}]"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"delta X: {Format(span.DeltaXMils)} mil; delta Y: {Format(span.DeltaYMils)} mil"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"distance: {Format(span.DistanceMils)} mil ({span.DistanceMillimeters:0.######} mm)"));
        text.AppendLine(span.AngleDegrees is { } angle
            ? string.Create(CultureInfo.InvariantCulture,
                $"angle: {angle:0.###} degrees (atan2 of delta Y over delta X)")
            : "angle: undefined (both endpoints coincide)");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"capture: {(span.CaptureId?.ToString() ?? "none")}; document: {span.Document ?? "none"}"));
        text.Append("units: ");
        text.AppendLine(millimeters ? "millimeters (primary display)" : "mils (primary display)");
        return text.ToString();

        static string Detail(MeasureEndpoint endpoint)
        {
            if (endpoint.ObjectKind is null)
            {
                return string.Empty;
            }

            return endpoint.NetName is null
                ? "; " + endpoint.ObjectKind
                : "; " + endpoint.ObjectKind + " net " + endpoint.NetName;
        }
    }

    /// <summary>Public C# recipe rebuilding this exact span. Documentation only.</summary>
    public static string ExportCSharp(MeasuredSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        var text = new StringBuilder();
        text.AppendLine("// Pick/measure recipe: display-only, never native copper.");
        text.AppendLine("// Positions resolve through CircuitHub.AllegroBridge.Engine.Design.");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"// start ({Format(span.Start.Snapped.X)}, {Format(span.Start.Snapped.Y)}) mils [{span.Start.Provenance}]"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"// end ({Format(span.End.Snapped.X)}, {Format(span.End.Snapped.Y)}) mils [{span.End.Provenance}]"));
        string angleText = span.AngleDegrees is { } angle
            ? string.Create(CultureInfo.InvariantCulture, $"{angle:0.###} deg")
            : "undefined";
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"// distance {Format(span.DistanceMils)} mils; angle {angleText}"));
        return text.ToString();
    }

    public static string FormatDistance(MeasuredSpan span, bool millimeters) =>
        millimeters
            ? $"{span.DistanceMillimeters:0.######} mm"
            : $"{Format(span.DistanceMils)} mil";

    private static void RequireCoordinate(string field, decimal value)
    {
        if (!IsInEnvelope(value))
        {
            throw new ArgumentOutOfRangeException(field,
                "The coordinate is outside the plausible board envelope (±1,000,000 mils).");
        }
    }

    private static void RequireGridStep(decimal step)
    {
        if (step <= 0 || step > MaximumGridStepMils)
        {
            throw new ArgumentOutOfRangeException(nameof(step),
                "Grid step must be positive and within the Engine snap envelope.");
        }
    }

    private static bool IsInEnvelope(decimal value) =>
        value is >= -CoordinateLimitMils and <= CoordinateLimitMils;

    private static string Format(decimal value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
