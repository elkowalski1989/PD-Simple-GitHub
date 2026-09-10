using System.Windows;
using System.Windows.Controls;
using CircuitHub.AllegroBridge.Windows;

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

    public AllegroDesktopCandidate? SelectedCandidate { get; private set; }

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
            var candidates = await Task.Run(
                () => AllegroDesktop.DiscoverRunningInstances(cancellationToken: cancellation.Token),
                cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested)
            {
                return;
            }

            ConnectionCandidateList.ItemsSource = candidates
                .Select(candidate => new CandidateRow(candidate))
                .OrderBy(candidate => candidate.Design, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(candidate => candidate.ProcessId)
                .ToArray();
            ConnectionCandidateList.SelectedIndex = -1;
            ConnectionStatusText.Text = candidates.Count == 0
                ? "No running Allegro instances were found."
                : candidates.Any(candidate => candidate.CanAttemptAttach)
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
            && row.Candidate.CanAttemptAttach;
    }

    private void AttachConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _discovering
            || ConnectionCandidateList.SelectedItem is not CandidateRow row
            || !row.Candidate.CanAttemptAttach)
        {
            return;
        }

        SelectedCandidate = row.Candidate;
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

    private sealed class CandidateRow(AllegroDesktopCandidate candidate)
    {
        public AllegroDesktopCandidate Candidate { get; } = candidate;

        public string Design => string.IsNullOrWhiteSpace(Candidate.Design)
            ? "Design unavailable"
            : Candidate.Design;

        public int ProcessId => Candidate.ProcessId;

        public string Availability => Candidate.UnavailableReason switch
        {
            null => "Bridge found; verified on attachment",
            "ambiguous_or_missing_window" => "A unique Allegro window could not be identified.",
            "compatible_resident_not_found" => "No compatible Bridge connection is running for this instance.",
            "ambiguous_resident" => "Multiple Bridge connections identify this instance.",
            "resident_outside_client_temporary_root" => "The Bridge connection is outside the configured temporary folder.",
            var reason => $"Unavailable: {reason.Replace('_', ' ')}",
        };

        public override string ToString() => $"{Design}, process {ProcessId}. {Availability}";
    }
}
