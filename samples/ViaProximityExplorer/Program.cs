using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Queries;
using CircuitHub.AllegroBridge.Engine.Scenes;
using ViaProximityExplorerSample;

if (args is ["--help"])
{
    PrintUsage();
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
        WriteJson(new
        {
            discovery.Diagnostics,
            targets = discovery.Targets.Select(target => new
            {
                target.Id,
                target.Kind,
                target.ProcessId,
                target.Design,
                target.Availability,
                target.Diagnostics
            })
        });
        return 0;
    }

    if (!ExplorerRequest.TryParse(args, out ExplorerRequest? request, out string error))
    {
        Console.Error.WriteLine(error);
        PrintUsage();
        return 64;
    }

    ProximitySpecification planningSpecification = request!.CreateSpecification(Guid.NewGuid());
    ProximityQueryRequirements requirements = ProximityQuery.GetRequirements(planningSpecification);
    DesignScene scene = request.InputKind == ExplorerInputKind.SavedScene
        ? await SceneArchive.LoadAsync(request.Input, lifetime.Token)
        : await AcquireLiveSceneAsync(request, requirements, lifetime.Token);

    ProximitySpecification specification = request.CreateSpecification(scene.Identity.CaptureId);
    ProximityAnalysis analysis = ProximityQuery.Execute(scene, specification, lifetime.Token);
    WriteReport(request, scene, analysis);
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

static async ValueTask<DesignScene> AcquireLiveSceneAsync(
    ExplorerRequest request,
    ProximityQueryRequirements requirements,
    CancellationToken cancellationToken)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "Live Engine acquisition runs on Windows beside Allegro. Use --scene for cross-platform analysis.");
    }

    EngineSessionTarget target;
    if (request.InputKind == ExplorerInputKind.LaunchContext)
    {
        EngineTargetResolution resolution = AllegroEngineDiscovery.ResolveLaunchTarget(
            ["--bridge-dir", request.Input]);
        target = resolution.Target ?? throw new InvalidOperationException(
            DescribeDiagnostics("Engine did not resolve the supplied launch context.", resolution.Diagnostics));
        RequireAvailable(target);
    }
    else
    {
        EngineDiscoveryResult discovery = await AllegroEngineDiscovery.DiscoverRunningAsync(cancellationToken);
        EngineSessionTarget[] matches = discovery.Targets
            .Where(candidate => candidate.Id == request.Input)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(DescribeDiagnostics(
                "The opaque running-target ID was not found exactly once. Run --list-running and select one current ID.",
                discovery.Diagnostics));
        }
        target = matches[0];
        RequireAvailable(target);
    }

    await using AllegroEngineSession session = AllegroEngineSession.Create(new EngineSessionOptions
    {
        RequiredCapabilities = [EngineCapabilities.SceneRead]
    });
    if (target.Kind == EngineSessionTargetKind.LaunchContext)
    {
        await session.ConnectAsync(target, cancellationToken);
    }
    else
    {
        await session.AttachAsync(target, cancellationToken);
    }

    SceneQuery query = requirements.CreateSceneQuery(maximumObjects: request.MaximumObjects);
    LiveDesignScene live = await session.Workspace.ReadAsync(query, cancellationToken);
    if (!live.IsCurrent)
    {
        throw new InvalidOperationException(
            "The live document changed before its captured scene could be admitted for local analysis.");
    }
    return live.Scene;
}

static void RequireAvailable(EngineSessionTarget target)
{
    if (target.Availability != EngineSessionTargetAvailability.Available)
    {
        throw new InvalidOperationException(DescribeDiagnostics(
            "The explicitly selected Engine target is unavailable.", target.Diagnostics));
    }
}

static string DescribeDiagnostics(string message, IEnumerable<EngineDiagnostic> diagnostics)
{
    string detail = string.Join(" ", diagnostics.Select(diagnostic =>
        $"{diagnostic.Code}: {diagnostic.Message}"));
    return string.IsNullOrWhiteSpace(detail) ? message : message + " " + detail;
}

static void WriteReport(ExplorerRequest request, DesignScene scene, ProximityAnalysis analysis)
{
    WriteJson(new
    {
        input = request.InputKind.ToString(),
        scene = new
        {
            analysis.CaptureId,
            scene.Identity.CapturedAt,
            scene.Identity.Provenance,
            scene.Document.Kind,
            scene.Document.Name,
            scene.Query
        },
        specification = new
        {
            analysis.Specification.DistanceMetric,
            analysis.Specification.Threshold,
            analysis.Specification.LayerPolicy,
            analysis.Specification.ThresholdEquality,
            analysis.Specification.Grouping,
            analysis.Specification.TiePolicy,
            analysis.Specification.MaximumCandidates,
            analysis.Specification.MaximumResults
        },
        requirements = new
        {
            data = analysis.Requirements.Data.Select(requirement => new
            {
                requirement.Family,
                requirement.Complete,
                requirement.QualifiedGeometry
            }),
            analysis.Requirements.CopperKinds,
            analysis.Requirements.Layers,
            analysis.Requirements.IncludeContours,
            analysis.Requirements.CompleteBoardRecommended
        },
        analysis.State,
        analysis.Subjects,
        analysis.Targets,
        analysis.CandidatesEvaluated,
        analysis.ResultsTruncated,
        results = analysis.Results.Select(result => new
        {
            result.Subject,
            result.Target,
            result.SubjectNetName,
            result.TargetNetName,
            result.Layers,
            result.DistanceMetric,
            result.Distance,
            result.ErrorBound,
            result.SubjectWitness,
            result.TargetWitness,
            result.SubjectGeometry,
            result.TargetGeometry,
            result.CaptureId,
            result.CoverageScope,
            result.Provenance,
            result.Classification,
            result.Diagnostics
        }),
        annotations = new
        {
            analysis.Annotations.CaptureId,
            analysis.Annotations.Revision,
            analysis.Annotations.Items
        },
        analysis.Diagnostics
    });
}

static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value,
    new JsonSerializerOptions
    {
        WriteIndented = true
    }));

static void PrintUsage()
{
    Console.Error.WriteLine("List live targets: ViaProximityExplorer --list-running");
    Console.Error.WriteLine("Analyze: ViaProximityExplorer (--scene <capture> | --bridge-dir <launch context> | --running-target <opaque ID>)");
    Console.Error.WriteLine("  (--source-net <name> | --source-via <scene object ID> [--source-via <ID> ...])");
    Console.Error.WriteLine("  (--target-net <name> [--target-net <name> ...] | --target-shape <scene object ID> [--target-shape <ID> ...])");
    Console.Error.WriteLine("  --threshold-mils <decimal> [--metric copper-gap|center-distance]");
    Console.Error.WriteLine("  [--layer-policy shared|projected] [--layer <name> ...] [--equality include|exclude]");
    Console.Error.WriteLine("  [--grouping pairwise|nearest|per-target-net] [--ties first|all]");
    Console.Error.WriteLine("  [--maximum-objects <1..200000>] [--maximum-candidates <1..1000000>] [--maximum-results <1..100000>]");
    Console.Error.WriteLine("All names and object IDs are exact and case-sensitive. Projected mode is planar only; it does not measure vertical separation.");
}
