using System.Windows.Media.Imaging;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf;

namespace PD.Simple.Corridor;

/// <summary>
/// PD workflow metadata around one immutable review returned by the shared
/// Engine/WPF facade. The frame is historical evidence, not live board,
/// geometry, viewport, picking, or edit authority.
/// </summary>
public sealed class DpViaCorridorReviewCapture
{
    private DpViaCorridorReviewCapture(
        AllegroReviewFrame review,
        DpViaCorridorBounds bounds,
        string findingId,
        string layer,
        DpViaCorridorDrawingSource drawingSource)
    {
        Review = review;
        Bounds = bounds;
        FindingId = findingId;
        Layer = layer;
        DrawingSource = drawingSource;
    }

    public AllegroReviewFrame Review { get; }

    public DpViaCorridorBounds Bounds { get; }

    public string FindingId { get; }

    public string Layer { get; }

    public DateTimeOffset CapturedAt => Review.Capture.ObservedAt;

    internal DpViaCorridorDrawingSource DrawingSource { get; }

    public BitmapSource Raw => Review.Raw;

    public BitmapSource Annotated => Review.Annotated;

    public string Qualification => Review.Capture.Qualification;

    internal static DpViaCorridorReviewCapture Create(
        AllegroReviewFrame review,
        LiveDesignScene source,
        DpViaCorridorDrawingSource drawingSource,
        DpViaCorridorZoomResult zoom)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(drawingSource);
        ArgumentNullException.ThrowIfNull(zoom);

        DrawingScene drawings = drawingSource.ForReview(source.Scene);
        RequireWorkflowIdentity(
            review.Capture.Document,
            review.Capture.SceneCaptureId,
            source.Document,
            source.Scene.Identity.CaptureId,
            drawings.CaptureId,
            zoom);

        // Capture/document/scene/viewport evidence was already admitted by
        // EngineWpfPresentation.CaptureReviewAsync. PD deliberately does not
        // reproduce or weaken that lower-layer validation here.
        return new(
            review,
            zoom.ActualBounds,
            zoom.FindingId,
            zoom.Layer,
            drawingSource);
    }

    internal static void RequireWorkflowIdentity(
        WorkspaceDocumentIdentity? capturedDocument,
        Guid? capturedSceneId,
        WorkspaceDocumentIdentity sourceDocument,
        Guid sourceCaptureId,
        Guid drawingCaptureId,
        DpViaCorridorZoomResult zoom)
    {
        if (capturedDocument != sourceDocument ||
            capturedSceneId != sourceCaptureId ||
            zoom.Schema != DpViaCorridorZoomResult.CurrentSchema ||
            zoom.Status != "complete" ||
            zoom.Units != "mils" ||
            zoom.BoardGeneration != sourceDocument.BoardGeneration ||
            !string.Equals(zoom.Design, sourceDocument.Design, StringComparison.Ordinal) ||
            drawingCaptureId != sourceCaptureId)
        {
            throw new InvalidOperationException(
                "The selected finding no longer belongs to this Engine review workflow.");
        }
    }
}
