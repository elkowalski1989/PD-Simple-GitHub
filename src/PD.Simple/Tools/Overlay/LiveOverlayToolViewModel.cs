using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media.Imaging;
using PixelFormats = System.Windows.Media.PixelFormats;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.PcbTools.OverlayTools;

namespace PD.Simple.Tools.Overlay;

/// <summary>
/// T06 Live overlay tools: simple public drawing creation, style, grouping,
/// source anchoring, update/replace/remove, scoped live overlay, publication
/// receipts, cancellation and disposal. Drawings are display-only: local
/// drawings never become native copper, and gestures never edit Allegro.
/// </summary>
public sealed class LiveOverlayToolViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaximumOperations = 50;
    private const string ScopeGroupPrefix = "tools-b-overlay-";

    /// <summary>
    /// Alpha at or above this value counts a published overlay pixel as
    /// painted. This mirrors the Bridge WPF gate's opaque-pixel convention.
    /// </summary>
    private const int PaintedAlphaThreshold = 32;

    private readonly BridgeSession _bridge;
    private readonly EngineWpfPresentation _presentation;
    private readonly ToolPublicationTracker _tracker = new();
    private readonly Dictionary<Guid, long> _revisions = new();
    private readonly object _gate = new();

    private LiveDesignScene? _live;
    private DrawingScene? _built;
    private Guid _builtOperation = Guid.Empty;
    private long _builtRevision;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _disposed;

    private string _status = "This page opens offline; publishing needs Allegro. When a board is connected, Build preview or Use visible canvas center acquires the live scene automatically.";
    private string _publication = "No publication yet.";
    private string _anchorX = "0";
    private string _anchorY = "0";
    private bool _useObjectAnchor;
    private string _objectId = string.Empty;
    private string _elementId = "note-1";
    private string _shape = "Text";
    private string _lineParams = "0, 0, 200, 0";
    private string _circleParams = "0, 0, 25";
    private string _ellipseParams = "0, 0, 60, 30";
    private string _rectParams = "0, 0, 200, 120";
    private string _polygonParams = "0, 0\r\n200, 0\r\n200, 120\r\n0, 120";
    private string _polylineParams = "0, 0\r\n200, 0\r\n200, 120";
    private bool _polylineClosed;
    private string _textParams = "review note";
    private string _fontSize = "24";
    private string _markerKind = "Cross";
    private string _markerSize = "24";
    private string _dimensionParams = "0, 0, 200, 0";
    private string _dimensionKind = "Aligned";
    private string _dimensionLabel = string.Empty;
    private string _stroke = "Blue";
    private string _strokeWidth = "2";
    private bool _hasFill;
    private string _fill = "Blue";
    private string _opacity = "1";
    private string _zOrder = "200";
    private bool _isVisible = true;
    private string _hitTest = "None";
    private string _validation = string.Empty;

    public LiveOverlayToolViewModel(BridgeSession bridge, EngineWpfPresentation presentation)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        if (!ReferenceEquals(_presentation.Session, _bridge.EngineSession))
        {
            throw new ArgumentException(
                "The presentation must borrow this tool's Engine session.", nameof(presentation));
        }

        Operations = new ObservableCollection<OverlayOperationRecord>();
        _bridge.StateChanged += Bridge_StateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the tool needs the shared Explorer Inspect section (anchor picking).</summary>
    public event EventHandler? NavigateToExplorerRequested;

    public ObservableCollection<OverlayOperationRecord> Operations { get; }

    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string Publication { get => _publication; private set => SetField(ref _publication, value); }
    public string Validation { get => _validation; private set => SetField(ref _validation, value); }

    public bool IsBusy { get => _busy; private set { if (SetField(ref _busy, value)) RefreshGates(); } }
    public bool HasLiveScene => _live is not null;
    public bool HasBuiltDrawing => _built is not null;

    // Action gates. Pages open disconnected; only unsafe/unavailable actions disable.
    public bool CanAcquire => AcquireGate();
    public bool CanBuild => !_busy && !_disposed && (HasLiveScene || _bridge.HasReadySession);
    public bool CanUseVisibleCenter => !_busy && !_disposed && (HasLiveScene || _bridge.HasReadySession);
    public bool CanPublish => !_busy && !_disposed && HasLiveScene && HasBuiltDrawing;
    public bool CanHide => CanPublish;
    public bool CanRemove => !_busy && !_disposed && HasLiveScene && (_built is not null || _builtOperation != Guid.Empty);
    public bool CanCancel => _busy && _operation is not null;
    public bool CanCopyRecipe => HasBuiltDrawing;

    // Recipe inputs (bound to the task panel).
    public string AnchorX { get => _anchorX; set { if (SetField(ref _anchorX, value)) RefreshGates(); } }
    public string AnchorY { get => _anchorY; set { if (SetField(ref _anchorY, value)) RefreshGates(); } }
    public bool UseObjectAnchor { get => _useObjectAnchor; set { if (SetField(ref _useObjectAnchor, value)) RefreshGates(); } }
    public string ObjectId { get => _objectId; set { if (SetField(ref _objectId, value)) RefreshGates(); } }
    public string ElementId { get => _elementId; set { if (SetField(ref _elementId, value)) RefreshGates(); } }
    public string Shape { get => _shape; set { if (SetField(ref _shape, value)) RefreshGates(); } }
    public string LineParams { get => _lineParams; set => SetField(ref _lineParams, value); }
    public string CircleParams { get => _circleParams; set => SetField(ref _circleParams, value); }
    public string EllipseParams { get => _ellipseParams; set => SetField(ref _ellipseParams, value); }
    public string RectParams { get => _rectParams; set => SetField(ref _rectParams, value); }
    public string PolygonParams { get => _polygonParams; set => SetField(ref _polygonParams, value); }
    public string PolylineParams { get => _polylineParams; set => SetField(ref _polylineParams, value); }
    public bool PolylineClosed { get => _polylineClosed; set => SetField(ref _polylineClosed, value); }
    public string TextParams { get => _textParams; set => SetField(ref _textParams, value); }
    public string FontSize { get => _fontSize; set => SetField(ref _fontSize, value); }
    public string MarkerKind { get => _markerKind; set => SetField(ref _markerKind, value); }
    public string MarkerSize { get => _markerSize; set => SetField(ref _markerSize, value); }
    public string DimensionParams { get => _dimensionParams; set => SetField(ref _dimensionParams, value); }
    public string DimensionKind { get => _dimensionKind; set => SetField(ref _dimensionKind, value); }
    public string DimensionLabel { get => _dimensionLabel; set => SetField(ref _dimensionLabel, value); }
    public string Stroke { get => _stroke; set => SetField(ref _stroke, value); }
    public string StrokeWidth { get => _strokeWidth; set => SetField(ref _strokeWidth, value); }
    public bool HasFill { get => _hasFill; set => SetField(ref _hasFill, value); }
    public string Fill { get => _fill; set => SetField(ref _fill, value); }
    public string Opacity { get => _opacity; set => SetField(ref _opacity, value); }
    public string ZOrder { get => _zOrder; set => SetField(ref _zOrder, value); }
    public bool IsVisible { get => _isVisible; set => SetField(ref _isVisible, value); }
    public string HitTest { get => _hitTest; set => SetField(ref _hitTest, value); }

    public static IReadOnlyList<string> Shapes { get; } =
        ["Line", "Circle", "Ellipse", "Rectangle", "Polygon", "Polyline", "Text", "Marker", "Dimension"];

    public static IReadOnlyList<string> NamedColors { get; } =
        ["Blue", "Red", "Green", "Yellow", "White", "Amber"];

    public static IReadOnlyList<string> MarkerKinds { get; } =
        ["Dot", "Cross", "Diamond", "Square", "Triangle"];

    public static IReadOnlyList<string> DimensionKinds { get; } =
        ["Aligned", "Horizontal", "Vertical"];

    public static IReadOnlyList<string> HitTests { get; } = ["None", "Select", "Drag"];

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
            LiveDesignScene live = await ReadAndAdoptSceneAsync(operation.Token);
            Status = $"Live scene acquired ({live.Document}). Build a drawing, then publish it.";
        }
        catch (OperationCanceledException)
        {
            Status = "Scene acquisition cancelled. No scene was adopted.";
        }
        catch (Exception error)
        {
            _live = null;
            Status = "Scene acquisition unavailable: " + error.Message;
        }
        finally
        {
            EndOperation(operation);
        }
    }

    /// <summary>
    /// Reads one live metadata scene and adopts it as this tool's current
    /// frame authority. Shared by the explicit refresh and the automatic
    /// acquisition on Build/Center so both carry the same cancellation,
    /// currency, and identity guards.
    /// </summary>
    private async Task<LiveDesignScene> ReadAndAdoptSceneAsync(CancellationToken token)
    {
        LiveDesignScene live = await _bridge.ReadEngineSceneAsync(SceneQuery.Metadata, token);
        token.ThrowIfCancellationRequested();
        live.RequireCurrent();
        _live = live;
        _built = null;
        _builtOperation = Guid.Empty;
        return live;
    }

    /// <summary>
    /// Returns true when a current live scene is held, acquiring one when
    /// none (or a stale one) is held and Allegro is connected. Returns false
    /// without building anything when offline or acquisition fails; the
    /// failure is reported through Status, never swallowed. Cancellation
    /// propagates to the caller.
    /// </summary>
    private async Task<bool> EnsureLiveSceneAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_live is not null && _live.IsCurrent)
        {
            return true;
        }

        if (!_bridge.HasReadySession)
        {
            Status = "No live Allegro connection. Reconnect or attach a board and retry; nothing changed.";
            return false;
        }

        Status = "No current live scene is held; acquiring one from Allegro…";
        try
        {
            await ReadAndAdoptSceneAsync(token);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _live = null;
            Status = "Scene acquisition unavailable: " + error.Message;
            return false;
        }
    }

    /// <summary>
    /// Anchors the drawing at the current live canvas viewport center,
    /// acquiring the live scene first when none is held. The
    /// viewport is read through this tool's Engine session, converted from
    /// its own units to decimal mils, and stored as a board anchor; any
    /// built preview is invalidated because its anchor moved. This never
    /// publishes: rebuild the preview, then publish.
    /// </summary>
    public async Task UseVisibleCanvasCenterAsync()
    {
        if (!CanUseVisibleCenter || _disposed)
        {
            return;
        }

        CancellationTokenSource operation = BeginOperation();
        try
        {
            if (!await EnsureLiveSceneAsync(operation.Token) || _live is null)
            {
                return;
            }

            Status = "Reading the current live canvas viewport\u2026";
            _live.RequireCurrent();
            EngineWpfCanvasCapture capture = await _presentation.CaptureAsync(_live, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _live.RequireCurrent();
            EngineWpfCanvasViewport viewport = capture.Viewport;
            LengthUnit units = Length.ParseUnit(viewport.Units);
            decimal centerX = Length.From((viewport.MinimumX + viewport.MaximumX) / 2, units).Mils;
            decimal centerY = Length.From((viewport.MinimumY + viewport.MaximumY) / 2, units).Mils;
            AnchorX = centerX.ToString("0.###", CultureInfo.InvariantCulture);
            AnchorY = centerY.ToString("0.###", CultureInfo.InvariantCulture);
            UseObjectAnchor = false;
            _built = null;
            Validation = string.Empty;
            Status = $"Anchor set to the visible canvas center ({AnchorX}, {AnchorY} mils). " +
                "Rebuild the preview, then publish.";
        }
        catch (OperationCanceledException)
        {
            Status = "Viewport read cancelled. The anchor is unchanged.";
        }
        catch (Exception error)
        {
            Status = "Visible canvas center unavailable: " + error.Message;
            InvalidateLiveScene(error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public void BuildPreview()
    {
        if (!CanBuild || _live is null)
        {
            return;
        }

        try
        {
            BuildPreviewCore(_live);
        }
        catch (Exception error)
        {
            Status = "Preview failed: " + error.Message;
        }
        finally
        {
            RefreshGates();
        }
    }

    /// <summary>
    /// Builds the preview, acquiring the live metadata scene first when
    /// none is held and Allegro is connected. This is the path the Build
    /// preview button uses; the synchronous BuildPreview stays for callers
    /// that already hold a scene. Offline, or when acquisition fails,
    /// nothing is built and Status explains why.
    /// </summary>
    public async Task BuildPreviewAsync()
    {
        if (!CanBuild)
        {
            return;
        }

        CancellationTokenSource operation = BeginOperation();
        try
        {
            if (!await EnsureLiveSceneAsync(operation.Token) || _live is null)
            {
                return;
            }

            BuildPreviewCore(_live);
        }
        catch (OperationCanceledException)
        {
            Status = "Preview build cancelled. Nothing was built.";
        }
        catch (Exception error)
        {
            Status = "Preview failed: " + error.Message;
        }
        finally
        {
            EndOperation(operation);
        }
    }

    /// <summary>
    /// Validates the recipe and builds the local preview against the
    /// adopted live scene. Callers must pass a current scene; nothing is
    /// published here.
    /// </summary>
    private void BuildPreviewCore(LiveDesignScene live)
    {
        OverlayToolRecipe recipe = CreateRecipe(live.Scene);
        var errors = recipe.Validate();
        if (errors.Length > 0)
        {
            StringBuilder text = new();
            foreach (OverlayToolRecipeError error in errors)
            {
                text.AppendLine(error.ToString());
            }

            Validation = text.ToString().TrimEnd();
            Status = "The recipe is not buildable. Fix the listed fields; nothing was published.";
            return;
        }

        Validation = string.Empty;
        if (!_revisions.TryGetValue(live.Scene.Identity.CaptureId, out long revision))
        {
            revision = 0;
        }

        revision++;
        _revisions[live.Scene.Identity.CaptureId] = revision;
        DrawingGroup group = recipe.Build(live.Scene, ScopeGroupPrefix + _elementId.Trim());
        _built = new DrawingScene(live.Scene.Identity.CaptureId, revision, [group]);
        _builtRevision = revision;
        _builtOperation = Guid.Empty;
        Status = $"Preview built: 1 element, revision {revision}. Publishing is display-only and never edits copper.";
        Publication = "Built locally, not yet published.";
    }

    public async Task PublishAsync()
    {
        if (!CanPublish || _live is null || _built is null)
        {
            return;
        }

        CancellationTokenSource operation = BeginOperation();
        Guid operationId = _tracker.StartOperation(CurrentEpoch(), _builtRevision);
        try
        {
            Status = "Publishing the drawing to the shared live overlay…";
            _live.RequireCurrent();
            EngineWpfPublicationReceipt receipt = await _presentation.PresentWithReceiptAsync(
                _live, _built, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _live.RequireCurrent();
            ToolPublicationOutcome outcome = _tracker.Complete(
                operationId, CurrentEpoch(), _presentation.CurrentSequence, Map(receipt));
            string pixelEvidence = outcome == ToolPublicationOutcome.Visible
                ? await DescribePublishedPixelsAsync(receipt)
                : string.Empty;
            RecordOperation(operationId, "Publish", outcome, receipt, pixelEvidence);
            string publishedText =
                $"Published revision {_builtRevision} (sequence {receipt.Sequence}). " +
                (pixelEvidence.Length == 0 ? string.Empty : pixelEvidence + " ") +
                "Display-only: no board geometry changed.";
            ReportOutcome(outcome, receipt, publishedText, pixelEvidence);
        }
        catch (OperationCanceledException)
        {
            ToolPublicationOutcome outcome = _tracker.Complete(
                operationId, CurrentEpoch(), _presentation.CurrentSequence, null);
            RecordOperation(operationId, "Publish", outcome, null);
            Status = "Publication cancelled. The request has no receipt; prior pixels may still be visible.";
        }
        catch (Exception error)
        {
            _tracker.Complete(operationId, CurrentEpoch(), _presentation.CurrentSequence, null);
            RecordOperation(operationId, "Publish", ToolPublicationOutcome.Cancelled, null);
            Status = "Publication unavailable: " + error.Message;
            InvalidateLiveScene(error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public async Task HideAsync()
    {
        if (!CanHide || _live is null || _built is null)
        {
            return;
        }

        CancellationTokenSource operation = BeginOperation();
        Guid operationId = _tracker.StartOperation(CurrentEpoch(), _builtRevision + 1);
        try
        {
            Status = "Hiding the tool's overlay scope…";
            _live.RequireCurrent();
            OverlayToolRecipe recipe = CreateRecipe(_live.Scene);
            DrawingGroup group = recipe.Build(_live.Scene, ScopeGroupPrefix + _elementId.Trim());
            var hidden = new DrawingGroup(group.Id, group.Frame, group.Elements, isVisible: false, group.ZOrder);
            DrawingScene drawings = new(_live.Scene.Identity.CaptureId, _builtRevision + 1, [hidden]);
            EngineWpfPublicationReceipt receipt = await _presentation.PresentWithReceiptAsync(
                _live, drawings, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _live.RequireCurrent();
            _builtRevision++;
            _revisions[_live.Scene.Identity.CaptureId] = _builtRevision;
            ToolPublicationOutcome outcome = _tracker.Complete(
                operationId, CurrentEpoch(), _presentation.CurrentSequence, Map(receipt));
            RecordOperation(operationId, "Hide", outcome, receipt);
            ReportOutcome(outcome, receipt, publishedText:
                $"Scope hidden at revision {_builtRevision} (sequence {receipt.Sequence}).");
        }
        catch (OperationCanceledException)
        {
            _tracker.Complete(operationId, CurrentEpoch(), _presentation.CurrentSequence, null);
            RecordOperation(operationId, "Hide", ToolPublicationOutcome.Cancelled, null);
            Status = "Hide cancelled; visibility is unchanged.";
        }
        catch (Exception error)
        {
            _tracker.Complete(operationId, CurrentEpoch(), _presentation.CurrentSequence, null);
            RecordOperation(operationId, "Hide", ToolPublicationOutcome.Cancelled, null);
            Status = "Hide unavailable: " + error.Message;
            InvalidateLiveScene(error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public async Task RemoveAsync()
    {
        if (!CanRemove || _live is null)
        {
            return;
        }

        CancellationTokenSource operation = BeginOperation();
        Guid operationId = _tracker.StartOperation(CurrentEpoch(), _builtRevision + 1);
        try
        {
            Status = "Removing the tool's overlay scope…";
            _live.RequireCurrent();
            // The shared presentation carries one overlay lease, so removal
            // publishes an empty scene on this tool's revision chain and keeps
            // the receipt as removal evidence. Other tools republish their own
            // overlays on next use; nothing of theirs is retired here.
            DrawingScene empty = new(_live.Scene.Identity.CaptureId, _builtRevision + 1, []);
            EngineWpfPublicationReceipt receipt = await _presentation.PresentWithReceiptAsync(
                _live, empty, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _live.RequireCurrent();
            _builtRevision++;
            _revisions[_live.Scene.Identity.CaptureId] = _builtRevision;
            _built = null;
            if (_builtOperation != Guid.Empty)
            {
                _tracker.Retire(_builtOperation);
                _builtOperation = Guid.Empty;
            }

            ToolPublicationOutcome outcome = _tracker.Complete(
                operationId, CurrentEpoch(), _presentation.CurrentSequence, Map(receipt));
            RecordOperation(operationId, "Remove", outcome, receipt);
            ReportOutcome(outcome, receipt, publishedText:
                $"Scope removed at revision {_builtRevision} (sequence {receipt.Sequence}). " +
                "Only this tool's scope was cleared; no board geometry changed.");
        }
        catch (OperationCanceledException)
        {
            _tracker.Complete(operationId, CurrentEpoch(), _presentation.CurrentSequence, null);
            RecordOperation(operationId, "Remove", ToolPublicationOutcome.Cancelled, null);
            Status = "Remove cancelled; the scope is unchanged.";
        }
        catch (Exception error)
        {
            _tracker.Complete(operationId, CurrentEpoch(), _presentation.CurrentSequence, null);
            RecordOperation(operationId, "Remove", ToolPublicationOutcome.Cancelled, null);
            Status = "Remove unavailable: " + error.Message;
            InvalidateLiveScene(error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public void CopyRecipe()
    {
        if (!CanCopyRecipe || _live is null || _built is null)
        {
            return;
        }

        try
        {
            OverlayToolRecipe recipe = CreateRecipe(_live.Scene);
            System.Windows.Clipboard.SetText(recipe.ExportCSharp());
            Status = "Copied the public C# drawing recipe to the clipboard.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "Clipboard unavailable. The recipe text stays in the preview.";
        }
        catch (Exception error)
        {
            Status = "Recipe export unavailable: " + error.Message;
        }
    }

    public void Cancel()
    {
        _operation?.Cancel();
    }

    public void RequestExplorerNavigation() => NavigateToExplorerRequested?.Invoke(this, EventArgs.Empty);

    public void RefreshGates()
    {
        OnPropertyChanged(nameof(CanAcquire));
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(CanUseVisibleCenter));
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(CanHide));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanCopyRecipe));
        OnPropertyChanged(nameof(HasLiveScene));
        OnPropertyChanged(nameof(HasBuiltDrawing));
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
        // View lifetime owns its operations and subscriptions only. The shared
        // presentation and Engine session stay alive for the other tools.
        _tracker.Clear();
        _live = null;
        _built = null;
    }

    private bool AcquireGate()
    {
        if (_disposed || _busy)
        {
            return false;
        }

        return _bridge.HasReadySession;
    }

    private OverlayToolRecipe CreateRecipe(DesignScene scene)
    {
        OverlayToolAnchor anchor = _useObjectAnchor
            ? new OverlayToolAnchor.CapturedObject(scene.ReferenceTo(new SceneObjectId(_objectId.Trim())))
            : new OverlayToolAnchor.Board(ParseDecimal(AnchorX, "anchor X"), ParseDecimal(AnchorY, "anchor Y"));

        OverlayToolShape shape = _shape switch
        {
            "Line" => ParseLine(),
            "Circle" => ParseCircle(),
            "Ellipse" => ParseEllipse(),
            "Rectangle" => ParseRectangle(),
            "Polygon" => ParsePolygon(),
            "Polyline" => ParsePolyline(),
            "Marker" => new OverlayToolShape.Marker(
                0, 0, Enum.Parse<DrawingMarkerKind>(_markerKind), ParseDouble(MarkerSize, "marker size")),
            "Dimension" => ParseDimension(),
            _ => new OverlayToolShape.Text(0, 0, _textParams, ParseDouble(FontSize, "font size")),
        };

        (byte a, byte r, byte g, byte b) stroke = ParseColor(Stroke, "stroke");
        OverlayToolStyle style = new(
            stroke.a, stroke.r, stroke.g, stroke.b, ParseDouble(StrokeWidth, "stroke width"),
            null, null, null, null,
            ParseDecimal(Opacity, "opacity"), ParseInt(ZOrder, "z-order"),
            _isVisible, _hitTest switch
            {
                "Select" => DrawingHitTestPolicy.Select,
                "Drag" => DrawingHitTestPolicy.Drag,
                _ => DrawingHitTestPolicy.None,
            });
        if (_hasFill)
        {
            (byte fa, byte fr, byte fg, byte fb) = ParseColor(Fill, "fill");
            style = style with { FillA = fa, FillR = fr, FillG = fg, FillB = fb };
        }

        return new OverlayToolRecipe(
            string.IsNullOrWhiteSpace(_elementId) ? string.Empty : _elementId.Trim(),
            anchor, shape, style);
    }

    private OverlayToolShape.Line ParseLine()
    {
        decimal[] values = ParseDecimals(LineParams, 4, "line x1, y1, x2, y2");
        return new(values[0], values[1], values[2], values[3]);
    }

    private OverlayToolShape.Circle ParseCircle()
    {
        decimal[] values = ParseDecimals(CircleParams, 3, "circle center-x, center-y, radius");
        return new(values[0], values[1], values[2]);
    }

    private OverlayToolShape.Ellipse ParseEllipse()
    {
        decimal[] values = ParseDecimals(EllipseParams, 4, "ellipse center-x, center-y, radius-x, radius-y");
        return new(values[0], values[1], values[2], values[3]);
    }

    private OverlayToolShape.Rectangle ParseRectangle()
    {
        decimal[] values = ParseDecimals(RectParams, 4, "rectangle x, y, width, height");
        return new(values[0], values[1], values[2], values[3]);
    }

    private OverlayToolShape.Dimension ParseDimension()
    {
        decimal[] values = ParseDecimals(DimensionParams, 4, "dimension x1, y1, x2, y2");
        return new(values[0], values[1], values[2], values[3],
            Enum.Parse<DrawingDimensionKind>(_dimensionKind),
            string.IsNullOrWhiteSpace(_dimensionLabel) ? null : _dimensionLabel);
    }

    private OverlayToolShape.Polygon ParsePolygon()
    {
        var points = new List<(decimal X, decimal Y)>();
        foreach (string line in _polygonParams.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            decimal[] values = ParseDecimals(line, 2, "polygon point x, y");
            points.Add((values[0], values[1]));
        }

        return new([.. points]);
    }

    private OverlayToolShape.Polyline ParsePolyline()
    {
        var points = new List<(decimal X, decimal Y)>();
        foreach (string line in _polylineParams.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            decimal[] values = ParseDecimals(line, 2, "polyline point x, y");
            points.Add((values[0], values[1]));
        }

        return new([.. points], _polylineClosed);
    }

    private static decimal[] ParseDecimals(string text, int expected, string what)
    {
        string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != expected)
        {
            throw new InvalidOperationException($"Enter the {what} as {expected} comma-separated mils.");
        }

        return parts.Select(part => decimal.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : throw new InvalidOperationException($"Enter the {what} as numbers in mils.")).ToArray();
    }

    private static decimal ParseDecimal(string text, string what) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : throw new InvalidOperationException($"Enter {what} as a number in mils.");

    private static double ParseDouble(string text, string what) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new InvalidOperationException($"Enter {what} as a number.");

    private static int ParseInt(string text, string what) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidOperationException($"Enter {what} as a whole number.");

    private static (byte A, byte R, byte G, byte B) ParseColor(string name, string what) => name switch
    {
        "Red" => (255, 220, 32, 32),
        "Green" => (255, 32, 180, 80),
        "Yellow" => (255, 240, 196, 32),
        "White" => (255, 255, 255, 255),
        "Amber" => (255, 255, 190, 96),
        "Blue" => (255, 32, 112, 220),
        _ => throw new InvalidOperationException($"Choose a {what} color."),
    };

    private long CurrentEpoch() => _live?.Scene.Identity.CapturedAt.Ticks ?? 0;

    private void RecordOperation(
        Guid operationId, string action, ToolPublicationOutcome outcome, EngineWpfPublicationReceipt? receipt,
        string pixelEvidence = "")
    {
        lock (_gate)
        {
            string evidence = receipt is null
                ? "No receipt: cancelled or failed before publication."
                : $"doc {receipt.Document} capture {receipt.CaptureId} rev {receipt.Revision} " +
                  $"seq {receipt.Sequence} {receipt.Availability} at {receipt.PublishedAt:O}";
            if (pixelEvidence.Length != 0)
            {
                evidence += " " + pixelEvidence;
            }

            Operations.Add(new OverlayOperationRecord(
                operationId, action, DateTimeOffset.UtcNow, _builtRevision, outcome, evidence));
            while (Operations.Count > MaximumOperations)
            {
                OverlayOperationRecord oldest = Operations[0];
                Operations.RemoveAt(0);
                _tracker.Retire(oldest.OperationId);
            }

            if (outcome == ToolPublicationOutcome.Visible && action is "Publish" or "Hide")
            {
                if (_builtOperation != Guid.Empty && _builtOperation != operationId)
                {
                    _tracker.Retire(_builtOperation);
                }

                _builtOperation = operationId;
            }
        }
    }

    /// <summary>
    /// Verifies the just-published frame against the presentation's own
    /// overlay bitmap. A Visible receipt proves the frame was published;
    /// only non-transparent pixels prove the annotation paints. Never
    /// throws: verification problems are reported as unavailable evidence,
    /// never as a publication failure.
    /// </summary>
    private async Task<string> DescribePublishedPixelsAsync(EngineWpfPublicationReceipt receipt)
    {
        try
        {
            EngineWpfOverlayDebugImage? image = await _presentation
                .CaptureOverlayDebugImageAsync(CancellationToken.None);
            if (image is null)
            {
                return "Painted-pixel evidence is unavailable: no overlay debug frame is published.";
            }

            if (image.CaptureId != receipt.CaptureId ||
                image.Revision != receipt.Revision ||
                receipt.Sequence != _presentation.CurrentSequence)
            {
                return "The published frame was superseded before pixel verification " +
                    $"(sequence {receipt.Sequence}, now {_presentation.CurrentSequence}). " +
                    "Republish to verify painted pixels.";
            }

            int painted = CountPaintedPixels(image);
            if (painted == 0)
            {
                return $"The published frame paints no pixels in its {image.PixelWidth}x{image.PixelHeight} frame: " +
                    "the annotation may be offscreen or clipped. Use the visible canvas center, " +
                    "rebuild, and republish.";
            }

            string count = painted == 1
                ? "1 pixel"
                : $"{painted.ToString("N0", CultureInfo.InvariantCulture)} pixels";
            return $"The published frame paints {count} " +
                $"in its {image.PixelWidth}x{image.PixelHeight} frame.";
        }
        catch (Exception error)
        {
            return "Painted-pixel evidence is unavailable: " + error.Message;
        }
    }

    /// <summary>
    /// Counts the pixels with meaningful alpha in one published overlay
    /// bitmap. The threshold mirrors the Bridge WPF gate's opaque-pixel
    /// convention.
    /// </summary>
    private static int CountPaintedPixels(EngineWpfOverlayDebugImage image)
    {
        using var png = new MemoryStream(image.PngBytes.ToArray());
        var decoder = new PngBitmapDecoder(
            png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32 && frame.Format != PixelFormats.Pbgra32)
        {
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        }

        int stride = checked(frame.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * frame.PixelHeight)];
        frame.CopyPixels(pixels, stride, 0);
        int painted = 0;
        for (int offset = 3; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] >= PaintedAlphaThreshold)
            {
                painted++;
            }
        }

        return painted;
    }

    private void ReportOutcome(
        ToolPublicationOutcome outcome, EngineWpfPublicationReceipt receipt, string publishedText,
        string pixelEvidence = "")
    {
        switch (outcome)
        {
            case ToolPublicationOutcome.Visible:
                Status = publishedText;
                Publication =
                    $"Visible: rev {receipt.Revision}, sequence {receipt.Sequence}, " +
                    $"published {receipt.PublishedAt:O}. " +
                    (pixelEvidence.Length == 0 ? string.Empty : pixelEvidence + " ") +
                    "A submitted drawing is fresh only while " +
                    "the sequence still equals the presentation's current sequence.";
                break;
            case ToolPublicationOutcome.Pending:
                Status = "The request is queued; pixels are not proven visible. Wait, then republish.";
                Publication = $"Pending: rev {receipt.Revision}, sequence {receipt.Sequence}.";
                break;
            case ToolPublicationOutcome.Empty:
                Status = "The request published nothing (empty outcome). Check the recipe and republish.";
                Publication = $"Empty: rev {receipt.Revision}, sequence {receipt.Sequence}.";
                break;
            case ToolPublicationOutcome.Unavailable:
            {
                EngineWpfOverlayState overlayState = _presentation.OverlayState;
                bool samePublication =
                    overlayState.Document == receipt.Document &&
                    overlayState.CaptureId == receipt.CaptureId &&
                    overlayState.Revision == receipt.Revision &&
                    overlayState.Availability == EngineWpfOverlayAvailability.Unavailable;
                string reason = samePublication &&
                    !string.IsNullOrWhiteSpace(overlayState.UnavailableReason)
                    ? overlayState.UnavailableReason
                    : "The current canvas or presentation window could not be validated.";
                Status = "The overlay is unavailable: " + reason;
                string diagnosticCodes = samePublication &&
                    !overlayState.Diagnostics.IsDefaultOrEmpty
                    ? " Diagnostics: " + string.Join(", ",
                        overlayState.Diagnostics.Select(static diagnostic => diagnostic.Code)) + "."
                    : string.Empty;
                Publication =
                    $"Unavailable: rev {receipt.Revision}, sequence {receipt.Sequence}." +
                    diagnosticCodes;
                break;
            }
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
            _live = null;
            _built = null;
            _builtOperation = Guid.Empty;
            Status += " The live scene changed; Build preview or Refresh scene adopts a fresh scene before republishing.";
            RefreshGates();
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
            _built = null;
            Status = "The Allegro connection changed. Reconnect, then Build preview or Refresh scene; old frame authority is retired.";
        }

        RefreshGates();
    }

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

/// <summary>One retained overlay-tool publication record for the operation list.</summary>
public sealed record OverlayOperationRecord(
    Guid OperationId,
    string Action,
    DateTimeOffset AtUtc,
    long Revision,
    ToolPublicationOutcome Outcome,
    string Evidence);
