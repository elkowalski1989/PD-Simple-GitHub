using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Queries;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace ViaProximityExplorerSample;

internal static class ExplorerReport
{
    public static void WriteRunningTargets(EngineDiscoveryResult discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
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
    }

    public static void Write(
        ExplorerRequest request,
        DesignScene scene,
        ProximityAnalysis analysis,
        bool presented)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(analysis);
        WriteJson(new
        {
            input = request.InputKind.ToString(),
            presentation = presented ? "LiveAnnotations" : "None",
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

    public static void PrintUsage(bool presentationOnly)
    {
        if (presentationOnly)
        {
            Console.Error.WriteLine(
                "Present: ViaProximityExplorer.Presentation (--bridge-dir <launch context> | --running-target <opaque ID>)");
        }
        else
        {
            Console.Error.WriteLine("List live targets: ViaProximityExplorer --list-running");
            Console.Error.WriteLine(
                "Analyze: ViaProximityExplorer (--scene <capture> | --bridge-dir <launch context> | --running-target <opaque ID>)");
        }
        Console.Error.WriteLine(
            "  (--source-net <name> | --source-via <scene object ID> [--source-via <ID> ...])");
        Console.Error.WriteLine(
            "  (--target-net <name> [--target-net <name> ...] | --target-shape <scene object ID> [--target-shape <ID> ...])");
        Console.Error.WriteLine(
            "  --threshold-mils <decimal> [--metric copper-gap|center-distance]");
        Console.Error.WriteLine(
            "  [--layer-policy shared|projected] [--layer <name> ...] [--equality include|exclude]");
        Console.Error.WriteLine(
            "  [--grouping pairwise|nearest|per-target-net] [--ties first|all]");
        Console.Error.WriteLine(
            "  [--maximum-objects <1..200000>] [--maximum-candidates <1..1000000>] [--maximum-results <1..100000>]");
        Console.Error.WriteLine(
            "All names and object IDs are exact and case-sensitive. Projected mode is planar only; it does not measure vertical separation.");
        if (presentationOnly)
        {
            Console.Error.WriteLine(
                "The live annotation window remains visible until Ctrl+C; saved scenes remain headless-only evidence.");
        }
    }

    private static void WriteJson(object value) => Console.WriteLine(
        JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
}
