using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PD.Simple.Corridor;

public partial class DpViaCorridorView : UserControl
{
    public DpViaCorridorView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => UpdateWorkspaceVisibility();
        DataContextChanged += (_, change) =>
        {
            if (change.OldValue is DpViaCorridorWorkspaceViewModel previous)
            {
                previous.SetWorkspaceVisible(false);
                previous.PropertyChanged -= Model_Changed;
            }

            if (change.NewValue is DpViaCorridorWorkspaceViewModel next)
            {
                next.PropertyChanged += Model_Changed;
            }

            UpdateWorkspaceVisibility();
            UpdateRiskBar();
        };
    }
    private void Model_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateRiskBar();
        if (e.PropertyName is null or nameof(DpViaCorridorWorkspaceViewModel.CrossingsExpanded) &&
            DataContext is DpViaCorridorWorkspaceViewModel model)
        {
            DpvRightColumn.Width = model.CrossingsExpanded ? new GridLength(330) : new GridLength(0);
        }
    }
    private void UpdateRiskBar()
    {
        if (DataContext is not DpViaCorridorWorkspaceViewModel model)
        {
            return;
        }

        DpvRiskCritical.Width = new GridLength(Math.Max(0, model.CriticalCount), GridUnitType.Star);
        DpvRiskMedium.Width = new GridLength(Math.Max(0, model.MediumCount), GridUnitType.Star);
        DpvRiskLow.Width = new GridLength(Math.Max(0, model.LowCount), GridUnitType.Star);
    }
    private void UpdateWorkspaceVisibility()
    {
        if (DataContext is DpViaCorridorWorkspaceViewModel model)
        {
            model.SetWorkspaceVisible(IsVisible);
        }
    }
    public event EventHandler? BackRequested;
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void Fit_Click(object sender, RoutedEventArgs e) => DpvCanvas.ResetView();
    private void CollapseSetup_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DpViaCorridorWorkspaceViewModel model)
        {
            model.SetupExpanded = false;
        }
    }
    private void ExpandSetup_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DpViaCorridorWorkspaceViewModel model)
        {
            model.SetupExpanded = true;
        }
    }
    private void CollapseCrossings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DpViaCorridorWorkspaceViewModel model)
        {
            model.CrossingsExpanded = false;
        }
    }
    private void ExpandCrossings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DpViaCorridorWorkspaceViewModel model)
        {
            model.CrossingsExpanded = true;
        }
    }
    private void ToggleCrossings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DpViaCorridorWorkspaceViewModel model)
        {
            model.CrossingsExpanded = !model.CrossingsExpanded;
        }
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => DpvCanvas.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => DpvCanvas.ZoomOut();
    private void Overlays_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
    private void LearnMore_Click(object sender, RoutedEventArgs e) =>
        DpvHelpMore.Visibility = DpvHelpMore.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner is null || DataContext is not DpViaCorridorWorkspaceViewModel model)
        {
            return;
        }

        var canvas = new DpViaCorridorCanvas
        {
            Margin = new Thickness(10),
            ShowLabels = true,
        };
        void Sync(object? _, EventArgs __)
        {
            canvas.Finding = model.SelectedFinding;
            canvas.ReviewCapture = model.CapturedReview;
            canvas.HasAnalysis = model.HasResult;
            canvas.ShowCorridor = model.ShowHighlighting;
        }
        Sync(null, EventArgs.Empty);
        var window = new Window
        {
            Title = "Canonical drawing — " + model.SelectedFindingTitle,
            Content = canvas,
            Width = 1100,
            Height = 760,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(11, 22, 38)),
        };
        model.PropertyChanged += Sync;
        window.Closed += (_, _) => model.PropertyChanged -= Sync;
        window.Show();
    }
    private void CopyStatus_Click(object sender, RoutedEventArgs e) => CopyTextToClipboard(DpvStatusDetailBox.Text);
    private void CopyPreviewStatus_Click(object sender, RoutedEventArgs e) => CopyTextToClipboard(DpvPreviewStatus.Text);
    private void CopyFindingDetail_Click(object sender, RoutedEventArgs e) => CopyTextToClipboard(DpvFindingDetailBox.Text);
    private static void CopyTextToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException)
        {
            // Clipboard contention: the text stays selectable for manual copy.
        }
    }
    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner is null || DpvCanvas.ReviewCapture is not { } capture)
        {
            return;
        }

        bool annotated = DpvShowCorridorToggle.IsChecked == true;
        var dialog = new SaveFileDialog
        {
            Title = annotated ? "Save highlighted corridor image" : "Save raw Allegro image",
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"dpvc-{capture.FindingId}-{(annotated ? "annotated" : "raw")}.png"
        };
        // Retain this immutable historical frame while the modal dialog pumps
        // updates. Saving it never reacquires or authorizes the live board.
        try
        {
            if (dialog.ShowDialog(owner) != true)
            {
                return;
            }

            using var file = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write);
            capture.Review.SavePng(file, annotated);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(owner, "The captured review could not be saved.\n" + error.Message,
                "Save corridor image", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
