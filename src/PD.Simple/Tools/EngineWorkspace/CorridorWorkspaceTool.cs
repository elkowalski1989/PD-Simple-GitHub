using System.Collections.Immutable;
using System.Globalization;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Interactions;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Engine.Tools;
using PD.PcbTools;

namespace PD.Simple.Tools.EngineWorkspace;

/// <summary>
/// Read-only Engine tool over the existing DP via corridor screening policy.
/// Analysis is delegated unchanged to <see cref="CorridorAnalyzer"/>: the same
/// options, reference-screening algorithm, coverage warnings, and finding
/// semantics as the established corridor workflow. This adapter only projects
/// the resulting <see cref="CorridorScan"/> onto the Engine finding and
/// annotation contracts.
///
/// Findings carry no involved-object references: corridor findings identify
/// aggressors by net name and location, and this adapter does not invent
/// object linkage it cannot prove. Each finding carries one point annotation
/// at its intrusion location so the workspace viewport can show it; the
/// annotation has no source object for the same reason.
/// </summary>
internal sealed class CorridorWorkspaceTool : ISceneTool<CorridorOptions>
{
    public const string ToolId = "pd.corridor-screening";

    public const string ToolName = "DP via corridor screening";

    internal const int MaximumFindings = 100_000;

    internal const int MaximumAnnotations = 10_000;

    private static readonly Version ToolVersionValue = new(1, 0);

    public ToolDescriptor Descriptor { get; } = new(
        ToolId,
        ToolName,
        ToolVersionValue,
        [DocumentKind.PcbBoard],
        [
            new(DataFamily.Nets, Complete: false),
            new(DataFamily.Modules, Complete: false),
            new(DataFamily.Layers, Complete: false),
            new(DataFamily.Copper, Complete: false),
        ]);

    public ValueTask<ToolResult> RunAsync(
        ToolContext context,
        CorridorOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Scene);
        ArgumentNullException.ThrowIfNull(options);
        CorridorScan scan = CorridorAnalyzer.Analyze(context.Scene, options, cancellationToken);
        return ValueTask.FromResult(MapScan(scan));
    }

    /// <summary>
    /// Projects one corridor scan onto Engine findings, annotations, and
    /// diagnostics. Coverage warnings become warning diagnostics so a
    /// zero-finding partial result is never presented as a clean result;
    /// finding order, identities, and engineering content are preserved.
    /// </summary>
    internal static ToolResult MapScan(CorridorScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        IReadOnlyList<CorridorFinding> published = scan.Findings;
        bool findingsTruncated = published.Count > MaximumFindings;
        if (findingsTruncated)
        {
            published = published.Take(MaximumFindings).ToArray();
        }

        var annotations = ImmutableArray.CreateBuilder<Annotation>();
        var findings = ImmutableArray.CreateBuilder<ToolFinding>();
        foreach (CorridorFinding finding in published)
        {
            Annotation? annotation = null;
            if (annotations.Count < MaximumAnnotations)
            {
                annotation = new Annotation(
                    finding.Id,
                    new PointGeometry(finding.Intrusion),
                    AnnotationRole.Finding,
                    Label: finding.Id,
                    HitBehavior: AnnotationHitBehavior.Click,
                    Source: null);
                annotations.Add(annotation);
            }

            findings.Add(new ToolFinding(
                finding.Id,
                DescribeFinding(finding),
                string.IsNullOrWhiteSpace(finding.Layer) ? null : new LayerId(finding.Layer),
                [],
                annotation));
        }

        // The scene retains these exact annotation instances, so each
        // finding's annotation is already a member of the published scene.
        AnnotationScene scenes = AnnotationScene
            .Empty(scan.Scene.Identity.CaptureId)
            .Replace(annotations.ToImmutable());

        var diagnostics = ImmutableArray.CreateBuilder<EngineDiagnostic>();
        foreach (string warning in scan.CoverageWarnings)
        {
            diagnostics.Add(new EngineDiagnostic(
                "pd.corridor.coverage",
                Truncate(warning),
                EngineDiagnosticSeverity.Warning));
        }

        diagnostics.Add(new EngineDiagnostic(
            "pd.corridor.summary",
            Truncate(string.Create(
                CultureInfo.InvariantCulture,
                $"reference-screening-v1: {scan.PairCount} pair(s), " +
                $"{scan.CorridorCount} corridor(s), {scan.Findings.Count} finding(s). " +
                $"{CorridorAnalyzer.Limitations}")),
            EngineDiagnosticSeverity.Information));

        if (findingsTruncated)
        {
            diagnostics.Add(new EngineDiagnostic(
                "pd.corridor.truncated",
                $"Only the first {MaximumFindings} findings were published; " +
                $"the scan produced {scan.Findings.Count}. Narrow the module filter and rerun.",
                EngineDiagnosticSeverity.Warning));
        }
        else if (published.Count > MaximumAnnotations)
        {
            diagnostics.Add(new EngineDiagnostic(
                "pd.corridor.truncated",
                $"Annotations were drawn for the first {MaximumAnnotations} of " +
                $"{published.Count} findings; every finding row remains inspectable.",
                EngineDiagnosticSeverity.Information));
        }

        return new ToolResult(findings.ToImmutable(), scenes, diagnostics.ToImmutable());
    }

    internal static string DescribeFinding(CorridorFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return Truncate(string.Create(
            CultureInfo.InvariantCulture,
            $"Pair {finding.PairName}: {finding.Risk} {finding.Category} -- " +
            $"{finding.ObjectType} '{finding.AggressorNet}' on {finding.Layer} " +
            $"at {finding.DistanceMils:F2} mils (width source {finding.WidthSourceLayer})."));
    }

    private static string Truncate(string message)
    {
        if (message.Length <= 4000)
        {
            return message;
        }

        return message[..3999] + "…";
    }
}
