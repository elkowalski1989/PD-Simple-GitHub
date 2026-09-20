namespace PD.Simple.Corridor;

/// <summary>How a corridor navigation request was triggered.</summary>
public static class DpViaCorridorNavigationOrigin
{
    public const string Explicit = "explicit";
    public const string Follow = "follow";
}

/// <summary>
/// Engine-verified facts a corridor navigation outcome may report. These name
/// what the Engine operation actually proved, not the corridor finding.
/// </summary>
public static class DpViaCorridorVerification
{
    /// <summary>
    /// The native ticket command resolved every admitted witness to exactly
    /// one live object at navigation time. No region was read; nothing about
    /// non-witness geometry or the finding analysis is proven.
    /// </summary>
    public const string WitnessIdentityAtNavigation = "witness-identity-at-navigation";

    /// <summary>
    /// A fresh region was acquired and the selected captured fields matched
    /// in it before the witness-scoped zoom. Fields outside the selected
    /// signature (contours, holes, surfaces) are excluded from the match;
    /// the corridor analysis itself was not rerun.
    /// </summary>
    public const string FreshRegionSelectedWitnessMatch = "fresh-region-selected-witness-match";
}

/// <summary>
/// One corridor navigation operation's own evidence. The mode, verification,
/// finding identity, and timings travel on the completing operation so a
/// published result can never be attributed to a different request through
/// shared mutable state. <c>NativeOperationId</c> names the Engine native
/// operation behind the outcome: the ticket token for Browse, the
/// region-read operation for strict revalidation. The service-wide
/// <c>LastNavigationPhases</c> remains a convenience diagnostic only.
/// </summary>
public sealed record DpViaCorridorNavigationOutcome(
    Guid OperationId,
    string Origin,
    DpViaCorridorNavigationMode RequestedMode,
    DpViaCorridorNavigationMode ExecutedMode,
    string Verification,
    string? NativeOperationId,
    string FindingId,
    Guid CaptureId,
    DpViaCorridorZoomResult Zoom,
    DpViaCorridorNavigationPhases Phases);

/// <summary>
/// Pure outcome-attribution decision: an outcome completes only the
/// request whose operation id and finding id it carries. Anything else
/// is a late or foreign completion and must be dropped before publish,
/// so a Browse result can never complete a strict request.
/// </summary>
internal static class DpViaCorridorOutcomeAttribution
{
    internal static bool IsForeignOutcome(
        DpViaCorridorNavigationOutcome outcome,
        Guid operationId,
        string findingId)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(findingId);
        return outcome.OperationId != operationId ||
            !string.Equals(outcome.FindingId, findingId, StringComparison.Ordinal);
    }
}
