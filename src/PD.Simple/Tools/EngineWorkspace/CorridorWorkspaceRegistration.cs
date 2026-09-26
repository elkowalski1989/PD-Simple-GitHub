using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Engine.Tools;
using PD.PcbTools;

namespace PD.Simple.Tools.EngineWorkspace;

/// <summary>
/// Standard Engine registration for the corridor screening tool. Options are
/// the existing <see cref="CorridorOptions"/> record: no second options type
/// and no policy fork. Acquisition reuses
/// <see cref="CorridorAnalyzer.CreateSceneQuery"/>, so the workspace acquires
/// exactly the audited families, copper kinds, and pad measurements the
/// screening consumes.
///
/// Per-kind copper completeness and partial coverage are judged by
/// <see cref="CorridorAnalyzer"/> warnings at analysis time, not by plan
/// admission: the plan admits available (possibly partial) coverage and the
/// published diagnostics carry the no-clear-conclusion warnings.
/// </summary>
internal static class CorridorWorkspaceRegistration
{
    internal const string SuffixCompatValue = "suffix-compat";

    internal const string DeclaredPairsValue = "declared-pairs";

    internal const string RetainedSourceIdentity = "pd-corridor-retained-capture";

    public static EngineToolRegistration<CorridorOptions> Create() => new(
        new CorridorWorkspaceTool(),
        CreateDefaultOptions,
        [
            new ToolParameter<CorridorOptions, decimal>(
                new()
                {
                    Key = "marginMils",
                    Label = "Corridor margin (mils)",
                    Help = "Extra corridor half-width in mils. Reference screening admits 0 to 50.",
                    Kind = ToolParameterKind.Decimal,
                    Effect = ToolParameterEffect.LocalAnalysis,
                    MinimumDecimal = 0m,
                    MaximumDecimal = 50m,
                    DefaultValue = 0m,
                },
                options => ToDecimalMargin(options.MarginMils),
                (options, value) => options with { MarginMils = (double)value }),
            new ToolParameter<CorridorOptions, string>(
                new()
                {
                    Key = "module",
                    Label = "Module filter",
                    Help = "Optional module name. Empty screens the whole board.",
                    Kind = ToolParameterKind.String,
                    Effect = ToolParameterEffect.Acquisition,
                    MaximumStringLength = 64,
                    DefaultValue = string.Empty,
                },
                options => options.ModuleName ?? string.Empty,
                (options, value) => options with
                {
                    ModuleName = string.IsNullOrWhiteSpace(value) ? null : value,
                }),
            new ToolParameter<CorridorOptions, bool>(
                new()
                {
                    Key = "includeUnused",
                    Label = "Include unused pairs",
                    Help = "Include NC_/NU_ provisional pairs in discovery.",
                    Kind = ToolParameterKind.Boolean,
                    Effect = ToolParameterEffect.LocalAnalysis,
                    DefaultValue = false,
                },
                options => options.IncludeUnused,
                (options, value) => options with { IncludeUnused = value }),
            new ToolParameter<CorridorOptions, string>(
                new()
                {
                    Key = "pairPolicy",
                    Label = "Pair discovery",
                    Help = "SuffixCompat follows the _P/_N net-name convention; " +
                        "DeclaredPairs follows Engine-declared pairs and Xnet membership.",
                    Kind = ToolParameterKind.Choice,
                    Effect = ToolParameterEffect.Acquisition,
                    Choices =
                    [
                        new(SuffixCompatValue, "Suffix _P/_N"),
                        new(DeclaredPairsValue, "Declared pairs"),
                    ],
                    DefaultValue = SuffixCompatValue,
                },
                options => options.PairPolicy switch
                {
                    CorridorPairPolicy.SuffixCompat => SuffixCompatValue,
                    CorridorPairPolicy.DeclaredPairs => DeclaredPairsValue,
                    // An undefined policy fails binding validation as an
                    // unknown choice; custom Validate names the cause.
                    _ => "unknown",
                },
                (options, value) => options with { PairPolicy = FromChoiceValue(value) }),
        ],
        Validate,
        Plan,
        new() { Kind = ToolSelectionKind.None },
        new()
        {
            ResultColumns = ["id", "pair", "aggressor", "layer", "risk"],
            AnnotationStyle = "standard",
        });

    internal static CorridorOptions CreateDefaultOptions() => new(
        MarginMils: 0,
        ModuleName: null,
        IncludeUnused: false,
        PairPolicy: CorridorPairPolicy.SuffixCompat);

    /// <summary>
    /// Total margin projection for parameter binding: values the decimal
    /// editor cannot represent fail binding validation instead of throwing
    /// from the getter, so validation stays total over all options.
    /// </summary>
    internal static decimal ToDecimalMargin(double margin)
    {
        if (!double.IsFinite(margin))
        {
            return 51m;
        }

        try
        {
            return (decimal)margin;
        }
        catch (OverflowException)
        {
            return 51m;
        }
    }

    internal static string ToChoiceValue(CorridorPairPolicy policy) => policy switch
    {
        CorridorPairPolicy.SuffixCompat => SuffixCompatValue,
        CorridorPairPolicy.DeclaredPairs => DeclaredPairsValue,
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    internal static CorridorPairPolicy FromChoiceValue(string? value) => value switch
    {
        SuffixCompatValue => CorridorPairPolicy.SuffixCompat,
        DeclaredPairsValue => CorridorPairPolicy.DeclaredPairs,
        _ => throw new ArgumentException(
            $"Unknown pair discovery choice '{value}'.",
            nameof(value)),
    };

    /// <summary>
    /// Tool-specific validation, shared identically by the workspace editors,
    /// recipe application, and programmatic runs. Parameter bindings already
    /// enforce the margin range, module length, and known choices; this adds
    /// the checks that need the candidate scene or cross-parameter meaning.
    /// </summary>
    internal static ImmutableArray<EngineDiagnostic> Validate(
        ToolValidationRequest<CorridorOptions> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CorridorOptions options = request.Options;
        if (!double.IsFinite(options.MarginMils) || options.MarginMils is < 0 or > 50)
        {
            return [Error(
                "pd.corridor.margin",
                "The corridor margin needs a value from 0 to 50 mils.")];
        }

        if (options.ModuleName is not null &&
            (options.ModuleName.Length > 64 || options.ModuleName.Any(char.IsControl)))
        {
            return [Error(
                "pd.corridor.module",
                "The module filter needs printable text up to 64 characters.")];
        }

        if (options.PairPolicy is not
            (CorridorPairPolicy.SuffixCompat or CorridorPairPolicy.DeclaredPairs))
        {
            return [Error(
                "pd.corridor.policy",
                "The pair discovery policy is unknown.")];
        }

        if (request.CandidateScene is { } scene)
        {
            if (options.ModuleName is not null &&
                scene.Coverage[DataFamily.Modules].IsComplete &&
                !scene.Data.Modules.Any(module => string.Equals(
                    module.Name, options.ModuleName, StringComparison.OrdinalIgnoreCase)))
            {
                return [Error(
                    "pd.corridor.module",
                    $"Module '{options.ModuleName}' was not found in the adopted capture.",
                    "Clear the module filter or adopt a capture containing that module.")];
            }

            if (options.PairPolicy == CorridorPairPolicy.DeclaredPairs &&
                scene.Coverage[DataFamily.Connectivity].Availability != DataAvailability.Available)
            {
                return [Error(
                    "pd.corridor.connectivity",
                    "Declared-pairs discovery needs Connectivity data, which the adopted " +
                    "capture does not carry.",
                    "Use suffix discovery or adopt a capture containing Connectivity.")];
            }
        }

        return [];
    }

    /// <summary>
    /// Pure acquisition intent for one run. Live runs name the exact expected
    /// document and the audited corridor query; offline runs reuse the adopted
    /// capture when its scope satisfies the same query. Deterministic in its
    /// inputs: no native work, no capture merging, no witness.
    /// </summary>
    internal static ToolAcquisitionPlan Plan(ToolPlanRequest<CorridorOptions> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CorridorOptions options = request.Options;
        string? module = string.IsNullOrWhiteSpace(options.ModuleName)
            ? null
            : options.ModuleName;
        SceneQuery query = CorridorAnalyzer.CreateSceneQuery(module, options.PairPolicy);
        ImmutableArray<DataRequirement> requirements =
            options.PairPolicy == CorridorPairPolicy.DeclaredPairs
                ? [
                    new(DataFamily.Nets, Complete: false),
                    new(DataFamily.Modules, Complete: false),
                    new(DataFamily.Layers, Complete: false),
                    new(DataFamily.Copper, Complete: false),
                    new(DataFamily.Connectivity, Complete: false),
                ]
                : [
                    new(DataFamily.Nets, Complete: false),
                    new(DataFamily.Modules, Complete: false),
                    new(DataFamily.Layers, Complete: false),
                    new(DataFamily.Copper, Complete: false),
                ];
        string scope = $"module {(module ?? "all")}, {ToChoiceValue(options.PairPolicy)} discovery";

        if (request is { LiveAcquisitionAvailable: true, CurrentDocument: not null })
        {
            return new ToolAcquisitionPlan(
                ToolAcquisitionSource.LiveSceneQuery,
                query,
                $"DP via corridor screening ({scope}) on the current board.",
                expectedDocument: request.CurrentDocument,
                requirements: requirements);
        }

        return new ToolAcquisitionPlan(
            ToolAcquisitionSource.RetainedCapture,
            query,
            $"DP via corridor screening ({scope}) over the adopted capture.",
            offlineSourceIdentity: RetainedSourceIdentity,
            requirements: requirements,
            historicalDataAcceptable: true);
    }

    private static EngineDiagnostic Error(string code, string message, string? action = null) =>
        new(code, message, EngineDiagnosticSeverity.Error, action);
}
