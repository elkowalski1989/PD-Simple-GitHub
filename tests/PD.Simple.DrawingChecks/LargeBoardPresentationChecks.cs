using PD.Simple.LargeBoards;

internal static class LargeBoardPresentationChecks
{
    public static void Run()
    {
        string clear = LargeBoardCorridorPresentation.CompletionStatus(0, 12, 0);
        Require(clear.Contains("no crossings", StringComparison.Ordinal) &&
                !clear.Contains("clear conclusion", StringComparison.Ordinal),
            "A warning-free screen lost its ordinary completion message.");

        string advisoryZero = LargeBoardCorridorPresentation.CompletionStatus(0, 12, 2);
        Require(advisoryZero.Contains("observed no crossings", StringComparison.Ordinal) &&
                advisoryZero.Contains("2 review warnings", StringComparison.Ordinal) &&
                advisoryZero.Contains("prohibit a clear conclusion", StringComparison.Ordinal),
            "A zero-finding advisory result was presented as clear.");

        string advisoryFindings = LargeBoardCorridorPresentation.CompletionStatus(4, 12, 2);
        Require(advisoryFindings.Contains("found 4 crossings", StringComparison.Ordinal) &&
                advisoryFindings.Contains("2 review warnings", StringComparison.Ordinal) &&
                advisoryFindings.Contains("prohibit a clear conclusion", StringComparison.Ordinal),
            "A finding result hid its advisory input limitations.");

        Console.WriteLine(
            "PASS: large-board corridor completion text distinguishes advisory review from clear screening.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "Large-board presentation check failed: " + message);
        }
    }
}
