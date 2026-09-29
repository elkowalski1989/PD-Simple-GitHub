using System.Collections.Immutable;
using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Reviews;
using CircuitHub.AllegroBridge.Engine.Tools;
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
/// bundle with capture identity and hashes, and reopen offline. Review capture is on demand only and independent of fast Browse;
/// no diagnostic images are produced unless explicitly requested here.
/// </summary>
public enum ReviewOperationState
{
    Idle,
    Acquiring,
    Capturing,
    Encoding,
    Saving,
    Opening,
    Disposing,
}

public sealed record ReviewSaveResult(string Path, bool Committed, DateTimeOffset SavedAtUtc);

public sealed record ReviewImageState(BitmapSource? Image, string? VariantId,
    ReviewArchiveContent Content, LoadedReviewFile? Loaded, bool Legacy);

public sealed class ShareReviewToolViewModel : INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private readonly BridgeSession _bridge;
    private readonly EngineWpfPresentation _presentation;

    private LiveDesignScene? _live;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _disposed;
    private bool _closing;
    private long _operationId;
    private Task _operationTask = Task.CompletedTask;
    private ReviewOperationState _operationState;
    private ReviewImageState? _images;

    public ReviewOperationState OperationState => _operationState;
    public ReviewSaveResult? LastSaveResult { get; private set; }
    public bool CanOpen => !_disposed && !_closing && !_busy;
    public bool CanShowRaw => AvailableVariant(ReviewImageKind.Raw) is not null;
    public bool CanShowAnnotated => AvailableVariant(ReviewImageKind.Annotated) is not null;
    public ReviewDocument? PortableReview => _images?.Content.Review;
    public bool IsLegacyReview => _images?.Legacy == true;
    public IReadOnlyList<ReviewFinding> Findings => PortableReview is { } review ? review.Findings : [];
    private string? _selectedFindingId;
    public string? SelectedFindingId
    {
        get => _selectedFindingId;
        set
        {
            if (SetField(ref _selectedFindingId, value))
            {
                OnPropertyChanged(nameof(SelectedFindingDetails));
            }
        }
    }
    public string SelectedFindingDetails
    {
        get
        {
            ReviewFinding? finding = PortableReview?.Findings.FirstOrDefault(item => item.Id == SelectedFindingId);
            if (finding is null)
            {
                if (PortableReview is not { } review)
                {
                    return "Open a review to inspect captured measurements and decision history.";
                }
                return string.Join("\n", review.Analyses.Select(analysis =>
                    $"Analysis {analysis.ToolId} {analysis.ToolVersion}: {analysis.Outcome}; {analysis.ResultCount} findings; coverage " +
                    (analysis.Coverage is null || analysis.Coverage.Rules.IsEmpty ? "Unknown" :
                        string.Join(", ", analysis.Coverage.Rules.Select(rule => rule.Completion).Distinct())) +
                    ". " + string.Join(" ", analysis.Omissions))) +
                    "\nSelect a finding, when present, to inspect measurements and disposition history.";
            }
            ReviewAnalysis analysis = PortableReview!.Analyses.Single(item => item.Id == finding.AnalysisId);
            return $"{finding.Title}\nCapture {finding.CaptureId}; rule {finding.RuleId ?? "unrecorded"}; {finding.Severity}.\n" +
                string.Join("\n", finding.Measurements.Select(item => $"{item.Name}: {item.Value} {item.Unit} ({item.Qualification})")) +
                $"\nAnalysis {analysis.ToolId} {analysis.ToolVersion}: {analysis.Outcome}; coverage " +
                (analysis.Coverage is null ? "Unknown." : string.Join("\n", analysis.Coverage.Rules.Select(rule =>
                    $"{rule.RuleId}: {rule.Completion}; scope {rule.Domain.Kind}; output truncated {rule.OutputTruncated}; " +
                    $"finding limit {rule.FindingLimit?.ToString() ?? "none recorded"}. " + string.Join(" ", rule.Omissions)))) + "\n" +
                string.Join("\n", analysis.Omissions.Concat(analysis.Diagnostics.Select(item => item.Message))) + "\n" +
                string.Join("\n", PortableReview.Dispositions.Where(item => item.Subject.FindingId == finding.Id)
                    .Select(item => $"{item.AtUtc:O} {item.Actor}: {item.StatusCode} — {item.Note}"));
        }
    }
    public bool CanSaveRevision => CanExportBundle && _images?.Loaded is { } previous &&
        _images.Content.Review.Revision > previous.Content.Review.Revision;
    public IReadOnlyList<ReviewDisposition> Dispositions => PortableReview is { } review ? review.Dispositions : [];
    public string RawUnavailableReason => CanShowRaw ? string.Empty : "This review has no verified raw image.";
    public string AnnotatedUnavailableReason => CanShowAnnotated ? string.Empty : "This review has no verified annotated image.";

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
    public string Evidence
    {
        get
        {
            if (PortableReview is not { } review)
            {
                return _evidence;
            }
            string summary = $"Historical review {review.ReviewId}, revision {review.Revision}; " +
                $"{review.Findings.Length} findings, {review.Dispositions.Length} decisions. " +
                string.Join(" ", review.Diagnostics.Select(item => item.Message));
            return string.IsNullOrWhiteSpace(_evidence) ? summary : summary + " " + _evidence;
        }
        private set => SetField(ref _evidence, value);
    }
    public string Retention { get => _retention; private set => SetField(ref _retention, value); }

    public bool IsBusy { get => _busy; private set { if (SetField(ref _busy, value)) RefreshGates(); } }
    public bool HasLiveScene => _live is not null;
    public bool HasFrame => _images?.Content.Images.Count > 0;
    public bool ShowAnnotated
    {
        get => _showAnnotated;
        set
        {
            if (value == _showAnnotated)
            {
                return;
            }
            ReviewImageKind kind = value ? ReviewImageKind.Annotated : ReviewImageKind.Raw;
            ReviewImageVariant? variant = AvailableVariant(kind);
            if (_images is null || variant is null)
            {
                Status = value ? AnnotatedUnavailableReason : RawUnavailableReason;
                return;
            }
            try
            {
                // One decoded variant is retained. Switching temporarily holds at most two
                // bounded images, so the 33,554,432-pixel display budget is preserved.
                BitmapSource image = DecodeImage(_images.Content.Images[variant.MemberName!].ToArray(),
                    variant.Width!.Value, variant.Height!.Value);
                _images = _images with { Image = image, VariantId = variant.Id };
                _showAnnotated = value;
                RefreshImages();
            }
            catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
            {
                Status = "Image variant rejected: " + error.Message;
            }
        }
    }

    public System.Windows.Media.ImageSource? DisplayedImage => _images?.Image;

    private ReviewImageVariant? AvailableVariant(ReviewImageKind kind) => _images?.Content.Review.Images
        .FirstOrDefault(item => item.Kind == kind && item.Availability == ReviewImageAvailability.Available);

    public bool CanAcquire => !_disposed && !_closing && !_busy && _bridge.HasReadySession;
    public bool CanCapture => !_disposed && !_closing && !_busy && HasLiveScene;
    public bool CanSavePng => !_disposed && !_closing && !_busy && HasFrame;
    public bool CanExportBundle => !_disposed && !_closing && !_busy && _images is not null;
    public bool CanCancel => _busy && _operation is not null;

    public string Title { get => _title; set => SetField(ref _title, value); }
    public string StampX { get => _stampX; set => SetField(ref _stampX, value); }
    public string StampY { get => _stampY; set => SetField(ref _stampY, value); }
    public bool LinkObject { get => _linkObject; set => SetField(ref _linkObject, value); }
    public string ObjectId { get => _objectId; set => SetField(ref _objectId, value); }
    public string OutputDirectory { get => _outputDirectory; set => SetField(ref _outputDirectory, value); }
    public string BundleNote { get => _bundleNote; set => SetField(ref _bundleNote, value); }

    public Task AcquireSceneAsync() => RunOperationAsync(ReviewOperationState.Acquiring, async (id, token) =>
    {
        if (!_bridge.HasReadySession)
        {
            Status = "Connect to Allegro before acquiring a review scene.";
            return;
        }
        Status = "Acquiring a live metadata scene from Allegro…";
        var requestedDocument = _bridge.EngineSession.State.Document;
        LiveDesignScene live = await _bridge.ReadEngineSceneAsync(SceneQuery.Metadata, token);
        token.ThrowIfCancellationRequested();
        live.RequireCurrent();
        if (!CanAdopt(id) || live.Document != requestedDocument)
        {
            ReportBoardChanged();
            return;
        }
        _live = live;
        Status = $"Live scene acquired ({live.Document}). Capture composes canvas pixels with review annotations.";
    });

    public Task CaptureAsync() => RunOperationAsync(ReviewOperationState.Capturing, async (id, token) =>
    {
        LiveDesignScene? live = _live;
        if (live is null)
        {
            Status = "Acquire a live scene before capturing a review.";
            return;
        }
        var inputs = new AnnotationInputs(Title, StampX, StampY, LinkObject, ObjectId);
        ValidateAnnotationInputs(live.Scene, inputs);
        live.RequireCurrent();
        Status = "Capturing the current qualified view…";
        EngineWpfCanvasCapture capture = await _presentation.CaptureAsync(live, token);
        token.ThrowIfCancellationRequested();
        live.RequireCurrent();
        long newPixels = (long)capture.Width * capture.Height;
        long heldPixels = _images?.Image is not { } heldImage ? 0 : (long)heldImage.PixelWidth * heldImage.PixelHeight;
        if (newPixels > 16_777_216 || newPixels * 2 + heldPixels > 33_554_432)
        {
            throw new InvalidOperationException("This capture exceeds the review display budget. Clear the held review or use a smaller viewport.");
        }
        AnnotationScene annotations = ComposeAnnotations(live.Scene, inputs, capture.Viewport);
        AllegroReviewFrame frame = AllegroReviewFrame.Compose(capture, live.Scene, annotations);
        byte[] raw = await EncodeAsync(frame, false);
        byte[] annotated = await EncodeAsync(frame, true);
        var descriptor = new ToolDescriptor("pd.review-capture", "PD review capture", new Version(1, 0),
            [live.Scene.Document.Kind], []);
        var result = new ToolResult([], annotations, []);
        ReviewArchiveContent content = ReviewDocumentBuilder.FromToolResult(live.Scene, descriptor, result,
            JsonSerializer.SerializeToElement(new { title = inputs.Title, inputs.LinkObject, inputs.ObjectId }),
            ProducerIdentity(), capture.ObservedAt, ReviewAnalysisOutcome.Unknown, BundleNote);
        var viewport = new ReviewViewport(capture.Viewport.MinimumX, capture.Viewport.MinimumY,
            capture.Viewport.MaximumX, capture.Viewport.MaximumY, capture.Viewport.Units);
        content = PortableReviewPolicy.WithPolicy(content with
        {
            Review = content.Review with
            {
                Images =
                [
                    new("raw", live.Scene.Identity.CaptureId, ReviewImageKind.Raw, ReviewImageAvailability.Available,
                        "images/0001.png", capture.Width, capture.Height, viewport),
                    new("annotated", live.Scene.Identity.CaptureId, ReviewImageKind.Annotated, ReviewImageAvailability.Available,
                        "images/0002.png", capture.Width, capture.Height, viewport),
                ],
            },
            Images = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
                .Add("images/0001.png", [.. raw]).Add("images/0002.png", [.. annotated]),
        });
        if (!CanAdopt(id) || !ReferenceEquals(live, _live) || capture.Document != live.Document)
        {
            ReportBoardChanged();
            return;
        }
        _images = new(frame.Annotated, "annotated", content, null, false);
        _showAnnotated = true;
        if (NeedsPlacement(inputs) && string.IsNullOrWhiteSpace(inputs.X))
        {
            DesignPoint center = ResolvePlacement(inputs, capture.Viewport);
            StampX = center.X.ToString(CultureInfo.InvariantCulture);
            StampY = center.Y.ToString(CultureInfo.InvariantCulture);
        }
        Evidence = $"Capture {capture.SceneCaptureId} from {capture.Document} " +
            $"({capture.Width}x{capture.Height}, observed {capture.ObservedAt:O}, " +
            $"qualification {capture.Qualification}). {annotations.Items.Length} review annotation(s). Historical evidence only.";
        RefreshImages();
        Status = "Captured raw and annotated historical review images. Neither grants live authority.";
    });

    public Task SavePngAsync(string path, bool includeAnnotations) =>
        RunOperationAsync(ReviewOperationState.Saving, async (id, token) =>
        {
            ReviewImageVariant? variant = AvailableVariant(includeAnnotations ? ReviewImageKind.Annotated : ReviewImageKind.Raw);
            if (_images is null || variant is null)
            {
                Status = "Nothing to save: capture or reopen the requested review image first.";
                return;
            }
            ImmutableArray<byte> bytes = _images.Content.Images[variant.MemberName!];
            ArchiveSaveResult saved = await Task.Run(async () => await ArchiveFile.SaveNewAsync(path,
                (stream, cancellation) => stream.WriteAsync(bytes.AsMemory(), cancellation), token));
            LastSaveResult = new(saved.Path, true, saved.CommittedAtUtc);
            if (CanAdopt(id))
            {
                Status = $"Saved {(includeAnnotations ? "annotated" : "raw")} PNG ({bytes.Length} bytes).";
            }
        });

    public Task ExportBundleAsync(string directory, string note) =>
        RunOperationAsync(ReviewOperationState.Saving, async (id, token) =>
        {
            if (_images is null)
            {
                Status = "Nothing to export: capture or reopen a review first.";
                return;
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            string path = Path.Combine(Path.GetFullPath(directory), "review-" + Guid.NewGuid().ToString("N") + ReviewArchive.FileExtension);
            await SaveCopyCoreAsync(path, note, id, token);
        });

    public Task SavePortableCopyAsync(string path, string note) =>
        RunOperationAsync(ReviewOperationState.Saving, (id, token) => SaveCopyCoreAsync(path, note, id, token));

    private async Task SaveCopyCoreAsync(string path, string note, long id, CancellationToken token)
    {
        ReviewImageState current = _images ?? throw new InvalidOperationException("Capture or open a review first.");
        ReviewArchiveContent content = current.Content;
        if (content.Review.Note != note)
        {
            content = content with { Review = content.Review with
            {
                Note = note, Revision = checked(content.Review.Revision + 1), UpdatedAtUtc = DateTimeOffset.UtcNow,
            } };
        }
        ArchiveSaveResult saved = await Task.Run(async () => await ReviewFile.SaveCopyAsync(path, content, cancellationToken: token));
        LastSaveResult = new(saved.Path, true, saved.CommittedAtUtc);
        if (CanAdopt(id))
        {
            _images = current with { Content = content, Loaded = new(saved.Path, saved.Sha256, content), Legacy = false };
            RetainedFrames.Add(new(content.Review.ReviewId, saved.Path, saved.CommittedAtUtc, content.Images.Count,
                $"portable historical review, revision {content.Review.Revision}"));
            OutputDirectory = saved.Path;
            Status = "Saved portable review with source evidence, findings, dispositions and independent image variants.";
            RefreshImages();
        }
    }

    public Task SaveRevisionAsync() => RunOperationAsync(ReviewOperationState.Saving, async (id, token) =>
    {
        ReviewImageState current = _images ?? throw new InvalidOperationException("Open a portable review first.");
        LoadedReviewFile previous = current.Loaded ?? throw new InvalidOperationException("Save a portable copy before updating this review.");
        ArchiveSaveResult saved = await Task.Run(async () => await ReviewFile.SaveRevisionAsync(previous, current.Content,
            cancellationToken: token));
        LastSaveResult = new(saved.Path, true, saved.CommittedAtUtc);
        if (CanAdopt(id))
        {
            _images = current with { Loaded = new(saved.Path, saved.Sha256, current.Content) };
            Status = $"Saved review revision {current.Content.Review.Revision}.";
            RefreshImages();
        }
    });

    public void AddDisposition(string status, string actor, string note)
    {
        if (_busy || _closing || _images is null || SelectedFindingId is not { } findingId ||
            !_images.Content.Review.Findings.Any(item => item.Id == findingId))
        {
            throw new InvalidOperationException("Select a finding in the currently displayed historical review.");
        }
        if (!PortableReviewPolicy.DispositionVocabulary.ContainsKey(status) ||
            status == "pd:dismissed" && string.IsNullOrWhiteSpace(note))
        {
            throw new InvalidOperationException("Choose a PD disposition and explain any dismissal.");
        }
        if (status == "pd:resolved-in-later-capture" && !_images.Content.Review.Comparisons.Any(comparison =>
            comparison.Findings.Any(finding => finding.Outcome == FindingComparisonOutcome.Resolved &&
                finding.BaselineFindingIds.Contains(findingId))))
        {
            throw new InvalidOperationException("A later-capture resolution requires recorded comparison evidence for this finding.");
        }
        ReviewDocument revised = _images.Content.Review.AddDisposition(findingId, status, actor, note, DateTimeOffset.UtcNow);
        _images = _images with { Content = _images.Content with { Review = revised } };
        Status = "Disposition recorded for this historical review subject. Save to retain it.";
        RefreshImages();
    }

    public Task LoadBundleAsync(string manifestPath) =>
        RunOperationAsync(ReviewOperationState.Opening, async (id, token) =>
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                Status = "Choose a portable review or legacy review-bundle.json file first.";
                return;
            }
            string fullPath = Path.GetFullPath(manifestPath);
            ReviewImageState candidate = await Task.Run(async () =>
            {
                bool legacy = !string.Equals(Path.GetExtension(fullPath), ReviewArchive.FileExtension, StringComparison.OrdinalIgnoreCase);
                LoadedReviewFile? loaded = legacy ? null : await ReviewFile.LoadAsync(fullPath, cancellationToken: token);
                ReviewArchiveContent content = loaded?.Content ?? PortableReviewPolicy.ImportLegacy(fullPath, token);
                return DecodeReview(content, loaded, legacy);
            }, token);
            token.ThrowIfCancellationRequested();
            if (!CanAdopt(id))
            {
                return;
            }
            AdoptReview(candidate);
            RetainedFrames.Add(new(candidate.Content.Review.ReviewId, fullPath, candidate.Content.Review.CreatedAtUtc,
                candidate.Content.Images.Count, "reopened offline, hashes verified"));
            Status = "Reopened a historical review offline. No Allegro connection was used.";
        });

    public Task PublishToolReviewAsync(DesignScene scene, ToolDescriptor tool, ToolResult result, JsonElement options,
        JsonElement? pdPolicy = null) => RunOperationAsync(ReviewOperationState.Opening, (id, token) =>
    {
        ReviewArchiveContent content = PortableReviewPolicy.WithPolicy(ReviewDocumentBuilder.FromToolResult(scene, tool,
            result, options, ProducerIdentity(), DateTimeOffset.UtcNow), pdPolicy);
        token.ThrowIfCancellationRequested();
        if (CanAdopt(id))
        {
            AdoptReview(DecodeReview(content, null, false));
            Status = "Historical tool findings are ready for review and portable export.";
        }
        return Task.CompletedTask;
    });

    public void ClearReview()
    {
        if (_busy || _closing)
        {
            return;
        }
        _images = null;
        SelectedFindingId = null;
        Evidence = "No historical review held.";
        RefreshImages();
    }

    private void AdoptReview(ReviewImageState candidate)
    {
        _images = candidate;
        _showAnnotated = candidate.Content.Review.Images.Any(item => item.Id == candidate.VariantId && item.Kind == ReviewImageKind.Annotated);
        SelectedFindingId = null;
        BundleNote = candidate.Content.Review.Note ?? string.Empty;
        Evidence = string.Empty;
        RefreshImages();
    }

    private static ReviewImageState DecodeReview(ReviewArchiveContent content, LoadedReviewFile? loaded, bool legacy)
    {
        ReviewImageVariant? variant = content.Review.Images.Where(item => item.Availability == ReviewImageAvailability.Available)
            .OrderByDescending(item => item.Kind == ReviewImageKind.Annotated).FirstOrDefault();
        BitmapSource? image = variant is null ? null : DecodeImage(content.Images[variant.MemberName!].ToArray(),
            variant.Width!.Value, variant.Height!.Value);
        return new(image, variant?.Id, content, loaded, legacy);
    }

    private static ReviewProducer ProducerIdentity()
    {
        var assemblies = new[] { typeof(ReviewArchive).Assembly, typeof(ReviewFile).Assembly, typeof(AllegroReviewFrame).Assembly };
        ImmutableArray<ReviewSoftwareIdentity> software = assemblies.Select(assembly => new ReviewSoftwareIdentity(
            assembly.GetName().Name!, assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion ??
                assembly.GetName().Version!.ToString())).ToImmutableArray();
        return new("PD.Simple", typeof(ShareReviewToolViewModel).Assembly.GetName().Version!.ToString(), software);
    }

    private static BitmapSource DecodeImage(byte[] bytes, int width, int height)
    {
        (int actualWidth, int actualHeight) = ReviewArchive.ReadPngDimensions(bytes);
        if (actualWidth != width || actualHeight != height)
        {
            throw new InvalidDataException("The PNG header does not match the bounded manifest dimensions.");
        }
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        if (image.PixelWidth != width || image.PixelHeight != height)
        {
            throw new InvalidDataException("The decoded image dimensions do not match the manifest.");
        }
        image.Freeze();
        return image;
    }

    public void RemoveRetained(RetainedReviewRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        RetainedFrames.Remove(record);
        Retention = $"Removed bundle {record.BundleId} from recent items. Its files are retained.";
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
        OnPropertyChanged(nameof(CanOpen));
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
        if (!_operationTask.IsCompleted)
        {
            throw new InvalidOperationException("Review work is active; await DisposeAsync before releasing presentation owners.");
        }
        CompleteDisposal();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await _presentation.InvokeOnDispatcherAsync(async () =>
        {
            _closing = true;
            SetOperationState(ReviewOperationState.Disposing);
            Cancel();
            await _operationTask;
            CompleteDisposal();
            return true;
        });
    }

    private void CompleteDisposal()
    {
        if (_disposed)
        {
            return;
        }
        _closing = true;
        _disposed = true;
        _bridge.StateChanged -= Bridge_StateChanged;
        _images = null;
        _live = null;
    }

    internal sealed record AnnotationInputs(string Title, string X, string Y, bool LinkObject, string ObjectId);

    private static bool NeedsPlacement(AnnotationInputs inputs) =>
        !string.IsNullOrWhiteSpace(inputs.Title) || inputs.LinkObject;

    internal static void ValidateAnnotationInputs(DesignScene scene, AnnotationInputs inputs)
    {
        if (!NeedsPlacement(inputs))
        {
            return;
        }
        bool xBlank = string.IsNullOrWhiteSpace(inputs.X);
        bool yBlank = string.IsNullOrWhiteSpace(inputs.Y);
        if (xBlank != yBlank)
        {
            throw new InvalidOperationException("Enter both stamp coordinates, or leave both blank for the captured viewport center.");
        }
        if (!xBlank)
        {
            _ = ParseDecimal(inputs.X, "stamp X");
            _ = ParseDecimal(inputs.Y, "stamp Y");
        }
        if (inputs.LinkObject && (string.IsNullOrWhiteSpace(inputs.ObjectId) ||
            !scene.Contains(new SceneObjectId(inputs.ObjectId.Trim()))))
        {
            throw new InvalidOperationException("The linked object must exist in this review scene. A metadata-only scene has no object authority.");
        }
    }

    internal static DesignPoint ResolvePlacement(AnnotationInputs inputs, EngineWpfCanvasViewport viewport)
    {
        if (!string.IsNullOrWhiteSpace(inputs.X))
        {
            return new(ParseDecimal(inputs.X, "stamp X"), ParseDecimal(inputs.Y, "stamp Y"));
        }
        LengthUnit units = Length.ParseUnit(viewport.Units);
        decimal x = viewport.MinimumX / 2 + viewport.MaximumX / 2;
        decimal y = viewport.MinimumY / 2 + viewport.MaximumY / 2;
        return new(Length.From(x, units).Mils, Length.From(y, units).Mils);
    }

    internal static AnnotationScene ComposeAnnotations(DesignScene scene, AnnotationInputs inputs,
        EngineWpfCanvasViewport viewport)
    {
        ValidateAnnotationInputs(scene, inputs);
        var items = new List<Annotation>();
        if (NeedsPlacement(inputs))
        {
            DesignPoint point = ResolvePlacement(inputs, viewport);
            if (!string.IsNullOrWhiteSpace(inputs.Title))
            {
                items.Add(new Annotation("review-title", new PointGeometry(point), AnnotationRole.Information,
                    inputs.Title.Trim(), AnnotationHitBehavior.Passive));
            }
            if (inputs.LinkObject)
            {
                items.Add(new Annotation("review-object", new PointGeometry(point), AnnotationRole.Finding,
                    "review marker", AnnotationHitBehavior.Passive,
                    scene.ReferenceTo(new SceneObjectId(inputs.ObjectId.Trim()))));
            }
        }
        return AnnotationScene.Empty(scene.Identity.CaptureId).Replace(items);
    }

    private static decimal ParseDecimal(string text, string what) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : throw new InvalidOperationException($"Enter {what} as a number in mils.");

    private Task<byte[]> EncodeAsync(AllegroReviewFrame frame, bool includeAnnotations) =>
        _presentation.InvokeOnDispatcherAsync(() =>
        {
            using var buffer = new MemoryStream();
            frame.SavePng(buffer, includeAnnotations);
            return Task.FromResult(buffer.ToArray());
        });

    private Task RunOperationAsync(ReviewOperationState state, Func<long, CancellationToken, Task> action) =>
        _presentation.InvokeOnDispatcherAsync(async () =>
        {
            if (_disposed || _closing || _busy)
            {
                return false;
            }
            long id = ++_operationId;
            var operation = new CancellationTokenSource();
            _operation = operation;
            IsBusy = true;
            SetOperationState(state);
            _operationTask = RunOperationCoreAsync(id, operation, action);
            await _operationTask;
            return true;
        });

    private async Task RunOperationCoreAsync(long id, CancellationTokenSource operation,
        Func<long, CancellationToken, Task> action)
    {
        try
        {
            await action(id, operation.Token);
        }
        catch (OperationCanceledException)
        {
            if (CanAdopt(id))
            {
                Status = "Review operation cancelled before adoption. Previously held review evidence is unchanged.";
            }
        }
        catch (Exception error)
        {
            if (CanAdopt(id))
            {
                Status = "Review operation failed: " + error.Message;
            }
        }
        finally
        {
            _operation = null;
            operation.Dispose();
            IsBusy = false;
            SetOperationState(_closing ? ReviewOperationState.Disposing : ReviewOperationState.Idle);
            RefreshGates();
        }
    }

    private bool CanAdopt(long id) => !_disposed && !_closing && id == _operationId;

    private void SetOperationState(ReviewOperationState state)
    {
        _operationState = state;
        OnPropertyChanged(nameof(OperationState));
    }

    private void RefreshImages()
    {
        OnPropertyChanged(nameof(Evidence));
        OnPropertyChanged(nameof(PortableReview));
        OnPropertyChanged(nameof(IsLegacyReview));
        OnPropertyChanged(nameof(Findings));
        OnPropertyChanged(nameof(Dispositions));
        OnPropertyChanged(nameof(SelectedFindingDetails));
        OnPropertyChanged(nameof(CanSaveRevision));
        OnPropertyChanged(nameof(DisplayedImage));
        OnPropertyChanged(nameof(ShowAnnotated));
        OnPropertyChanged(nameof(CanShowRaw));
        OnPropertyChanged(nameof(CanShowAnnotated));
        RefreshGates();
    }

    private void ReportBoardChanged()
    {
        if (!_closing && !_disposed)
        {
            Status = "board_changed: acquire a fresh scene before capturing this document.";
        }
    }

    public void ReportOperationFailure(Exception error)
    {
        if (!_closing && !_disposed)
        {
            Status = "Review operation failed: " + error;
        }
    }

    private async void Bridge_StateChanged(object? sender, SimpleSessionState state)
    {
        try
        {
            await _presentation.InvokeOnDispatcherAsync(() =>
            {
                if (!_disposed && !_closing)
                {
                    if (_live is not null && (!state.IsReady || !_live.IsCurrent))
                    {
                        _live = null;
                        ReportBoardChanged();
                    }
                    RefreshGates();
                }
                return Task.FromResult(true);
            });
        }
        catch (ObjectDisposedException)
        {
            // The presentation owner can be gone only after this view's drain.
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Review connection publication failed: {0}", error);
        }
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

    private void OnPropertyChanged(string? name)
    {
        if (!_disposed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>One retained review bundle record (live export or offline reopen).</summary>
public sealed record RetainedReviewRecord(
    Guid BundleId,
    string Directory,
    DateTimeOffset AtUtc,
    int ImageCount,
    string Evidence);
