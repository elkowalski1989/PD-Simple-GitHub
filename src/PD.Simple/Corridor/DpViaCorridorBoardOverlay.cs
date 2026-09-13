using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Live;

namespace PD.Simple.Corridor;

/// <summary>
/// One selected finding expressed through the same canonical drawing scene used
/// for its captured review. This carries no canvas, projection, or native handle.
/// </summary>
internal sealed record DpViaCorridorBoardOverlay(
    DpViaCorridorZoomResult Zoom,
    DpViaCorridorFinding Finding,
    LiveDesignScene Source,
    DrawingScene Drawings,
    long Revision)
{
    internal void RequireCurrent(AllegroEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.State.ConnectionState != EngineConnectionState.Ready ||
            session.State.Document != Source.Document ||
            !Source.IsCurrent ||
            Revision < 0 ||
            Drawings.Revision != Revision ||
            Drawings.CaptureId != Source.Scene.Identity.CaptureId ||
            Zoom.Schema != DpViaCorridorZoomResult.CurrentSchema ||
            Zoom.Status != "complete" ||
            Zoom.Units != "mils" ||
            Zoom.BoardGeneration != Source.Document.BoardGeneration ||
            !string.Equals(Zoom.Design, Source.Document.Design, StringComparison.Ordinal) ||
            !string.Equals(Zoom.Design, Source.Scene.Document.Name, StringComparison.Ordinal) ||
            !string.Equals(Zoom.FindingId, Finding.Id, StringComparison.Ordinal) ||
            !string.Equals(Zoom.Layer, Finding.Layer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected corridor drawing does not belong to the current Engine scene and document.");
        }
    }
}
