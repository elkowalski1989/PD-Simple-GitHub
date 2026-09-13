using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Queries;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace ViaProximityExplorerSample;

internal static class ExplorerLiveAcquisition
{
    public static async ValueTask<LiveDesignScene> ReadAsync(
        AllegroEngineSession session,
        ExplorerRequest request,
        ProximityQueryRequirements requirements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requirements);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Live Engine acquisition runs on Windows beside Allegro. Use --scene for cross-platform analysis.");
        }
        if (request.InputKind == ExplorerInputKind.SavedScene)
        {
            throw new ArgumentException(
                "Live acquisition requires an explicit launch context or running target.",
                nameof(request));
        }

        EngineSessionTarget target = await ResolveTargetAsync(request, cancellationToken);
        if (request.InputKind == ExplorerInputKind.LaunchContext)
        {
            await session.ConnectAsync(target, cancellationToken);
        }
        else
        {
            await session.AttachAsync(target, cancellationToken);
        }

        SceneQuery query = requirements.CreateSceneQuery(
            maximumObjects: request.MaximumObjects);
        LiveDesignScene live = await session.Workspace.ReadAsync(query, cancellationToken);
        live.RequireCurrent();
        return live;
    }

    private static async ValueTask<EngineSessionTarget> ResolveTargetAsync(
        ExplorerRequest request,
        CancellationToken cancellationToken)
    {
        if (request.InputKind == ExplorerInputKind.LaunchContext)
        {
            EngineTargetResolution resolution = AllegroEngineDiscovery.ResolveLaunchTarget(
                ["--bridge-dir", request.Input]);
            EngineSessionTarget target = resolution.Target ?? throw new InvalidOperationException(
                DescribeDiagnostics(
                    "Engine did not resolve the supplied launch context.",
                    resolution.Diagnostics));
            RequireAvailable(target, EngineSessionTargetKind.LaunchContext);
            return target;
        }

        EngineDiscoveryResult discovery = await AllegroEngineDiscovery.DiscoverRunningAsync(
            cancellationToken);
        EngineSessionTarget[] matches = discovery.Targets
            .Where(candidate => candidate.Id == request.Input)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(DescribeDiagnostics(
                "The opaque running-target ID was not found exactly once. Run --list-running and select one current ID.",
                discovery.Diagnostics));
        }
        RequireAvailable(matches[0], EngineSessionTargetKind.RunningInstance);
        return matches[0];
    }

    private static void RequireAvailable(
        EngineSessionTarget target,
        EngineSessionTargetKind expectedKind)
    {
        if (target.Kind != expectedKind)
        {
            throw new InvalidOperationException(
                "Engine resolved a target of the wrong kind for the explicit acquisition mode.");
        }
        if (target.Availability != EngineSessionTargetAvailability.Available)
        {
            throw new InvalidOperationException(DescribeDiagnostics(
                "The explicitly selected Engine target is unavailable.",
                target.Diagnostics));
        }
    }

    private static string DescribeDiagnostics(
        string message,
        IEnumerable<EngineDiagnostic> diagnostics)
    {
        string detail = string.Join(" ", diagnostics.Select(diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
        return string.IsNullOrWhiteSpace(detail) ? message : message + " " + detail;
    }
}
