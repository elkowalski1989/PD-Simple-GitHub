using System.Windows;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Live;

namespace PD.Simple;

public partial class BoardConnectionDialog : Window
{
    private readonly bool _canReconnectCurrent;
    private CancellationTokenSource? _discoveryCancellation;
    private bool _discovering;
    private bool _closed;

    public BoardConnectionDialog(bool canReconnectCurrent, string? currentDesign)
    {
        InitializeComponent();
        _canReconnectCurrent = canReconnectCurrent;
        ReconnectCurrentButton.IsEnabled = canReconnectCurrent;
        CurrentConnectionText.Text = string.IsNullOrWhiteSpace(currentDesign)
            ? "Choose from the Allegro instances running in this Windows session."
            : $"Current board: {currentDesign}";
        Loaded += async (_, _) => await RefreshCandidatesAsync();
    }

    public EngineSessionTarget? SelectedTarget { get; private set; }

    public bool ReconnectCurrent { get; private set; }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _discoveryCancellation?.Cancel();
        base.OnClosed(e);
    }

    private async void RefreshConnections_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCandidatesAsync();
    }

    private async Task RefreshCandidatesAsync()
    {
        if (_closed || _discovering)
        {
            return;
        }

        _discovering = true;
        RefreshConnectionsButton.IsEnabled = false;
        AttachConnectionButton.IsEnabled = false;
        ConnectionCandidateList.ItemsSource = null;
        ConnectionStatusText.Text = "Looking for open Allegro boards…";

        using var cancellation = new CancellationTokenSource();
        _discoveryCancellation = cancellation;
        try
        {
            EngineDiscoveryResult discovery =
                await AllegroEngineDiscovery.DiscoverRunningAsync(cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested)
            {
                return;
            }

            CandidateRow[] candidates = discovery.Targets
                .Select(target => new CandidateRow(target))
                .OrderBy(candidate => candidate.Design, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(candidate => candidate.ProcessId)
                .ToArray();
            ConnectionCandidateList.ItemsSource = candidates;
            ConnectionCandidateList.SelectedIndex = -1;
            ConnectionStatusText.Text = candidates.Length == 0
                ? ConnectionSwitchPolicy.DescribeDiagnostics(
                    discovery.Diagnostics,
                    "No running Allegro Engine targets were found.")
                : candidates.Any(candidate => candidate.CanAttach)
                    ? "Select an available board, then choose Attach selected. Attachment verifies the board is still available."
                    : "No instances are available to attach. See each instance's availability for details.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Closing the chooser cancels discovery and discards any pending results.
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                ConnectionCandidateList.ItemsSource = null;
                ConnectionStatusText.Text = $"Could not discover Allegro boards: {exception.Message}";
            }
        }
        finally
        {
            _discoveryCancellation = null;
            _discovering = false;
            if (!_closed)
            {
                RefreshConnectionsButton.IsEnabled = true;
                UpdateAttachButton();
            }
        }
    }

    private void ConnectionCandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAttachButton();
    }

    private void UpdateAttachButton()
    {
        AttachConnectionButton.IsEnabled = !_closed && !_discovering
            && ConnectionCandidateList.SelectedItem is CandidateRow row
            && row.CanAttach;
    }

    private void AttachConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _discovering
            || ConnectionCandidateList.SelectedItem is not CandidateRow row
            || !row.CanAttach)
        {
            return;
        }

        SelectedTarget = row.Target;
        DialogResult = true;
    }

    private void ReconnectCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || !_canReconnectCurrent)
        {
            return;
        }

        ReconnectCurrent = true;
        DialogResult = true;
    }

    private sealed class CandidateRow(EngineSessionTarget target)
    {
        public EngineSessionTarget Target { get; } = target;

        public string Design => string.IsNullOrWhiteSpace(Target.Design)
            ? "Design unavailable"
            : Target.Design;

        public int? ProcessId => Target.ProcessId;

        public bool CanAttach =>
            Target.Availability == EngineSessionTargetAvailability.Available;

        public string Availability => CanAttach
            ? "Engine target found; verified on attachment"
            : ConnectionSwitchPolicy.DescribeDiagnostics(
                Target.Diagnostics,
                "This Engine target is unavailable.");

        public override string ToString() => $"{Design}, process {ProcessId}. {Availability}";
    }
}
