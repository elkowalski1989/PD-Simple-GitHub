internal static class Program
{
    private static int Main()
    {
        try
        {
            OverlayRecipeChecks.Run();
            PublicationTrackerChecks.Run();
            ReviewBundleChecks.Run();
            MeasureChecks.Run();
            Console.WriteLine("PASS: Lane B shared tool logic (overlay recipes, publication receipts, review bundles, pick/measure).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }
}
