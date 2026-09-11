using System.Globalization;
using System.Windows.Media.Imaging;
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Windows;
using CircuitHub.AllegroBridge.Wpf;

namespace PD.Simple.Corridor;

/// <summary>Tool-specific viewport admission over the SDK-owned pixel capture.</summary>
public sealed record DpViaCorridorNativeCapture(BitmapSource Image, DpViaCorridorBounds Bounds,
    string FindingId, string Layer, DateTimeOffset CapturedAt)
{
    internal static async Task<DpViaCorridorNativeCapture> CaptureAsync(AllegroBridgeSession session,
        AllegroDesktopBinding desktop, DpViaCorridorZoomResult zoom, CancellationToken cancellationToken)
    {
        AllegroBoardObservation initial = session.CaptureObservation();
        AllegroSessionBinding binding = initial.Context.Binding;
        void RequireContext()
        {
            AllegroBoardObservation current = session.CaptureObservation();
            if (!desktop.IsCurrentFor(binding) || !desktop.IsCurrentFor(session.Binding) ||
                !desktop.IsCurrentFor(current.Context.Binding) ||
                current.Context.Binding.BoardGeneration != zoom.BoardGeneration ||
                current.Snapshot.Design != zoom.Design || current.Snapshot.DesignLocator != initial.Snapshot.DesignLocator)
            {
                throw new InvalidOperationException("The board changed before its image could be captured.");
            }
        }
        RequireContext();
        AllegroCanvasPixelCapture capture = await AllegroCanvasCapture.CaptureAsync(session, desktop, cancellationToken);
        RequireContext();
        if (!Matches(capture.Viewport, zoom.ActualBounds))
        {
            throw new InvalidOperationException("Allegro moved away from the selected crossing before its image was captured.");
        }
        BitmapSource raw = AllegroReviewFrame.Compose(capture).Raw;
        return new(raw, zoom.ActualBounds, zoom.FindingId, zoom.Layer, capture.CapturedAt);
    }

    internal static bool Matches(AllegroBoardViewport viewport, DpViaCorridorBounds bounds)
    {
        double scale = viewport.Units.ToUpperInvariant() switch
        {
            "MILS" => 1d,
            "MILLIMETERS" => 1d / 0.0254d,
            _ => double.NaN
        };
        // Retain the existing native %.12g serialization rule. This is not
        // a new spatial tolerance or relaxed native-geometry admission.
        double Wire(decimal value) => double.Parse(((double)value * scale)
            .ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return double.IsFinite(scale) && Wire(viewport.MinimumX) == bounds.MinimumXMil &&
            Wire(viewport.MinimumY) == bounds.MinimumYMil && Wire(viewport.MaximumX) == bounds.MaximumXMil &&
            Wire(viewport.MaximumY) == bounds.MaximumYMil;
    }
}
