// Minimal public-API handshake probe: connects to a resident-published
// bridge directory and prints the full result or exception. Used to surface
// the real handshake failure behind PD's 90-second connection timeout.
using CircuitHub.AllegroBridge;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--raw")
        {
            if (args.Length < 3)
            {
                Console.WriteLine("FAILED usage: Probe --raw <host-exe> <bridge-dir> [timeout-seconds]");
                return 2;
            }

            int rawTimeout = args.Length > 3 ? int.Parse(args[3]) : 60;
            return await RawHostProbe.RunAsync(args[1], args[2], rawTimeout);
        }

        if (args.Length >= 1 && args[0] == "--raw-once")
        {
            if (args.Length < 3)
            {
                Console.WriteLine("FAILED usage: Probe --raw-once <host-exe> <bridge-dir> [timeout-seconds]");
                return 2;
            }

            int onceTimeout = args.Length > 3 ? int.Parse(args[3]) : 40;
            return await RawHostOnceProbe.RunAsync(args[1], args[2], onceTimeout);
        }

        if (args.Length >= 1 && args[0] == "--drc")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("FAILED usage: Probe --drc <bridge-dir> [timeout-seconds]");
                return 2;
            }

            int drcTimeout = args.Length > 2 ? int.Parse(args[2]) : 120;
            return await DrcProbe.RunAsync(args[1], drcTimeout);
        }

        if (args.Length >= 1 && args[0] == "--targets")
        {
            if (args.Length < 4)
            {
                Console.WriteLine("FAILED usage: Probe --targets <bridge-dir> <kinds> <maximum> [timeout-seconds]");
                return 2;
            }

            int targetsTimeout = args.Length > 4 ? int.Parse(args[4]) : 120;
            return await TargetsProbe.RunAsync(args[1], args[2], int.Parse(args[3]), targetsTimeout);
        }

        if (args.Length >= 1 && args[0] == "--drc-read")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("FAILED usage: Probe --drc-read <bridge-dir> [timeout-seconds]");
                return 2;
            }

            int readTimeout = args.Length > 2 ? int.Parse(args[2]) : 120;
            return await DrcReadProbe.RunAsync(args[1], readTimeout);
        }

        if (args.Length >= 1 && args[0] == "--region")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("FAILED usage: Probe --region <bridge-dir> [max-objects] [timeout-seconds]");
                return 2;
            }

            int regionMax = args.Length > 2 ? int.Parse(args[2]) : 10;
            int regionTimeout = args.Length > 3 ? int.Parse(args[3]) : 120;
            return await RegionProbe.RunAsync(args[1], regionMax, regionTimeout);
        }

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
