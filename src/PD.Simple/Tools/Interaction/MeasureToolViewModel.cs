using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.PcbTools.MeasureTools;
using PD.PcbTools.OverlayTools;
using PD.PcbTools.Review;

namespace PD.Simple.Tools.Interaction;

/// <summary>
/// T03 Pick/measure/ruler: captured two-point measurement and live native
/// picking through the Engine pick lifecycle, snap policy, persistent
/// movable/removable rulers published as display-only dimension drawings with
/// per-request receipts, units switching, and copy/export. Measurements and
/// picks perform zero native copper mutation: the only Engine calls are scene
/// read, native pick observation, and overlay presentation. Visual
/// (captured-scene) checks stay advisory; native object kind/net rides on the
/// pick result as native authority.
/// </summary>
public sealed class MeasureToolViewModel : INotifyPropertyChanged, IDisposable
{
    private const string ScopeGroupId = "tools-b-measure";

    private readonly BridgeSession _bridge;
    private readonly EngineWpfPresentation _presentation;
    private readonly MeasureSession _session = new();
    private readonly ToolPublicationTracker _tracker = new();
    private readonly object _gate = new();

    private LiveDesignScene? _live;
    private EngineEndpointPick? _pick;
    private Guid _pickOperation = Guid.Empty;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _disposed;

    private string _status = "Acquire a live scene to begin. This page opens offline; measuring needs a scene, native picking needs Allegro.";
    private string _result = "No measurement yet.";
    private string _feedback = string.Empty;
    private string _validation = string.Empty;
    private string _publication = "No ruler publication yet.";
    private string _firstX = "0";
    private string _firstY = "0";
    private string _secondX = "300";
    private string _secondY = "400";
    private string _snapMode = "Grid 1 mil";
    private string _gridStep = "1";
    private bool _useMillimeters;
    private string _rulerLabel = string.Empty;
    private string _objectId = string.Empty;
    private string _exportPath = string.Empty;
    private MeasureRulerRecord? _selectedRuler;
    private Guid _scopeOperation = Guid.Empty;
    private long _scopeRevision;

    public MeasureToolViewModel(BridgeSession bridge, EngineWpfPresentation presentation)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        if (!ReferenceEquals(_presentation.Session, _bridge.EngineSession))
        {
            throw new ArgumentException(
                "The presentation must borrow this tool's Engine session.", nameof(presentation));
        }

        Rulers = new ObservableCollection<MeasureRulerRecord>();
        _bridge.StateChanged += Bridge_StateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the tool needs the shared Explorer Inspect section.</summary>
    public event EventHandler? NavigateToExplorerRequested;

    public ObservableCollection<MeasureRulerRecord> Rulers { get; }

    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string Result { get => _result; private set => SetField(ref _result, value); }
    public string Feedback { get => _feedback; private set => SetField(ref _feedback, value); }
    public string Validation { get => _validation; private set => SetField(ref _validation, value); }
    public string Publication { get => _publication; private set => SetField(ref _publication, value); }

    public bool IsBusy { get => _busy; private set { if (SetField(ref _busy, value)) RefreshGates(); } }
    public bool HasLiveScene => _live is not null;
    public bool HasCompletedSpan => _session.Completed is not null;
    public bool HasActivePick => _pick is not null;

    // Action gates. Pages open disconnected; only unsafe/unavailable actions disable.
    public bool CanAcquire => !_disposed && !_busy && _bridge.HasReadySession;
    public bool CanSetFirst => !_disposed && !_busy && HasLiveScene && _pick is null;
    public bool CanSetSecond => CanSetFirst && _session.HasFirst;
    public bool CanClearFirst => !_disposed && !_busy && _session.HasFirst && _pick is null;
    public bool CanStartNativePick => !_disposed && !_busy && HasLiveScene && _pick is null &&
        _bridge.HasReadySession && SupportsPicking();
    public bool CanClearNativeFirst => _pick is not null && !_pick.IsTerminal;
    public bool CanCancelNativePick => _pick is not null;
    public bool CanKeepRuler => !_disposed && !_busy && _pick is null && HasCompletedSpan;
    public bool CanMoveRuler => CanKeepRuler && _selectedRuler is not null;
    public bool CanRemoveRuler => !_disposed && !_busy && _pick is null && HasLiveScene && _selectedRuler is not null;
    public bool CanPublishRulers => !_disposed && !_busy && _pick is null && HasLiveScene && _session.Rulers.Count > 0;
    public bool CanClearScope => !_disposed && !_busy && _pick is null && HasLiveScene &&
        (_session.Rulers.Count > 0 || _scopeOperation != Guid.Empty);
    public bool CanCancel => _busy && _operation is not null;
    public bool CanCopy => _session.Completed is not null;
    public bool CanExport => CanCopy;
    public bool CanCheckObject => !_disposed && !_busy && HasLiveScene;

    public static IReadOnlyList<string> SnapModes { get; } = ["No snap", "Grid 1 mil", "Custom grid"];

    public string FirstX { get => _firstX; set => SetField(ref _firstX, value); }
    public string FirstY { get => _firstY; set => SetField(ref _firstY, value); }
    public string SecondX { get => _secondX; set => SetField(ref _secondX, value); }
    public string SecondY { get => _secondY; set => SetField(ref _secondY, value); }
    public string SnapMode { get => _snapMode; set => SetField(ref _snapMode, value); }
    public string GridStep { get => _gridStep; set => SetField(ref _gridStep, value); }
    public bool UseMillimeters
    {
        get => _useMillimeters;
        set { if (SetField(ref _useMillimeters, value)) RefreshResult(); }
    }
    public string RulerLabel { get => _rulerLabel; set => SetField(ref _rulerLabel, value); }
    public string ObjectId { get => _objectId; set => SetField(ref _objectId, value); }
    public string ExportPath { get => _exportPath; set => SetField(ref _exportPath, value); }
    public MeasureRulerRecord? SelectedRuler
    {
        get => _selectedRuler;
        set { if (SetField(ref _selectedRuler, value)) RefreshGates(); }
    }

    public async Task AcquireSceneAsync()
    {
        if (!CanAcquire)
        {
            return;
        }

        CancellationTokenSource operation = BeginOperation();
        try
        {
            Status = "Acquiring a live metadata scene from Allegro…";
            LiveDesignScene live = await _bridge.ReadEngineSceneAsync(SceneQuery.Metadata, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            live.RequireCurrent();
            AdoptScene(live);
            Status = $"Live scene acquired ({live.Document}). Set captured endpoints or start a native pick.";
        }
        catch (OperationCanceledException)
        {
            Status = "Scene acquisition cancelled. No scene was adopted.";
        }
        catch (Exception error)
        {
            Status = "Scene acquisition unavailable: " + error.Message;
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public void SetFirstFromCaptured()
    {
        if (!CanSetFirst || _live is null)
        {
            return;
        }

        try
        {
            MeasureEndpoint endpoint = CapturedEndpoint(
                ParseDecimal(FirstX, "first X"), ParseDecimal(FirstY, "first Y"));
            _session.SetFirst(endpoint);
            Validation = string.Empty;
            Status = $"First captured point held at {endpoint}. Set the second point, or Clear first to restart.";
            RefreshResult();
        }
        catch (Exception error)
        {
            Validation = error.Message;
            Status = "The first point was not held. Fix the listed field; nothing was measured.";
        }
        finally
        {
            RefreshGates();
        }
    }

    public void SetSecondFromCaptured()
    {
        if (!CanSetSecond || _live is null)
        {
            return;
        }

        try
        {
            MeasureEndpoint endpoint = CapturedEndpoint(
                ParseDecimal(SecondX, "second X"), ParseDecimal(SecondY, "second Y"));
            _session.SetSecond(endpoint, _live.Scene.Identity.CaptureId, _live.Document.ToString());
            Validation = string.Empty;
            if (_session.Completed is { } span)
            {
                MeasureRuler ruler = _session.KeepRuler(DefaultRulerLabel());
                SyncRulers();
                Status = $"Captured span complete: {Describe(span)}. Kept persistent ruler {ruler.Label}; publish rulers to show it live.";
            }

            RefreshResult();
        }
        catch (Exception error)
        {
            Validation = error.Message;
            Status = "The second point was not set. Fix the listed field; the first point is still held.";
        }
        finally
        {
            RefreshGates();
        }
    }

    public void ClearFirst()
    {
        if (!CanClearFirst)
        {
            return;
        }

        _session.ClearFirst();
        RefreshResult();
        Status = "Cleared the pending measurement. Persisted rulers are unaffected.";
        RefreshGates();
    }

    public async Task StartNativePickAsync()
    {
        if (!CanStartNativePick || _live is null)
        {
            return;
        }

        CancellationTokenSource operation = BeginOperation();
        Guid operationId = _session.StartPickOperation();
        EngineEndpointPick? pick = null;
        try
        {
            Status = "Native pick started: click the first, then the second point in Allegro. Clear first or Cancel stays available.";
            Feedback = string.Empty;
            _live.RequireCurrent();
            pick = await _bridge.Workspace.Picking.StartTwoPointPickAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _pick = pick;
            _pickOperation = operationId;
            RefreshGates();

            Task drain = Task.Run(() => DrainFeedbackAsync(pick, operationId, operation.Token));
            EngineOperationTerminal terminal = await pick.WaitForTerminalAsync(operation.Token);
            await AwaitDrainAsync(drain, operation);
            operation.Token.ThrowIfCancellationRequested();

            if (!_session.IsCurrentOperation(operationId))
            {
                Status = "The pick finished after a newer measurement started; its result was ignored.";
                return;
            }

            if (!terminal.IsComplete)
            {
                Status = $"Native pick ended without endpoints ({terminal.State}): {terminal.Message} Nothing was measured or changed.";
                return;
            }

            EnginePickedEndpoints endpoints = await pick.WaitForResultAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            RequireCurrentLive();
            if (endpoints.Document != _live.Document)
            {
                Status = "The native pick completed against another live document; its result was rejected. Acquire a fresh scene.";
                return;
            }

            _session.SetFirst(ToNativeEndpoint(endpoints.First));
            _session.SetSecond(
                ToNativeEndpoint(endpoints.Second),
                _live.Scene.Identity.CaptureId, _live.Document.ToString());
            if (_session.Completed is { } span)
            {
                MeasureRuler ruler = _session.KeepRuler(DefaultRulerLabel());
                SyncRulers();
                Status = $"Native span complete: {Describe(span)}. Kept persistent ruler {ruler.Label}; no copper was created or changed.";
            }

            RefreshResult();
        }
        catch (OperationCanceledException)
        {
            _session.RetirePickOperation(operationId);
            Status = "Native pick cancelled. No endpoints were adopted; nothing was measured or changed.";
        }
        catch (Exception error)
        {
            _session.RetirePickOperation(operationId);
            Status = "Native pick unavailable: " + error.Message;
            InvalidateLiveScene(error);
        }
        finally
        {
            if (pick is not null)
            {
                await DisposePickAsync(pick);
            }

            _pick = null;
            _pickOperation = Guid.Empty;
            EndOperation(operation);
        }
    }

    public async Task ClearNativeFirstAsync()
    {
        EngineEndpointPick? pick = _pick;
        if (pick is null || pick.IsTerminal)
        {
            return;
        }

        try
        {
            await pick.ClearFirstAsync();
            Feedback = "Native first pick cleared. Click the first point again in Allegro.";
        }
        catch (Exception error)
        {
            Feedback = "Clear-first unavailable: " + error.Message;
        }
    }

    public async Task CancelNativePickAsync()
    {
        EngineEndpointPick? pick = _pick;
        if (pick is null)
        {
            Cancel();
            return;
        }

        try
        {
            // Explicitly requests native cancellation; the terminal outcome
            // still arrives through WaitForTerminalAsync, never assumed here.
            await pick.CancelAsync();
        }
        catch (Exception error)
        {
            Feedback = "Native cancel request unavailable: " + error.Message;
        }
        finally
        {
            Cancel();
        }
    }

    public void Cancel() => _operation?.Cancel();

    public void RequestExplorerNavigation() => NavigateToExplorerRequested?.Invoke(this, EventArgs.Empty);

    public void KeepCurrentAsRuler()
    {
        if (!CanKeepRuler || _session.Completed is null)
        {
            return;
        }

        MeasureRuler ruler = _session.KeepRuler(DefaultRulerLabel());
        SyncRulers();
        Status = $"Kept persistent ruler {ruler.Label}. Publish rulers to show it on the live overlay.";
        RefreshGates();
    }

    public void MoveSelectedRulerToCurrentSpan()
    {
        if (!CanMoveRuler || _selectedRuler is null || _session.Completed is null)
        {
            return;
        }

        try
        {
            MeasureRuler moved = _session.MoveRuler(_selectedRuler.Id, _session.Completed);
            SyncRulers();
            SelectedRuler = FindRulerRecord(moved.Id);
            Status = $"Moved ruler {moved.Label} to the current span (revision {moved.Revision}). Publish rulers to show it live.";
        }
        catch (Exception error)
        {
            Status = "Move ruler unavailable: " + error.Message;
        }
        finally
        {
            RefreshGates();
        }
    }

    public async Task RemoveSelectedRulerAsync()
    {
        if (!CanRemoveRuler || _selectedRuler is null || _live is null)
        {
            return;
        }

        Guid rulerId = _selectedRuler.Id;
        if (!_session.RemoveRuler(rulerId))
        {
            Status = "The selected ruler is already gone.";
            SyncRulers();
            return;
        }

        SyncRulers();
        SelectedRuler = null;
        // Removal republishes the remaining rulers so the receipt proves
        // exactly which revisions stay visible; an empty remainder publishes
        // an empty scene as removal evidence.
        await PublishRulersAsync();
        if (!Status.StartsWith("Removed ruler", StringComparison.Ordinal))
        {
            Status = "Removed ruler " + rulerId.ToString("N")[..8] +
                ". Publish state: " + Status;
        }
    }

    public async Task PublishRulersAsync()
    {
        if (!CanPublishRulers || _live is null)
        {
            if (_session.Rulers.Count == 0)
            {
                Status = "No rulers to publish: complete a measurement first.";
            }

            return;
        }

        CancellationTokenSource operation = BeginOperation();
        Guid operationId = _tracker.StartOperation(CurrentEpoch(), CurrentRulerRevision());
        try
        {
            Status = "Publishing rulers to the shared live overlay…";
            _live.RequireCurrent();
            DrawingScene drawings = BuildRulerScene(_live.Scene);
            EngineWpfPublicationReceipt receipt = await _presentation.PresentWithReceiptAsync(
                _live, drawings, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _live.RequireCurrent();
            ToolPublicationOutcome outcome = _tracker.Complete(
                operationId, CurrentEpoch(), _presentation.CurrentSequence, Map(receipt));
            RecordScopeOperation(operationId, outcome);
            ReportOutcome(outcome, receipt,
                $"Published {_session.Rulers.Count} ruler(s) at revision {CurrentRulerRevision()} " +
                $"(sequence {receipt.Sequence}). Display-only: no board geometry changed.");
        }
        catch (OperationCanceledException)
        {
            _tracker.Complete(operationId, CurrentEpoch(), _presentation.CurrentSequence, null);
            Status = "Ruler publication cancelled. The request has no receipt; prior pixels may still be visible.";
        }
        catch (Exception error)
        {
            _tracker.Complete(operationId, CurrentEpoch(), _presentation.CurrentSequence, null);
            Status = "Ruler publication unavailable: " + error.Message;
            InvalidateLiveScene(error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public async Task ClearScopeAsync()
    {
        if (!CanClearScope || _live is null)
        {
            return;
        }

        CancellationTokenSource operation = BeginOperation();
        Guid operationId = _tracker.StartOperation(CurrentEpoch(), CurrentRulerRevision() + 1);
        try
        {
            Status = "Removing the tool's ruler scope…";
            _live.RequireCurrent();
            // The shared presentation carries one overlay lease, so removal
            // publishes an empty scene on this tool's revision chain and keeps
            // the receipt as removal evidence. Other tools republish their own
            // overlays on next use; nothing of theirs is retired here.
            DrawingScene empty = new(_live.Scene.Identity.CaptureId, CurrentRulerRevision() + 1, []);
            EngineWpfPublicationReceipt receipt = await _presentation.PresentWithReceiptAsync(
                _live, empty, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _live.RequireCurrent();
            _scopeRevision = CurrentRulerRevision() + 1;
            _session.ClearRulers();
            SyncRulers();
            if (_scopeOperation != Guid.Empty)
            {
                _tracker.Retire(_scopeOperation);
                _scopeOperation = Guid.Empty;
            }

            ToolPublicationOutcome outcome = _tracker.Complete(
                operationId, CurrentEpoch(), _presentation.CurrentSequence, Map(receipt));
            RecordScopeOperation(operationId, outcome);
            ReportOutcome(outcome, receipt,
                $"Ruler scope removed at revision {_scopeRevision} (sequence {receipt.Sequence}). " +
                "Only this tool's scope was cleared; no board geometry changed.");
        }
        catch (OperationCanceledException)
        {
            _tracker.Complete(operationId, CurrentEpoch(), _presentation.CurrentSequence, null);
            Status = "Scope removal cancelled; rulers are unchanged.";
        }
        catch (Exception error)
        {
            _tracker.Complete(operationId, CurrentEpoch(), _presentation.CurrentSequence, null);
            Status = "Scope removal unavailable: " + error.Message;
            InvalidateLiveScene(error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public void CopyMeasurement()
    {
        if (!CanCopy || _session.Completed is null)
        {
            return;
        }

        try
        {
            string text = MeasureMath.ExportText(_session.Completed, _useMillimeters) +
                Environment.NewLine + MeasureMath.ExportCSharp(_session.Completed);
            System.Windows.Clipboard.SetText(text);
            Status = "Copied the measurement and its public C# recipe to the clipboard.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "Clipboard unavailable. The measurement text stays in the result box.";
        }
        catch (Exception error)
        {
            Status = "Copy unavailable: " + error.Message;
        }
    }

    public Task ExportMeasurementAsync()
    {
        if (!CanExport || _session.Completed is null)
        {
            Status = "Nothing to export: complete a two-point measurement first.";
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(ExportPath))
        {
            Status = "Choose an output .txt path first.";
            return Task.CompletedTask;
        }

        if (File.Exists(ExportPath))
        {
            Status = $"Refused to overwrite {Path.GetFileName(ExportPath)}. Choose a new name or remove the old file first.";
            return Task.CompletedTask;
        }

        string text = MeasureMath.ExportText(_session.Completed, _useMillimeters);
        string path = ExportPath.Trim();
        return Task.Run(() =>
        {
            try
            {
                ReviewBundleManifest.WriteTextAtomically(path, text);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ReportOnDispatcher("Measurement export failed: " + error.Message);
                return;
            }

            ReportOnDispatcher(
                $"Exported measurement ({text.Length} chars) to {Path.GetFileName(path)}. " +
                "A failed write never leaves a partial file.");
        });
    }

    public void CheckObjectInCapture()
    {
        if (!CanCheckObject || _live is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ObjectId))
        {
            Status = "Enter a captured object id first (copy it from Explorer Inspect).";
            return;
        }

        try
        {
            SceneObjectReference target = _live.Scene.ReferenceTo(new SceneObjectId(ObjectId.Trim()));
            bool present = _live.Scene.Contains(target.ObjectId);
            Status = present
                ? $"Advisory visual check: object '{target.ObjectId}' is present in captured scene " +
                  $"{_live.Scene.Identity.CaptureId}. Presence in a capture is not native edit authority " +
                  "and never addresses the native object array."
                : $"Advisory visual check: object '{target.ObjectId}' is absent from the current capture. " +
                  "Reacquire the scene or pick the id again.";
        }
        catch (Exception error)
        {
            Status = "Object check unavailable: " + error.Message;
        }
    }

    public void RefreshGates()
    {
        OnPropertyChanged(nameof(CanAcquire));
        OnPropertyChanged(nameof(CanSetFirst));
        OnPropertyChanged(nameof(CanSetSecond));
        OnPropertyChanged(nameof(CanClearFirst));
        OnPropertyChanged(nameof(CanStartNativePick));
        OnPropertyChanged(nameof(CanClearNativeFirst));
        OnPropertyChanged(nameof(CanCancelNativePick));
        OnPropertyChanged(nameof(CanKeepRuler));
        OnPropertyChanged(nameof(CanMoveRuler));
        OnPropertyChanged(nameof(CanRemoveRuler));
        OnPropertyChanged(nameof(CanPublishRulers));
        OnPropertyChanged(nameof(CanClearScope));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanCheckObject));
        OnPropertyChanged(nameof(HasLiveScene));
        OnPropertyChanged(nameof(HasCompletedSpan));
        OnPropertyChanged(nameof(HasActivePick));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bridge.StateChanged -= Bridge_StateChanged;
        try
        {
            _operation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _operation?.Dispose();
        _operation = null;
        EngineEndpointPick? pick = _pick;
        _pick = null;
        _pickOperation = Guid.Empty;
        if (pick is not null)
        {
            // View lifetime owns its operations only. The pick's native
            // cancellation stays explicit; disposal never claims it.
            _ = pick.DisposeAsync();
        }

        // The shared presentation and Engine session stay alive for the
        // other tools; only this tool's pick operations retire here.
        _tracker.Clear();
        _live = null;
    }

    private async Task DrainFeedbackAsync(
        EngineEndpointPick pick, Guid operationId, CancellationToken token)
    {
        try
        {
            await foreach (EngineInteractionFeedback feedback in
                pick.ReadInteractionFeedbackAsync(token))
            {
                if (!_session.IsCurrentOperation(operationId) || !feedback.IsCurrent)
                {
                    continue;
                }

                string? message = feedback.FeedbackKind switch
                {
                    EngineInteractionFeedbackKind.SelectionAccepted
                        when feedback.SelectionOrdinal == 1 && feedback.Point is not null =>
                        $"First native observation held: {feedback.ObjectKind} at " +
                        $"{Format(feedback.Point.Position)}. Select the second point in Allegro.",
                    EngineInteractionFeedbackKind.SelectionAccepted =>
                        "Second native observation accepted. Engine is admitting the endpoints.",
                    EngineInteractionFeedbackKind.SelectionCleared =>
                        "The native first pick was cleared without dispatching an edit.",
                    EngineInteractionFeedbackKind.SelectionRejected =>
                        "Allegro rejected that pick; the rejected selection changed no geometry.",
                    _ => null,
                };

                if (message is not null)
                {
                    await ReportFeedbackAsync(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Measure feedback drain ended: {0}", error.Message);
        }
    }

    private async Task AwaitDrainAsync(Task drain, CancellationTokenSource operation)
    {
        Task finished = await Task.WhenAny(
            drain, Task.Delay(TimeSpan.FromSeconds(10), operation.Token));
        if (!drain.IsCompleted)
        {
            // The feedback stream outlived the terminal outcome: stop waiting
            // for it. Cancellation aborts the stream; the terminal result
            // below still decides what is adopted.
            operation.Cancel();
            await drain;
            _ = finished;
        }
    }

    private Task ReportFeedbackAsync(string message) =>
        _presentation.InvokeOnDispatcherAsync(() =>
        {
            if (!_disposed)
            {
                Feedback = message;
            }

            return Task.FromResult<object?>(null);
        });

    private void ReportOnDispatcher(string message) =>
        _presentation.InvokeOnDispatcherAsync<object?>(() =>
        {
            Status = message;
            return Task.FromResult<object?>(null);
        }).GetAwaiter().GetResult();

    private MeasureEndpoint CapturedEndpoint(decimal xMils, decimal yMils)
    {
        var errors = MeasureMath.ValidatePoint("point", xMils, yMils);
        if (errors.Length > 0)
        {
            throw new InvalidOperationException(errors[0].ToString());
        }

        return MeasureMath.CaptureEndpoint(
            new DesignPoint(xMils, yMils), CurrentGridStep());
    }

    private decimal? CurrentGridStep() => _snapMode switch
    {
        "Grid 1 mil" => 1m,
        "Custom grid" => decimal.TryParse(GridStep, NumberStyles.Float,
            CultureInfo.InvariantCulture, out decimal step) && step > 0
                ? step
                : throw new InvalidOperationException(
                    "Enter a positive custom grid step in mils."),
        _ => null,
    };

    private static MeasureEndpoint ToNativeEndpoint(EnginePickedPoint point) =>
        MeasureMath.NativeEndpoint(
            point.Position, point.ObjectKind.ToString(), point.NetName);

    private void AdoptScene(LiveDesignScene live)
    {
        _live = live;
        _session.ClearFirst();
        _session.ClearRulers();
        SyncRulers();
        _scopeOperation = Guid.Empty;
        RefreshResult();
    }

    private void RequireCurrentLive()
    {
        if (_live is null)
        {
            throw new InvalidOperationException("Acquire a live scene first.");
        }

        _live.RequireCurrent();
    }

    private void RefreshResult()
    {
        Result = _session.Completed is { } span
            ? MeasureMath.ExportText(span, _useMillimeters)
            : _session.First is { } first
                ? $"First point held: {first}. Waiting for the second point."
                : "No measurement yet.";
        OnPropertyChanged(nameof(HasCompletedSpan));
    }

    private string Describe(MeasuredSpan span)
    {
        string delta = string.Create(CultureInfo.InvariantCulture,
            $"delta ({span.DeltaXMils:0.###}, {span.DeltaYMils:0.###}) mil");
        string angle = span.AngleDegrees is { } degrees
            ? string.Create(CultureInfo.InvariantCulture, $", angle {degrees:0.###} deg")
            : ", angle undefined";
        string provenance = string.Create(CultureInfo.InvariantCulture,
            $" [{span.Start.Provenance}/{span.End.Provenance}]");
        return $"distance {MeasureMath.FormatDistance(span, _useMillimeters)}, " +
            delta + angle + provenance;
    }

    private static string Format(DesignPoint point) =>
        string.Create(CultureInfo.InvariantCulture, $"({point.X:0.###}, {point.Y:0.###})");

    private string DefaultRulerLabel() =>
        string.IsNullOrWhiteSpace(RulerLabel) ? string.Empty : RulerLabel.Trim();

    private void SyncRulers()
    {
        Rulers.Clear();
        foreach (MeasureRuler ruler in _session.Rulers)
        {
            Rulers.Add(new MeasureRulerRecord(
                ruler.Id, ruler.Label, ruler.Revision,
                $"{ruler.Label}: {Describe(ruler.Span)} (rev {ruler.Revision})"));
        }
    }

    private MeasureRulerRecord? FindRulerRecord(Guid id)
    {
        foreach (MeasureRulerRecord record in Rulers)
        {
            if (record.Id == id)
            {
                return record;
            }
        }

        return null;
    }

    private DrawingScene BuildRulerScene(DesignScene scene)
    {
        var groups = new List<DrawingGroup>(_session.Rulers.Count);
        foreach (MeasureRuler ruler in _session.Rulers)
        {
            var recipe = new OverlayToolRecipe(
                "ruler-" + ruler.Id.ToString("N")[..8],
                new OverlayToolAnchor.Board(0, 0),
                new OverlayToolShape.Dimension(
                    ruler.Span.Start.Snapped.X, ruler.Span.Start.Snapped.Y,
                    ruler.Span.End.Snapped.X, ruler.Span.End.Snapped.Y,
                    DrawingDimensionKind.Aligned, ruler.Label),
                new OverlayToolStyle(255, 32, 112, 220, 2));
            if (recipe.Validate() is not [])
            {
                throw new InvalidOperationException(
                    $"Ruler {ruler.Label} is no longer buildable; re-measure it.");
            }

            groups.Add(recipe.Build(scene, ScopeGroupId));
        }

        return new DrawingScene(scene.Identity.CaptureId, CurrentRulerRevision(), [.. groups]);
    }

    private long CurrentRulerRevision() =>
        _session.Rulers.Count == 0 ? _scopeRevision : _session.Rulers[^1].Revision;

    private long CurrentEpoch() => _live?.Scene.Identity.CapturedAt.Ticks ?? 0;

    private void RecordScopeOperation(Guid operationId, ToolPublicationOutcome outcome)
    {
        lock (_gate)
        {
            if (outcome == ToolPublicationOutcome.Visible)
            {
                if (_scopeOperation != Guid.Empty && _scopeOperation != operationId)
                {
                    _tracker.Retire(_scopeOperation);
                }

                _scopeOperation = operationId;
            }
        }
    }

    private void ReportOutcome(
        ToolPublicationOutcome outcome, EngineWpfPublicationReceipt receipt, string publishedText)
    {
        switch (outcome)
        {
            case ToolPublicationOutcome.Visible:
                Status = publishedText;
                Publication =
                    $"Visible: rev {receipt.Revision}, sequence {receipt.Sequence}, " +
                    $"published {receipt.PublishedAt:O}. A submitted drawing is fresh only while " +
                    "the sequence still equals the presentation's current sequence.";
                break;
            case ToolPublicationOutcome.Pending:
                Status = "The request is queued; pixels are not proven visible. Wait, then republish.";
                Publication = $"Pending: rev {receipt.Revision}, sequence {receipt.Sequence}.";
                break;
            case ToolPublicationOutcome.Empty:
                Status = "The request published nothing (empty outcome). Check the rulers and republish.";
                Publication = $"Empty: rev {receipt.Revision}, sequence {receipt.Sequence}.";
                break;
            case ToolPublicationOutcome.Unavailable:
                Status = "The overlay is unavailable (window minimized, occluded, or retired). Restore the view and republish.";
                Publication = $"Unavailable: rev {receipt.Revision}, sequence {receipt.Sequence}.";
                break;
            default:
                Status = "The receipt belongs to a stale revision or a superseded request. Rebuild and republish.";
                Publication = $"Superseded: rev {receipt.Revision}, sequence {receipt.Sequence}.";
                break;
        }
    }

    private void InvalidateLiveScene(Exception error)
    {
        if (error is InvalidOperationException && _live is not null && !_live.IsCurrent)
        {
            AdoptSceneFailure(" The live scene changed; acquire a fresh scene before measuring.");
        }
    }

    private void AdoptSceneFailure(string note)
    {
        _live = null;
        Status += note;
        RefreshGates();
    }

    private static async Task DisposePickAsync(EngineEndpointPick pick)
    {
        try
        {
            await pick.DisposeAsync();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Measure pick disposal failed; Engine operation tracking is unchanged: {0}",
                error.Message);
        }
    }

    private static ToolPublicationReceipt Map(EngineWpfPublicationReceipt receipt) => new(
        receipt.Document.ToString() ?? "unknown document",
        receipt.CaptureId,
        receipt.Revision,
        receipt.Sequence,
        receipt.Availability switch
        {
            EngineWpfOverlayAvailability.Visible => ToolPublicationAvailability.Visible,
            EngineWpfOverlayAvailability.Pending => ToolPublicationAvailability.Pending,
            EngineWpfOverlayAvailability.Empty => ToolPublicationAvailability.Empty,
            EngineWpfOverlayAvailability.Unavailable => ToolPublicationAvailability.Unavailable,
            EngineWpfOverlayAvailability.Disposed => ToolPublicationAvailability.Disposed,
            _ => ToolPublicationAvailability.Unavailable,
        },
        receipt.PublishedAt);

    private bool SupportsPicking()
    {
        try
        {
            return _bridge.EngineSession.State.Capabilities.Supports(EngineCapabilities.Picking);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private CancellationTokenSource BeginOperation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Cancel();
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        IsBusy = true;
        return _operation;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_operation, operation))
        {
            _operation = null;
        }

        operation.Dispose();
        IsBusy = false;
        RefreshGates();
    }

    private void Bridge_StateChanged(object? sender, SimpleSessionState state)
    {
        if (_disposed)
        {
            return;
        }

        if (!state.IsReady && _live is not null)
        {
            _live = null;
            Status = "The Allegro connection changed. Acquire a fresh scene; old frame authority is retired.";
        }

        RefreshGates();
    }

    private static decimal ParseDecimal(string text, string what) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : throw new InvalidOperationException($"Enter {what} as a number in mils.");

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One persistent ruler row for the tool's ruler list.</summary>
public sealed record MeasureRulerRecord(
    Guid Id,
    string Label,
    long Revision,
    string Summary);
