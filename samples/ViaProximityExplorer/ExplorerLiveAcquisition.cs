using PcbEngine.Advanced;
using PcbEngine.Queries;
using PcbEngine.Scenes;

namespace ViaProximityExplorerSample;

internal static class ExplorerLiveAcquisition
{
    public static async ValueTask<LiveDesignScene> ReadAsync(
        PcbSession session,
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

        SessionTarget target = await ResolveTargetAsync(request, cancellationToken);
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

    private static async ValueTask<SessionTarget> ResolveTargetAsync(
        ExplorerRequest request,
        CancellationToken cancellationToken)
    {
        if (request.InputKind == ExplorerInputKind.LaunchContext)
        {
            TargetResolution resolution = AllegroDiscovery.ResolveLaunchTarget(
                ["--bridge-dir", request.Input]);
            SessionTarget target = resolution.Target ?? throw new InvalidOperationException(
                DescribeDiagnostics(
                    "Engine did not resolve the supplied launch context.",
                    resolution.Diagnostics));
            RequireAvailable(target, SessionTargetKind.LaunchContext);
            return target;
        }

        DiscoveryResult discovery = await AllegroDiscovery.DiscoverRunningAsync(
            cancellationToken);
        SessionTarget[] matches = discovery.Targets
            .Where(candidate => candidate.Id == request.Input)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(DescribeDiagnostics(
                "The opaque running-target ID was not found exactly once. Run --list-running and select one current ID.",
                discovery.Diagnostics));
        }
        RequireAvailable(matches[0], SessionTargetKind.RunningInstance);
        return matches[0];
    }

    private static void RequireAvailable(
        SessionTarget target,
        SessionTargetKind expectedKind)
    {
        if (target.Kind != expectedKind)
        {
            throw new InvalidOperationException(
                "Engine resolved a target of the wrong kind for the explicit acquisition mode.");
        }
        if (target.Availability != SessionTargetAvailability.Available)
        {
            throw new InvalidOperationException(DescribeDiagnostics(
                "The explicitly selected Engine target is unavailable.",
                target.Diagnostics));
        }
    }

    private static string DescribeDiagnostics(
        string message,
        IEnumerable<Diagnostic> diagnostics)
    {
        string detail = string.Join(" ", diagnostics.Select(diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
        return string.IsNullOrWhiteSpace(detail) ? message : message + " " + detail;
    }
}
