// DRC evidence probe: runs update-drc through the public SDK and prints the
// raw result payload plus the session binding so validation mismatches are
// visible field-by-field. Usage: Probe --drc <bridge-dir> [timeout-seconds].
using CircuitHub.AllegroBridge;

internal static class DrcProbe
{
    public static async Task<int> RunAsync(string bridgeDirectory, int timeoutSeconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            Console.WriteLine($"Connecting to {bridgeDirectory} (bundled license)...");
            await using var session = await AllegroBridgeSession.ConnectAsync(
                new AllegroBridgeConnectOptions(bridgeDirectory),
                timeout.Token);
            Console.WriteLine("CONNECTED.");
            AllegroPcbSession pcb = await session.OpenPcbAsync(timeout.Token);
            Console.WriteLine("PCB opened; invoking update-drc via the PCB operations channel...");
            var operationsField = typeof(AllegroPcbSession).GetField(
                "_operations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            object? operations = operationsField?.GetValue(pcb);
            Console.WriteLine("operations=" + (operations?.GetType().FullName ?? "(null)"));
            var arguments = new Dictionary<string, AllegroArgumentValue>(StringComparer.Ordinal)
            {
                ["scope"] = AllegroArgumentValue.FromText("full_board"),
            };
            var execute = operations?.GetType().GetMethod(
                "ExecuteToTerminalAsync",
                new[]
                {
                    typeof(string),
                    typeof(System.Collections.Generic.IReadOnlyDictionary<string, AllegroArgumentValue>),
                    typeof(System.Threading.CancellationToken),
                });
            object? taskObj = execute?.Invoke(operations, new object[]
            {
                AllegroPcbDrcRunContract.UpdateDrcCommand, arguments, timeout.Token,
            });
            if (taskObj is not System.Threading.Tasks.ValueTask<AllegroOperationReceipt> typed)
            {
                Console.WriteLine("unexpected task shape: " + (taskObj?.GetType().FullName ?? "(null)"));
                return 2;
            }

            AllegroOperationReceipt receipt = await typed;
            Console.WriteLine($"STATE={receipt.State} MESSAGE={receipt.Message}");
            Console.WriteLine("PAYLOAD=" + (receipt.ResultPayloadJson ?? "(null)"));
            Console.WriteLine($"BINDING session={receipt.Binding.SessionId} gen={receipt.Binding.BoardGeneration}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("FAILED " + exception.GetType().FullName + ": " + exception.Message);
            Console.WriteLine(exception.ToString());
            return 1;
        }
    }
}
