using System.Windows;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Exploration;

namespace PD.Simple.Tools.Analysis;

/// <summary>
/// T02 task view over <see cref="GeometryInspectorWorkspace"/>. Search runs
/// locally; native highlight/zoom stay in the shared Workbench and are only
/// described here as typed requests.
/// </summary>
public partial class GeometryInspectorView : UserControl
{
    private CancellationTokenSource? _searchCancellation;

    public GeometryInspectorView()
    {
        InitializeComponent();
        Inspector = new GeometryInspectorWorkspace();
        DataContext = Inspector;
        FamilyBox.ItemsSource = Enum.GetValues<ObjectFamily>();
        FamilyBox.SelectedItem = Inspector.Family;
        FamilyBox.SelectionChanged += (_, _) =>
        {
            if (FamilyBox.SelectedItem is ObjectFamily family)
            {
                Inspector.Family = family;
            }
        };
    }

    public GeometryInspectorWorkspace Inspector { get; set; }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        try
        {
            await Inspector.SearchAsync(_searchCancellation.Token);
        }
        catch (Exception error)
        {
            Inspector.SetStatusFromView("Search dispatch failed: " + error.Message);
        }
    }

    private void Hits_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HitsList.SelectedItem is InspectorHit hit)
        {
            LaneAOperationResult result = Inspector.Select(hit);
            if (!result.IsSuccess)
            {
                Inspector.SetStatusFromView(result.Outcome);
            }
        }
    }

    private void LoadDetails_Click(object sender, RoutedEventArgs e)
    {
        LaneAOperationResult result = Inspector.MarkContoursLoaded();
        Inspector.SetStatusFromView(result.Outcome);
    }

    private void MeasureA_Click(object sender, RoutedEventArgs e)
    {
        Inspector.SetMeasureEndpoint(first: true, Inspector.SelectedHit);
    }

    private void MeasureB_Click(object sender, RoutedEventArgs e)
    {
        Inspector.SetMeasureEndpoint(first: false, Inspector.SelectedHit);
    }

    private void Measure_Click(object sender, RoutedEventArgs e)
    {
        LaneAOperationResult result = Inspector.MeasureSelectedPair();
        if (!result.IsSuccess)
        {
            Inspector.SetStatusFromView(result.Outcome);
        }
    }

    private void Sample_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(VertexPageInput.Text, out int page) || page < 0)
        {
            Inspector.SetStatusFromView("Vertex page index must be a non-negative integer.");
            return;
        }
        LaneAOperationResult result = Inspector.SampleSelectedVertices(page);
        if (!result.IsSuccess)
        {
            Inspector.SetStatusFromView(result.Outcome);
        }
    }

    private void CopyRecipe_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Inspector.RecipeText);
            Inspector.SetStatusFromView("Recipe copied to the clipboard.");
        }
        catch (Exception error)
        {
            Inspector.SetStatusFromView("Copy failed: " + error.Message);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Inspector.ExportFactsAsync(
                ExportPathInput.Text,
                ExportOverwriteBox.IsChecked == true);
        }
        catch (Exception error)
        {
            Inspector.SetStatusFromView("Export dispatch failed: " + error.Message);
        }
    }
}
