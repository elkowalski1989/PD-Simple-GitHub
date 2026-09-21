using System.Collections.Immutable;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.Simple.Tools.Captures;

/// <summary>
/// T08 task view over <see cref="CapturedSceneWorkspace"/>. Live capture,
/// replay, and export run through host delegates; archive open and manifest
/// export are offline.
/// </summary>
public partial class CapturedScenesView : UserControl
{
    private static readonly DataFamily[] OfferedFamilies =
    [
        DataFamily.Copper,
        DataFamily.Nets,
        DataFamily.Components,
        DataFamily.Pins,
        DataFamily.Layers,
        DataFamily.BoardGeometry,
        DataFamily.Routes,
    ];

    private readonly List<CheckBox> _familyBoxes = [];
    private CancellationTokenSource? _operationCancellation;

    public CapturedScenesView()
    {
        InitializeComponent();
        Scenes = new CapturedSceneWorkspace();
        DataContext = Scenes;
        foreach (DataFamily family in OfferedFamilies)
        {
            var box = new CheckBox
            {
                Content = family.ToString(),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            box.SetValue(AutomationProperties.NameProperty, $"Capture family {family}");
            box.Checked += (_, _) => SyncFamilies();
            box.Unchecked += (_, _) => SyncFamilies();
            FamilyPanel.Children.Add(box);
            _familyBoxes.Add(box);
        }
        SyncBoxesFromWorkspace();
    }

    public CapturedSceneWorkspace Scenes { get; set; }

    private void SyncFamilies()
    {
        ImmutableArray<DataFamily> selected = _familyBoxes
            .Where(box => box.IsChecked == true)
            .Select(box => Enum.Parse<DataFamily>((string)box.Content))
            .ToImmutableArray();
        if (selected.Length > 0)
        {
            Scenes.Families = selected;
        }
    }

    private void SyncBoxesFromWorkspace()
    {
        foreach (CheckBox box in _familyBoxes)
        {
            var family = Enum.Parse<DataFamily>((string)box.Content);
            box.IsChecked = Scenes.Families.Contains(family);
        }
    }

    private CancellationToken NextOperationToken()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        return _operationCancellation.Token;
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Scenes.CaptureNowAsync(NextOperationToken());
        }
        catch (Exception error)
        {
            Scenes.SetStatusFromView("Capture dispatch failed: " + error.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
    }

    private async void Replay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Scenes.ReplayBulkAsync(NextOperationToken());
        }
        catch (Exception error)
        {
            Scenes.SetStatusFromView("Replay dispatch failed: " + error.Message);
        }
    }

    private async void Reacquire_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Scenes.ReacquireAsync(NextOperationToken());
        }
        catch (Exception error)
        {
            Scenes.SetStatusFromView("Reacquire dispatch failed: " + error.Message);
        }
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Scenes.CloseAsync();
        }
        catch (Exception error)
        {
            Scenes.SetStatusFromView("Close dispatch failed: " + error.Message);
        }
    }

    private void ClearRegion_Click(object sender, RoutedEventArgs e)
    {
        Scenes.ClearRegion();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Scenes.SaveArchiveAsync(
                SavePathInput.Text,
                SaveOverwriteBox.IsChecked == true,
                NextOperationToken());
        }
        catch (Exception error)
        {
            Scenes.SetStatusFromView("Save dispatch failed: " + error.Message);
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Scenes.OpenArchiveAsync(OpenPathInput.Text, NextOperationToken());
        }
        catch (Exception error)
        {
            Scenes.SetStatusFromView("Open dispatch failed: " + error.Message);
        }
    }

    private async void Manifest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Scenes.ExportManifestAsync(
                ManifestPathInput.Text,
                ManifestOverwriteBox.IsChecked == true,
                NextOperationToken());
        }
        catch (Exception error)
        {
            Scenes.SetStatusFromView("Manifest dispatch failed: " + error.Message);
        }
    }

    private async void OpenBulk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Scenes.OpenBulkDirectoryAsync(BulkPathInput.Text, NextOperationToken());
        }
        catch (Exception error)
        {
            Scenes.SetStatusFromView("Bulk open dispatch failed: " + error.Message);
        }
    }

    private async void ExportBulk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Scenes.ExportBulkAsync(BulkPathInput.Text, NextOperationToken());
        }
        catch (Exception error)
        {
            Scenes.SetStatusFromView("Bulk export dispatch failed: " + error.Message);
        }
    }
}
