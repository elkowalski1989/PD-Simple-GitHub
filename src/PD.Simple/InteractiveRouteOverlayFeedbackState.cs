using PcbEngine.Advanced;

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
    internal InteractionFeedback? Pointer
    {
        get; private set;
    }
    internal InteractionFeedback? Viewport
    {
        get; private set;
    }
    internal InteractionFeedback? SemanticFeedback
    {
        get; private set;
    }
    internal InteractionPoint? Point => SemanticFeedback?.Point;
    internal PickedObjectKind ObjectKind =>
        SemanticFeedback?.ObjectKind ?? PickedObjectKind.None;
    internal PointerState PointerState => SemanticFeedback?.FeedbackKind switch
    {
        InteractionFeedbackKind.SelectionRejected => PointerState.Invalid,
        InteractionFeedbackKind.SelectionAccepted => PointerState.Valid,
        InteractionFeedbackKind.Pointer => SemanticFeedback.PointerState,
        _ => PointerState.Unknown
    };

    internal void Begin()
    {
        End();
        CanRenderPicking = true;
    }

    internal bool Apply(InteractionFeedback feedback)
    {
        if (!CanRenderPicking || feedback.Sequence <= _lastSequence)
        {
            return false;
        }
        _lastSequence = feedback.Sequence;
        switch (feedback.FeedbackKind)
        {
            case InteractionFeedbackKind.Pointer:
                Pointer = feedback;
                SemanticFeedback = feedback;
                break;
            case InteractionFeedbackKind.ViewportChanged:
                Viewport = feedback;
                Pointer = null;
                SemanticFeedback = null;
                break;
            case InteractionFeedbackKind.SelectionAccepted
                when feedback.SelectionOrdinal == 2:
                CanRenderPicking = false;
                SemanticFeedback = null;
                break;
            case InteractionFeedbackKind.SelectionAccepted
                when feedback.SelectionOrdinal == 1:
            case InteractionFeedbackKind.SelectionRejected:
                SemanticFeedback = feedback;
                break;
            case InteractionFeedbackKind.SelectionCleared:
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
        SemanticFeedback?.FeedbackKind == InteractionFeedbackKind.SelectionRejected
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
