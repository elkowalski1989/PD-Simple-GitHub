using System.Globalization;
using System.Text.Json;
using CircuitHub.AllegroBridge;

if (args is ["--help"])
{
    PrintUsage();
    return 0;
}

string? bridgeDirectory = null;
var netNames = new List<string>();
int maximumObjects = 512;
for (int index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length)
    {
        PrintUsage();
        return 64;
    }

    string value = args[index + 1];
    switch (args[index])
    {
        case "--bridge-dir" when bridgeDirectory is null:
            bridgeDirectory = value;
            break;
        case "--net":
            netNames.Add(value);
            break;
        case "--maximum-objects" when int.TryParse(value, NumberStyles.None,
            CultureInfo.InvariantCulture, out int parsedLimit):
            maximumObjects = parsedLimit;
            break;
        default:
            PrintUsage();
            return 64;
    }
}

if (string.IsNullOrWhiteSpace(bridgeDirectory) || netNames.Count is < 1 or > 32 ||
    netNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl)) ||
    netNames.Distinct(StringComparer.Ordinal).Count() != netNames.Count || maximumObjects is < 1 or > 2048)
{
    PrintUsage();
    return 64;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Build anywhere with .NET 10; run this sample on Windows beside the active Allegro session.");
    return 1;
}

using var waitLifetime = new CancellationTokenSource();
ConsoleCancelEventHandler cancelWait = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    waitLifetime.Cancel();
};
Console.CancelKeyPress += cancelWait;

try
{
    await using AllegroBridgeSession session = await AllegroBridgeSession.ConnectAndWaitUntilReadyAsync(
        new AllegroBridgeConnectOptions(bridgeDirectory), cancellationToken: waitLifetime.Token);
    AllegroPcbSession pcb = await session.OpenPcbAsync(waitLifetime.Token);
    if (!pcb.Capabilities.Any(command => command.Id == AllegroPcbContract.ReadGeometryCommand && command.IsAvailable))
    {
        Console.Error.WriteLine("The current session does not authorize the PCB geometry reader.");
        return 1;
    }

    Console.WriteLine($"Reading {netNames.Count} exact, case-sensitive net names; shared object limit: {maximumObjects}.");
    Console.WriteLine("Scope: bounded segments, vias and pins on these nets, not all-board copper or clearance analysis.");
    AllegroPcbGeometryResult result = await pcb.ReadGeometryAsync(
        new AllegroPcbNetQuery(netNames, maximumObjects), waitLifetime.Token);

    // Keep the native receipt alongside its data, including on a failed read.
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.Receipt,
        result.Geometry
    },
        new JsonSerializerOptions { WriteIndented = true }));
    if (result.Receipt.State != AllegroOperationState.Complete || result.Geometry is null)
    {
        Console.Error.WriteLine("The native read did not return a complete, verified geometry result.");
        return 1;
    }

    AllegroPcbGeometry geometry = result.Geometry;
    Console.WriteLine($"Public coordinates: {geometry.Units}; native units: {geometry.NativeUnits}; precision: {geometry.NativePrecision}.");
    Console.WriteLine($"Truncated: {geometry.Truncated}; unavailable: {string.Join(", ", geometry.Unavailable)}.");
    Console.WriteLine("Missing or unavailable data is not empty space. This bounded read cannot establish routing clearance.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("The managed read wait stopped. This does not claim native cancellation. No edit was requested.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelWait;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: ReadPcb --bridge-dir <active bridge directory> --net <exact name> [--net <another name>] [--maximum-objects <1..2048>]");
    Console.Error.WriteLine("Supply 1–32 unique, case-sensitive net names. The default shared object limit is 512. No edits are performed.");
}
