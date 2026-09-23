namespace PD.Simple.Corridor;

/// <summary>
/// Run/outcome kind for the corridor checker's always-visible status. The
/// workspace view model derives this from its run flags and adopted result;
/// the view renders one compact badge near the Run action plus an expandable
/// detail surface, so a failure is visible with Setup closed.
/// </summary>
public enum DpViaCorridorRunStatusKind
{
    Idle,
    Running,
    Success,
    Cancelled,
    Incomplete,
    Failed,
}

/// <summary>
/// WPF-free classification and compact text for the corridor run status.
/// Success, cancellation, incomplete coverage, and failure each map to a
/// distinct kind and one-line text; color is never the only signal. Any
/// nonempty coverage warnings force review-required status even when the
/// result is otherwise complete for screening.
/// </summary>
public static class DpViaCorridorRunStatus
{
    public static DpViaCorridorRunStatusKind Classify(
        bool isBusy,
        bool cancelled,
        bool hasProblem,
        bool hasResult,
        bool completeInputs,
        bool hasCoverageWarnings = false)
    {
        if (isBusy)
        {
            return DpViaCorridorRunStatusKind.Running;
        }
        if (cancelled)
        {
            return DpViaCorridorRunStatusKind.Cancelled;
        }
        if (hasProblem)
        {
            return DpViaCorridorRunStatusKind.Failed;
        }
        if (!hasResult)
        {
            return DpViaCorridorRunStatusKind.Idle;
        }
        return completeInputs && !hasCoverageWarnings
            ? DpViaCorridorRunStatusKind.Success
            : DpViaCorridorRunStatusKind.Incomplete;
    }

    public static string CompactText(
        DpViaCorridorRunStatusKind kind,
        string statusTitle,
        int findingCount,
        bool hasReadySession,
        bool resultCurrent,
        int coverageWarningCount = 0,
        bool hasBlockingCoverageGaps = true)
    {
        return kind switch
        {
            DpViaCorridorRunStatusKind.Running => "Checking\u2026",
            DpViaCorridorRunStatusKind.Cancelled => "Check cancelled",
            DpViaCorridorRunStatusKind.Failed => statusTitle,
            DpViaCorridorRunStatusKind.Incomplete =>
                IncompleteText(coverageWarningCount, hasBlockingCoverageGaps),
            DpViaCorridorRunStatusKind.Success =>
                !resultCurrent
                    ? $"Previous result \u00B7 {findingCount:N0} crossings"
                    : findingCount == 0
                        ? "Snapshot: no crossings found"
                        : $"{findingCount:N0} crossings ready",
            _ => hasReadySession ? "Ready to check" : "Connect to Allegro",
        };
    }

    private static string IncompleteText(int coverageWarningCount, bool hasBlockingCoverageGaps) =>
        !hasBlockingCoverageGaps && coverageWarningCount > 0
            ? $"Review required: {coverageWarningCount:N0} coverage " +
                (coverageWarningCount == 1 ? "warning" : "warnings")
            : "Review required: incomplete inputs";
}
