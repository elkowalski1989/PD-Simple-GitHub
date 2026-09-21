using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.PcbTools.OverlayTools;

/// <summary>
/// Typed validation failure for one overlay-tool recipe field, with the
/// corrective step the PD tool page displays beside the blocked action.
/// </summary>
public sealed record OverlayToolRecipeError(string Field, string Message, string NextStep)
{
    public override string ToString() => $"{Field}: {Message} Next: {NextStep}";
}

/// <summary>Where one overlay-tool drawing anchors: an explicit board point or a captured object.</summary>
public abstract record OverlayToolAnchor
{
    private OverlayToolAnchor() { }

    /// <summary>Board-coordinate anchor in mils. Never a screen pixel position.</summary>
    public sealed record Board(decimal XMils, decimal YMils) : OverlayToolAnchor;

    /// <summary>Object-local anchor resolved through the Engine frame resolver at build time.</summary>
    public sealed record CapturedObject(SceneObjectReference Target, DrawingAnchor? Anchor = null) : OverlayToolAnchor;
}

/// <summary>One overlay-tool shape with explicit parameters in board mils or object-local mils.</summary>
public abstract record OverlayToolShape
{
    private OverlayToolShape() { }

    public sealed record Line(decimal X1Mils, decimal Y1Mils, decimal X2Mils, decimal Y2Mils) : OverlayToolShape;
    public sealed record Circle(decimal CenterXMils, decimal CenterYMils, decimal RadiusMils) : OverlayToolShape;
    public sealed record Ellipse(decimal CenterXMils, decimal CenterYMils, decimal RadiusXMils, decimal RadiusYMils) : OverlayToolShape;
    public sealed record Rectangle(decimal XMils, decimal YMils, decimal WidthMils, decimal HeightMils) : OverlayToolShape;
    public sealed record Polygon(ImmutableArray<(decimal X, decimal Y)> PointsMils) : OverlayToolShape;
    public sealed record Polyline(ImmutableArray<(decimal X, decimal Y)> PointsMils, bool Closed) : OverlayToolShape;
    public sealed record Text(decimal XMils, decimal YMils, string Content, double FontSizePx) : OverlayToolShape;
    public sealed record Marker(decimal XMils, decimal YMils, DrawingMarkerKind Kind, double SizePx) : OverlayToolShape;
    public sealed record Dimension(
        decimal X1Mils, decimal Y1Mils, decimal X2Mils, decimal Y2Mils,
        DrawingDimensionKind Kind, string? Label) : OverlayToolShape;
}

/// <summary>PD-owned presentation style for one overlay-tool drawing group.</summary>
public sealed record OverlayToolStyle(
    byte StrokeA, byte StrokeR, byte StrokeG, byte StrokeB,
    double StrokeWidthPx,
    byte? FillA = null, byte? FillR = null, byte? FillG = null, byte? FillB = null,
    decimal Opacity = 1m,
    int ZOrder = 200,
    bool IsVisible = true,
    DrawingHitTestPolicy HitTest = DrawingHitTestPolicy.None)
{
    public DrawingColor StrokeColor => new(StrokeA, StrokeR, StrokeG, StrokeB);
    public DrawingColor? FillColor =>
        FillA is { } a && FillR is { } r && FillG is { } g && FillB is { } b
            ? new DrawingColor(a, r, g, b)
            : null;
}

/// <summary>
/// One validated overlay-tool drawing recipe. PD owns the recipe text and the
/// validation policy; Engine owns frame resolution, projection, clipping, and
/// the live overlay lease. Building never performs native I/O and never
/// creates copper: the result is a display-only <see cref="DrawingGroup"/>.
/// </summary>
public sealed record OverlayToolRecipe(
    string ElementId,
    OverlayToolAnchor Anchor,
    OverlayToolShape Shape,
    OverlayToolStyle Style)
{
    /// <summary>Validates every field without touching a scene. Empty means buildable.</summary>
    public ImmutableArray<OverlayToolRecipeError> Validate()
    {
        var errors = new List<OverlayToolRecipeError>();
        if (string.IsNullOrWhiteSpace(ElementId))
        {
            errors.Add(new(nameof(ElementId), "An element id is required.", "Enter a short id such as note-1."));
        }
        else if (ElementId.Any(char.IsWhiteSpace))
        {
            errors.Add(new(nameof(ElementId), "An element id cannot contain whitespace.", "Use dashes, e.g. note-1."));
        }

        switch (Anchor)
        {
            case OverlayToolAnchor.Board board:
                RequireFinite(nameof(OverlayToolAnchor.Board.XMils), board.XMils, errors);
                RequireFinite(nameof(OverlayToolAnchor.Board.YMils), board.YMils, errors);
                break;
            case OverlayToolAnchor.CapturedObject captured:
                if (captured.Target.ObjectId.Value is not { Length: > 0 })
                {
                    errors.Add(new("Target", "A captured object reference is required.",
                        "Pick an object from the captured scene first."));
                }

                if (captured.Target.CaptureId == Guid.Empty)
                {
                    errors.Add(new("Target", "The captured object belongs to no scene.",
                        "Reacquire the scene, then pick the object again."));
                }

                break;
            default:
                errors.Add(new("Anchor", "An anchor is required.", "Choose a board point or a captured object."));
                break;
        }

        ValidateShape(errors);

        if (Style.Opacity is < 0m or > 1m)
        {
            errors.Add(new("Opacity", "Opacity must be between 0 and 1.", "Enter a value such as 0.85."));
        }

        if (Style.StrokeWidthPx <= 0 || !double.IsFinite(Style.StrokeWidthPx))
        {
            errors.Add(new("StrokeWidth", "Stroke width must be a positive pixel size.", "Enter a value such as 2."));
        }

        return [.. errors];
    }

    /// <summary>
    /// Builds one bounded Engine drawing group for the supplied immutable scene.
    /// Throws <see cref="InvalidOperationException"/> carrying the first
    /// validation error when the recipe is not buildable.
    /// </summary>
    public DrawingGroup Build(DesignScene scene, string groupId)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ImmutableArray<OverlayToolRecipeError> errors = Validate();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException("The overlay recipe is not buildable: " + errors[0]);
        }

        DrawingBuilder builder = Anchor switch
        {
            OverlayToolAnchor.Board board =>
                new DrawingFactory(scene).At(new BoardPoint(board.XMils.Mils(), board.YMils.Mils())),
            OverlayToolAnchor.CapturedObject captured =>
                new DrawingFactory(scene).On(captured.Target, captured.Anchor),
            _ => throw new InvalidOperationException("The overlay recipe has no anchor."),
        };

        builder = builder.Named(groupId).GroupZOrder(Style.ZOrder).GroupVisible(Style.IsVisible);
        builder = AddShape(builder, scene, groupId);
        return builder.Build();
    }

    /// <summary>
    /// Exports compilable C# that rebuilds this exact drawing through the
    /// public PD/Engine drawing API: a static <c>Rebuild</c> method that
    /// reconstructs this recipe and calls <c>Build(scene, groupId)</c>.
    /// The rebuilt drawing is display-only and performs no native work.
    /// Object-local Engine anchors that are not one of the well-known
    /// <c>DrawingAnchor</c> singletons cannot be spelled from PD-side facts
    /// and are refused instead of silently approximated.
    /// </summary>
    public string ExportCSharp()
    {
        var text = new StringBuilder();
        text.AppendLine("// Live overlay recipe: display-only, never native copper.");
        text.AppendLine("// Generated rebuilding code: compiles against PD.PcbTools and the staged Engine packages.");
        text.AppendLine("// Rebuild(scene, groupId) reconstructs this exact recipe and builds it; no native work is performed.");
        text.AppendLine($"// element: {ElementId}");
        text.AppendLine(Anchor switch
        {
            OverlayToolAnchor.Board board =>
                $"// anchor: board point ({Format(board.XMils)}, {Format(board.YMils)}) mils",
            OverlayToolAnchor.CapturedObject captured =>
                $"// anchor: captured object '{captured.Target.ObjectId.Value}' (capture {captured.Target.CaptureId})",
            _ => "// anchor: missing",
        });
        text.AppendLine($"// shape: {DescribeShape()}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"// style: stroke argb({Style.StrokeA},{Style.StrokeR},{Style.StrokeG},{Style.StrokeB}) " +
            $"{Format(Style.StrokeWidthPx)}px, opacity {Format(Style.Opacity)}, z {Style.ZOrder}, " +
            $"visible {Format(Style.IsVisible)}, hit-test {Style.HitTest}");
        text.AppendLine("using System;");
        text.AppendLine("using CircuitHub.AllegroBridge.Engine.Design;");
        text.AppendLine("using CircuitHub.AllegroBridge.Engine.Drawing;");
        text.AppendLine("using CircuitHub.AllegroBridge.Engine.Scenes;");
        text.AppendLine("using PD.PcbTools.OverlayTools;");
        text.AppendLine();
        text.AppendLine("public static class OverlayRecipeExport");
        text.AppendLine("{");
        text.AppendLine("    public static DrawingGroup Rebuild(DesignScene scene, string groupId)");
        text.AppendLine("    {");
        text.AppendLine("        ArgumentNullException.ThrowIfNull(scene);");
        text.AppendLine("        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);");
        text.AppendLine("        var recipe = new OverlayToolRecipe(");
        text.AppendLine($"            {Literal(ElementId)},");
        text.AppendLine($"            {EmitAnchor()},");
        text.AppendLine($"            {EmitShape()},");
        text.AppendLine($"            {EmitStyle()});");
        text.AppendLine("        return recipe.Build(scene, groupId);");
        text.AppendLine("    }");
        text.AppendLine("}");
        return text.ToString();
    }

    private string EmitAnchor() => Anchor switch
    {
        OverlayToolAnchor.Board board =>
            $"new OverlayToolAnchor.Board({Mils(board.XMils)}, {Mils(board.YMils)})",
        OverlayToolAnchor.CapturedObject captured =>
            $"new OverlayToolAnchor.CapturedObject(scene.ReferenceTo(new SceneObjectId({Literal(captured.Target.ObjectId.Value)}))" +
            (captured.Anchor is null ? string.Empty : $", {EmitObjectAnchor(captured.Anchor)}") + ")",
        _ => throw new InvalidOperationException("The overlay recipe has no anchor to export."),
    };

    private static string EmitObjectAnchor(DrawingAnchor anchor)
    {
        if (ReferenceEquals(anchor, DrawingAnchor.ObjectOrigin)) return "DrawingAnchor.ObjectOrigin";
        if (ReferenceEquals(anchor, DrawingAnchor.BoundsCenter)) return "DrawingAnchor.BoundsCenter";
        if (ReferenceEquals(anchor, DrawingAnchor.BoundsMinimum)) return "DrawingAnchor.BoundsMinimum";
        if (ReferenceEquals(anchor, DrawingAnchor.BoundsMaximum)) return "DrawingAnchor.BoundsMaximum";
        if (ReferenceEquals(anchor, DrawingAnchor.BoundsMinimumXMaximumY)) return "DrawingAnchor.BoundsMinimumXMaximumY";
        if (ReferenceEquals(anchor, DrawingAnchor.BoundsMaximumXMinimumY)) return "DrawingAnchor.BoundsMaximumXMinimumY";
        throw new InvalidOperationException(
            "The overlay recipe carries a runtime-computed object anchor that cannot be spelled from PD-side facts; rebuild it through the Engine frame resolver instead.");
    }

    private string EmitShape() => Shape switch
    {
        OverlayToolShape.Line line =>
            $"new OverlayToolShape.Line({Mils(line.X1Mils)}, {Mils(line.Y1Mils)}, {Mils(line.X2Mils)}, {Mils(line.Y2Mils)})",
        OverlayToolShape.Circle circle =>
            $"new OverlayToolShape.Circle({Mils(circle.CenterXMils)}, {Mils(circle.CenterYMils)}, {Mils(circle.RadiusMils)})",
        OverlayToolShape.Ellipse ellipse =>
            $"new OverlayToolShape.Ellipse({Mils(ellipse.CenterXMils)}, {Mils(ellipse.CenterYMils)}, {Mils(ellipse.RadiusXMils)}, {Mils(ellipse.RadiusYMils)})",
        OverlayToolShape.Rectangle rect =>
            $"new OverlayToolShape.Rectangle({Mils(rect.XMils)}, {Mils(rect.YMils)}, {Mils(rect.WidthMils)}, {Mils(rect.HeightMils)})",
        OverlayToolShape.Polygon polygon =>
            $"new OverlayToolShape.Polygon([{string.Join(", ", polygon.PointsMils.Select(p => $"({Mils(p.X)}, {Mils(p.Y)})"))}])",
        OverlayToolShape.Polyline polyline =>
            $"new OverlayToolShape.Polyline([{string.Join(", ", polyline.PointsMils.Select(p => $"({Mils(p.X)}, {Mils(p.Y)})"))}], {Bool(polyline.Closed)})",
        OverlayToolShape.Text text =>
            $"new OverlayToolShape.Text({Mils(text.XMils)}, {Mils(text.YMils)}, {Literal(text.Content)}, {Pixels(text.FontSizePx)})",
        OverlayToolShape.Marker marker =>
            $"new OverlayToolShape.Marker({Mils(marker.XMils)}, {Mils(marker.YMils)}, DrawingMarkerKind.{marker.Kind}, {Pixels(marker.SizePx)})",
        OverlayToolShape.Dimension dim =>
            $"new OverlayToolShape.Dimension({Mils(dim.X1Mils)}, {Mils(dim.Y1Mils)}, {Mils(dim.X2Mils)}, {Mils(dim.Y2Mils)}, DrawingDimensionKind.{dim.Kind}, {(dim.Label is null ? "null" : Literal(dim.Label))})",
        _ => throw new InvalidOperationException("The overlay recipe has no shape to export."),
    };

    private string EmitStyle()
    {
        string fill = Style.FillA is { } a && Style.FillR is { } r && Style.FillG is { } g && Style.FillB is { } b
            ? $"{a}, {r}, {g}, {b}"
            : "null, null, null, null";
        return $"new OverlayToolStyle({Style.StrokeA}, {Style.StrokeR}, {Style.StrokeG}, {Style.StrokeB}, " +
            $"{Pixels(Style.StrokeWidthPx)}, {fill}, {Mils(Style.Opacity)}, {Style.ZOrder}, " +
            $"{Bool(Style.IsVisible)}, DrawingHitTestPolicy.{Style.HitTest})";
    }

    private DrawingBuilder AddShape(DrawingBuilder builder, DesignScene scene, string groupId)
    {
        // Coordinates are object-local mils after Engine frame resolution, so a
        // board anchor lands on identity while an object anchor stays local.
        static LocalPoint P(decimal x, decimal y) => new(x.Mils(), y.Mils());
        DrawingBuilder styled(DrawingBuilder b) => ApplyStyle(b);

        return Shape switch
        {
            OverlayToolShape.Line line =>
                styled(builder.Line(P(line.X1Mils, line.Y1Mils), P(line.X2Mils, line.Y2Mils)).ElementId(ElementId)),
            OverlayToolShape.Circle circle =>
                styled(builder.Circle(P(circle.CenterXMils, circle.CenterYMils), circle.RadiusMils.Mils())
                    .ElementId(ElementId)),
            OverlayToolShape.Ellipse ellipse =>
                styled(builder.Ellipse(
                        P(ellipse.CenterXMils, ellipse.CenterYMils),
                        ellipse.RadiusXMils.Mils(), ellipse.RadiusYMils.Mils())
                    .ElementId(ElementId)),
            OverlayToolShape.Rectangle rect =>
                styled(builder.Rectangle(rect.XMils.Mils(), rect.YMils.Mils(), rect.WidthMils.Mils(), rect.HeightMils.Mils())
                    .ElementId(ElementId)),
            OverlayToolShape.Polygon polygon =>
                styled(builder.Polygon(polygon.PointsMils.Select(p => P(p.X, p.Y))).ElementId(ElementId)),
            OverlayToolShape.Polyline polyline =>
                styled(builder.Polyline(polyline.PointsMils.Select(p => P(p.X, p.Y)), polyline.Closed)
                    .ElementId(ElementId)),
            OverlayToolShape.Text text =>
                styled(builder.Text(P(text.XMils, text.YMils), text.Content, new PhysicalPixels(text.FontSizePx))
                    .ElementId(ElementId)),
            OverlayToolShape.Marker marker =>
                styled(builder.Marker(P(marker.XMils, marker.YMils), marker.Kind, new PhysicalPixels(marker.SizePx))
                    .ElementId(ElementId)),
            OverlayToolShape.Dimension dim =>
                styled(builder.Measurement(P(dim.X1Mils, dim.Y1Mils), P(dim.X2Mils, dim.Y2Mils), dim.Label,
                        kind: dim.Kind).ElementId(ElementId)),
            _ => throw new InvalidOperationException("The overlay recipe has no shape."),
        };
    }

    private DrawingBuilder ApplyStyle(DrawingBuilder builder)
    {
        builder = builder.Stroke(Style.StrokeColor, new PhysicalPixels(Style.StrokeWidthPx));
        builder = Style.FillColor is { } fill ? builder.Fill(fill) : builder.NoFill();
        builder = builder.Opacity(Style.Opacity).ZOrder(Style.ZOrder)
            .Visible(Style.IsVisible).HitTest(Style.HitTest);
        return builder;
    }

    private void ValidateShape(List<OverlayToolRecipeError> errors)
    {
        switch (Shape)
        {
            case OverlayToolShape.Line line:
                RequireFinite("X1", line.X1Mils, errors);
                RequireFinite("Y1", line.Y1Mils, errors);
                RequireFinite("X2", line.X2Mils, errors);
                RequireFinite("Y2", line.Y2Mils, errors);
                if (line.X1Mils == line.X2Mils && line.Y1Mils == line.Y2Mils)
                {
                    errors.Add(new("Shape", "A line needs two distinct endpoints.", "Move one endpoint."));
                }

                break;
            case OverlayToolShape.Circle circle:
                RequireFinite("CenterX", circle.CenterXMils, errors);
                RequireFinite("CenterY", circle.CenterYMils, errors);
                if (!IsPositiveFinite(circle.RadiusMils))
                {
                    errors.Add(new("Radius", "A circle needs a positive finite radius.",
                        "Enter a radius in mils, e.g. 25."));
                }

                break;
            case OverlayToolShape.Rectangle rect:
                RequireFinite("X", rect.XMils, errors);
                RequireFinite("Y", rect.YMils, errors);
                if (!IsPositiveFinite(rect.WidthMils) || !IsPositiveFinite(rect.HeightMils))
                {
                    errors.Add(new("Size", "A rectangle needs positive finite width and height.",
                        "Enter sizes in mils, e.g. 100 x 60."));
                }

                break;
            case OverlayToolShape.Polygon polygon:
                if (polygon.PointsMils.Length < 3)
                {
                    errors.Add(new("Points", "A polygon needs at least three points.",
                        "Add points in X,Y mils, one per line."));
                }
                else if (polygon.PointsMils.Length > DrawingGroup.MaximumPoints)
                {
                    errors.Add(new("Points",
                        $"A polygon holds at most {DrawingGroup.MaximumPoints} points.",
                        "Split the region into smaller polygons."));
                }

                foreach ((decimal x, decimal y) in polygon.PointsMils)
                {
                    RequireFinite("PointX", x, errors);
                    RequireFinite("PointY", y, errors);
                }

                break;
            case OverlayToolShape.Ellipse ellipse:
                RequireFinite("CenterX", ellipse.CenterXMils, errors);
                RequireFinite("CenterY", ellipse.CenterYMils, errors);
                if (!IsPositiveFinite(ellipse.RadiusXMils) || !IsPositiveFinite(ellipse.RadiusYMils))
                {
                    errors.Add(new("Radii", "An ellipse needs positive finite x and y radii.",
                        "Enter radii in mils, e.g. 60 x 30."));
                }

                break;
            case OverlayToolShape.Polyline polyline:
                if (polyline.PointsMils.Length < 2)
                {
                    errors.Add(new("Points", "A polyline needs at least two points.",
                        "Add points in X,Y mils, one per line."));
                }
                else if (polyline.PointsMils.Length > DrawingGroup.MaximumPoints)
                {
                    errors.Add(new("Points",
                        $"A polyline holds at most {DrawingGroup.MaximumPoints} points.",
                        "Split the path into shorter polylines."));
                }

                foreach ((decimal x, decimal y) in polyline.PointsMils)
                {
                    RequireFinite("PointX", x, errors);
                    RequireFinite("PointY", y, errors);
                }

                break;
            case OverlayToolShape.Text text:
                RequireFinite("X", text.XMils, errors);
                RequireFinite("Y", text.YMils, errors);
                if (string.IsNullOrWhiteSpace(text.Content))
                {
                    errors.Add(new("Content", "Text needs content.", "Enter the label to display."));
                }

                if (text.FontSizePx <= 0 || !double.IsFinite(text.FontSizePx))
                {
                    errors.Add(new("FontSize", "Text needs a positive font size.", "Enter a size such as 24."));
                }

                break;
            case OverlayToolShape.Marker marker:
                RequireFinite("X", marker.XMils, errors);
                RequireFinite("Y", marker.YMils, errors);
                if (!Enum.IsDefined(marker.Kind))
                {
                    errors.Add(new("Kind", "Choose a marker kind.", "Pick dot, cross, diamond, square, or triangle."));
                }

                if (marker.SizePx <= 0 || !double.IsFinite(marker.SizePx))
                {
                    errors.Add(new("Size", "A marker needs a positive size.", "Enter a size such as 24."));
                }

                break;
            case OverlayToolShape.Dimension dim:
                RequireFinite("X1", dim.X1Mils, errors);
                RequireFinite("Y1", dim.Y1Mils, errors);
                RequireFinite("X2", dim.X2Mils, errors);
                RequireFinite("Y2", dim.Y2Mils, errors);
                if (!Enum.IsDefined(dim.Kind))
                {
                    errors.Add(new("Kind", "Choose a dimension kind.", "Pick aligned, horizontal, or vertical."));
                }

                if (dim.X1Mils == dim.X2Mils && dim.Y1Mils == dim.Y2Mils)
                {
                    errors.Add(new("Shape", "A dimension needs two distinct endpoints.", "Move one endpoint."));
                }

                break;
            default:
                errors.Add(new("Shape", "A shape is required.", "Choose line, circle, ellipse, rectangle, polygon, polyline, text, marker, or dimension."));
                break;
        }
    }

    private string DescribeShape() => Shape switch
    {
        OverlayToolShape.Line line =>
            $"line ({Format(line.X1Mils)},{Format(line.Y1Mils)})-({Format(line.X2Mils)},{Format(line.Y2Mils)}) mils",
        OverlayToolShape.Circle circle =>
            $"circle center ({Format(circle.CenterXMils)},{Format(circle.CenterYMils)}) r {Format(circle.RadiusMils)} mils",
        OverlayToolShape.Ellipse ellipse =>
            $"ellipse center ({Format(ellipse.CenterXMils)},{Format(ellipse.CenterYMils)}) rx {Format(ellipse.RadiusXMils)} ry {Format(ellipse.RadiusYMils)} mils",
        OverlayToolShape.Rectangle rect =>
            $"rectangle ({Format(rect.XMils)},{Format(rect.YMils)}) {Format(rect.WidthMils)}x{Format(rect.HeightMils)} mils",
        OverlayToolShape.Polygon polygon => $"polygon {polygon.PointsMils.Length} points (mils)",
        OverlayToolShape.Polyline polyline => $"{(polyline.Closed ? "closed" : "open")} polyline {polyline.PointsMils.Length} points (mils)",
        OverlayToolShape.Text text => $"text '{text.Content}' at ({Format(text.XMils)},{Format(text.YMils)}) mils",
        OverlayToolShape.Marker marker =>
            $"marker {marker.Kind} at ({Format(marker.XMils)},{Format(marker.YMils)}) mils",
        OverlayToolShape.Dimension dim =>
            $"{dim.Kind} dimension ({Format(dim.X1Mils)},{Format(dim.Y1Mils)})-({Format(dim.X2Mils)},{Format(dim.Y2Mils)}) mils",
        _ => "missing",
    };

    private static void RequireFinite(string field, decimal value, List<OverlayToolRecipeError> errors)
    {
        // decimal is always finite; guard the plausible board envelope instead.
        if (value is < -1_000_000m or > 1_000_000m)
        {
            errors.Add(new(field, "The coordinate is outside the plausible board envelope (±1,000,000 mils).",
                "Enter board coordinates in mils."));
        }
    }

    private static bool IsPositiveFinite(decimal value) => value > 0 && value <= 1_000_000m;

    private static string Format(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Format(bool value) => value ? "yes" : "no";

    private static string Mils(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture) + "m";

    private static string Pixels(double value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Literal(string value) => JsonSerializer.Serialize(value);
}
