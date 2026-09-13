using System.Collections.Immutable;
using System.Globalization;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Queries;

namespace ViaProximityExplorerSample;

internal enum ExplorerInputKind
{
    SavedScene,
    LaunchContext,
    RunningTarget
}

internal sealed record ExplorerRequest(
    ExplorerInputKind InputKind,
    string Input,
    string? SourceNet,
    ImmutableArray<string> SourceViaIds,
    ImmutableArray<string> TargetNets,
    ImmutableArray<string> TargetShapeIds,
    Length Threshold,
    ProximityDistanceMetric DistanceMetric,
    ProximityLayerPolicy LayerPolicy,
    ProximityThresholdEquality ThresholdEquality,
    ProximityGrouping Grouping,
    ProximityTiePolicy TiePolicy,
    ImmutableArray<LayerId> Layers,
    int MaximumObjects,
    int MaximumCandidates,
    int MaximumResults)
{
    public ProximitySpecification CreateSpecification(Guid captureId)
    {
        if (captureId == Guid.Empty)
        {
            throw new ArgumentException("A proximity specification requires an explicit capture identity.", nameof(captureId));
        }

        ProximityCopperFilter filter = new() { Layers = Layers };
        ProximitySet subjects = SourceNet is { } sourceNet
            ? new ProximityViasOnNet(sourceNet) { Filter = filter }
            : new ProximitySelectedVias(SourceViaIds
                .Select(id => new SceneObjectReference(captureId, new SceneObjectId(id)))
                .ToImmutableArray())
            {
                Filter = filter
            };
        ProximitySet targets = !TargetNets.IsEmpty
            ? new ProximityCopperOnNets(TargetNets) { Filter = filter }
            : new ProximitySelectedShapes(TargetShapeIds
                .Select(id => new SceneObjectReference(captureId, new SceneObjectId(id)))
                .ToImmutableArray())
            {
                Filter = filter
            };

        return new(subjects, targets, DistanceMetric, Threshold)
        {
            LayerPolicy = LayerPolicy,
            ThresholdEquality = ThresholdEquality,
            Grouping = Grouping,
            TiePolicy = TiePolicy,
            MaximumCandidates = MaximumCandidates,
            MaximumResults = MaximumResults
        };
    }

    public static bool TryParse(string[] arguments, out ExplorerRequest? request, out string error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        request = null;
        error = string.Empty;

        string? scenePath = null;
        string? bridgeDirectory = null;
        string? runningTarget = null;
        string? sourceNet = null;
        var sourceViaIds = new List<string>();
        var targetNets = new List<string>();
        var targetShapeIds = new List<string>();
        var layerNames = new List<string>();
        Length? threshold = null;
        ProximityDistanceMetric distanceMetric = ProximityDistanceMetric.CopperGap;
        ProximityLayerPolicy layerPolicy = ProximityLayerPolicy.SharedConductiveLayersOnly;
        ProximityThresholdEquality equality = ProximityThresholdEquality.IncludeEqual;
        ProximityGrouping grouping = ProximityGrouping.NearestPerSubject;
        ProximityTiePolicy ties = ProximityTiePolicy.FirstInStableOrder;
        int maximumObjects = 100_000;
        int maximumCandidates = 100_000;
        int maximumResults = 10_000;

        for (int index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length)
            {
                error = $"Option '{arguments[index]}' requires a value.";
                return false;
            }
            string option = arguments[index];
            string value = arguments[index + 1];
            switch (option)
            {
                case "--scene" when scenePath is null:
                    scenePath = value;
                    break;
                case "--bridge-dir" when bridgeDirectory is null:
                    bridgeDirectory = value;
                    break;
                case "--running-target" when runningTarget is null:
                    runningTarget = value;
                    break;
                case "--source-net" when sourceNet is null:
                    sourceNet = value;
                    break;
                case "--source-via":
                    sourceViaIds.Add(value);
                    break;
                case "--target-net":
                    targetNets.Add(value);
                    break;
                case "--target-shape":
                    targetShapeIds.Add(value);
                    break;
                case "--threshold-mils" when TryDecimal(value, out decimal thresholdMils):
                    threshold = new(thresholdMils);
                    break;
                case "--metric" when TryMetric(value, out ProximityDistanceMetric parsedMetric):
                    distanceMetric = parsedMetric;
                    break;
                case "--layer-policy" when TryLayerPolicy(value, out ProximityLayerPolicy parsedPolicy):
                    layerPolicy = parsedPolicy;
                    break;
                case "--equality" when TryEquality(value, out ProximityThresholdEquality parsedEquality):
                    equality = parsedEquality;
                    break;
                case "--grouping" when TryGrouping(value, out ProximityGrouping parsedGrouping):
                    grouping = parsedGrouping;
                    break;
                case "--ties" when TryTies(value, out ProximityTiePolicy parsedTies):
                    ties = parsedTies;
                    break;
                case "--layer":
                    layerNames.Add(value);
                    break;
                case "--maximum-objects" when TryInteger(value, out int parsedObjects):
                    maximumObjects = parsedObjects;
                    break;
                case "--maximum-candidates" when TryInteger(value, out int parsedCandidates):
                    maximumCandidates = parsedCandidates;
                    break;
                case "--maximum-results" when TryInteger(value, out int parsedResults):
                    maximumResults = parsedResults;
                    break;
                default:
                    error = $"Unknown option or invalid value: {option} {value}";
                    return false;
            }
        }

        int inputModes = Count(scenePath) + Count(bridgeDirectory) + Count(runningTarget);
        if (inputModes != 1)
        {
            error = "Choose exactly one input: --scene, --bridge-dir, or --running-target.";
            return false;
        }
        if ((sourceNet is null) == (sourceViaIds.Count == 0))
        {
            error = "Choose exactly one subject mode: --source-net or one or more --source-via values.";
            return false;
        }
        if ((targetNets.Count == 0) == (targetShapeIds.Count == 0))
        {
            error = "Choose exactly one target mode: one or more --target-net or --target-shape values.";
            return false;
        }
        if (threshold is not { Mils: >= 0 } || threshold.Value.Mils > 100_000_000m)
        {
            error = "--threshold-mils must be a finite decimal from 0 through 100000000.";
            return false;
        }
        if (!DistinctBounded(sourceViaIds, 10_000) || !DistinctBounded(targetNets, 256) ||
            !DistinctBounded(targetShapeIds, 10_000) || !DistinctBounded(layerNames, 64) ||
            sourceNet is not null && !ValidText(sourceNet) ||
            maximumObjects is < 1 or > 200_000 || maximumCandidates is < 1 or > 1_000_000 ||
            maximumResults is < 1 or > 100_000)
        {
            error = "Names, selections, layers, or resource budgets are invalid or duplicated.";
            return false;
        }

        ExplorerInputKind inputKind;
        string input;
        if (scenePath is not null)
        {
            inputKind = ExplorerInputKind.SavedScene;
            input = scenePath;
        }
        else if (bridgeDirectory is not null)
        {
            inputKind = ExplorerInputKind.LaunchContext;
            input = bridgeDirectory;
        }
        else
        {
            inputKind = ExplorerInputKind.RunningTarget;
            input = runningTarget!;
        }
        if (!ValidText(input))
        {
            error = "The selected input path or opaque target ID is invalid.";
            return false;
        }

        request = new(inputKind, input, sourceNet, sourceViaIds.ToImmutableArray(),
            targetNets.ToImmutableArray(), targetShapeIds.ToImmutableArray(), threshold.Value,
            distanceMetric, layerPolicy, equality, grouping, ties,
            layerNames.Select(name => new LayerId(name)).ToImmutableArray(),
            maximumObjects, maximumCandidates, maximumResults);
        return true;
    }

    private static bool DistinctBounded(List<string> values, int maximum) =>
        values.Count <= maximum && values.All(ValidText) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool ValidText(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && !value.Any(char.IsControl);

    private static int Count(string? value) => value is null ? 0 : 1;

    private static bool TryDecimal(string value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out result);

    private static bool TryInteger(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    private static bool TryMetric(string value, out ProximityDistanceMetric result)
    {
        result = value switch
        {
            "copper-gap" => ProximityDistanceMetric.CopperGap,
            "center-distance" => ProximityDistanceMetric.CenterDistance,
            _ => default
        };
        return value is "copper-gap" or "center-distance";
    }

    private static bool TryLayerPolicy(string value, out ProximityLayerPolicy result)
    {
        result = value switch
        {
            "shared" => ProximityLayerPolicy.SharedConductiveLayersOnly,
            "projected" => ProximityLayerPolicy.PlanarProjectionAcrossLayers,
            _ => default
        };
        return value is "shared" or "projected";
    }

    private static bool TryEquality(string value, out ProximityThresholdEquality result)
    {
        result = value switch
        {
            "include" => ProximityThresholdEquality.IncludeEqual,
            "exclude" => ProximityThresholdEquality.ExcludeEqual,
            _ => default
        };
        return value is "include" or "exclude";
    }

    private static bool TryGrouping(string value, out ProximityGrouping result)
    {
        result = value switch
        {
            "pairwise" => ProximityGrouping.Pairwise,
            "nearest" => ProximityGrouping.NearestPerSubject,
            "per-target-net" => ProximityGrouping.NearestPerSubjectAndTargetNet,
            _ => default
        };
        return value is "pairwise" or "nearest" or "per-target-net";
    }

    private static bool TryTies(string value, out ProximityTiePolicy result)
    {
        result = value switch
        {
            "first" => ProximityTiePolicy.FirstInStableOrder,
            "all" => ProximityTiePolicy.IncludeAll,
            _ => default
        };
        return value is "first" or "all";
    }
}
