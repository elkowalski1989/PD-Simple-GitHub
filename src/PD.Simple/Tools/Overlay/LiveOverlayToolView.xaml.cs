using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Scenes;
using Microsoft.Win32;

namespace PD.Simple.Tools.Overlay;

/// <summary>
/// T06 task panel. Binds the overlay view model; keeps only view concerns
/// (panel switching, control sync, disposal) in code-behind.
/// </summary>
public partial class LiveOverlayToolView : UserControl, IDisposable
{
    private bool _disposed;

    public LiveOverlayToolView()
    {
        InitializeComponent();
        ShapePicker.ItemsSource = LiveOverlayToolViewModel.Shapes;
        ShapePicker.SelectedItem = "Text";
        StrokePicker.ItemsSource = LiveOverlayToolViewModel.NamedColors;
        StrokePicker.SelectedItem = "Blue";
        FillPicker.ItemsSource = LiveOverlayToolViewModel.NamedColors;
        FillPicker.SelectedItem = "Blue";
        MarkerKindPicker.ItemsSource = LiveOverlayToolViewModel.MarkerKinds;
        MarkerKindPicker.SelectedItem = "Cross";
        DimensionKindPicker.ItemsSource = LiveOverlayToolViewModel.DimensionKinds;
        DimensionKindPicker.SelectedItem = "Aligned";
        HitTestPicker.ItemsSource = LiveOverlayToolViewModel.HitTests;
        HitTestPicker.SelectedItem = "None";
        IsVisibleChanged += (_, _) => SyncControls();
    }

    internal LiveOverlayToolViewModel? ViewModel
    {
        get => DataContext as LiveOverlayToolViewModel;
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
        if (args.PropertyName is nameof(LiveOverlayToolViewModel.Status))
        {
            StatusText.Text = ViewModel?.Status ?? string.Empty;
        }
        else if (args.PropertyName is nameof(LiveOverlayToolViewModel.Publication))
        {
            PublicationText.Text = ViewModel?.Publication ?? string.Empty;
        }
        else if (args.PropertyName is nameof(LiveOverlayToolViewModel.Validation))
        {
            ValidationText.Text = ViewModel?.Validation ?? string.Empty;
        }

        SyncControls();
    }

    private void PushInputs(LiveOverlayToolViewModel value)
    {
        AnchorXInput.Text = value.AnchorX;
        AnchorYInput.Text = value.AnchorY;
        ObjectAnchorCheck.IsChecked = value.UseObjectAnchor;
        ObjectIdInput.Text = value.ObjectId;
        ElementIdInput.Text = value.ElementId;
        ShapePicker.SelectedItem = value.Shape;
        LineInput.Text = value.LineParams;
        CircleInput.Text = value.CircleParams;
        EllipseInput.Text = value.EllipseParams;
        RectInput.Text = value.RectParams;
        PolygonInput.Text = value.PolygonParams;
        PolylineInput.Text = value.PolylineParams;
        PolylineClosedCheck.IsChecked = value.PolylineClosed;
        OverlayTextInput.Text = value.TextParams;
        FontSizeInput.Text = value.FontSize;
        MarkerKindPicker.SelectedItem = value.MarkerKind;
        MarkerSizeInput.Text = value.MarkerSize;
        DimensionInput.Text = value.DimensionParams;
        DimensionKindPicker.SelectedItem = value.DimensionKind;
        DimensionLabelInput.Text = value.DimensionLabel;
        StrokePicker.SelectedItem = value.Stroke;
        StrokeWidthInput.Text = value.StrokeWidth;
        FillCheck.IsChecked = value.HasFill;
        FillPicker.SelectedItem = value.Fill;
        OpacityInput.Text = value.Opacity;
        ZOrderInput.Text = value.ZOrder;
        VisibleCheck.IsChecked = value.IsVisible;
        HitTestPicker.SelectedItem = value.HitTest;
        StatusText.Text = value.Status;
        PublicationText.Text = value.Publication;
        OperationsList.ItemsSource = value.Operations;
    }

    private void PullInputs()
    {
        if (ViewModel is not { } value)
        {
            return;
        }

        value.AnchorX = AnchorXInput.Text;
        value.AnchorY = AnchorYInput.Text;
        value.UseObjectAnchor = ObjectAnchorCheck.IsChecked == true;
        value.ObjectId = ObjectIdInput.Text;
        value.ElementId = ElementIdInput.Text;
        value.Shape = ShapePicker.SelectedItem as string ?? "Text";
        value.LineParams = LineInput.Text;
        value.CircleParams = CircleInput.Text;
        value.EllipseParams = EllipseInput.Text;
        value.RectParams = RectInput.Text;
        value.PolygonParams = PolygonInput.Text;
        value.PolylineParams = PolylineInput.Text;
        value.PolylineClosed = PolylineClosedCheck.IsChecked == true;
        value.TextParams = OverlayTextInput.Text;
        value.FontSize = FontSizeInput.Text;
        value.MarkerKind = MarkerKindPicker.SelectedItem as string ?? "Cross";
        value.MarkerSize = MarkerSizeInput.Text;
        value.DimensionParams = DimensionInput.Text;
        value.DimensionKind = DimensionKindPicker.SelectedItem as string ?? "Aligned";
        value.DimensionLabel = DimensionLabelInput.Text;
        value.Stroke = StrokePicker.SelectedItem as string ?? "Blue";
        value.StrokeWidth = StrokeWidthInput.Text;
        value.HasFill = FillCheck.IsChecked == true;
        value.Fill = FillPicker.SelectedItem as string ?? "Blue";
        value.Opacity = OpacityInput.Text;
        value.ZOrder = ZOrderInput.Text;
        value.IsVisible = VisibleCheck.IsChecked == true;
        value.HitTest = HitTestPicker.SelectedItem as string ?? "None";
    }

    private void SyncControls()
    {
        if (_disposed || ViewModel is not { } value)
        {
            return;
        }

        AcquireButton.IsEnabled = value.CanAcquire;
        BuildButton.IsEnabled = value.CanBuild;
        PublishButton.IsEnabled = value.CanPublish;
        HideButton.IsEnabled = value.CanHide;
        RemoveButton.IsEnabled = value.CanRemove;
        CancelButton.IsEnabled = value.CanCancel;
        RecipeButton.IsEnabled = value.CanCopyRecipe;
        OfflineNote.Text = value.HasLiveScene
            ? "Live scene held. Publishing replaces the shared lease's visible pixels; other tools republish on next use."
            : value.HasOfflineScene
                ? "Offline scene held. Build a preview or export the recipe; publishing, hiding, and removing need a live Allegro scene."
                : "Offline: acquire a live scene (Reconnect / Attach, then Acquire) or open an offline scene file. Building a preview and exporting the recipe need no connection once a scene is held.";
    }

    private void Shape_Changed(object sender, SelectionChangedEventArgs e)
    {
        string shape = ShapePicker.SelectedItem as string ?? "Text";
        LinePanel.Visibility = VisibilityFor(shape == "Line");
        CirclePanel.Visibility = VisibilityFor(shape == "Circle");
        EllipsePanel.Visibility = VisibilityFor(shape == "Ellipse");
        RectPanel.Visibility = VisibilityFor(shape == "Rectangle");
        PolygonPanel.Visibility = VisibilityFor(shape == "Polygon");
        PolylinePanel.Visibility = VisibilityFor(shape == "Polyline");
        TextPanel.Visibility = VisibilityFor(shape == "Text");
        MarkerPanel.Visibility = VisibilityFor(shape == "Marker");
        DimensionPanel.Visibility = VisibilityFor(shape == "Dimension");
    }

    private static Visibility VisibilityFor(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private async void Acquire_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            await RunAsync(value.AcquireSceneAsync);
        }
    }

    private async void OpenOffline_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } value)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open an offline Engine scene archive",
            Filter = "Scene archive (*.zip)|*.zip",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            DesignScene scene = await BridgeSession.OpenEngineSceneAsync(dialog.FileName);
            value.ShowOfflineScene(scene);
        }
        catch (Exception error)
        {
            // A failed load never clears a previously held scene; the trace
            // carries the refusal and the page keeps its prior state.
            System.Diagnostics.Trace.TraceWarning("Overlay offline scene load failed: {0}", error.Message);
        }
    }

    private void Explorer_Click(object sender, RoutedEventArgs e) => ViewModel?.RequestExplorerNavigation();

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel?.Cancel();

    private void Build_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            value.BuildPreview();
        }
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            await RunAsync(value.PublishAsync);
        }
    }

    private async void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            await RunAsync(value.HideAsync);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            await RunAsync(value.RemoveAsync);
        }
    }

    private void Recipe_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } value)
        {
            PullInputs();
            value.CopyRecipe();
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
            System.Diagnostics.Trace.TraceWarning("Overlay tool action failed: {0}", error.Message);
        }
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
    }
}
