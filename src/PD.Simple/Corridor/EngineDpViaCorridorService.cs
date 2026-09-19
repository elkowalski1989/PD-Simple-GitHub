using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.Corridor;

/// <summary>
/// PD corridor workflow over the caller's stable Engine workspace. Engine owns
/// acquisition, document freshness, retained navigation evidence, and display
/// dispatch; this service owns only corridor interpretation and report content.
/// </summary>
internal sealed class EngineDpViaCorridorService : IDpViaCorridorService
{
    private readonly AllegroEngineSession _session;
    private readonly AllegroWorkspace _workspace;
    private DpViaCorridorAnalysis? _currentAnalysis;

    internal EngineDpViaCorridorService(AllegroEngineSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _workspace = session.Workspace;
    }

    public async Task<DpViaCorridorAnalysis> AnalyzeAsync(
        DpViaCorridorOptions options,
        string reportPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        ValidateReportPath(reportPath);
        RequireCapability(EngineCapabilities.SceneRead);
        _currentAnalysis = null;

        string? module = string.IsNullOrWhiteSpace(options.ModuleFilter)
            ? null
            : options.ModuleFilter;
        SceneQuery query = CorridorAnalyzer.CreateSceneQuery(module);
        var stageTimer = Stopwatch.StartNew();
        LiveDesignScene live = await _workspace.ReadAsync(query, cancellationToken);
        long acquisitionMilliseconds = stageTimer.ElapsedMilliseconds;
        live.RequireCurrent();
        DesignScene scene = live.Scene;
        stageTimer.Restart();
        CorridorScan scan = await Task.Run(
            () => CorridorAnalyzer.Analyze(
                scene,
                new((double)options.MarginMils, module, options.IncludeUnused),
                cancellationToken),
            cancellationToken);
        long analysisMilliseconds = stageTimer.ElapsedMilliseconds;
        live.RequireCurrent();
        stageTimer.Restart();
        await WriteManagedReportAsync(
            scan,
            scene.Document.Name,
            reportPath,
            cancellationToken);
        long reportMilliseconds = stageTimer.ElapsedMilliseconds;
        live.RequireCurrent();

        DpViaCorridorFinding[] shown = scan.Findings
            .Take(DpViaCorridorResult.MaximumFindings)
            .Select(static finding => new DpViaCorridorFinding(
                finding.Id,
                finding.PairName,
                finding.AggressorNet,
                finding.ObjectType,
                finding.Layer,
                finding.Category,
                finding.Risk,
                new((double)finding.P.X, (double)finding.P.Y),
                new((double)finding.N.X, (double)finding.N.Y),
                new((double)finding.Intrusion.X, (double)finding.Intrusion.Y),
                finding.DistanceMils,
                finding.HalfWidthMils,
                finding.HalfLengthMils))
            .ToArray();
        var result = new DpViaCorridorResult(
            "pd-dp-via-corridor-managed-v1",
            scan.HasCompleteInputs ? "complete" : "partial",
            live.Document.BoardGeneration,
            scene.Document.Name,
            "mils",
            scene.Document.NativeUnits,
            reportPath,
            reportPath + ".dat",
            true,
            scan.PairCount,
            scan.CorridorCount,
            scan.Findings
                .Select(static item => item.AggressorNet)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            scan.Findings.Count,
            scan.Findings.Count(static item => item.Risk == "CRITICAL"),
            scan.Findings.Count(static item => item.Risk == "MEDIUM"),
            scan.Findings.Count(static item => item.Risk == "LOW"),
            shown.Length < scan.Findings.Count,
            Array.AsReadOnly(shown))
        {
            CoverageWarnings = scan.CoverageWarnings,
        };
        var analysis = new DpViaCorridorAnalysis(live.Document, result)
        {
            ManagedScan = scan,
            LiveScene = live,
            Timings = new(
                acquisitionMilliseconds,
                analysisMilliseconds,
                reportMilliseconds,
                ToMilliseconds(live.AcquisitionTiming?.NativeCommandRoundTrip),
                ToMilliseconds(live.AcquisitionTiming?.SnapshotTransferAndSeal),
                ToMilliseconds(live.AcquisitionTiming?.NativeRelease),
                ToMilliseconds(live.AcquisitionTiming?.SnapshotReplayAndConversion),
                ToMilliseconds(live.AcquisitionTiming?.SceneConstruction),
                ToMilliseconds(live.AcquisitionTiming?.SnapshotDisposal),
                live.AcquisitionResources),
        };
        _currentAnalysis = analysis;
        return analysis;
    }

    private static long? ToMilliseconds(TimeSpan? value) =>
        value is { } elapsed
            ? checked((long)Math.Round(elapsed.TotalMilliseconds))
            : null;

    public DpViaCorridorNavigationPhases? LastNavigationPhases { get; private set; }

    /// <summary>
    /// Captured browsing: admits a lightweight navigation ticket from the
    /// current analysis scene and navigates without a fresh region read.
    /// The native command resolves each admitted witness to exactly one
    /// live object, rejecting stale and ambiguous witnesses. This proves
    /// witness identity at navigation time only, not full finding
    /// revalidation; a ticket never authorizes edits. Callers needing the
    /// strict full check must use <see cref="NavigateAsync"/>.
    /// </summary>
    public async Task<DpViaCorridorZoomResult> BrowseAsync(
        DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(finding);
        if (!ReferenceEquals(analysis, _currentAnalysis) ||
            analysis.ManagedScan is null ||
            !analysis.Result.Findings.Contains(finding) ||
            !analysis.IsCurrentFor(_session.State.Document))
        {
            throw new InvalidOperationException(
                "Only a finding from the current in-memory Engine analysis can be navigated. " +
                "Offline files and historical captures are not live display authority.");
        }

        RequireCapability(EngineCapabilities.Display);
        CorridorScan scan = analysis.ManagedScan;
        CorridorFinding source = scan.Findings.Single(item => item.Id == finding.Id);
        SceneQuery query = CorridorNavigation.CreateQuery(scan, source);
        DesignBounds scope = query.Region ??
            throw new InvalidDataException(
                "The corridor navigation query has no region scope.");
        if (_workspace.Document != analysis.Document)
        {
            throw new InvalidDataException(
                "The live Engine workspace no longer matches this captured corridor analysis. " +
                "Run the analysis again.");
        }

        System.Diagnostics.Stopwatch browseTimer = System.Diagnostics.Stopwatch.StartNew();
        EngineNavigationTicket ticket = _workspace.Display.AdmitTicket(
            scan.Scene,
            [source.PositiveViaIndex, source.NegativeViaIndex, source.AggressorIndex],
            analysis.Document,
            query.Layers,
            scope);
        EngineViewport viewport = await _workspace.Display.ZoomTicketAsync(
            ticket,
            scope,
            new(finding.Layer),
            cancellationToken);
        browseTimer.Stop();
        LastNavigationPhases = new(
            0,
            0,
            browseTimer.ElapsedMilliseconds,
            Mode: DpViaCorridorNavigationMode.Browse);
        if (viewport.Document != analysis.Document)
        {
            throw new InvalidDataException(
                "Engine navigation returned a viewport for another document.");
        }
        string nativeDesign = analysis.Document.Design ??
            throw new InvalidDataException(
                "The current Engine document has no native design identity.");

        return new(
            DpViaCorridorZoomResult.CurrentSchema,
            "complete",
            analysis.Document.BoardGeneration,
            nativeDesign,
            analysis.Result.ReportPath,
            finding.Id,
            finding.Layer,
            "mils",
            ToBounds(viewport.RequestedBounds),
            ToBounds(viewport.ActualBounds));
    }


    public async Task<DpViaCorridorZoomResult> NavigateAsync(
        DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(finding);
        if (!ReferenceEquals(analysis, _currentAnalysis) ||
            analysis.ManagedScan is null ||
            !analysis.Result.Findings.Contains(finding) ||
            !analysis.IsCurrentFor(_session.State.Document))
        {
            throw new InvalidOperationException(
                "Only a finding from the current in-memory Engine analysis can be navigated. " +
                "Offline files and historical captures are not live display authority.");
        }

        RequireCapability(EngineCapabilities.Display);
        CorridorScan scan = analysis.ManagedScan;
        CorridorFinding source = scan.Findings.Single(item => item.Id == finding.Id);
        SceneQuery query = CorridorNavigation.CreateQuery(scan, source);
        if (_workspace.Document != analysis.Document)
        {
            throw new InvalidDataException(
                "The live Engine workspace no longer matches this captured corridor analysis. " +
                "Run the analysis again.");
        }

        System.Diagnostics.Stopwatch regionTimer = System.Diagnostics.Stopwatch.StartNew();
        LiveRegionScene region = await _workspace.ReadRegionAsync(query, cancellationToken);
        regionTimer.Stop();
        System.Diagnostics.Stopwatch validationTimer = System.Diagnostics.Stopwatch.StartNew();
        EngineWitnessMatch witnesses = CorridorNavigation.MatchFreshWitnesses(scan, source, region, analysis.Document);
        validationTimer.Stop();
        System.Diagnostics.Stopwatch zoomTimer = System.Diagnostics.Stopwatch.StartNew();
        EngineViewport viewport = await _workspace.Display.ZoomWitnessesAsync(
            region,
            region.Scene.Document.Bounds,
            new(finding.Layer),
            witnesses,
            cancellationToken);
        zoomTimer.Stop();
        EngineRegionTiming? regionTiming = region.Timing;
        LastNavigationPhases = new(
            regionTimer.ElapsedMilliseconds,
            validationTimer.ElapsedMilliseconds,
            zoomTimer.ElapsedMilliseconds,
            regionTiming?.NativeReadWallMilliseconds,
            regionTiming?.DecodingWallMilliseconds,
            regionTiming?.ConversionWallMilliseconds,
            regionTiming?.NativeCpuPhases?.EnumerationMilliseconds,
            regionTiming?.NativeCpuPhases?.MetadataMilliseconds,
            regionTiming?.NativeCpuPhases?.PadMilliseconds,
            regionTiming?.NativeCpuPhases?.ContourMilliseconds,
            regionTiming?.NativeCpuPhases?.ObjectLoopMilliseconds);
        if (viewport.Document != analysis.Document)
        {
            throw new InvalidDataException(
                "Engine navigation returned a viewport for another document.");
        }
        string nativeDesign = analysis.Document.Design ??
            throw new InvalidDataException(
                "The current Engine document has no native design identity.");

        return new(
            DpViaCorridorZoomResult.CurrentSchema,
            "complete",
            analysis.Document.BoardGeneration,
            nativeDesign,
            analysis.Result.ReportPath,
            finding.Id,
            finding.Layer,
            "mils",
            ToBounds(viewport.RequestedBounds),
            ToBounds(viewport.ActualBounds));
    }

    private void RequireCapability(EngineCapabilityId capabilityId)
    {
        EngineSessionSnapshot state = _session.State;
        if (state.ConnectionState != EngineConnectionState.Ready ||
            state.Document is null ||
            !_workspace.IsConnected)
        {
            throw new InvalidOperationException(
                "A ready Allegro Engine workspace is required.");
        }
        if (!state.Capabilities.Supports(capabilityId))
        {
            EngineCapability? capability = state.Capabilities.Items
                .FirstOrDefault(item => item.Id == capabilityId);
            throw new InvalidOperationException(
                capability?.UnavailableReason ??
                $"Engine capability '{capabilityId.Value}' is unavailable.");
        }
    }

    private static void ValidateOptions(DpViaCorridorOptions options)
    {
        if (options.MarginMils is < 0 or > 50 ||
            options.ModuleFilter is null ||
            options.ModuleFilter.Length > 64 ||
            options.ModuleFilter.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Invalid corridor margin or module filter.",
                nameof(options));
        }
    }

    private static void ValidateReportPath(string reportPath)
    {
        if (!Path.IsPathFullyQualified(reportPath) ||
            reportPath.Length > 1024 ||
            reportPath.Any(char.IsControl) ||
            !string.Equals(
                Path.GetExtension(reportPath),
                ".rpt",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A fully qualified .rpt output path is required.",
                nameof(reportPath));
        }
    }

    private static DpViaCorridorBounds ToBounds(DesignBounds bounds) =>
        new(
            (double)bounds.Minimum.X,
            (double)bounds.Minimum.Y,
            (double)bounds.Maximum.X,
            (double)bounds.Maximum.Y);

    private static async Task WriteManagedReportAsync(
        CorridorScan scan,
        string design,
        string reportPath,
        CancellationToken cancellationToken)
    {
        string navigatorPath = reportPath + ".dat";
        string directory = Path.GetDirectoryName(reportPath)!;
        Directory.CreateDirectory(directory);
        string temporaryReport = Path.Combine(
            directory,
            $".dpvc-{Guid.NewGuid():N}.tmp");
        string temporaryNavigator = temporaryReport + ".dat";
        bool movedNavigator = false;
        try
        {
            var text = new StringBuilder();
            text.AppendLine("Differential Pair Via Corridor Screening Report");
            text.AppendLine($"Design: {design}");
            text.AppendLine($"Algorithm: {CorridorAnalyzer.Algorithm}");
            text.AppendLine(
                $"Input status: {(scan.HasCompleteInputs ? "Complete for reference screening" : "PARTIAL: no clear/pass conclusion is permitted")}");
            text.AppendLine(
                $"Native units: {scan.Scene.Document.NativeUnits}; report dimensions: mils");
            text.AppendLine(
                $"Scope: {scan.Options.ModuleName ?? "Whole board"}; " +
                $"Engine capture: {scan.Scene.Identity.CaptureId:N}; " +
                $"provider: {scan.Scene.Identity.Provenance.Provider}");
            string margin = scan.Options.MarginMils.ToString(
                "0.###",
                CultureInfo.InvariantCulture);
            text.AppendLine(
                $"Margin: {margin} mils; " +
                $"include unused pairs: {scan.Options.IncludeUnused}");
            text.AppendLine(
                $"Pairs: {scan.PairCount}; via corridors: {scan.CorridorCount}; " +
                $"findings: {scan.Findings.Count}");
            text.AppendLine(CorridorAnalyzer.Limitations);
            text.AppendLine(
                "Risk/category labels are retained rule classifications, " +
                "not measured coupling or capacitance.");
            foreach (string warning in scan.CoverageWarnings)
            {
                text.AppendLine("REVIEW REQUIRED: " + warning);
            }
            text.AppendLine();
            foreach (CorridorFinding finding in scan.Findings)
            {
                text.AppendLine(
                    $"{finding.Id} | {finding.Risk} | {finding.Category} | " +
                    $"{finding.PairName} | {finding.AggressorNet} | " +
                    $"{finding.Layer} | {finding.ObjectType}");
                string pX = FormatCoordinate(finding.P.X);
                string pY = FormatCoordinate(finding.P.Y);
                string nX = FormatCoordinate(finding.N.X);
                string nY = FormatCoordinate(finding.N.Y);
                string distance = FormatCoordinate(finding.DistanceMils);
                string halfWidth = FormatCoordinate(finding.HalfWidthMils);
                text.AppendLine(
                    $"  P=({pX}, {pY}); N=({nX}, {nY}); " +
                    $"distance={distance}; half-width={halfWidth}");
            }

            await File.WriteAllTextAsync(
                temporaryReport,
                text.ToString(),
                new UTF8Encoding(false),
                cancellationToken);
            await File.WriteAllTextAsync(
                temporaryNavigator,
                JsonSerializer.Serialize(
                    new
                    {
                        Schema = "pd-managed-corridor-export-v2",
                        Algorithm = CorridorAnalyzer.Algorithm,
                        Design = design,
                        Units = "mils",
                        scan.Scene.Document.NativeUnits,
                        scan.Options,
                        scan.PairCount,
                        scan.CorridorCount,
                        scan.HasCompleteInputs,
                        scan.CoverageWarnings,
                        Findings = scan.Findings,
                        CaptureId = scan.Scene.Identity.CaptureId,
                        Authority =
                            "Offline report only. Live navigation requires the current " +
                            "in-memory analysis and a fresh Engine region revalidation.",
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false),
                cancellationToken);
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

    private static string FormatCoordinate(decimal value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);

    private static string FormatCoordinate(double value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);
}
