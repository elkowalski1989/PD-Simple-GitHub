using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.Simple.Corridor;

namespace PD.Simple;

public sealed record InteractiveRoutePickSummary(
    string ObjectType,
    string Location);

public sealed record InteractiveRouteInteractionState(
    bool IsActive,
    bool HasFirstPick,
    string PhaseTitle,
    string Detail,
    InteractiveRoutePickSummary? StartPick = null,
    InteractiveRoutePickSummary? EndPick = null)
{
    public static InteractiveRouteInteractionState Inactive { get; } =
        new(false, false, "Interactive Route inactive", string.Empty);
}

/// <summary>
/// Applies PD-owned drawing intent through the shared same-session WPF facade.
/// Engine/WPF own canvas discovery, projection, clipping, freshness, recovery,
/// and the live overlay lease. Disposing this controller never disposes either
/// the supplied presentation or its Engine session.
/// </summary>
internal sealed class BoardOverlayController : IDisposable
{
    private readonly AllegroEngineSession _session;
    private readonly EngineWpfPresentation _presentation;
    private bool _disposed;

    internal BoardOverlayController(
        AllegroEngineSession session,
        EngineWpfPresentation presentation)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        if (!ReferenceEquals(_presentation.Session, _session))
        {
            throw new ArgumentException(
                "The WPF presentation must borrow the supplied Engine session.",
                nameof(presentation));
        }
    }

    internal async ValueTask PresentAsync(
        DpViaCorridorBoardOverlay overlay,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(overlay);
        overlay.RequireCurrent(_session);

        await _presentation.PresentAsync(
            overlay.Source,
            overlay.Drawings,
            cancellationToken);

        // The facade fences the actual publication. Keep PD's selected-finding
        // state equally strict if the document changes as the await resumes.
        overlay.RequireCurrent(_session);
    }

    internal void Clear()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            _presentation.Clear();
        }
        catch (ObjectDisposedException)
        {
            // A coordinator that retires presentation first has already
            // cleared its sole overlay lease. PD still owns no shared lifetime.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            Clear();
        }
        finally
        {
            _disposed = true;
        }
    }
}
