using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitHub.AllegroBridge;

internal static class CatalogProbe
{
    internal static async Task<int> RunAsync(string bridgeDirectory, int timeoutSeconds)
    {
        Console.WriteLine($"Connecting to {bridgeDirectory} (bundled license)...");
        await using var session = await AllegroBridgeSession.ConnectAsync(
            new AllegroBridgeConnectOptions(bridgeDirectory),
            CancellationToken.None);
        AllegroPcbSession pcb = await session.OpenPcbAsync(CancellationToken.None);
        Console.WriteLine("Reading component catalog...");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var result = await pcb.ReadCatalogAsync(
                new AllegroPcbCatalogQuery(AllegroPcbCatalogScope.Components),
                cts.Token);
            Console.WriteLine($"RECEIPT-STATE={result.Receipt.State}");
            Console.WriteLine($"CODE={result.Receipt.Code} MESSAGE={result.Receipt.Message}");
            if (result.Catalog is null)
            {
                Console.WriteLine("CATALOG=null");
                return 1;
            }
            var components = result.Catalog.Components.Items;
            int bounded = components.Count(c => c.Bounds is not null);
            Console.WriteLine($"COMPONENTS={components.Count} WITH-BOUNDS={bounded}");
            string? outPath = Environment.GetEnvironmentVariable("PD_PROBE_CATALOG_OUT");
            if (!string.IsNullOrWhiteSpace(outPath))
            {
                var lines = components.Select(component =>
                {
                    string bounds = component.Bounds is { } b
                        ? $"{b.Minimum.X},{b.Minimum.Y},{b.Maximum.X},{b.Maximum.Y}"
                        : "null";
                    string pos = component.Position is { } p ? $"{p.X},{p.Y}" : "null";
                    return $"{component.Refdes} pos={pos} bounds={bounds}";
                });
                await File.WriteAllLinesAsync(outPath, lines, cts.Token);
                Console.WriteLine($"BOUNDS-FILE={outPath}");
            }
            foreach (var component in components.Take(3))
            {
                string bounds = component.Bounds is { } b
                    ? $"[{b.Minimum.X},{b.Minimum.Y}]-[{b.Maximum.X},{b.Maximum.Y}]"
                    : "null";
                string pos = component.Position is { } p ? $"({p.X},{p.Y})" : "null";
                Console.WriteLine($"{component.Refdes} pos={pos} bounds={bounds}");
            }
            return result.Receipt.State == CircuitHub.AllegroBridge.AllegroOperationState.Complete ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"FAILED {exception.GetType().FullName}: {exception.Message}");
            return 1;
        }
    }
}
