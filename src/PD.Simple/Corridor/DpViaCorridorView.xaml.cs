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
