using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

if (args is ["--help"])
{
    PrintUsage();
    return 0;
}
if (args is not ["--bridge-dir", var bridgeDirectory] || string.IsNullOrWhiteSpace(bridgeDirectory))
{
    PrintUsage();
    return 64;
}
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Build anywhere with .NET 10; run on Windows beside the active Allegro session.");
    return 1;
}
if (Console.IsInputRedirected)
{
    Console.Error.WriteLine("Use an interactive console so Clear and explicit native Cancel remain available.");
    return 64;
}

int cancellationRequested = 0;
using var startupLifetime = new CancellationTokenSource();
ConsoleCancelEventHandler requestCancellation = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    Interlocked.Exchange(ref cancellationRequested, 1);
    startupLifetime.Cancel();
};
Console.CancelKeyPress += requestCancellation;

try
{
    EngineTargetResolution resolution = AllegroEngineDiscovery.ResolveLaunchTarget(args);
    EngineSessionTarget target = resolution.Target ?? throw new InvalidOperationException(
        DescribeDiagnostics(resolution.Diagnostics));
    await using AllegroEngineSession session = AllegroEngineSession.Create(
        new EngineSessionOptions
        {
            RequiredCapabilities = [EngineCapabilities.Picking, EngineCapabilities.Routing],
        });
    await session.ConnectAsync(target, startupLifetime.Token);

    startupLifetime.Token.ThrowIfCancellationRequested();
    EngineEndpointPick picker = await session.Workspace.Picking.StartTwoPointPickAsync();
    try
    {
        Console.WriteLine($"Endpoint pick admitted for document {picker.Document.SessionId}.");
        Console.WriteLine("Pick two supported objects in Allegro. Positions are observed clicks, not inferred centers.");
        Console.WriteLine("Console keys: C = clear first pick; X or Ctrl+C = request native cancellation. No geometry is edited.");

        using var controlsLifetime = new CancellationTokenSource();
        Task<Exception?> feedbackTask = ReadFeedbackAsync(picker, controlsLifetime.Token);
        Task<Exception?> controlsTask = ReadControlsAsync(
            picker,
            controlsLifetime.Token,
            () => Interlocked.Exchange(ref cancellationRequested, 0) != 0);

        EngineOperationTerminal terminal;
        EnginePickedEndpoints? endpoints = null;
        try
        {
            terminal = await picker.WaitForTerminalAsync();
            if (terminal.IsComplete)
            {
                endpoints = await picker.WaitForResultAsync();
            }
        }
        finally
        {
            controlsLifetime.Cancel();
            ReportLocalFailure("Feedback", await feedbackTask);
            ReportLocalFailure("Console controls", await controlsTask);
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            terminal,
            endpoints
        },
            new JsonSerializerOptions { WriteIndented = true }));
        if (!terminal.IsComplete)
        {
            Console.Error.WriteLine($"Engine endpoint picking ended as {terminal.State}: {terminal.Code}: {terminal.Message}");
            return string.Equals(terminal.State, "Cancelled", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        }

        EnginePickedEndpoints measured = endpoints ?? throw new InvalidDataException(
            "Engine reported a complete pick without endpoint data.");
        decimal distanceMils = measured.First.Position.DistanceTo(measured.Second.Position).Mils;
        Console.WriteLine(FormattableString.Invariant($"Straight-line distance: {distanceMils:G29} mils."));
        Console.WriteLine($"Native units: {measured.NativeUnits}; native decimal precision: {measured.NativePrecision}.");
        Console.WriteLine("This is Euclidean distance between observed clicks, not routed length or copper clearance.");
        return 0;
    }
    finally
    {
        try
        {
            await picker.DisposeAsync();
        }
        catch (Exception exception)
        {
            ReportLocalFailure("Local picker cleanup", exception);
        }
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Engine connection/setup was canceled. Do not infer native cancellation from this exception.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("If Engine recorded an unresolved operation, inspect the session state and Allegro instead of retrying.");
    return 1;
}
finally
{
    Console.CancelKeyPress -= requestCancellation;
}

static async Task<Exception?> ReadFeedbackAsync(EngineEndpointPick picker, CancellationToken stop)
{
    try
    {
        await foreach (EngineInteractionFeedback feedback in picker.ReadInteractionFeedbackAsync(stop))
        {
            if (feedback.FeedbackKind is EngineInteractionFeedbackKind.SelectionAccepted or
                EngineInteractionFeedbackKind.SelectionRejected or
                EngineInteractionFeedbackKind.SelectionCleared)
            {
                Console.WriteLine(
                    $"{feedback.FeedbackKind}: operation {feedback.OperationId}, pick {feedback.SelectionOrdinal}, " +
                    $"{feedback.ObjectKind}, {feedback.ReasonCode}");
            }
        }
        return null;
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        return null;
    }
    catch (Exception exception)
    {
        return exception;
    }
}

static async Task<Exception?> ReadControlsAsync(
    EngineEndpointPick picker,
    CancellationToken stop,
    Func<bool> takeCancellationRequest)
{
    try
    {
        while (!stop.IsCancellationRequested && !picker.IsTerminal)
        {
            bool cancel = takeCancellationRequest();
            bool clear = false;
            if (Console.KeyAvailable)
            {
                ConsoleKey key = Console.ReadKey(intercept: true).Key;
                cancel |= key == ConsoleKey.X;
                clear = key == ConsoleKey.C;
            }
            if (cancel || clear)
            {
                using var actionLifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
                actionLifetime.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    if (cancel)
                    {
                        Console.WriteLine("Requesting cancellation of the Engine endpoint operation…");
                        await picker.CancelAsync(actionLifetime.Token);
                    }
                    else
                    {
                        await picker.ClearFirstAsync(actionLifetime.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"The Engine action was not confirmed: {exception.Message}");
                    Console.Error.WriteLine("The terminal wait remains active; inspect Allegro or explicitly request the action again.");
                }
            }
            await Task.Delay(100, stop);
        }
        return null;
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        return null;
    }
    catch (Exception exception)
    {
        return exception;
    }
}

static string DescribeDiagnostics(IEnumerable<EngineDiagnostic> diagnostics)
{
    string message = string.Join(" ", diagnostics.Select(item => $"{item.Code}: {item.Message}"));
    return string.IsNullOrWhiteSpace(message)
        ? "The launch context did not identify an available Engine target."
        : message;
}

static void ReportLocalFailure(string activity, Exception? exception)
{
    if (exception is not null)
    {
        Console.Error.WriteLine($"{activity} failed locally: {exception.Message}. This does not replace the Engine terminal result.");
    }
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: PickAndMeasure --bridge-dir <active bridge directory>");
    Console.Error.WriteLine("Interactive keys: C clears the first pick; X or Ctrl+C requests native cancellation through Engine. No edits are performed.");
}
