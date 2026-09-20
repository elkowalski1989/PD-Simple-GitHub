using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools.OverlayTools;

internal static class OverlayRecipeChecks
{
    private static readonly OverlayToolStyle Style = new(255, 20, 120, 220, 2);

    internal static void Run()
    {
        DesignScene scene = EngineExamples.CreateBoard();

        CheckBoardAnchoredShapes(scene);
        CheckObjectAnchor(scene);
        CheckValidation();
        CheckRecipeExport();
    }

    private static void CheckBoardAnchoredShapes(DesignScene scene)
    {
        var recipes = new OverlayToolRecipe[]
        {
            new("line-1", new OverlayToolAnchor.Board(0, 0),
                new OverlayToolShape.Line(0, 0, 100, 50), Style),
            new("circle-1", new OverlayToolAnchor.Board(10, 20),
                new OverlayToolShape.Circle(10, 20, 25), Style),
            new("ellipse-1", new OverlayToolAnchor.Board(10, 20),
                new OverlayToolShape.Ellipse(10, 20, 60, 30), Style),
            new("polyline-1", new OverlayToolAnchor.Board(0, 0),
                new OverlayToolShape.Polyline([(0, 0), (100, 0), (100, 60)], false), Style),
            new("polyline-closed-1", new OverlayToolAnchor.Board(0, 0),
                new OverlayToolShape.Polyline([(0, 0), (100, 0), (100, 60)], true), Style),
            new("rect-1", new OverlayToolAnchor.Board(-5, -5),
                new OverlayToolShape.Rectangle(-5, -5, 100, 60), Style),
            new("poly-1", new OverlayToolAnchor.Board(0, 0),
                new OverlayToolShape.Polygon([(0, 0), (100, 0), (100, 60), (0, 60)]), Style),
            new("text-1", new OverlayToolAnchor.Board(5, 5),
                new OverlayToolShape.Text(5, 5, "note", 24), Style),
            new("marker-1", new OverlayToolAnchor.Board(5, 5),
                new OverlayToolShape.Marker(5, 5, DrawingMarkerKind.Cross, 24), Style),
            new("dim-1", new OverlayToolAnchor.Board(0, 0),
                new OverlayToolShape.Dimension(0, 0, 200, 0, DrawingDimensionKind.Aligned, "200 mils"), Style),
        };

        foreach (OverlayToolRecipe recipe in recipes)
        {
            if (recipe.Validate() is not [])
            {
                throw new InvalidOperationException($"Valid recipe failed validation: {recipe.ElementId}");
            }

            DrawingGroup group = recipe.Build(scene, "tools-b-" + recipe.ElementId);
            if (group.Elements.Length != 1)
            {
                throw new InvalidOperationException($"Recipe built {group.Elements.Length} elements, expected 1.");
            }

            var drawings = new DrawingScene(scene.Identity.CaptureId, 1, [group]);
            if (drawings.CaptureId != scene.Identity.CaptureId || drawings.Groups.Length != 1)
            {
                throw new InvalidOperationException("The drawing scene did not retain the built group.");
            }
        }
    }

    private static void CheckObjectAnchor(DesignScene scene)
    {
        // Resolve one real captured object so the object-anchor path binds
        // through the Engine frame resolver instead of a fabricated id.
        SceneObjectReference? target = FindFirstObject(scene);
        if (target is null)
        {
            throw new InvalidOperationException("The synthetic board exposes no object to anchor.");
        }

        var recipe = new OverlayToolRecipe(
            "obj-note",
            new OverlayToolAnchor.CapturedObject(target.Value),
            new OverlayToolShape.Marker(0, 0, DrawingMarkerKind.Dot, 12),
            Style);
        if (recipe.Validate() is not [])
        {
            throw new InvalidOperationException("Valid object-anchored recipe failed validation.");
        }

        DrawingGroup group = recipe.Build(scene, "tools-b-obj");
        if (group.Elements.Length != 1)
        {
            throw new InvalidOperationException("Object-anchored build produced no element.");
        }
    }

    private static void CheckValidation()
    {
        // Degenerate line.
        var degenerate = new OverlayToolRecipe("x", new OverlayToolAnchor.Board(0, 0),
            new OverlayToolShape.Line(1, 1, 1, 1), Style);
        RequireError(degenerate, "Shape");

        // Zero radius.
        var zeroRadius = new OverlayToolRecipe("x", new OverlayToolAnchor.Board(0, 0),
            new OverlayToolShape.Circle(0, 0, 0), Style);
        RequireError(zeroRadius, "Radius");

        // Non-positive ellipse radii.
        var flatEllipse = new OverlayToolRecipe("x", new OverlayToolAnchor.Board(0, 0),
            new OverlayToolShape.Ellipse(0, 0, 60, 0), Style);
        RequireError(flatEllipse, "Radii");

        // Single-point polyline.
        var dot = new OverlayToolRecipe("x", new OverlayToolAnchor.Board(0, 0),
            new OverlayToolShape.Polyline([(0, 0)], false), Style);
        RequireError(dot, "Points");

        // Negative rectangle size.
        var negative = new OverlayToolRecipe("x", new OverlayToolAnchor.Board(0, 0),
            new OverlayToolShape.Rectangle(0, 0, -5, 4), Style);
        RequireError(negative, "Size");

        // Two-point polygon.
        var thin = new OverlayToolRecipe("x", new OverlayToolAnchor.Board(0, 0),
            new OverlayToolShape.Polygon([(0, 0), (1, 1)]), Style);
        RequireError(thin, "Points");

        // Blank text.
        var blank = new OverlayToolRecipe("x", new OverlayToolAnchor.Board(0, 0),
            new OverlayToolShape.Text(0, 0, "  ", 24), Style);
        RequireError(blank, "Content");

        // Bad opacity.
        var opaque = new OverlayToolRecipe("x", new OverlayToolAnchor.Board(0, 0),
            new OverlayToolShape.Line(0, 0, 1, 1), Style with { Opacity = 2m });
        RequireError(opaque, "Opacity");

        // Whitespace element id.
        var spaced = new OverlayToolRecipe("has space", new OverlayToolAnchor.Board(0, 0),
            new OverlayToolShape.Line(0, 0, 1, 1), Style);
        RequireError(spaced, nameof(OverlayToolRecipe.ElementId));

        // Empty object reference.
        var emptyRef = new OverlayToolRecipe("x",
            new OverlayToolAnchor.CapturedObject(new SceneObjectReference(Guid.NewGuid(), new SceneObjectId(string.Empty))),
            new OverlayToolShape.Line(0, 0, 1, 1), Style);
        RequireError(emptyRef, "Target");

        // Build throws on invalid recipes.
        try
        {
            degenerate.Build(EngineExamples.CreateBoard(), "g");
            throw new InvalidOperationException("Build accepted a degenerate recipe.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void CheckRecipeExport()
    {
        var recipe = new OverlayToolRecipe("note-1", new OverlayToolAnchor.Board(10, 20),
            new OverlayToolShape.Text(10, 20, "review", 24), Style);
        string text = recipe.ExportCSharp();
        foreach (string required in new[] { "note-1", "10", "20", "review", "display-only" })
        {
            if (!text.Contains(required, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Recipe export omits '{required}'.");
            }
        }

        if (text.Contains("axl", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("native", StringComparison.OrdinalIgnoreCase) && text.Contains("edit", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Recipe export must not imply native editing.");
        }
    }

    private static SceneObjectReference? FindFirstObject(DesignScene scene)
    {
        foreach (ComponentObject component in scene.Components.Items)
        {
            return scene.ReferenceTo(component.Id);
        }

        foreach (NetObject net in scene.Nets.Items)
        {
            return scene.ReferenceTo(net.Id);
        }

        return null;
    }

    private static void RequireError(OverlayToolRecipe recipe, string field)
    {
        if (!recipe.Validate().Any(error => error.Field == field))
        {
            throw new InvalidOperationException($"Recipe validation missed the {field} error.");
        }
    }
}
