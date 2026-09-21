// Edit-target bisect probe: queries read-edit-targets per kind with a small
// bound so a native per-kind/per-object failure is isolated. Usage:
// Probe --targets <bridge-dir> <kinds:component,trace,via> <maximum> [timeout]
using CircuitHub.AllegroBridge;

internal static class TargetsProbe
{
    public static async Task<int> RunAsync(string bridgeDirectory, string kinds, int maximum, int timeoutSeconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            Console.WriteLine($"Connecting to {bridgeDirectory} (bundled license)...");
            await using var session = await AllegroBridgeSession.ConnectAsync(
                new AllegroBridgeConnectOptions(bridgeDirectory),
                timeout.Token);
            AllegroPcbSession pcb = await session.OpenPcbAsync(timeout.Token);
            var parsed = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => Enum.Parse<AllegroPcbEditTargetKind>(k, ignoreCase: true))
                .ToArray();
            var query = new AllegroPcbEditQuery(parsed, [], [], IncludeUnnetted: false, MaximumObjects: maximum);
            Console.WriteLine($"Query kinds=[{string.Join(",", parsed)}] max={maximum}...");
            AllegroPcbEditTargetsResult result = await pcb.ReadEditTargetsAsync(query, timeout.Token);
            Console.WriteLine($"STATE={result.Receipt.State} MESSAGE={result.Receipt.Message}");
            string payload = result.Receipt.ResultPayloadJson ?? "(null)";
            Console.WriteLine($"PAYLOAD-CHARS={payload.Length}");
            Console.WriteLine(payload.Length <= 3000 ? payload : payload[..3000]);
            string? outPath = Environment.GetEnvironmentVariable("PD_PROBE_TARGETS_OUT");
            if (!string.IsNullOrWhiteSpace(outPath) && result.Receipt.ResultPayloadJson is not null)
            {
                await File.WriteAllTextAsync(outPath, result.Receipt.ResultPayloadJson, timeout.Token);
                Console.WriteLine($"PAYLOAD-FILE={outPath}");
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
