using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;
using PD.Simple.LargeBoards;

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
    private readonly IAcquisitionDiagnosticOwner _acquisitionDiagnostics;
    private readonly Func<string?> _nativeProductProvider;
    private readonly string _applicationVersion;
    private readonly string _engineVersion;
    private DpViaCorridorAnalysis? _currentAnalysis;

    internal EngineDpViaCorridorService(
        AllegroEngineSession session,
        IAcquisitionDiagnosticOwner? acquisitionDiagnostics = null,
        Func<string?>? nativeProductProvider = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _workspace = session.Workspace;
        _nativeProductProvider = nativeProductProvider ??
            (() => Environment.GetEnvironmentVariable("PD_SIMPLE_NATIVE_PRODUCT"));
        string diagnosticsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PD-Simple",
            "diagnostics",
            "acquisition.jsonl");
        _acquisitionDiagnostics = acquisitionDiagnostics ??
            new JsonLinesAcquisitionDiagnosticOwner(diagnosticsPath);
        _applicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ??
            "unknown";
        _engineVersion = typeof(AllegroWorkspace).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
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
        string nativeProduct = EngineLargeBoardCaptureGateway.RequireNativeProduct(
            _nativeProductProvider());
        _currentAnalysis = null;
        await TryPublishCandidatesAsync(options, cancellationToken);

        string? module = string.IsNullOrWhiteSpace(options.ModuleFilter)
            ? null
            : options.ModuleFilter;
        var stageTimer = Stopwatch.StartNew();
        DpViaCorridorCaptureResult captured = await DpViaCorridorCaptureRunner.RunAsync(
            new EngineLargeBoardCaptureGateway(_workspace, () => nativeProduct),
            _acquisitionDiagnostics,
            () => _session.State.Document,
            _applicationVersion,
            _engineVersion,
            new((double)options.MarginMils, module, options.IncludeUnused, options.PairPolicy),
            Application.Current?.MainWindow?.IsVisible == true,
            cancellationToken,
            options.Progress);
        ScalableCorridorScan scalable = captured.Run.Scan;
        DesignScene scene = scalable.PlanningScene;
        // The report projection contains all findings. Navigation always uses
        // the scalable scan's admitted three-object witness for each finding.
        var scan = new CorridorScan(
            scene,
            scalable.Options,
            scalable.PairCount,
            scalable.CorridorCount,
            scalable.Findings.Select(item => item.Finding).ToArray(),
            scalable.CoverageWarnings);
        bool stagedObservation = captured.PlanningCapture is not null ||
            captured.ObservationStartupMilliseconds is not null;
        long acquisitionMilliseconds;
        long? nativeCommandMilliseconds;
        long? snapshotTransferMilliseconds;
        long? nativeReleaseMilliseconds;
        if (captured.PlanningCapture is { } planningCapture)
        {
            // Staged run: the positional Capture is the final scan (B) and
            // PlanningCapture is the planning catalog (A). Aggregate from the
            // terminal receipt (observation startup plus both capture totals);
            // planning replay and analysis wall time stay out of acquisition.
            acquisitionMilliseconds = captured.TerminalReceipt?.CaptureElapsedMilliseconds ??
                (captured.ObservationStartupMilliseconds.GetValueOrDefault() +
                    (planningCapture.Timing?.TotalMilliseconds ?? 0) +
                    (captured.Capture.Timing?.TotalMilliseconds ?? 0));
            nativeCommandMilliseconds = SumWhenBothPresent(
                planningCapture.Timing?.NativeCommandMilliseconds,
                captured.Capture.Timing?.NativeCommandMilliseconds);
            snapshotTransferMilliseconds = SumWhenBothPresent(
                planningCapture.Timing?.PageTransferAndSealMilliseconds,
                captured.Capture.Timing?.PageTransferAndSealMilliseconds);
            nativeReleaseMilliseconds = SumWhenBothPresent(
                planningCapture.Timing?.NativeReleaseMilliseconds,
                captured.Capture.Timing?.NativeReleaseMilliseconds);
        }
        else
        {
            acquisitionMilliseconds = captured.Capture.Timing?.TotalMilliseconds ??
                Math.Max(0, stageTimer.ElapsedMilliseconds - captured.Run.Timings.TotalMilliseconds);
            if (captured.Capture.Timing is not null)
            {
                // A staged observation whose planning store was reused as the
                // scan store charges the frozen-observation lease once on top
                // of the single capture total. Classic single captures carry
                // no lease, so this addition is a no-op for them.
                acquisitionMilliseconds += captured.ObservationStartupMilliseconds.GetValueOrDefault();
            }
            nativeCommandMilliseconds = captured.Capture.Timing?.NativeCommandMilliseconds;
            snapshotTransferMilliseconds = captured.Capture.Timing?.PageTransferAndSealMilliseconds;
            nativeReleaseMilliseconds = captured.Capture.Timing?.NativeReleaseMilliseconds;
        }
        long analysisMilliseconds = captured.Run.Timings.TotalMilliseconds;
        DpViaCorridorFinding[] all = scan.Findings
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
            DpViaCorridorResult.ManagedSchema,
            scan.HasCompleteInputs ? "complete" : "partial",
            captured.Document.BoardGeneration,
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
            Array.AsReadOnly(all))
        {
            CoverageWarnings = scan.CoverageWarnings,
            BlockingCoverageWarnings = scalable.BlockingCoverageWarnings,
            CaptureStartedAt = captured.TerminalReceipt?.StartedAt,
            CaptureCompletedAt = CaptureCompletedAtFor(captured),
            SourceExportedAt = SourceExportedAtFor(captured),
        };
        cancellationToken.ThrowIfCancellationRequested();
        stageTimer.Restart();
        options.Progress?.Report("Writing the complete corridor report…");
        AcquisitionDiagnosticReceipt scanReceipt = captured.TerminalReceipt ??
            throw new InvalidDataException("The corridor capture has no terminal cleanup receipt.");
        bool reportPublished = false;
        try
        {
            await WriteManagedReportAsync(
                scan,
                scene.Document.Name,
                reportPath,
                captured,
                () => RequireCapturedDocument(captured),
                cancellationToken);
            reportPublished = true;
            await _acquisitionDiagnostics.RetainAsync(scanReceipt with
            {
                Feature = "dp-via-corridor-run",
                UserMessage = "The complete corridor result and report are ready; the capture store was released.",
            }, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            RequireCapturedDocument(captured);
        }
        catch (Exception exception)
        {
            if (reportPublished)
            {
                File.Delete(reportPath);
                File.Delete(reportPath + ".dat");
            }
            await _acquisitionDiagnostics.RetainAsync(scanReceipt with
            {
                Feature = "dp-via-corridor-run",
                TerminalState = exception is OperationCanceledException
                    ? AcquisitionTerminalState.Cancelled
                    : AcquisitionTerminalState.Failed,
                PartialDataWithheld = true,
                FailureCode = exception is OperationCanceledException ? "cancelled" : "report_publication_failed",
                UserMessage = "The corridor result was not published; the capture store was released.",
            }, CancellationToken.None);
            throw;
        }
        long reportMilliseconds = stageTimer.ElapsedMilliseconds;
        var analysis = new DpViaCorridorAnalysis(captured.Document, result)
        {
            ScalableScan = scalable,
            CaptureEvidence = captured,
            Timings = new(
                acquisitionMilliseconds,
                analysisMilliseconds,
                reportMilliseconds,
                nativeCommandMilliseconds,
                snapshotTransferMilliseconds,
                nativeReleaseMilliseconds,
                captured.Run.Timings.GlobalViaReplayMilliseconds +
                    captured.Run.Timings.BoundedReplayMilliseconds)
            {
                ObservationStartupMilliseconds = stagedObservation
                    ? captured.ObservationStartupMilliseconds
                    : null,
                PlanningCaptureMilliseconds =
                    captured.PlanningCapture?.Timing?.TotalMilliseconds,
                ScanCaptureMilliseconds = stagedObservation
                    ? captured.Capture.Timing?.TotalMilliseconds
                    : null,
            },
        };
        _currentAnalysis = analysis;
        return analysis;
    }

    /// <summary>
    /// Provisional preflight: reads live net metadata and publishes candidate
    /// pairs before the expensive capture starts. Failures here never block
    /// the full scan; cancellation still aborts the whole run.
    /// </summary>
    private async Task TryPublishCandidatesAsync(
        DpViaCorridorOptions options,
        CancellationToken cancellationToken)
    {
        if (options.CandidateProgress is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            options.Progress?.Report("Reading candidate net pairs…");
            SceneQuery candidateQuery = CorridorAnalyzer.CreateCandidateQuery(options.PairPolicy);
            LiveDesignScene live = await _workspace.ReadAsync(candidateQuery, cancellationToken);
            live.RequireCurrent();
            cancellationToken.ThrowIfCancellationRequested();
            CorridorCandidateDigest digest = CorridorAnalyzer.DiscoverCandidates(
                live.Scene,
                options.PairPolicy,
                options.IncludeUnused,
                cancellationToken);
            DpViaCorridorCandidatePair[] pairs = digest.Pairs
                .Select(item => new DpViaCorridorCandidatePair(item.PairName, item.PositiveNet, item.NegativeNet))
                .ToArray();
            var snapshot = new DpViaCorridorCandidateSnapshot(
                live.Document,
                live,
                Array.AsReadOnly(pairs),
                digest.CoverageWarnings,
                digest.Policy,
                digest.IncludeUnused,
                DateTimeOffset.UtcNow);
            options.CandidateProgress.Report(snapshot);
            options.Progress?.Report(
                $"Found {pairs.Length:N0} candidate net pairs; starting full corridor capture…");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            options.CandidateIssueProgress?.Report(
                $"{error.GetType().Name}: {error.Message}");
            options.Progress?.Report(
                "Candidate net pairs unavailable (" + error.Message +
                "); continuing with full corridor screening…");
        }
    }

    private static long? SumWhenBothPresent(long? planningMilliseconds, long? scanMilliseconds) =>
        planningMilliseconds is { } planning && scanMilliseconds is { } scan
            ? planning + scan
            : null;

    private static DateTimeOffset? CaptureCompletedAtFor(DpViaCorridorCaptureResult captured)
    {
        // StartedAt plus the receipt's capture elapsed time is the capture
        // seal time only when one capture ran. For staged runs the elapsed
        // sum spans observation startup plus both captures with planning
        // replay between stages, so it names no real event; the exact B seal
        // time is unavailable and stays null rather than invented.
        if (captured.PlanningCapture is not null)
        {
            return null;
        }
        return captured.TerminalReceipt is { } receipt
            ? receipt.StartedAt.AddMilliseconds(receipt.CaptureElapsedMilliseconds)
            : null;
    }

    private static string ScanCaptureIdentityText(DpViaCorridorCaptureResult captured)
    {
        // The report scene is the planning catalog (A); the scan capture (B)
        // is the acquisition the detail replay ran against. Single-capture
        // runs replay both stages from the same store, so the same identity
        // text stays truthful on the legacy path.
        LargeBoardCaptureIdentity identity = captured.Capture.Identity;
        string text =
            $"token {identity.CaptureToken}; session {identity.SessionId}; " +
            $"board generation {identity.BoardGeneration}; protocol {identity.ProtocolVersion}";
        if (captured.Capture.SourceTraversal?.ObservationToken is { Length: > 0 } observation)
        {
            text += $"; frozen source observation {observation}";
        }
        return text;
    }

    private static DateTimeOffset? SourceExportedAtFor(DpViaCorridorCaptureResult captured) =>
        // Staged captures share one verified frozen source; B carries it and
        // A is only a fallback for stores without B-side provenance.
        captured.Capture.SourceTraversal?.FrozenSource?.ExportedAt ??
        captured.PlanningCapture?.SourceTraversal?.FrozenSource?.ExportedAt;

    private void RequireCapturedDocument(DpViaCorridorCaptureResult captured)
    {
        if (captured.PlanningCapture is { } planningCapture)
        {
            LargeBoardPublicationFence.RequireCurrent(
                planningCapture.Identity, _session.State.Document);
        }
        LargeBoardPublicationFence.RequireCurrent(captured.Run.CaptureIdentity, _session.State.Document);
        if (_session.State.Document != captured.Document || !_workspace.IsConnected)
        {
            throw new InvalidDataException("The Engine document changed during corridor screening. Run again.");
        }
    }

    public DpViaCorridorNavigationPhases? LastNavigationPhases { get; private set; }

    /// <summary>
    /// Direct-scene browsing admits a lightweight navigation ticket from the
    /// current analysis scene and navigates without a fresh region read.
    /// The native command resolves each admitted witness to exactly one
    /// live object, rejecting stale and ambiguous witnesses. This proves
    /// witness identity at navigation time only, not full finding
    /// revalidation; a ticket never authorizes edits. Callers needing the
    /// strict fresh-region witness recheck must use <see cref="NavigateAsync"/>.
    /// Sealed bulk results use the same guarded ticket for Browse. After the
    /// native zoom, a metadata-only live scene supplies WPF presentation
    /// identity without turning Browse into a fresh copper-region check.
    /// </summary>
    public async Task<DpViaCorridorNavigationOutcome> BrowseAsync(
        DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding,
        DpViaCorridorNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(request);
        if (!ReferenceEquals(analysis, _currentAnalysis) ||
            (analysis.ManagedScan is null && analysis.ScalableScan is null) ||
            !analysis.Result.Findings.Contains(finding) ||
            !analysis.IsCurrentFor(_session.State.Document))
        {
            throw new InvalidOperationException(
                "Only a finding from the current in-memory Engine analysis can be navigated. " +
                "Offline files and historical captures are not live display authority.");
        }

        RequireCapability(EngineCapabilities.Display);
        (CorridorScan scan, CorridorFinding source) = FindingSource(analysis, finding);
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
        var browsePhases = new DpViaCorridorNavigationPhases(
            0,
            0,
            browseTimer.ElapsedMilliseconds,
            Mode: DpViaCorridorNavigationMode.Browse);
        LastNavigationPhases = browsePhases;
        if (viewport.Document != analysis.Document)
        {
            throw new InvalidDataException(
                "Engine navigation returned a viewport for another document.");
        }
        if (analysis.ScalableScan is not null)
        {
            // The sealed witness scene is snapshot evidence, not a live WPF
            // source. Metadata acquisition avoids a second native copper
            // traversal; it does not upgrade Browse to fresh verification.
            analysis.LiveScene = await ReadLivePresentationSceneAsync(
                analysis, cancellationToken);
        }
        string nativeDesign = analysis.Document.Design ??
            throw new InvalidDataException(
                "The current Engine document has no native design identity.");

        var browseZoom = new DpViaCorridorZoomResult(
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
        return new DpViaCorridorNavigationOutcome(
            request.OperationId,
            request.Origin,
            DpViaCorridorNavigationMode.Browse,
            DpViaCorridorNavigationMode.Browse,
            DpViaCorridorVerification.WitnessIdentityAtNavigation,
            ticket.Token,
            finding.Id,
            CaptureIdOf(analysis),
            browseZoom,
            browsePhases);
    }


    /// <summary>
    /// Candidate net zoom through the snapshot's live metadata scene. The
    /// genuine semantic net reference is resolved by net name at navigation
    /// time; a renamed or absent net fails closed. Net navigation only.
    /// </summary>
    public async Task<DpViaCorridorCandidateNavigationOutcome> ZoomCandidateNetAsync(
        DpViaCorridorCandidateSnapshot snapshot,
        DpViaCorridorCandidatePair pair,
        bool positive,
        DpViaCorridorCandidateNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(request);
        if (!snapshot.Pairs.Contains(pair))
        {
            throw new ArgumentException("The candidate pair does not belong to this snapshot.", nameof(pair));
        }

        RequireCapability(EngineCapabilities.Display);
        snapshot.LiveScene.RequireCurrent();
        if (_workspace.Document != snapshot.Document ||
            _session.State.Document != snapshot.Document ||
            !_workspace.IsConnected)
        {
            throw new InvalidDataException(
                "The live Engine workspace no longer matches this candidate snapshot. Run the analysis again.");
        }

        string netName = positive ? pair.PositiveNet : pair.NegativeNet;
        SceneObjectReference target = DpViaCorridorCandidateNavigation.ResolveNetReference(
            snapshot.LiveScene.Scene,
            netName);
        var zoomTimer = Stopwatch.StartNew();
        await _workspace.Display.ZoomAsync(snapshot.LiveScene, target, cancellationToken);
        zoomTimer.Stop();
        snapshot.LiveScene.RequireCurrent();
        if (_workspace.Document != snapshot.Document)
        {
            throw new InvalidDataException("Engine candidate navigation returned for another document.");
        }

        return new(
            request.OperationId,
            request.Origin,
            pair.PairName,
            netName,
            positive,
            snapshot.CaptureId,
            zoomTimer.ElapsedMilliseconds);
    }

    public async Task<DpViaCorridorNavigationOutcome> NavigateAsync(
        DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding,
        DpViaCorridorNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(request);
        if (!ReferenceEquals(analysis, _currentAnalysis) ||
            (analysis.ManagedScan is null && analysis.ScalableScan is null) ||
            !analysis.Result.Findings.Contains(finding) ||
            !analysis.IsCurrentFor(_session.State.Document))
        {
            throw new InvalidOperationException(
                "Only a finding from the current in-memory Engine analysis can be navigated. " +
                "Offline files and historical captures are not live display authority.");
        }

        RequireCapability(EngineCapabilities.Display);
        (CorridorScan scan, CorridorFinding source) = FindingSource(analysis, finding);
        SceneQuery query = CorridorNavigation.CreateQuery(scan, source);
        if (_workspace.Document != analysis.Document)
        {
            throw new InvalidDataException(
                "The live Engine workspace no longer matches this captured corridor analysis. " +
                "Run the analysis again.");
        }

        request.Progress?.Report(DpViaCorridorNavigationStage.ReadingRegion);
        System.Diagnostics.Stopwatch regionTimer = System.Diagnostics.Stopwatch.StartNew();
        LiveRegionScene region = await _workspace.ReadRegionAsync(query, cancellationToken);
        regionTimer.Stop();
        request.Progress?.Report(DpViaCorridorNavigationStage.MatchingWitnesses);
        System.Diagnostics.Stopwatch validationTimer = System.Diagnostics.Stopwatch.StartNew();
        CorridorNavigationEvidence evidence = CorridorNavigation.MatchFreshWitnessesWithEvidence(
            scan, source, region, analysis.Document);
        EngineWitnessMatch witnesses = evidence.Witnesses;
        validationTimer.Stop();
        request.Progress?.Report(DpViaCorridorNavigationStage.Zooming);
        System.Diagnostics.Stopwatch zoomTimer = System.Diagnostics.Stopwatch.StartNew();
        EngineViewport viewport = await _workspace.Display.ZoomWitnessesAsync(
            region,
            region.Scene.Document.Bounds,
            new(finding.Layer),
            witnesses,
            cancellationToken);
        zoomTimer.Stop();
        EngineRegionTiming? regionTiming = region.Timing;
        var strictPhases = new DpViaCorridorNavigationPhases(
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
        LastNavigationPhases = strictPhases;
        if (viewport.Document != analysis.Document)
        {
            throw new InvalidDataException(
                "Engine navigation returned a viewport for another document.");
        }
        if (analysis.ScalableScan is not null)
        {
            // WPF presentation needs a current Engine scene, not another
            // region traversal after strict witness verification.
            request.Progress?.Report(DpViaCorridorNavigationStage.ReadingPresentation);
            analysis.LiveScene = await ReadLivePresentationSceneAsync(
                analysis, cancellationToken);
        }
        string nativeDesign = analysis.Document.Design ??
            throw new InvalidDataException(
                "The current Engine document has no native design identity.");

        var strictZoom = new DpViaCorridorZoomResult(
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
        return new DpViaCorridorNavigationOutcome(
            request.OperationId,
            request.Origin,
            DpViaCorridorNavigationMode.Revalidate,
            DpViaCorridorNavigationMode.Revalidate,
            DpViaCorridorVerification.FreshRegionSelectedWitnessMatch,
            region.NativeOperationId.ToString("N"),
            finding.Id,
            CaptureIdOf(analysis),
            strictZoom,
            strictPhases)
        {
            DetailedEvidence = evidence.DetailedEvidence,
        };
    }

    private async Task<LiveDesignScene> ReadLivePresentationSceneAsync(
        DpViaCorridorAnalysis analysis,
        CancellationToken cancellationToken)
    {
        SceneQuery query = SceneQuery.Metadata with
        {
            Families = [DataFamily.Layers],
            CopperKinds = [],
        };
        LiveDesignScene presentation = await _workspace.ReadAsync(
            query, cancellationToken);
        presentation.RequireCurrent();
        if (presentation.Document != analysis.Document ||
            !ReferenceEquals(_currentAnalysis, analysis))
        {
            throw new InvalidDataException(
                "The selected finding's live presentation document changed.");
        }
        return presentation;
    }

    private static Guid CaptureIdOf(DpViaCorridorAnalysis analysis) =>
        analysis.CaptureId ??
        throw new InvalidDataException(
            "The corridor analysis has no captured Engine evidence. Run the analysis again.");

    private static (CorridorScan Scan, CorridorFinding Finding) FindingSource(
        DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding)
    {
        if (analysis.ScalableScan is { } scalable)
        {
            ScalableCorridorFinding selected = scalable.Findings.Single(item => item.Finding.Id == finding.Id);
            CorridorFinding witnessed = selected.Finding with
            {
                PositiveViaIndex = 0,
                NegativeViaIndex = 1,
                AggressorIndex = 2,
            };
            return (new(
                scalable.CreateSourceWitnessScene(selected),
                scalable.Options,
                scalable.PairCount,
                scalable.CorridorCount,
                [witnessed],
                scalable.CoverageWarnings), witnessed);
        }
        CorridorScan scan = analysis.ManagedScan ??
            throw new InvalidDataException("The corridor analysis has no captured witnesses.");
        return (scan, scan.Findings.Single(item => item.Id == finding.Id));
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
        DpViaCorridorCaptureResult captured,
        Action requireCurrent,
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
        bool movedReport = false;
        try
        {
            string reportText = CorridorReportText.Build(scan, design, ScanCaptureIdentityText(captured));

            await File.WriteAllTextAsync(
                temporaryReport,
                reportText,
                new UTF8Encoding(false),
                cancellationToken);
            // The navigator payload keeps Acquisition as the scan capture (B)
            // and Replay.CaptureIdentity as the scan authority. Staged keys
            // appear only when staged evidence exists, so single-capture
            // payloads keep their exact historical shape.
            var replayEvidence = new Dictionary<string, object?>
            {
                ["CaptureIdentity"] = captured.Run.CaptureIdentity,
                ["Counts"] = captured.Run.Counts,
                ["Timings"] = captured.Run.Timings,
                ["Budgets"] = captured.Run.ReplayBudgets,
            };
            if (captured.PlanningCapture is { } stagedPlanningCapture)
            {
                replayEvidence["PlanningCaptureIdentity"] = stagedPlanningCapture.Identity;
            }
            var navigatorEvidence = new Dictionary<string, object?>
            {
                ["Schema"] = "pd-managed-corridor-export-v2",
                ["Algorithm"] = CorridorAnalyzer.Algorithm,
                ["Design"] = design,
                ["Units"] = "mils",
                ["NativeUnits"] = scan.Scene.Document.NativeUnits,
                ["Options"] = scan.Options,
                ["PairCount"] = scan.PairCount,
                ["CorridorCount"] = scan.CorridorCount,
                ["HasCompleteInputs"] = scan.HasCompleteInputs,
                ["CoverageWarnings"] = scan.CoverageWarnings,
                ["Findings"] = scan.Findings,
                ["CaptureId"] = scan.Scene.Identity.CaptureId,
                ["CaptureStartedAt"] = captured.TerminalReceipt?.StartedAt,
                ["CaptureCompletedAt"] = CaptureCompletedAtFor(captured),
                ["SourceExportedAt"] = SourceExportedAtFor(captured),
                ["AcquisitionCorrelationId"] = captured.TerminalReceipt?.CorrelationId,
                ["Acquisition"] = captured.Capture,
            };
            if (captured.PlanningCapture is { } planningAcquisition)
            {
                navigatorEvidence["PlanningAcquisition"] = planningAcquisition;
            }
            if (captured.ObservationStartupMilliseconds is { } observationStartupMilliseconds)
            {
                navigatorEvidence["ObservationStartupMilliseconds"] = observationStartupMilliseconds;
            }
            navigatorEvidence["Replay"] = replayEvidence;
            navigatorEvidence["Authority"] =
                "Offline report only. Fast Browse verifies selected witness identity at navigation; " +
                "explicit Revalidate checks selected witnesses in a fresh Engine region. " +
                "Neither certifies a live full-board Clear/Pass result.";
            await File.WriteAllTextAsync(
                temporaryNavigator,
                JsonSerializer.Serialize(
                    navigatorEvidence,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            requireCurrent();
            File.Move(temporaryNavigator, navigatorPath, overwrite: false);
            movedNavigator = true;
            File.Move(temporaryReport, reportPath, overwrite: false);
            movedReport = true;
            cancellationToken.ThrowIfCancellationRequested();
            requireCurrent();
        }
        catch
        {
            if (movedNavigator)
            {
                File.Delete(navigatorPath);
            }
            if (movedReport)
            {
                File.Delete(reportPath);
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
