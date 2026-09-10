using CircuitHub.AllegroBridge;

namespace PD.Simple;

// Presentation of an already operation-bound SDK stream, not a native picker or
// operation state machine. The SDK and controller retain their identity fences.
internal sealed class InteractiveRouteOverlayFeedbackState
{
    private long _lastSequence;

    internal bool CanRenderPicking
    {
        get; private set;
    }
    internal AllegroInteractionFeedback? Pointer
    {
        get; private set;
    }
    internal AllegroInteractionFeedback? Viewport
    {
        get; private set;
    }
    internal AllegroInteractionFeedback? SemanticFeedback
    {
        get; private set;
    }
    internal AllegroBoardPoint? Point => SemanticFeedback?.Point;
    internal AllegroInteractionObjectKind ObjectKind =>
        SemanticFeedback?.ObjectKind ?? AllegroInteractionObjectKind.None;
    internal AllegroPointerState PointerState => SemanticFeedback?.FeedbackKind switch
    {
        AllegroInteractionFeedbackKind.SelectionRejected => AllegroPointerState.Invalid,
        AllegroInteractionFeedbackKind.SelectionAccepted => AllegroPointerState.Valid,
        AllegroInteractionFeedbackKind.Pointer => SemanticFeedback.PointerState,
        _ => AllegroPointerState.Unknown
    };

    internal void Begin()
    {
        End();
        CanRenderPicking = true;
    }

    internal bool Apply(AllegroInteractionFeedback feedback)
    {
        if (!CanRenderPicking || feedback.Sequence <= _lastSequence)
        {
            return false;
        }
        _lastSequence = feedback.Sequence;
        switch (feedback.FeedbackKind)
        {
            case AllegroInteractionFeedbackKind.Pointer:
                Pointer = feedback;
                SemanticFeedback = feedback;
                break;
            case AllegroInteractionFeedbackKind.ViewportChanged:
                Viewport = feedback;
                Pointer = null;
                SemanticFeedback = null;
                break;
            case AllegroInteractionFeedbackKind.SelectionAccepted
                when feedback.SelectionOrdinal == 2:
                CanRenderPicking = false;
                SemanticFeedback = null;
                break;
            case AllegroInteractionFeedbackKind.SelectionAccepted
                when feedback.SelectionOrdinal == 1:
            case AllegroInteractionFeedbackKind.SelectionRejected:
                SemanticFeedback = feedback;
                break;
            case AllegroInteractionFeedbackKind.SelectionCleared:
                Pointer = null;
                SemanticFeedback = null;
                break;
        }
        return true;
    }

    // Presentation budget only. The controller obtains the age from the SDK's
    // causal evidence; replay can supply explicit ages without a live session.
    internal bool HasFreshSemanticFeedback(TimeSpan? captureAge, TimeSpan maximumAge) =>
        CanRenderPicking && Point is not null && SemanticFeedback is not null &&
        maximumAge > TimeSpan.Zero && captureAge >= TimeSpan.Zero && captureAge <= maximumAge;

    internal string RejectionLabel =>
        SemanticFeedback?.FeedbackKind == AllegroInteractionFeedbackKind.SelectionRejected
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
