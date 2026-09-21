// Region probe: runs read-region (same Candidates core as edit-target vias)
// to isolate whether candidate discovery or via-state extraction fails.
// Usage: Probe --region <bridge-dir> [max-objects] [timeout-seconds]
using CircuitHub.AllegroBridge;

internal static class RegionProbe
{
    public static async Task<int> RunAsync(string bridgeDirectory, int maximumObjects, int timeoutSeconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            Console.WriteLine($"Connecting to {bridgeDirectory} (bundled license)...");
            await using var session = await AllegroBridgeSession.ConnectAsync(
                new AllegroBridgeConnectOptions(bridgeDirectory),
                timeout.Token);
            AllegroPcbSession pcb = await session.OpenPcbAsync(timeout.Token);
            var query = new AllegroPcbRegionQuery(MaximumObjects: maximumObjects);
            Console.WriteLine($"Query max={maximumObjects}...");
            AllegroPcbRegionResult result = await pcb.ReadRegionAsync(query, timeout.Token);
            Console.WriteLine($"STATE={result.Receipt.State} MESSAGE={result.Receipt.Message}");
            var objects = result.Geometry?.Objects;
            Console.WriteLine($"OBJECTS={objects?.Count ?? -1}");
            if (objects is not null)
            {
                foreach (var group in objects.GroupBy(o => o.Kind).OrderBy(g => g.Key.ToString()))
                {
                    Console.WriteLine($"KIND {group.Key}={group.Count()}");
                }
            }
            return result.Receipt.State == AllegroOperationState.Complete ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine("FAILED " + exception.GetType().FullName + ": " + exception.Message);
            return 1;
        }
    }
}
