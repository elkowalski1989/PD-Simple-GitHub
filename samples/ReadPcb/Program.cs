using System.Globalization;
using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

if (args is ["--help"])
{
    PrintUsage();
    return 0;
}

string? bridgeDirectory = null;
var netNames = new List<string>();
int maximumResults = 512;
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
        case "--maximum-results" when int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int parsedLimit):
            maximumResults = parsedLimit;
            break;
        default:
            PrintUsage();
            return 64;
    }
}

if (string.IsNullOrWhiteSpace(bridgeDirectory) ||
    netNames.Count is < 1 or > 32 ||
    netNames.Any(name => string.IsNullOrWhiteSpace(name) ||
        name.Length > 256 ||
        name.Any(char.IsControl)) ||
    netNames.Distinct(StringComparer.Ordinal).Count() != netNames.Count ||
    maximumResults is < 1 or > 2048)
{
    PrintUsage();
    return 64;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Build anywhere with .NET 10; run on Windows beside the active Allegro session.");
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
    EngineTargetResolution resolution = AllegroEngineDiscovery.ResolveLaunchTarget(args);
    EngineSessionTarget target = resolution.Target ?? throw new InvalidOperationException(
        DescribeDiagnostics(resolution.Diagnostics));
    await using AllegroEngineSession session = AllegroEngineSession.Create(
        new EngineSessionOptions { RequiredCapabilities = [EngineCapabilities.SceneRead] });
    await session.ConnectAsync(target, waitLifetime.Token);

    Console.WriteLine($"Reading one coherent board-geometry capture and filtering {netNames.Count} exact net names locally.");
    LiveDesignScene capture = await session.Workspace.ReadAsync(
        SceneQuery.BoardGeometry(includeContours: false), waitLifetime.Token);
    FamilyCoverage coverage = capture.Scene.Copper.Coverage;
    var selectedNames = netNames.ToHashSet(StringComparer.Ordinal);
    CopperObject[] matching = capture.Scene.Copper.RequireAvailable()
        .Where(item => item.NetName is not null && selectedNames.Contains(item.NetName))
        .Take(maximumResults + 1)
        .ToArray();

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        capture.Document,
        capture.Scene.Identity,
        Nets = netNames,
        Copper = matching.Take(maximumResults),
        ResultLimitReached = matching.Length > maximumResults,
        coverage.Availability,
        coverage.Completeness,
        coverage.Fidelity,
        coverage.Truncated,
        coverage.Reasons,
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Canonical coordinates are decimal mils. Source native units and precision remain in scene provenance.");
    Console.WriteLine("Incomplete capture coverage or limited output must not be interpreted as empty space or routing clearance.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("The managed Engine connection/read wait stopped. No edit or replay was requested.");
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

static string DescribeDiagnostics(IEnumerable<EngineDiagnostic> diagnostics)
{
    string message = string.Join(" ", diagnostics.Select(item => $"{item.Code}: {item.Message}"));
    return string.IsNullOrWhiteSpace(message)
        ? "The launch context did not identify an available Engine target."
        : message;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: ReadPcb --bridge-dir <active bridge directory> --net <exact name> [--net <another name>] [--maximum-results <1..2048>]");
    Console.Error.WriteLine("Engine acquires coherent board geometry, then filters 1–32 unique, case-sensitive net names locally. No edits are performed.");
}
