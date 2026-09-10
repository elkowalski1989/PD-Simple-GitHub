using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Media.Imaging;
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
            }

            UpdateWorkspaceVisibility();
        };
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
    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner is null || DpvCanvas.NativeCapture is not { } capture)
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
        // Freeze the selected preview before the modal dialog pumps updates.
        try
        {
            var image = DpvCanvas.ExportNativeImage(annotated);
            if (dialog.ShowDialog(owner) != true)
            {
                return;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write);
            encoder.Save(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(owner, "The preview could not be saved.\n" + error.Message,
                "Save corridor image", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
