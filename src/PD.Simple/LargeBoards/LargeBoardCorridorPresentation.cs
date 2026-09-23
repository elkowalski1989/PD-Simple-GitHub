namespace PD.Simple.LargeBoards;

internal static class LargeBoardCorridorPresentation
{
    public static string CompletionStatus(
        int findingCount,
        int corridorCount,
        int reviewWarningCount)
    {
        if (findingCount < 0 || corridorCount < 0 || reviewWarningCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(findingCount),
                "Corridor result counts cannot be negative.");
        }
        if (reviewWarningCount > 0)
        {
            return findingCount == 0
                ? $"Large-board corridor screening observed no crossings, but {reviewWarningCount:N0} review warnings prohibit a clear conclusion."
                : $"Large-board corridor screening found {findingCount:N0} crossings across {corridorCount:N0} corridors; {reviewWarningCount:N0} review warnings prohibit a clear conclusion.";
        }
        return findingCount == 0
            ? "Large-board corridor check completed with no crossings. This is a screening result, not SI sign-off."
            : $"Large-board corridor check found {findingCount:N0} crossings across {corridorCount:N0} corridors.";
    }
}
