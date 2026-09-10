using System.Windows.Media;
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Wpf;

namespace PD.Simple;

/// <summary>Tool annotations only; the SDK projects, clips and rasterizes them.</summary>
internal static class BoardOverlayDrawingPolicy
{
    internal static IReadOnlyList<AllegroCanvasPolyline> Corridor(
        IReadOnlyList<AllegroBoardPoint> points, double styleScale)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count is not (6 or 7))
        {
            throw new ArgumentException("A corridor needs four corners, P/N centers and an optional intrusion.", nameof(points));
        }
        if (!double.IsFinite(styleScale) || styleScale <= 0 || styleScale > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(styleScale));
        }

        AllegroBoardPoint[] outline = [points[0], points[1], points[2], points[3], points[0]];
        return
        [
            new(outline, Color.FromRgb(7, 21, 34), 3.5 * styleScale),
            new(outline, Color.FromRgb(88, 191, 255), 1.6 * styleScale),
            new([points[4], points[5]], Color.FromRgb(121, 201, 255), styleScale)
        ];
    }
}
