using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Engine.Live;
using PD.Bridge;
using PD.Simple.Corridor;

namespace PD.Simple.Corridor;

/// <summary>Captured finding annotations for a current board, not a continuously rerun analysis.</summary>
internal sealed record DpViaCorridorBoardOverlay(
    string SessionId,
    DpViaCorridorZoomResult Zoom,
    DpViaCorridorFinding Finding,
    LiveDesignScene Source,
    long Revision)
{
    internal bool IsCurrentFor(AllegroSessionBinding binding, BridgeSnapshot snapshot) =>
        snapshot.Connected && !string.IsNullOrWhiteSpace(SessionId) && Zoom is not null && Finding is not null &&
        Source is not null && Source.IsCurrent && Revision >= 0 &&
        string.Equals(SessionId, binding.SessionId, StringComparison.Ordinal) &&
        string.Equals(SessionId, snapshot.SessionId, StringComparison.Ordinal) &&
        string.Equals(SessionId, Source.Document.SessionId, StringComparison.Ordinal) &&
        Zoom.BoardGeneration == binding.BoardGeneration && snapshot.BoardGeneration == binding.BoardGeneration &&
        Source.Document.BoardGeneration == binding.BoardGeneration &&
        string.Equals(Zoom.Design, snapshot.Design, StringComparison.Ordinal) &&
        string.Equals(Zoom.Design, Source.Scene.Document.Name, StringComparison.Ordinal) &&
        Zoom.Schema == DpViaCorridorZoomResult.CurrentSchema && Zoom.Status == "complete" && Zoom.Units == "mils" &&
        string.Equals(Zoom.FindingId, Finding.Id, StringComparison.Ordinal) &&
        string.Equals(Zoom.Layer, Finding.Layer, StringComparison.Ordinal);
}
