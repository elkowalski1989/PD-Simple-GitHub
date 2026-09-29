using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PD.PcbTools.Review;
using CircuitHub.AllegroBridge.Engine.Reviews;

namespace PD.Simple.Tools.Review;

/// <summary>
/// T07 task panel. Binds the review view model; keeps dialogs, zoom, and
/// disposal in code-behind. The view model never touches a dialog.
/// </summary>
public partial class ShareReviewToolView : UserControl, IDisposable, IAsyncDisposable
{
    private bool _disposed;
    private ReviewDocument? _presentedReview;

    public ShareReviewToolView()
    {
        InitializeComponent();
        DispositionInput.ItemsSource = PortableReviewPolicy.DispositionVocabulary;
    }

    internal ShareReviewToolViewModel? ViewModel
    {
        get => DataContext as ShareReviewToolViewModel;
        set
        {
            if (ViewModel is { } old)
            {
                old.PropertyChanged -= ViewModel_Changed;
            }

            DataContext = value;
            if (value is not null)
            {
                value.PropertyChanged += ViewModel_Changed;
                PushInputs(value);
            }

            SyncControls();
        }
    }

    private void ViewModel_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (ViewModel is null)
        {
            return;
        }

        switch (args.PropertyName)
        {
            case nameof(ShareReviewToolViewModel.Status):
                StatusText.Text = ViewModel.Status;
                break;
            case nameof(ShareReviewToolViewModel.Evidence):
                EvidenceText.Text = ViewModel.Evidence;
                break;
            case nameof(ShareReviewToolViewModel.Retention):
                RetentionText.Text = ViewModel.Retention;
                break;
            case nameof(ShareReviewToolViewModel.DisplayedImage):
            case nameof(ShareReviewToolViewModel.ShowAnnotated):
                ReviewImage.Source = ViewModel.DisplayedImage;
                break;
            case nameof(ShareReviewToolViewModel.StampX):
                StampXInput.Text = ViewModel.StampX;
                break;
            case nameof(ShareReviewToolViewModel.StampY):
                StampYInput.Text = ViewModel.StampY;
                break;
        }

        SyncControls();
    }

    private void PushInputs(ShareReviewToolViewModel value)
    {
        TitleInput.Text = value.Title;
        StampXInput.Text = value.StampX;
        StampYInput.Text = value.StampY;
        LinkObjectCheck.IsChecked = value.LinkObject;
        ObjectIdInput.Text = value.ObjectId;
        AnnotatedRadio.IsChecked = value.ShowAnnotated;
        RawRadio.IsChecked = !value.ShowAnnotated;
        OutputDirInput.Text = value.OutputDirectory;
        BundleNoteInput.Text = value.BundleNote;
        StatusText.Text = value.Status;
        EvidenceText.Text = value.Evidence;
        RetentionText.Text = value.Retention;
        ReviewImage.Source = value.DisplayedImage;
        RetainedList.ItemsSource = value.RetainedFrames;
    }

    private void PullInputs()
    {
        if (ViewModel is not { } value)
        {
            return;
        }

        value.Title = TitleInput.Text;
        value.StampX = StampXInput.Text;
        value.StampY = StampYInput.Text;
        value.LinkObject = LinkObjectCheck.IsChecked == true;
        value.ObjectId = ObjectIdInput.Text;
        value.OutputDirectory = OutputDirInput.Text;
        value.BundleNote = BundleNoteInput.Text;
    }

    private void SyncControls()
    {
        if (_disposed || ViewModel is not { } value)
        {
            return;
        }

        AcquireButton.IsEnabled = value.CanAcquire;
        CaptureButton.IsEnabled = value.CanCapture;
        CancelButton.IsEnabled = value.CanCancel;
        SaveRawButton.IsEnabled = value.CanSavePng && value.CanShowRaw;
        SaveAnnotatedButton.IsEnabled = value.CanSavePng && value.CanShowAnnotated;
        ExportBundleButton.IsEnabled = value.CanExportBundle;
        ReopenButton.IsEnabled = value.CanOpen;
        RawRadio.IsEnabled = value.CanShowRaw;
        RawRadio.ToolTip = value.RawUnavailableReason;
        AnnotatedRadio.IsEnabled = value.CanShowAnnotated;
        AnnotatedRadio.ToolTip = value.AnnotatedUnavailableReason;
        RawRadio.IsChecked = !value.ShowAnnotated;
        AnnotatedRadio.IsChecked = value.ShowAnnotated;
        SaveRevisionButton.IsEnabled = value.CanSaveRevision;
        if (!ReferenceEquals(_presentedReview, value.PortableReview))
        {
            string? selected = value.SelectedFindingId;
            _presentedReview = value.PortableReview;
            FindingsList.ItemsSource = value.Findings;
            FindingsList.SelectedItem = value.Findings.FirstOrDefault(item => item.Id == selected);
        }
        DispositionHistory.ItemsSource = value.Dispositions;
        FindingDetails.Text = value.SelectedFindingDetails;
    }

    private async void Acquire_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            await RunAsync(value.AcquireSceneAsync);
        }
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            await RunAsync(value.CaptureAsync);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel?.Cancel();

    private void Display_Changed(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            value.ShowAnnotated = AnnotatedRadio.IsChecked != false;
        }
    }

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomSlider is null || ReviewImage is null || ZoomText is null)
        {
            return;
        }

        double zoom = ZoomSlider.Value;
        ReviewImage.LayoutTransform = new ScaleTransform(zoom, zoom);
        ZoomText.Text = ((int)Math.Round(zoom * 100)) + "%";
    }

    private async void SaveRaw_Click(object sender, RoutedEventArgs e) => await SavePngAsync(includeAnnotations: false);

    private async void SaveAnnotated_Click(object sender, RoutedEventArgs e) => await SavePngAsync(includeAnnotations: true);

    private async Task SavePngAsync(bool includeAnnotations)
    {
        if (ViewModel is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = includeAnnotations ? "pd-review-annotated.png" : "pd-review-raw.png",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        OutputDirInput.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        PullInputs();
        await RunAsync(() => ViewModel.SavePngAsync(dialog.FileName, includeAnnotations));
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        PullInputs();
        ViewModel?.CopyOutputPath(OutputDirInput.Text);
    }

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick any file inside the bundle directory",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            OutputDirInput.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            PullInputs();
        }
    }

    private async void ExportBundle_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } value)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save portable historical review",
            Filter = "Portable review|*.allegroreview",
            DefaultExt = ".allegroreview",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = "pd-review-" + Guid.NewGuid().ToString("N") + ".allegroreview",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            PullInputs();
            await RunAsync(() => value.SavePortableCopyAsync(dialog.FileName, value.BundleNote));
        }
    }

    private async void Reopen_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } value)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open a historical review",
            Filter = "Portable or legacy review|*.allegroreview;review-bundle.json|Portable review|*.allegroreview|Legacy review|review-bundle.json",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        await RunAsync(() => value.LoadBundleAsync(dialog.FileName));
    }

    private void Finding_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            value.SelectedFindingId = (FindingsList.SelectedItem as ReviewFinding)?.Id;
        }
    }

    private async void Disposition_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            await RunAsync(() =>
            {
                value.AddDisposition(DispositionInput.SelectedValue as string ?? string.Empty,
                    ReviewerInput.Text, DispositionNoteInput.Text);
                return Task.CompletedTask;
            });
        }
    }

    private async void SaveRevision_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            await RunAsync(value.SaveRevisionAsync);
        }
    }

    private void ClearReview_Click(object sender, RoutedEventArgs e) => ViewModel?.ClearReview();

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value && RetainedList.SelectedItem is RetainedReviewRecord record)
        {
            value.RemoveRetained(record);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            ViewModel?.ReportOperationFailure(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        if (ViewModel is { } value)
        {
            await value.DisposeAsync();
        }
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ViewModel is { } value)
        {
            value.PropertyChanged -= ViewModel_Changed;
            value.Dispose();
            DataContext = null;
        }

        ReviewImage.Source = null;
    }
}
