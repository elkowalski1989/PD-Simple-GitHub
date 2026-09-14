using System.ComponentModel;
using System.Windows;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Wpf.Engine;

namespace BoardExplorer;

/// <summary>
/// Thin public-package host for the ordinary Engine Workbench. The sample owns
/// one Engine session; WPF borrows it and never opens another connection.
/// </summary>
public partial class EngineFirstWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AllegroEngineSession _session;
    private readonly EngineWpfPresentation _presentation;
    private readonly EngineWorkbenchView _workbench;
    private CancellationTokenSource? _operation;
    private Task _pending = Task.CompletedTask;
    private bool _closing;
    private bool _closeReady;

    public EngineFirstWindow()
    {
        InitializeComponent();
        _session = AllegroEngineSession.Create(
            new EngineSessionOptions
            {
                RequiredCapabilities =
                [
                    EngineCapabilities.SceneRead,
                    EngineCapabilities.Presentation,
                ],
            });
        _presentation = EngineWpfPresentation.Attach(_session, Dispatcher);
        _workbench = new EngineWorkbenchView(_presentation);
        _workbench.StatusChanged += (_, message) => Status.Text = message;
        WorkbenchHost.Content = _workbench;
    }

    private void Discover_Click(object sender, RoutedEventArgs args) => Start(async token =>
    {
        EngineDiscoveryResult discovery = await AllegroEngineDiscovery.DiscoverRunningAsync(token);
        Boards.ItemsSource = discovery.Targets.Select(target => new BoardChoice(target)).ToArray();
        Status.Text = discovery.Targets.IsEmpty
            ? DescribeDiagnostics(
                discovery.Diagnostics,
                "No compatible Engine target was discovered. Start the matching Bridge in the intended Allegro application.")
            : "Select the intended board. Engine revalidates that exact target when attaching.";
    });

    private void Connect_Click(object sender, RoutedEventArgs args) => Start(async token =>
    {
        if (Boards.SelectedItem is not BoardChoice selected ||
            selected.Target.Availability != EngineSessionTargetAvailability.Available)
        {
            throw new InvalidOperationException("Select an available Engine target.");
        }
        if (!_workbench.CanSwitchNativeSession)
        {
            throw new InvalidOperationException(
                _workbench.NativeSessionRetentionReason ??
                "Finish the current Workbench operation before switching boards.");
        }

        if (_session.State.ConnectionState == EngineConnectionState.Ready)
        {
            await _session.SwitchAsync(selected.Target, token);
        }
        else if (_session.State.ConnectionState is
            EngineConnectionState.Disconnected or EngineConnectionState.Faulted)
        {
            await _session.AttachAsync(selected.Target, token);
        }
        else
        {
            throw new InvalidOperationException(
                $"The Engine session cannot attach while it is {_session.State.ConnectionState}.");
        }

        _workbench.RefreshHostState();
        Status.Text = "Connected through the one Engine session. Use Workbench to acquire an explicit live scene.";
    });

    private void Cancel_Click(object sender, RoutedEventArgs args)
    {
        _operation?.Cancel();
    }

    private void Start(Func<CancellationToken, Task> action)
    {
        if (_operation is not null || _closing)
        {
            return;
        }

        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ConnectButton.IsEnabled = false;
        DiscoverButton.IsEnabled = false;
        Boards.IsEnabled = false;
        CancelButton.IsEnabled = true;
        _workbench.HostBusy = true;
        _pending = RunAsync(action, _operation);
    }

    private async Task RunAsync(
        Func<CancellationToken, Task> action,
        CancellationTokenSource operation)
    {
        try
        {
            await action(operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Status.Text = "The sample stopped waiting. Engine state remains authoritative; no native work was replayed.";
        }
        catch (Exception error)
        {
            Status.Text = error.Message;
        }
        finally
        {
            operation.Dispose();
            _operation = null;
            if (!_closing)
            {
                ConnectButton.IsEnabled = true;
                DiscoverButton.IsEnabled = true;
                Boards.IsEnabled = true;
                CancelButton.IsEnabled = false;
                _workbench.HostBusy = false;
            }
        }
    }

    private async void EngineFirstWindow_Closing(object? sender, CancelEventArgs args)
    {
        if (_closeReady)
        {
            return;
        }

        args.Cancel = true;
        if (_closing)
        {
            return;
        }
        if (!_workbench.CanClose)
        {
            Status.Text = _workbench.NativeSessionRetentionReason ??
                "A native operation is still reaching its terminal result.";
            return;
        }

        _closing = true;
        IsEnabled = false;
        _lifetime.Cancel();
        try
        {
            await _pending;
            await DisposeWithoutSkippingAsync(
                "Workbench",
                () => _workbench.DisposeAsync());
            await DisposeWithoutSkippingAsync(
                "WPF presentation",
                () => _presentation.DisposeAsync());
            await DisposeWithoutSkippingAsync(
                "Engine session",
                () => _session.DisposeAsync());
        }
        finally
        {
            _lifetime.Dispose();
            _closeReady = true;
            Close();
        }
    }

    private static async ValueTask DisposeWithoutSkippingAsync(
        string owner,
        Func<ValueTask> dispose)
    {
        try
        {
            await dispose();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning(
                "{0} cleanup failed without skipping the remaining ownership chain: {1}",
                owner,
                error);
        }
    }

    private static string DescribeDiagnostics(
        IEnumerable<EngineDiagnostic> diagnostics,
        string fallback)
    {
        string message = string.Join(
            " ",
            diagnostics.Select(item => $"{item.Code}: {item.Message}"));
        return string.IsNullOrWhiteSpace(message) ? fallback : message;
    }

    private sealed record BoardChoice(EngineSessionTarget Target)
    {
        public string Label
        {
            get
            {
                string process = Target.ProcessId?.ToString() ?? "unknown";
                string identity = $"{Target.Design ?? "Unknown board"} | process {process}";
                return Target.Availability == EngineSessionTargetAvailability.Available
                    ? identity + " | available (revalidated on attach)"
                    : identity + " | " + DescribeDiagnostics(Target.Diagnostics, "unavailable");
            }
        }
    }
}
