using System.Windows;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Analysis;

namespace PD.Simple.Tools.Analysis;

/// <summary>
/// T01 task view. Binds to a <see cref="CrossingReviewWorkspace"/>; the host
/// may replace <see cref="Review"/> with a live-wired workspace. Without one,
/// the view stays in offline mode and reports setup instead of running.
/// </summary>
public partial class CrossingReviewView : UserControl
{
    private CancellationTokenSource? _runCancellation;

    public CrossingReviewView()
    {
        InitializeComponent();
        Review = new CrossingReviewWorkspace();
        DataContext = Review;
        RepresentationBox.ItemsSource = Enum.GetValues<CrossingRepresentation>();
        RepresentationBox.SelectedItem = Review.Representation;
        RepresentationBox.SelectionChanged += (_, _) =>
        {
            if (RepresentationBox.SelectedItem is CrossingRepresentation representation)
            {
                Review.Representation = representation;
            }
        };
    }

    public CrossingReviewWorkspace Review { get; set; }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        try
        {
            await Review.RunAsync(_runCancellation.Token);
        }
        catch (Exception error)
        {
            Review.SetStatusFromView("Run dispatch failed: " + error.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
    }

    private async void Rerun_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        try
        {
            await Review.RerunAsync(_runCancellation.Token);
        }
        catch (Exception error)
        {
            Review.SetStatusFromView("Rerun dispatch failed: " + error.Message);
        }
    }

    private async void Acquire_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        try
        {
            await Review.AcquireMissingScopeAsync(_runCancellation.Token);
        }
        catch (Exception error)
        {
            Review.SetStatusFromView("Acquisition dispatch failed: " + error.Message);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Review.ExportReportAsync(
                ExportPathInput.Text,
                ExportOverwriteBox.IsChecked == true);
        }
        catch (Exception error)
        {
            Review.SetStatusFromView("Export dispatch failed: " + error.Message);
        }
    }

    private void Findings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Review.SelectedFinding = FindingsList.SelectedItem as CrossingFindingRow;
    }
}
