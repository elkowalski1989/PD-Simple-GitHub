using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Queries;
using CircuitHub.AllegroBridge.Engine.Scenes;
using ViaProximityExplorerSample;

if (args is ["--help"])
{
    ExplorerReport.PrintUsage(presentationOnly: false);
    return 0;
}

using var lifetime = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    lifetime.Cancel();
};
Console.CancelKeyPress += cancel;

try
{
    if (args is ["--list-running"])
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Running Allegro discovery is available on Windows. Saved-scene analysis remains cross-platform.");
            return 1;
        }
        EngineDiscoveryResult discovery = await AllegroEngineDiscovery.DiscoverRunningAsync(lifetime.Token);
        ExplorerReport.WriteRunningTargets(discovery);
        return 0;
    }

    if (!ExplorerRequest.TryParse(args, out ExplorerRequest? request, out string error))
    {
        Console.Error.WriteLine(error);
        ExplorerReport.PrintUsage(presentationOnly: false);
        return 64;
    }

    ProximitySpecification planningSpecification = request!.CreateSpecification(Guid.NewGuid());
    ProximityQueryRequirements requirements = ProximityQuery.GetRequirements(planningSpecification);
    DesignScene scene;
    if (request.InputKind == ExplorerInputKind.SavedScene)
    {
        scene = await SceneArchive.LoadAsync(request.Input, lifetime.Token);
    }
    else
    {
        await using AllegroEngineSession session = AllegroEngineSession.Create(
            new EngineSessionOptions
            {
                RequiredCapabilities = [EngineCapabilities.SceneRead]
            });
        LiveDesignScene live = await ExplorerLiveAcquisition.ReadAsync(
            session,
            request,
            requirements,
            lifetime.Token);
        scene = live.Scene;
    }

    ProximitySpecification specification = request.CreateSpecification(scene.Identity.CaptureId);
    ProximityAnalysis analysis = ProximityQuery.Execute(scene, specification, lifetime.Token);
    ExplorerReport.Write(request, scene, analysis, presented: false);
    return analysis.State == ProximityQueryState.Complete ? 0 : 3;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Acquisition or local analysis was canceled. No mutation or replay was requested.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancel;
}
