// Minimal public-API handshake probe: connects to a resident-published
// bridge directory and prints the full result or exception. Used to surface
// the real handshake failure behind PD's 90-second connection timeout.
using CircuitHub.AllegroBridge;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("FAILED usage: Probe <bridge-dir> [timeout-seconds]");
            return 2;
        }

        int timeoutSeconds = args.Length > 1 ? int.Parse(args[1]) : 60;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            Console.WriteLine($"Connecting to {args[0]} (bundled license)...");
            await using var session = await AllegroBridgeSession.ConnectAsync(
                new AllegroBridgeConnectOptions(args[0]),
                timeout.Token);
            Console.WriteLine("CONNECTED session=" + session);
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
