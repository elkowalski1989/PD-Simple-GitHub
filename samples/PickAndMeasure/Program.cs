using System.Text.Json;
using CircuitHub.AllegroBridge;

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
    Console.Error.WriteLine("Build anywhere with .NET 10; run this sample on Windows beside the active Allegro session.");
    return 1;
}
if (Console.IsInputRedirected)
{
    Console.Error.WriteLine("Use an interactive console so Clear and native Cancel remain available.");
    return 64;
}

int cancellationRequested = 0;
using var startupLifetime = new CancellationTokenSource();
ConsoleCancelEventHandler requestCancellation = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    Interlocked.Exchange(ref cancellationRequested, 1);
    // Before picking this stops connection/setup. Once picking starts, its wait
    // deliberately uses no such token: the controls request native cancellation.
    startupLifetime.Cancel();
};
Console.CancelKeyPress += requestCancellation;

try
{
    await using AllegroBridgeSession session = await AllegroBridgeSession.ConnectAndWaitUntilReadyAsync(
        new AllegroBridgeConnectOptions(bridgeDirectory), cancellationToken: startupLifetime.Token);
    AllegroPcbSession pcb = await session.OpenPcbAsync(startupLifetime.Token);
    if (!pcb.Capabilities.Any(command => command.Id == AllegroPcbContract.PickEndpointsCommand && command.IsAvailable))
    {
        Console.Error.WriteLine("The current session does not authorize native endpoint picking.");
        return 1;
    }

    startupLifetime.Token.ThrowIfCancellationRequested();
    // Keep a late launch handle even if Ctrl+C arrives during dispatch. The
    // queued request will cancel that exact operation after the handle arrives.
    AllegroPcbEndpointPick picker = await pcb.PickEndpointsAsync();
    try
    {
        Console.WriteLine($"Endpoint operation: {picker.Operation.OperationId}");
        Console.WriteLine("Pick two supported objects in Allegro. Positions are clicked points, not inferred object centers.");
        Console.WriteLine("Console keys: C = clear first pick; X or Ctrl+C = request native cancellation. No geometry is edited.");

        using var controlsLifetime = new CancellationTokenSource();
        Task<Exception?> feedbackTask = ReadFeedbackAsync(picker.Operation, controlsLifetime.Token);
        Task<Exception?> controlsTask = ReadControlsAsync(picker, controlsLifetime.Token,
            () => Interlocked.Exchange(ref cancellationRequested, 0) != 0);

        AllegroPcbEndpointResult result;
        try
        {
            // Feedback and console-control errors do not replace this native
            // result. Ctrl+C does not merely abandon this terminal wait.
            result = await picker.WaitForResultAsync();
        }
        finally
        {
            controlsLifetime.Cancel();
            ReportLocalFailure("Feedback", await feedbackTask);
            ReportLocalFailure("Console controls", await controlsTask);
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.Receipt,
            result.Endpoints
        },
            new JsonSerializerOptions { WriteIndented = true }));
        if (result.Receipt.State == AllegroOperationState.Cancelled)
        {
            Console.WriteLine("Native endpoint cancellation confirmed. No geometry was edited.");
            return 2;
        }
        if (result.Receipt.State != AllegroOperationState.Complete || result.Endpoints is null)
        {
            Console.Error.WriteLine("Native picking did not complete. Inspect the original receipt; no measurement is reported.");
            return 1;
        }

        AllegroPcbEndpoints endpoints = result.Endpoints;
        double deltaX = endpoints.Second.Position.X - endpoints.First.Position.X;
        double deltaY = endpoints.Second.Position.Y - endpoints.First.Position.Y;
        double distanceMils = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (!double.IsFinite(distanceMils))
        {
            throw new InvalidDataException("The endpoint distance exceeds the numeric range.");
        }
        Console.WriteLine(FormattableString.Invariant($"Straight-line distance: {distanceMils:G17} mils."));
        Console.WriteLine($"Native units: {endpoints.NativeUnits}; native decimal precision: {endpoints.NativePrecision}.");
        Console.WriteLine("This is Euclidean distance between two observed clicks, not routed length or copper clearance.");
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
    Console.Error.WriteLine("Setup was canceled, or an SDK wait ended without a terminal result. Do not infer native cancellation from this exception.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("If a pick operation was printed without a terminal result, inspect Allegro; no retry or edit was requested.");
    return 1;
}
finally
{
    Console.CancelKeyPress -= requestCancellation;
}

static async Task<Exception?> ReadFeedbackAsync(IAllegroOperationHandle operation, CancellationToken stop)
{
    try
    {
        await foreach (AllegroInteractionFeedback feedback in operation.ReadInteractionFeedbackAsync(stop))
        {
            // Pointer/viewport events can arrive frequently; print semantic
            // selections only. The SDK still owns native hit-testing.
            if (feedback.FeedbackKind is AllegroInteractionFeedbackKind.SelectionAccepted or
                AllegroInteractionFeedbackKind.SelectionRejected or AllegroInteractionFeedbackKind.SelectionCleared)
            {
                Console.WriteLine($"{feedback.FeedbackKind}: pick {feedback.SelectionOrdinal}, {feedback.ObjectKind}, {feedback.ReasonCode}");
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

static async Task<Exception?> ReadControlsAsync(AllegroPcbEndpointPick picker, CancellationToken stop,
    Func<bool> takeCancellationRequest)
{
    try
    {
        while (!stop.IsCancellationRequested && !picker.Operation.Current.IsTerminal)
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
                        Console.WriteLine("Requesting cancellation of the native endpoint operation…");
                        await picker.Operation.CancelAsync(actionLifetime.Token);
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
                    Console.Error.WriteLine($"The native action was not confirmed: {exception.Message}");
                    Console.Error.WriteLine("The terminal wait is still active. Inspect Allegro or explicitly request the action again.");
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

static void ReportLocalFailure(string activity, Exception? exception)
{
    if (exception is not null)
    {
        Console.Error.WriteLine($"{activity} failed locally: {exception.Message}. This does not replace the native result.");
    }
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: PickAndMeasure --bridge-dir <active bridge directory>");
    Console.Error.WriteLine("Interactive console keys: C clears the first pick; X or Ctrl+C requests native cancellation. No edits are performed.");
}
