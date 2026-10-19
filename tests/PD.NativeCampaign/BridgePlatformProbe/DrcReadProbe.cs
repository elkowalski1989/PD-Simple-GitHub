using System;
using System.Threading;
using System.Threading.Tasks;
using PcbEngine.Native;

internal static class DrcReadProbe
{
    internal static async Task<int> RunAsync(string bridgeDirectory, int timeoutSeconds)
    {
        Console.WriteLine($"Connecting to {bridgeDirectory} (bundled license)...");
        await using var session = await PcbBridgeSession.ConnectAsync(
            new PcbEngineConnectOptions(bridgeDirectory),
            CancellationToken.None);
        AllegroSession pcb = await session.OpenPcbAsync(CancellationToken.None);
        Console.WriteLine("Reading existing DRC markers (no execution)...");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var result = await pcb.ReadDrcAsync(cts.Token);
            Console.WriteLine($"RECEIPT-STATE={result.Receipt.State}");
            Console.WriteLine($"SNAPSHOT={(result.Snapshot is null ? "null" : "present")}");
            if (result.Snapshot is not null)
            {
                Console.WriteLine($"TOTAL={result.Snapshot.TotalMarkers} CAPTURED={result.Snapshot.Markers.Count} COMPLETE={result.Snapshot.EnumerationComplete} FRESHNESS={result.Snapshot.RunFreshness}");
            }
            Console.WriteLine($"CODE={result.Receipt.Code} MESSAGE={result.Receipt.Message}");
            return result.Receipt.State == PcbEngine.Native.AllegroOperationState.Complete && result.Snapshot is not null ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"FAILED {exception.GetType().FullName}: {exception.Message}");
            return 1;
        }
    }
}
