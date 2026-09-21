internal static class Program
{
    private static int Main()
    {
        try
        {
            OverlayRecipeChecks.Run();
            PublicationTrackerChecks.Run();
            ReviewBundleChecks.Run();
            Console.WriteLine("PASS: Lane B shared tool logic (overlay recipes, publication receipts, review bundles).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }
}
