using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CircuitHub.AllegroBridge;
using PD.PcbTools;
using PD.Simple.Corridor;

namespace PD.Simple;

public sealed partial class BridgeSession
{
    private AllegroPcbMutationResult? _lastEdit;
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
        var pcb = BeginOperation(CorridorCommand);
        var binding = RequireSession().Binding;
        string design = State.Design;
        _managedAnalysis = null;
        var task = AnalyzeCoreAsync(pcb, binding, design, options, reportPath);
        Track(task);
        return task.WaitAsync(cancellationToken);
    }

    private async Task<DpViaCorridorAnalysis> AnalyzeCoreAsync(AllegroPcbSession pcb, AllegroSessionBinding binding,
        string design, DpViaCorridorOptions options, string reportPath)
    {
        await Task.Yield();
        try
        {
            var read = await pcb.ReadBoardInputsAsync(new(string.IsNullOrWhiteSpace(options.ModuleFilter) ? null : options.ModuleFilter), _lifetime.Token);
            var inputs = read.RequireInputs();
            RequireCorridorContext(binding, design);
            var scan = await Task.Run(() => CorridorAnalyzer.Analyze(inputs,
                new((double)options.MarginMils, options.ModuleFilter, options.IncludeUnused), _lifetime.Token), _lifetime.Token);
            RequireCorridorContext(binding, design);
            await WriteManagedReportAsync(scan, design, reportPath, _lifetime.Token);
            RequireCorridorContext(binding, design);
            var shown = scan.Findings.Take(DpViaCorridorResult.MaximumFindings).Select(finding => new DpViaCorridorFinding(
                finding.Id, finding.PairName, finding.AggressorNet, finding.ObjectType, finding.Layer,
                finding.Category, finding.Risk, new(finding.P.X, finding.P.Y), new(finding.N.X, finding.N.Y),
                new(finding.Intrusion.X, finding.Intrusion.Y), finding.DistanceMils, finding.HalfWidthMils, finding.HalfLengthMils)).ToArray();
            var result = new DpViaCorridorResult("pd-dp-via-corridor-managed-v1", scan.HasCompleteInputs ? "complete" : "partial",
                binding.BoardGeneration, design, "mils", inputs.NativeUnits, reportPath, reportPath + ".dat", true,
                scan.PairCount, scan.CorridorCount, scan.Findings.Select(item => item.AggressorNet).Distinct(StringComparer.Ordinal).Count(),
                scan.Findings.Count, scan.Findings.Count(item => item.Risk == "CRITICAL"),
                scan.Findings.Count(item => item.Risk == "MEDIUM"), scan.Findings.Count(item => item.Risk == "LOW"),
                shown.Length < scan.Findings.Count, Array.AsReadOnly(shown))
            {
                CoverageWarnings = scan.CoverageWarnings
            };
            var analysis = new DpViaCorridorAnalysis(binding, result, State.CatalogGeneration) { ManagedScan = scan };
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
        RequireCorridorContext(analysis.Binding, analysis.Result.Design);
        if (!ReferenceEquals(analysis, _managedAnalysis) || analysis.ManagedScan is null ||
            !analysis.Result.Findings.Contains(finding) ||
            !analysis.IsCurrentFor(State.SessionId, State.BoardGeneration, State.CatalogGeneration))
        {
            throw new InvalidOperationException("Only a finding from the current in-memory analysis can be navigated. Offline files are not native authority.");
        }
        var pcb = BeginOperation(ZoomCommand);
        var task = NavigateCoreAsync(pcb, analysis, finding);
        Track(task);
        return task.WaitAsync(cancellationToken);
    }

    private async Task<DpViaCorridorZoomResult> NavigateCoreAsync(AllegroPcbSession pcb,
        DpViaCorridorAnalysis analysis, DpViaCorridorFinding finding)
    {
        await Task.Yield();
        try
        {
            var scan = analysis.ManagedScan!;
            var source = scan.Findings.Single(item => item.Id == finding.Id);
            var query = CorridorNavigation.CreateQuery(scan, source);
            var region = await pcb.ReadRegionAsync(query, _lifetime.Token);
            var geometry = region.RequireGeometry();
            RequireCorridorContext(analysis.Binding, analysis.Result.Design);
            CorridorNavigation.ValidateFreshRead(scan, source, geometry);
            // Bounds may be normalized onto the native design grid. The SDK
            // validates requested-versus-returned bounds; use that observed box.
            var navigation = await pcb.ZoomRegionAsync(region, geometry.Bounds, finding.Layer, _lifetime.Token);
            RequireCorridorReceipt(navigation.Receipt, analysis.Binding, ZoomCommand);
            RequireCorridorContext(analysis.Binding, analysis.Result.Design);
            var viewport = navigation.Viewport ?? throw new InvalidDataException("Native navigation has no verified viewport.");
            return new(DpViaCorridorZoomResult.CurrentSchema, "complete", analysis.Binding.BoardGeneration,
                analysis.Result.Design, analysis.Result.ReportPath, finding.Id, finding.Layer, "mils",
                ToBounds(viewport.RequestedBounds), ToBounds(viewport.ActualBounds));
        }
        finally
        {
            EndOperation();
        }
    }

    private static DpViaCorridorBounds ToBounds(AllegroPcbBounds bounds) =>
        new(bounds.Minimum.X, bounds.Minimum.Y, bounds.Maximum.X, bounds.Maximum.Y);

    private async Task<AllegroOperationReceipt> ExecuteRouteAsync(AllegroPcbSession pcb, decimal width)
    {
        await Task.Yield();
        var binding = RequireSession().Binding;
        bool dispatchUnresolved = false;
        bool completed = false;
        IAllegroOperationHandle? handle = null;
        try
        {
            var catalog = (await pcb.ReadLayersAsync(_lifetime.Token)).RequireCatalog();
            string layer = HorizontalFirstPlanner.SelectLayer(catalog);
            var geometry = await pcb.ReadRoutingContextAsync(_lifetime.Token);
            ThrowIfRouteCancelledBeforeEdit();
            dispatchUnresolved = true;
            var pick = await pcb.PickEndpointsAsync(_lifetime.Token);
            handle = pick.Operation;
            await AdmitRouteHandleAsync(handle);
            PresentRoute(() => _overlay?.Begin(binding.BoardGeneration));
            var completion = await InteractiveRouteCompletion.WaitAsync(handle,
                token => ReadFeedbackAsync(handle, token), _lifetime.Token);
            dispatchUnresolved = false;
            if (completion.FeedbackFailure is { } feedbackFailure)
            {
                ReportRoutePresentationFailure(feedbackFailure);
            }
            var endpoints = await pick.WaitForResultAsync(_lifetime.Token);
            if (endpoints.Receipt.State != AllegroOperationState.Complete)
            {
                return endpoints.Receipt;
            }
            ThrowIfRouteCancelledBeforeEdit();
            var plan = HorizontalFirstPlanner.Plan(endpoints.Endpoints!, (double)width, layer);
            var prepared = await pcb.PrepareTraceAsync(geometry, endpoints, plan, _lifetime.Token);
            ThrowIfRouteCancelledBeforeEdit();
            await DisposeRouteHandleAsync(handle);
            handle = null;
            PrepareNextRouteStage();
            ThrowIfRouteCancelledBeforeEdit();
            // StartAsync owns the one-attempt guard. Failure here can mean
            // dispatch occurred; do not infer absence of native effects.
            dispatchUnresolved = true;
            var mutation = await prepared.StartAsync(_lifetime.Token);
            handle = mutation.Operation;
            await AdmitRouteHandleAsync(handle);
            var result = await mutation.WaitForResultAsync(_lifetime.Token);
            dispatchUnresolved = false;
            AdmitCoreMutation(result, isUndo: false);
            completed = result.IsVerifiedSuccess && !_outcomeUncertain;
            if (completed)
            {
                PresentRoute(() => _overlay?.CompleteSuccessfully());
            }
            return result.Receipt;
        }
        catch (Exception error)
        {
            if (dispatchUnresolved)
            {
                _outcomeUncertain = true;
                _lastEdit = null;
                _undoBinding = null;
            }
            Post(() => Faulted?.Invoke(this, dispatchUnresolved ? FailureMessage(error) : error.Message));
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _routeReady?.TrySetResult(null);
            }
            if (!completed)
            {
                PresentRoute(() => _overlay?.End());
            }
            await DisposeRouteHandleAsync(handle);
            EndOperation();
        }
    }

    private async Task<AllegroOperationReceipt> ExecuteUndoAsync(AllegroPcbSession pcb)
    {
        await Task.Yield();
        var edit = _lastEdit;
        _lastEdit = null;
        _undoBinding = null;
        IAllegroOperationHandle? handle = null;
        bool admitted = false;
        try
        {
            var operation = await pcb.UndoTraceAsync(edit ?? throw new InvalidOperationException("The edit-specific recovery receipt is missing."), _lifetime.Token);
            handle = operation.Operation;
            var result = await operation.WaitForResultAsync(_lifetime.Token);
            AdmitCoreMutation(result, isUndo: true);
            admitted = true;
            return result.Receipt;
        }
        catch (Exception error)
        {
            if (!admitted)
            {
                _outcomeUncertain = true;
                _recoveryRequired = false;
            }
            Post(() => Faulted?.Invoke(this, FailureMessage(error)));
            throw;
        }
        finally
        {
            await DisposeRouteHandleAsync(handle);
            EndOperation();
        }
    }

    private void AdmitCoreMutation(AllegroPcbMutationResult result, bool isUndo)
    {
        bool same = SameBoard(result.Receipt.Binding) && SameNativeIdentity(RequireSession().Binding, result.Receipt.Binding);
        _outcomeUncertain = !same || result.Mutation == AllegroPcbMutationState.Uncertain;
        _recoveryRequired = same && result.CanRecover && (!result.IsVerifiedSuccess || isUndo);
        _lastEdit = same && result.CanRecover ? result : null;
        _undoBinding = _lastEdit?.Receipt.Binding;
        if (result.EvidenceError is { } error)
        {
            Post(() => Faulted?.Invoke(this, error));
        }
    }

    private void ThrowIfRouteCancelledBeforeEdit()
    {
        if (_routeCancelRequested || _disposed || _lifetime.IsCancellationRequested)
        {
            throw new OperationCanceledException("Route cancelled before trace dispatch; no trace was created.", _lifetime.Token);
        }
    }

    private void PrepareNextRouteStage()
    {
        lock (_gate)
        {
            _route = null;
            _routeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private async Task AdmitRouteHandleAsync(IAllegroOperationHandle handle)
    {
        bool cancel;
        lock (_gate)
        {
            _route = handle;
            _routeReady!.TrySetResult(handle);
            cancel = _routeCancelRequested || _disposed;
        }
        if (cancel)
        {
            await CancelNativeRouteOnceAsync(handle);
        }
    }

    private async Task DisposeRouteHandleAsync(IAllegroOperationHandle? handle)
    {
        if (handle is null)
        {
            return;
        }
        try
        {
            await handle.DisposeAsync();
        }
        catch (Exception error)
        {
            ReportRoutePresentationFailure(error);
        }
    }

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
            text.AppendLine($"Native units: {scan.Inputs.NativeUnits}; report dimensions: mils");
            text.AppendLine($"Scope: {scan.Options.ModuleName ?? "Whole board"}; native capture: {scan.Inputs.StateToken}; pages: {scan.Inputs.PageCount}");
            text.AppendLine(FormattableString.Invariant($"Margin: {scan.Options.MarginMils:0.###} mils; include unused pairs: {scan.Options.IncludeUnused}"));
            text.AppendLine($"Pairs: {scan.PairCount}; via corridors: {scan.CorridorCount}; findings: {scan.Findings.Count}");
            text.AppendLine(CorridorAnalyzer.Limitations);
            text.AppendLine("Risk/category labels are retained rule classifications, not measured coupling or capacitance.");
            foreach (string warning in scan.CoverageWarnings)
            {
                text.AppendLine("REVIEW REQUIRED: " + warning);
            }
            text.AppendLine();
            foreach (var finding in scan.Findings)
            {
                text.AppendLine(FormattableString.Invariant($"{finding.Id} | {finding.Risk} | {finding.Category} | {finding.PairName} | {finding.AggressorNet} | {finding.Layer} | {finding.ObjectType}"));
                text.AppendLine(FormattableString.Invariant($"  P=({finding.P.X:0.##########}, {finding.P.Y:0.##########}); N=({finding.N.X:0.##########}, {finding.N.Y:0.##########}); distance={finding.DistanceMils:0.##########}; half-width={finding.HalfWidthMils:0.##########}"));
            }
            await File.WriteAllTextAsync(temporaryReport, text.ToString(), new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(temporaryNavigator, JsonSerializer.Serialize(new
            {
                Schema = "pd-managed-corridor-export-v1", Algorithm = CorridorAnalyzer.Algorithm,
                Design = design, Units = "mils", scan.Inputs.NativeUnits, scan.Options,
                scan.PairCount, scan.CorridorCount, scan.HasCompleteInputs, scan.CoverageWarnings,
                Findings = scan.Findings,
                Authority = "Offline report only. Live navigation requires the current in-memory analysis and fresh SDK revalidation."
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
