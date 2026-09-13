using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;
using PD.Simple.Corridor;

namespace PD.Simple;

public sealed partial class BridgeSession
{
    private DpViaCorridorAnalysis? _managedAnalysis;

    public Task<DpViaCorridorAnalysis> AnalyzeAsync(DpViaCorridorOptions options, string reportPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        if (options.MarginMils is < 0 or > 50 || options.ModuleFilter is null ||
            options.ModuleFilter.Length > 64 || options.ModuleFilter.Any(char.IsControl))
        {
            throw new ArgumentException("Invalid corridor margin or module filter.");
        }
        ValidateReportPath(reportPath);
        _ = BeginOperation(CorridorCommand);
        _managedAnalysis = null;
        var task = AnalyzeCoreAsync(options, reportPath);
        Track(task);
        return task.WaitAsync(cancellationToken);
    }

    private async Task<DpViaCorridorAnalysis> AnalyzeCoreAsync(DpViaCorridorOptions options, string reportPath)
    {
        await Task.Yield();
        try
        {
            AllegroWorkspace workspace = await RequireEngineWorkspaceAsync(_lifetime.Token);
            string? module = string.IsNullOrWhiteSpace(options.ModuleFilter) ? null : options.ModuleFilter;
            SceneQuery query = SceneQuery.CompleteBoard(includeContours: false) with { Module = module };
            LiveDesignScene live = await workspace.ReadAsync(query, _lifetime.Token);
            live.RequireCurrent();
            DesignScene scene = live.Scene;
            var scan = await Task.Run(() => CorridorAnalyzer.Analyze(scene,
                new((double)options.MarginMils, module, options.IncludeUnused), _lifetime.Token), _lifetime.Token);
            live.RequireCurrent();
            await WriteManagedReportAsync(scan, scene.Document.Name, reportPath, _lifetime.Token);
            live.RequireCurrent();
            var shown = scan.Findings.Take(DpViaCorridorResult.MaximumFindings).Select(finding => new DpViaCorridorFinding(
                finding.Id, finding.PairName, finding.AggressorNet, finding.ObjectType, finding.Layer,
                finding.Category, finding.Risk, new((double)finding.P.X, (double)finding.P.Y),
                new((double)finding.N.X, (double)finding.N.Y),
                new((double)finding.Intrusion.X, (double)finding.Intrusion.Y),
                finding.DistanceMils, finding.HalfWidthMils, finding.HalfLengthMils)).ToArray();
            var result = new DpViaCorridorResult("pd-dp-via-corridor-managed-v1", scan.HasCompleteInputs ? "complete" : "partial",
                live.Document.BoardGeneration, scene.Document.Name, "mils", scene.Document.NativeUnits,
                reportPath, reportPath + ".dat", true,
                scan.PairCount, scan.CorridorCount,
                scan.Findings.Select(item => item.AggressorNet).Distinct(StringComparer.Ordinal).Count(),
                scan.Findings.Count, scan.Findings.Count(item => item.Risk == "CRITICAL"),
                scan.Findings.Count(item => item.Risk == "MEDIUM"), scan.Findings.Count(item => item.Risk == "LOW"),
                shown.Length < scan.Findings.Count, Array.AsReadOnly(shown))
            {
                CoverageWarnings = scan.CoverageWarnings
            };
            var analysis = new DpViaCorridorAnalysis(live.Document, result, State.CatalogGeneration)
            {
                ManagedScan = scan,
                LiveScene = live,
            };
            _managedAnalysis = analysis;
            return analysis;
        }
        catch (Exception error)
        {
            Post(() => Faulted?.Invoke(this, error.Message));
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    public Task<DpViaCorridorZoomResult> NavigateAsync(DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(finding);
        if (!ReferenceEquals(analysis, _managedAnalysis) || analysis.ManagedScan is null ||
            !analysis.Result.Findings.Contains(finding) ||
            !analysis.IsCurrentFor(State.SessionId, State.BoardGeneration, State.CatalogGeneration))
        {
            throw new InvalidOperationException("Only a finding from the current in-memory Engine analysis can be navigated. Offline files are not native authority.");
        }
        _ = BeginOperation(ZoomCommand);
        var task = NavigateCoreAsync(analysis, finding);
        Track(task);
        return task.WaitAsync(cancellationToken);
    }

    private async Task<DpViaCorridorZoomResult> NavigateCoreAsync(
        DpViaCorridorAnalysis analysis, DpViaCorridorFinding finding)
    {
        await Task.Yield();
        try
        {
            CorridorScan scan = analysis.ManagedScan!;
            CorridorFinding source = scan.Findings.Single(item => item.Id == finding.Id);
            SceneQuery query = CorridorNavigation.CreateQuery(scan, source);
            AllegroWorkspace workspace = await RequireEngineWorkspaceAsync(_lifetime.Token);
            if (workspace.Document != analysis.Document)
            {
                throw new InvalidDataException("The live Engine workspace no longer matches this captured corridor analysis. Run the analysis again.");
            }
            LiveRegionScene region = await workspace.ReadRegionAsync(query, _lifetime.Token);
            CorridorNavigation.ValidateFreshRead(scan, source, region, analysis.Document);
            // Engine owns the retained native region/state token. Use the
            // admitted region bounds after native normalization for guarded zoom.
            EngineViewport viewport = await workspace.Display.ZoomRegionAsync(
                region, region.Scene.Document.Bounds, new(finding.Layer), _lifetime.Token);
            if (viewport.Document != analysis.Document)
            {
                throw new InvalidDataException("Native navigation returned a viewport for another Engine document.");
            }
            return new(DpViaCorridorZoomResult.CurrentSchema, "complete", analysis.Document.BoardGeneration,
                analysis.Result.Design, analysis.Result.ReportPath, finding.Id, finding.Layer, "mils",
                ToBounds(viewport.RequestedBounds), ToBounds(viewport.ActualBounds));
        }
        finally
        {
            EndOperation();
        }
    }

    private static DpViaCorridorBounds ToBounds(DesignBounds bounds) =>
        new((double)bounds.Minimum.X, (double)bounds.Minimum.Y, (double)bounds.Maximum.X, (double)bounds.Maximum.Y);

    private static async Task WriteManagedReportAsync(CorridorScan scan, string design, string reportPath,
        CancellationToken cancellationToken)
    {
        string navigatorPath = reportPath + ".dat";
        string directory = Path.GetDirectoryName(reportPath)!;
        Directory.CreateDirectory(directory);
        string temporaryReport = Path.Combine(directory, $".dpvc-{Guid.NewGuid():N}.tmp");
        string temporaryNavigator = temporaryReport + ".dat";
        bool movedNavigator = false;
        try
        {
            var text = new StringBuilder();
            text.AppendLine("Differential Pair Via Corridor Screening Report");
            text.AppendLine($"Design: {design}");
            text.AppendLine($"Algorithm: {CorridorAnalyzer.Algorithm}");
            text.AppendLine($"Input status: {(scan.HasCompleteInputs ? "Complete for reference screening" : "PARTIAL: no clear/pass conclusion is permitted")}");
            text.AppendLine($"Native units: {scan.Scene.Document.NativeUnits}; report dimensions: mils");
            text.AppendLine($"Scope: {scan.Options.ModuleName ?? "Whole board"}; Engine capture: {scan.Scene.Identity.CaptureId:N}; provider: {scan.Scene.Identity.Provenance.Provider}");
            text.AppendLine(FormattableString.Invariant($"Margin: {scan.Options.MarginMils:0.###} mils; include unused pairs: {scan.Options.IncludeUnused}"));
            text.AppendLine($"Pairs: {scan.PairCount}; via corridors: {scan.CorridorCount}; findings: {scan.Findings.Count}");
            text.AppendLine(CorridorAnalyzer.Limitations);
            text.AppendLine("Risk/category labels are retained rule classifications, not measured coupling or capacitance.");
            foreach (string warning in scan.CoverageWarnings)
            {
                text.AppendLine("REVIEW REQUIRED: " + warning);
            }
            text.AppendLine();
            foreach (CorridorFinding finding in scan.Findings)
            {
                text.AppendLine(FormattableString.Invariant($"{finding.Id} | {finding.Risk} | {finding.Category} | {finding.PairName} | {finding.AggressorNet} | {finding.Layer} | {finding.ObjectType}"));
                text.AppendLine(FormattableString.Invariant($"  P=({finding.P.X:0.##########}, {finding.P.Y:0.##########}); N=({finding.N.X:0.##########}, {finding.N.Y:0.##########}); distance={finding.DistanceMils:0.##########}; half-width={finding.HalfWidthMils:0.##########}"));
            }
            await File.WriteAllTextAsync(temporaryReport, text.ToString(), new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(temporaryNavigator, JsonSerializer.Serialize(new
            {
                Schema = "pd-managed-corridor-export-v2", Algorithm = CorridorAnalyzer.Algorithm,
                Design = design, Units = "mils", scan.Scene.Document.NativeUnits, scan.Options,
                scan.PairCount, scan.CorridorCount, scan.HasCompleteInputs, scan.CoverageWarnings,
                Findings = scan.Findings,
                CaptureId = scan.Scene.Identity.CaptureId,
                Authority = "Offline report only. Live navigation requires the current in-memory analysis and a fresh Engine region revalidation."
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryNavigator, navigatorPath, overwrite: false);
            movedNavigator = true;
            File.Move(temporaryReport, reportPath, overwrite: false);
        }
        catch
        {
            if (movedNavigator)
            {
                File.Delete(navigatorPath);
            }
            throw;
        }
        finally
        {
            File.Delete(temporaryReport);
            File.Delete(temporaryNavigator);
        }
    }
}
