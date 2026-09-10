using CircuitHub.AllegroBridge;
using PD.Bridge;
using PD.Simple.Corridor;

namespace PD.Simple.Corridor;

/// <summary>Captured finding annotations for a current board, not a continuously rerun analysis.</summary>
internal sealed record DpViaCorridorBoardOverlay(
    string SessionId, DpViaCorridorZoomResult Zoom, DpViaCorridorFinding Finding)
{
    internal bool IsCurrentFor(AllegroSessionBinding binding, BridgeSnapshot snapshot) =>
        snapshot.Connected && !string.IsNullOrWhiteSpace(SessionId) && Zoom is not null && Finding is not null &&
        string.Equals(SessionId, binding.SessionId, StringComparison.Ordinal) &&
        string.Equals(SessionId, snapshot.SessionId, StringComparison.Ordinal) &&
        Zoom.BoardGeneration == binding.BoardGeneration && snapshot.BoardGeneration == binding.BoardGeneration &&
        string.Equals(Zoom.Design, snapshot.Design, StringComparison.Ordinal) &&
        Zoom.Schema == DpViaCorridorZoomResult.CurrentSchema && Zoom.Status == "complete" && Zoom.Units == "mils" &&
        string.Equals(Zoom.FindingId, Finding.Id, StringComparison.Ordinal) &&
        string.Equals(Zoom.Layer, Finding.Layer, StringComparison.Ordinal);

    internal IReadOnlyList<DpViaCorridorPoint> GetPoints()
    {
        var points = DpViaCorridorGeometry.CorridorCorners(Finding).ToList();
        points.Add(Finding.P);
        points.Add(Finding.N);
        if (Finding.Intrusion is { } intrusion)
        {
            if (!double.IsFinite(intrusion.XMil) || !double.IsFinite(intrusion.YMil))
            {
                throw new ArgumentException("Captured intrusion coordinates must be finite.", nameof(Finding));
            }

            points.Add(intrusion);
        }
        return points.AsReadOnly();
    }
}
