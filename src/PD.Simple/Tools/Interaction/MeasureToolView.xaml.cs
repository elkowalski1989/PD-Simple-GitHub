using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PD.Simple.Tools.Interaction;

/// <summary>
/// T03 task panel. Binds the measure view model; keeps only view concerns
/// (control sync, Escape cancellation, disposal) in code-behind.
/// </summary>
public partial class MeasureToolView : UserControl, IDisposable
{
    private bool _disposed;

    public MeasureToolView()
    {
        InitializeComponent();
        SnapPicker.ItemsSource = MeasureToolViewModel.SnapModes;
        SnapPicker.SelectedItem = "Grid 1 mil";
        PreviewKeyDown += OnPreviewKeyDown;
        IsVisibleChanged += (_, _) => SyncControls();
    }

    internal MeasureToolViewModel? ViewModel
    {
        get => DataContext as MeasureToolViewModel;
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Escape cancels the in-flight pick or operation like the Cancel actions.
        if (e.Key == Key.Escape && ViewModel is { } value &&
            (value.CanCancelNativePick || value.CanCancel))
        {
            if (value.HasActivePick)
            {
                _ = RunAsync(value.CancelNativePickAsync);
            }
            else
            {
                value.Cancel();
            }

            e.Handled = true;
        }
    }

    private void ViewModel_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MeasureToolViewModel.Status))
        {
            StatusText.Text = ViewModel?.Status ?? string.Empty;
        }
        else if (args.PropertyName is nameof(MeasureToolViewModel.Result))
        {
            ResultBox.Text = ViewModel?.Result ?? string.Empty;
        }
        else if (args.PropertyName is nameof(MeasureToolViewModel.Feedback))
        {
            FeedbackText.Text = ViewModel?.Feedback ?? string.Empty;
        }
        else if (args.PropertyName is nameof(MeasureToolViewModel.Publication))
        {
            PublicationText.Text = ViewModel?.Publication ?? string.Empty;
        }
        else if (args.PropertyName is nameof(MeasureToolViewModel.Validation))
        {
            ValidationText.Text = ViewModel?.Validation ?? string.Empty;
        }

        SyncControls();
    }

    private void PushInputs(MeasureToolViewModel value)
    {
        FirstXInput.Text = value.FirstX;
        FirstYInput.Text = value.FirstY;
        SecondXInput.Text = value.SecondX;
        SecondYInput.Text = value.SecondY;
        SnapPicker.SelectedItem = value.SnapMode;
        GridStepInput.Text = value.GridStep;
        MillimetersCheck.IsChecked = value.UseMillimeters;
        RulerLabelInput.Text = value.RulerLabel;
        ObjectIdInput.Text = value.ObjectId;
        ExportPathInput.Text = value.ExportPath;
        StatusText.Text = value.Status;
        ResultBox.Text = value.Result;
        FeedbackText.Text = value.Feedback;
        PublicationText.Text = value.Publication;
        RulersList.ItemsSource = value.Rulers;
    }

    private void PullInputs()
    {
        if (ViewModel is not { } value)
        {
            return;
        }

        value.FirstX = FirstXInput.Text;
        value.FirstY = FirstYInput.Text;
        value.SecondX = SecondXInput.Text;
        value.SecondY = SecondYInput.Text;
        value.SnapMode = SnapPicker.SelectedItem as string ?? "Grid 1 mil";
        value.GridStep = GridStepInput.Text;
        value.UseMillimeters = MillimetersCheck.IsChecked == true;
        value.RulerLabel = RulerLabelInput.Text;
        value.ObjectId = ObjectIdInput.Text;
        value.ExportPath = ExportPathInput.Text;
    }

    private void SyncControls()
    {
        if (_disposed || ViewModel is not { } value)
        {
            return;
        }

        AcquireButton.IsEnabled = value.CanAcquire;
        SetFirstButton.IsEnabled = value.CanSetFirst;
        SetSecondButton.IsEnabled = value.CanSetSecond;
        ClearFirstButton.IsEnabled = value.CanClearFirst;
        StartPickButton.IsEnabled = value.CanStartNativePick;
        ClearNativeButton.IsEnabled = value.CanClearNativeFirst;
        CancelPickButton.IsEnabled = value.CanCancelNativePick;
        CancelButton.IsEnabled = value.CanCancel;
        KeepButton.IsEnabled = value.CanKeepRuler;
        MoveButton.IsEnabled = value.CanMoveRuler;
        PublishButton.IsEnabled = value.CanPublishRulers;
        RemoveButton.IsEnabled = value.CanRemoveRuler;
        ClearScopeButton.IsEnabled = value.CanClearScope;
        CopyButton.IsEnabled = value.CanCopy;
        ExportButton.IsEnabled = value.CanExport;
        CheckObjectButton.IsEnabled = value.CanCheckObject;
        OfflineNote.Text = value.HasLiveScene
            ? "Live scene held. Captured spans measure the held capture; native picks observe the current Allegro board. Neither path edits copper."
            : "Offline: acquire a live scene first (Reconnect / Attach, then Acquire). All actions stay disabled until a scene is held.";
    }

    private async void Acquire_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            await RunAsync(value.AcquireSceneAsync);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel?.Cancel();

    private void SetFirst_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            value.SetFirstFromCaptured();
        }
    }

    private void SetSecond_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            value.SetSecondFromCaptured();
        }
    }

    private void ClearFirst_Click(object sender, RoutedEventArgs e) => ViewModel?.ClearFirst();

    private async void StartPick_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            await RunAsync(value.StartNativePickAsync);
        }
    }

    private async void ClearNative_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            await RunAsync(value.ClearNativeFirstAsync);
        }
    }

    private async void CancelPick_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            await RunAsync(value.CancelNativePickAsync);
        }
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            value.KeepCurrentAsRuler();
        }
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            value.MoveSelectedRulerToCurrentSpan();
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            await RunAsync(value.RemoveSelectedRulerAsync);
        }
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            await RunAsync(value.PublishRulersAsync);
        }
    }

    private async void ClearScope_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            await RunAsync(value.ClearScopeAsync);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            value.CopyMeasurement();
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            await RunAsync(value.ExportMeasurementAsync);
        }
    }

    private void CheckObject_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            value.CheckObjectInCapture();
        }
    }

    private void Explorer_Click(object sender, RoutedEventArgs e) => ViewModel?.RequestExplorerNavigation();

    private void Rulers_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.SelectedRuler = RulersList.SelectedItem as MeasureRulerRecord;
        }
    }

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            // View-model actions already report through Status; this guards the
            // async-void boundary so clicks never fail silently.
            System.Diagnostics.Trace.TraceWarning("Measure tool action failed: {0}", error.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PreviewKeyDown -= OnPreviewKeyDown;
        if (ViewModel is { } value)
        {
            value.PropertyChanged -= ViewModel_Changed;
            value.Dispose();
            DataContext = null;
        }
    }
}
