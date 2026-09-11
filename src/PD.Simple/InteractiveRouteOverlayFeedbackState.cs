using CircuitHub.AllegroBridge.Engine.Live;

namespace PD.Simple;

// Presentation of an already operation-bound Engine stream, not a native picker or
// operation state machine. Engine and the controller retain their identity fences.
internal sealed class InteractiveRouteOverlayFeedbackState
{
    private long _lastSequence;

    internal bool CanRenderPicking
    {
        get; private set;
    }
    internal EngineInteractionFeedback? Pointer
    {
        get; private set;
    }
    internal EngineInteractionFeedback? Viewport
    {
        get; private set;
    }
    internal EngineInteractionFeedback? SemanticFeedback
    {
        get; private set;
    }
    internal EngineInteractionPoint? Point => SemanticFeedback?.Point;
    internal EnginePickedObjectKind ObjectKind =>
        SemanticFeedback?.ObjectKind ?? EnginePickedObjectKind.None;
    internal EnginePointerState PointerState => SemanticFeedback?.FeedbackKind switch
    {
        EngineInteractionFeedbackKind.SelectionRejected => EnginePointerState.Invalid,
        EngineInteractionFeedbackKind.SelectionAccepted => EnginePointerState.Valid,
        EngineInteractionFeedbackKind.Pointer => SemanticFeedback.PointerState,
        _ => EnginePointerState.Unknown
    };

    internal void Begin()
    {
        End();
        CanRenderPicking = true;
    }

    internal bool Apply(EngineInteractionFeedback feedback)
    {
        if (!CanRenderPicking || feedback.Sequence <= _lastSequence)
        {
            return false;
        }
        _lastSequence = feedback.Sequence;
        switch (feedback.FeedbackKind)
        {
            case EngineInteractionFeedbackKind.Pointer:
                Pointer = feedback;
                SemanticFeedback = feedback;
                break;
            case EngineInteractionFeedbackKind.ViewportChanged:
                Viewport = feedback;
                Pointer = null;
                SemanticFeedback = null;
                break;
            case EngineInteractionFeedbackKind.SelectionAccepted
                when feedback.SelectionOrdinal == 2:
                CanRenderPicking = false;
                SemanticFeedback = null;
                break;
            case EngineInteractionFeedbackKind.SelectionAccepted
                when feedback.SelectionOrdinal == 1:
            case EngineInteractionFeedbackKind.SelectionRejected:
                SemanticFeedback = feedback;
                break;
            case EngineInteractionFeedbackKind.SelectionCleared:
                Pointer = null;
                SemanticFeedback = null;
                break;
        }
        return true;
    }

    // Presentation budget only. Engine obtains the age from causal SDK evidence;
    // replay can supply explicit ages without a live session.
    internal bool HasFreshSemanticFeedback(TimeSpan? captureAge, TimeSpan maximumAge) =>
        CanRenderPicking && Point is not null && SemanticFeedback is not null &&
        maximumAge > TimeSpan.Zero && captureAge >= TimeSpan.Zero && captureAge <= maximumAge;

    internal string RejectionLabel =>
        SemanticFeedback?.FeedbackKind == EngineInteractionFeedbackKind.SelectionRejected
            ? ExplainRejection(SemanticFeedback.ReasonCode).Label
            : "Not selectable";

    internal void End()
    {
        CanRenderPicking = false;
        Pointer = null;
        Viewport = null;
        SemanticFeedback = null;
        _lastSequence = 0;
    }

    internal static (string Label, string Detail) ExplainRejection(string? reasonCode) =>
        reasonCode switch
        {
            "same_location" => (
                "Choose a different endpoint",
                "The end point matches the start point. Choose a different endpoint; the rejected click changed nothing."),
            "unsupported_object" => (
                "No supported object here",
                "No supported board object is available at that location. The rejected click changed nothing."),
            _ => (
                "Allegro rejected this pick",
                "Allegro rejected that selection. The rejected click changed nothing.")
        };
}
