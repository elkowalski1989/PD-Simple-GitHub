using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Interactions;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Wpf;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.PcbTools.Review;

namespace PD.Simple.Tools.Review;

/// <summary>
/// T07 Share/review view: capture the current qualified view, compose review
/// annotations, choose raw or annotated display, save PNG, export a review
/// bundle with capture identity and hashes, reopen offline, and prune obsolete
/// frames. Review capture is on demand only and independent of fast Browse;
/// no diagnostic images are produced unless explicitly requested here.
/// </summary>
public sealed class ShareReviewToolViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly BridgeSession _bridge;
    private readonly EngineWpfPresentation _presentation;

    private LiveDesignScene? _live;
    private AllegroReviewFrame? _frame;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _disposed;

    private string _status = "Acquire a live scene to capture, or reopen a saved bundle offline.";
    private string _title = "PD review";
    private string _stampX = string.Empty;
    private string _stampY = string.Empty;
    private bool _linkObject;
    private string _objectId = string.Empty;
    private bool _showAnnotated = true;
    private string _outputDirectory = string.Empty;
    private string _bundleNote = string.Empty;
    private string _evidence = "No review frame held.";
    private string _retention = string.Empty;

    public ShareReviewToolViewModel(BridgeSession bridge, EngineWpfPresentation presentation)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        if (!ReferenceEquals(_presentation.Session, _bridge.EngineSession))
        {
            throw new ArgumentException(
                "The presentation must borrow this tool's Engine session.", nameof(presentation));
        }

        RetainedFrames = new ObservableCollection<RetainedReviewRecord>();
        _bridge.StateChanged += Bridge_StateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RetainedReviewRecord> RetainedFrames { get; }

    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string Evidence { get => _evidence; private set => SetField(ref _evidence, value); }
    public string Retention { get => _retention; private set => SetField(ref _retention, value); }

    public bool IsBusy { get => _busy; private set { if (SetField(ref _busy, value)) RefreshGates(); } }
    public bool HasLiveScene => _live is not null;
    public bool HasFrame => _frame is not null;

    public AllegroReviewFrame? CurrentFrame => _frame;
    public bool ShowAnnotated
    {
        get => _showAnnotated;
        set { if (SetField(ref _showAnnotated, value)) OnPropertyChanged(nameof(DisplayedImage)); }
    }

    public System.Windows.Media.ImageSource? DisplayedImage =>
        ReopenedImage ?? (_showAnnotated ? _frame?.Annotated : _frame?.Raw);

    public bool CanAcquire => !_disposed && !_busy && _bridge.HasReadySession;
    public bool CanCapture => !_disposed && !_busy && HasLiveScene;
    public bool CanSavePng => !_disposed && !_busy && HasFrame;
    public bool CanExportBundle => CanSavePng;
    public bool CanCancel => _busy && _operation is not null;

    public string Title { get => _title; set => SetField(ref _title, value); }
    public string StampX { get => _stampX; set => SetField(ref _stampX, value); }
    public string StampY { get => _stampY; set => SetField(ref _stampY, value); }
    public bool LinkObject { get => _linkObject; set => SetField(ref _linkObject, value); }
    public string ObjectId { get => _objectId; set => SetField(ref _objectId, value); }
    public string OutputDirectory { get => _outputDirectory; set => SetField(ref _outputDirectory, value); }
    public string BundleNote { get => _bundleNote; set => SetField(ref _bundleNote, value); }

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
            _live = live;
            Status = $"Live scene acquired ({live.Document}). Capture composes canvas pixels with review annotations.";
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

    public async Task CaptureAsync()
    {
        if (!CanCapture || _live is null)
        {
            return;
        }

        CancellationTokenSource operation = BeginOperation();
        try
        {
            Status = "Capturing the current qualified view…";
            _live.RequireCurrent();
            AnnotationScene annotations = ComposeAnnotations(_live.Scene);
            AllegroReviewFrame frame = await _presentation.CaptureReviewAsync(_live, annotations, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _live.RequireCurrent();
            RetireFrame("A newer capture replaced this frame.");
            _frame = frame;
            DescribeFrame(frame, annotations);
            OnPropertyChanged(nameof(CurrentFrame));
            OnPropertyChanged(nameof(DisplayedImage));
            Status = "Captured a composed historical review image. Raw and annotated variants are both held; " +
                "neither grants live authority.";
        }
        catch (OperationCanceledException)
        {
            Status = "Capture cancelled. No frame was adopted; navigation drawings are unaffected.";
        }
        catch (Exception error)
        {
            Status = "Capture unavailable: " + error.Message;
            InvalidateLiveScene(error);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public Task SavePngAsync(string path, bool includeAnnotations)
    {
        if (!CanSavePng || _frame is null)
        {
            Status = "Nothing to save: capture a review frame first.";
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            Status = "Choose an output PNG path first.";
            return Task.CompletedTask;
        }

        if (File.Exists(path))
        {
            Status = $"Refused to overwrite {Path.GetFileName(path)}. Choose a new name or remove the old file first.";
            return Task.CompletedTask;
        }

        AllegroReviewFrame frame = _frame;
        return Task.Run(() =>
        {
            // Encoding must stay on the dispatcher (STA renderer thread); the
            // atomic write runs here on immutable bytes.
            byte[] png = _presentation.InvokeOnDispatcherAsync(() =>
            {
                using var buffer = new MemoryStream();
                frame.SavePng(buffer, includeAnnotations);
                return Task.FromResult(buffer.ToArray());
            }).GetAwaiter().GetResult();
            try
            {
                ReviewBundleManifest.WriteFileAtomically(path, png);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ReportOnDispatcher("Image export failed: " + error.Message);
                return;
            }

            ReportOnDispatcher(
                $"Saved {(includeAnnotations ? "annotated" : "raw")} PNG ({png.Length} bytes, sha256 " +
                $"{ReviewBundleManifest.Sha256Hex(png)[..16]}…). A failed write never leaves a partial file.");
        });
    }

    public Task ExportBundleAsync(string directory, string note)
    {
        if (!CanExportBundle || _frame is null)
        {
            Status = "Nothing to export: capture a review frame first.";
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            Status = "Choose an output directory first.";
            return Task.CompletedTask;
        }

        AllegroReviewFrame frame = _frame;
        return Task.Run(async () =>
        {
            byte[] raw = await _presentation.InvokeOnDispatcherAsync(() =>
            {
                using var buffer = new MemoryStream();
                frame.SavePng(buffer, includeAnnotations: false);
                return Task.FromResult(buffer.ToArray());
            });
            byte[] annotated = await _presentation.InvokeOnDispatcherAsync(() =>
            {
                using var buffer = new MemoryStream();
                frame.SavePng(buffer, includeAnnotations: true);
                return Task.FromResult(buffer.ToArray());
            });
            Guid bundleId = Guid.NewGuid();
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string rawName = $"review-{stamp}-raw.png";
            string annotatedName = $"review-{stamp}-annotated.png";
            var settings = new Dictionary<string, string>
            {
                ["captureId"] = frame.Capture.SceneCaptureId?.ToString() ?? string.Empty,
                ["document"] = frame.Capture.Document?.ToString() ?? string.Empty,
                ["observedAtUtc"] = frame.Capture.ObservedAt.ToString("O", CultureInfo.InvariantCulture),
                ["qualification"] = frame.Capture.Qualification,
                ["provider"] = frame.Capture.Provider ?? string.Empty,
                ["title"] = Title,
            };
            ReviewBundleManifest.Manifest manifest = ReviewBundleManifest.Build(
                bundleId, DateTimeOffset.UtcNow,
                frame.Capture.SceneCaptureId, frame.Capture.Document?.ToString(),
                "live-canvas", includeAnnotations: true,
                frame.Capture.Width, frame.Capture.Height, frame.Capture.ObservedAt,
                ViewportText(frame), frame.Capture.Qualification, settings,
                [("raw", rawName, raw), ("annotated", annotatedName, annotated)],
                string.IsNullOrWhiteSpace(note) ? null : note);
            try
            {
                Directory.CreateDirectory(directory);
                ReviewBundleManifest.WriteFileAtomically(Path.Combine(directory, rawName), raw);
                ReviewBundleManifest.WriteFileAtomically(Path.Combine(directory, annotatedName), annotated);
                ReviewBundleManifest.WriteTextAtomically(
                    Path.Combine(directory, ReviewBundleManifest.ManifestFileName),
                    ReviewBundleManifest.Serialize(manifest));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ReportOnDispatcher("Bundle export failed: " + error.Message +
                    " Valid live drawings are unaffected; no partial bundle is advertised as complete.");
                return;
            }

            string? verification = ReviewBundleManifest.VerifyImages(directory, manifest);
            if (verification is not null)
            {
                ReportOnDispatcher("Bundle written but verification failed: " + verification);
                return;
            }

            RetainedFrames.Add(new RetainedReviewRecord(
                bundleId, directory, DateTimeOffset.UtcNow, manifest.Images.Length,
                $"capture {frame.Capture.SceneCaptureId} {frame.Capture.Width}x{frame.Capture.Height}"));
            ReportOnDispatcher(
                $"Exported review bundle {bundleId} with raw + annotated PNG and manifest " +
                $"(raw sha256 {ReviewBundleManifest.Sha256Hex(raw)[..16]}…, " +
                $"annotated {ReviewBundleManifest.Sha256Hex(annotated)[..16]}…). " +
                "The bundle carries evidence only: reopening it grants no live authority.");
        });
    }

    public Task LoadBundleAsync(string manifestPath)
    {
        if (_busy || _disposed)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            Status = "Choose a review-bundle.json file first.";
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            string json;
            try
            {
                json = File.ReadAllText(manifestPath);
            }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException)
            {
                ReportOnDispatcher("Bundle reopen failed: " + readError.Message);
                return;
            }

            if (!ReviewBundleManifest.TryParse(json, out ReviewBundleManifest.Manifest? manifest, out string? error) ||
                manifest is null)
            {
                ReportOnDispatcher("Bundle reopen failed: " + error);
                return;
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            string? verification = ReviewBundleManifest.VerifyImages(directory, manifest);
            if (verification is not null)
            {
                ReportOnDispatcher("Bundle reopen failed: " + verification);
                return;
            }

            // Load the annotated PNG (fall back to the first image) for display.
            string imageName = manifest.Images.FirstOrDefault(image =>
                string.Equals(image.Kind, "annotated", StringComparison.Ordinal))?.FileName
                ?? manifest.Images[0].FileName;
            BitmapImage image = new();
            image.BeginInit();
            image.UriSource = new Uri(Path.Combine(directory, imageName));
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            _presentation.InvokeOnDispatcherAsync<object?>(() =>
            {
                RetireFrame("A reopened bundle replaced the live frame.");
                ReopenedImage = image;
                OnPropertyChanged(nameof(DisplayedImage));
                Evidence =
                    $"Reopened bundle {manifest.BundleId} ({manifest.Images.Length} images, " +
                    $"{manifest.ImageWidth}x{manifest.ImageHeight}, mode {manifest.Mode}, " +
                    $"captured {manifest.ObservedAtUtc:O}). Historical evidence only: no credentials, " +
                    $"no live authority, hashes verified.";
                if (!string.IsNullOrWhiteSpace(manifest.Note))
                {
                    Evidence += " Note: " + manifest.Note;
                }

                RetainedFrames.Add(new RetainedReviewRecord(
                    manifest.BundleId, directory, manifest.CreatedAtUtc, manifest.Images.Length,
                    "reopened offline, hashes verified"));
                Status = "Reopened a historical review bundle offline. No Allegro connection was used.";
                return Task.FromResult<object?>(null);
            }).GetAwaiter().GetResult();
        });
    }

    public void RemoveRetained(RetainedReviewRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            string manifestPath = Path.Combine(record.Directory, ReviewBundleManifest.ManifestFileName);
            if (File.Exists(manifestPath))
            {
                string json = File.ReadAllText(manifestPath);
                if (ReviewBundleManifest.TryParse(json, out ReviewBundleManifest.Manifest? manifest, out _) &&
                    manifest is not null)
                {
                    // Delete only manifest-linked files by their actual names.
                    foreach (var image in manifest.Images)
                    {
                        TryDelete(Path.Combine(record.Directory, image.FileName));
                    }

                    TryDelete(manifestPath);
                }
            }

            RetainedFrames.Remove(record);
            Retention = $"Removed bundle {record.BundleId}. Only its manifest-linked files were deleted.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Retention = $"Removal failed: {error.Message}";
        }
    }

    public void PruneDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Retention = "Choose an existing bundle directory first.";
            return;
        }

        string manifestPath = Path.Combine(directory, ReviewBundleManifest.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            Retention = "No review-bundle.json in that directory; nothing was pruned.";
            return;
        }

        try
        {
            string json = File.ReadAllText(manifestPath);
            if (!ReviewBundleManifest.TryParse(json, out ReviewBundleManifest.Manifest? manifest, out string? error) ||
                manifest is null)
            {
                Retention = "Prune refused: " + error;
                return;
            }

            IReadOnlyList<string> deleted = ReviewBundleManifest.PruneUnlinkedFiles(directory, manifest);
            Retention = deleted.Count == 0
                ? "No orphan files: every file is manifest-linked."
                : $"Pruned {deleted.Count} orphan file(s): {string.Join(", ", deleted)}. Manifest-linked images were kept.";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Retention = "Prune failed: " + failure.Message;
        }
    }

    public void CopyOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Status = "No output path to copy.";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(path);
            Status = "Copied the output path to the clipboard.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "Clipboard unavailable. The path stays in the output box.";
        }
    }

    public void Cancel() => _operation?.Cancel();

    public void RefreshGates()
    {
        OnPropertyChanged(nameof(CanAcquire));
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CanSavePng));
        OnPropertyChanged(nameof(CanExportBundle));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(HasLiveScene));
        OnPropertyChanged(nameof(HasFrame));
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
        RetireFrame("The tool view was disposed.");
        _live = null;
        ReopenedImage = null;
    }

    internal System.Windows.Media.ImageSource? ReopenedImage { get; private set; }

    private AnnotationScene ComposeAnnotations(DesignScene scene)
    {
        var items = new List<Annotation>();
        if (!string.IsNullOrWhiteSpace(Title))
        {
            items.Add(new Annotation(
                "review-title",
                new PointGeometry(new DesignPoint(ParseDecimal(StampX, "stamp X"), ParseDecimal(StampY, "stamp Y"))),
                AnnotationRole.Information,
                Title.Trim(),
                AnnotationHitBehavior.Passive));
        }

        if (_linkObject && !string.IsNullOrWhiteSpace(ObjectId))
        {
            SceneObjectReference target = scene.ReferenceTo(new SceneObjectId(ObjectId.Trim()));
            if (!scene.Contains(target.ObjectId))
            {
                throw new InvalidOperationException(
                    $"Object '{ObjectId.Trim()}' is absent from the current scene. Pick its id from Explorer Inspect.");
            }

            items.Add(new Annotation(
                "review-object",
                new PointGeometry(new DesignPoint(ParseDecimal(StampX, "stamp X"), ParseDecimal(StampY, "stamp Y"))),
                AnnotationRole.Finding,
                "review marker",
                AnnotationHitBehavior.Passive,
                target));
        }

        return AnnotationScene.Empty(scene.Identity.CaptureId).Replace(items);
    }

    private void DescribeFrame(AllegroReviewFrame frame, AnnotationScene annotations)
    {
        if (string.IsNullOrWhiteSpace(StampX) || string.IsNullOrWhiteSpace(StampY))
        {
            // Default the next stamp to the captured viewport center so review
            // marks land on visible pixels without inventing coordinates.
            StampX = ((frame.Capture.Viewport.MinimumX + frame.Capture.Viewport.MaximumX) / 2)
                .ToString("0.###", CultureInfo.InvariantCulture);
            StampY = ((frame.Capture.Viewport.MinimumY + frame.Capture.Viewport.MaximumY) / 2)
                .ToString("0.###", CultureInfo.InvariantCulture);
        }

        Evidence =
            $"Capture {frame.Capture.SceneCaptureId} from {frame.Capture.Document} " +
            $"({frame.Capture.Width}x{frame.Capture.Height}, observed {frame.Capture.ObservedAt:O}, " +
            $"qualification {frame.Capture.Qualification}). Raw versus annotated: " +
            (annotations.Items.IsEmpty
                ? "identical (no review annotations composed)."
                : $"{annotations.Items.Length} review annotation(s) composed over raw pixels.") +
            " Historical evidence only.";
    }

    private static string ViewportText(AllegroReviewFrame frame)
    {
        var viewport = frame.Capture.Viewport;
        return string.Create(CultureInfo.InvariantCulture,
            $"{viewport.MinimumX:0.###},{viewport.MinimumY:0.###},{viewport.MaximumX:0.###},{viewport.MaximumY:0.###} {viewport.Units}");
    }

    private static decimal ParseDecimal(string text, string what) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : throw new InvalidOperationException($"Enter {what} as a number in mils.");

    private void RetireFrame(string reason)
    {
        _frame = null;
        ReopenedImage = null;
        OnPropertyChanged(nameof(CurrentFrame));
        OnPropertyChanged(nameof(DisplayedImage));
        System.Diagnostics.Trace.TraceInformation("Review frame retired: {0}", reason);
    }

    private void InvalidateLiveScene(Exception error)
    {
        if (error is InvalidOperationException && _live is not null && !_live.IsCurrent)
        {
            _live = null;
            Status += " The live scene changed; acquire a fresh scene before recapturing.";
            RefreshGates();
        }
    }

    private void ReportOnDispatcher(string message)
    {
        _presentation.InvokeOnDispatcherAsync<object?>(() =>
        {
            Status = message;
            return Task.FromResult<object?>(null);
        }).GetAwaiter().GetResult();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Review retention could not delete {0}: {1}", path, error.Message);
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

/// <summary>One retained review bundle record (live export or offline reopen).</summary>
public sealed record RetainedReviewRecord(
    Guid BundleId,
    string Directory,
    DateTimeOffset AtUtc,
    int ImageCount,
    string Evidence);
