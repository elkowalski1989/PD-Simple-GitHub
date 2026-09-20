using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.Simple.Corridor;
using PD.Simple.Tools.Overlay;
using PD.Simple.Tools.Review;

namespace PD.Simple;

public partial class MainWindow : Window
{
    private readonly BridgeSession _bridge = new();
    private readonly EngineWpfPresentation _presentation;
    private readonly DpViaCorridorWorkspaceViewModel _corridor;
    private readonly LiveOverlayToolViewModel _overlay;
    private readonly ShareReviewToolViewModel _review;
    private readonly EngineTargetResolution _launchTarget;
    private readonly EngineSessionTarget? _recoveryTarget;
    private readonly System.Collections.Generic.IReadOnlyList<EngineUnresolvedOperation> _abandonedRecovery =
        System.Array.Empty<EngineUnresolvedOperation>();
    private bool _connecting;
    private bool _closed;
    private bool _closeReady;
    private bool _teardownComplete;
    private bool _recoveryBlocked;
    private bool _padstacksRefreshing;

    internal MainWindow(
        EngineSessionTarget recoveryTarget,
        System.Collections.Generic.IReadOnlyList<EngineUnresolvedOperation> abandonedRecovery)
        : this([])
    {
        _recoveryTarget = recoveryTarget ?? throw new ArgumentNullException(nameof(recoveryTarget));
        ArgumentNullException.ThrowIfNull(abandonedRecovery);
        _abandonedRecovery = abandonedRecovery;
    }

    public MainWindow(string[] args)
    {
        InitializeComponent();
        Title = $"PD Simple — Allegro Engine Showcase (Bridge {BridgeVersion()})";
        _launchTarget = AllegroEngineDiscovery.ResolveLaunchTarget(args);
        _presentation = EngineWpfPresentation.Attach(
            _bridge.EngineSession,
            Dispatcher);
        if (!ReferenceEquals(_presentation.Session, _bridge.EngineSession))
        {
            throw new InvalidOperationException(
                "The WPF presentation did not retain PD Simple's Engine session.");
        }
        ExplorerView.AttachPresentation(_presentation);
        _corridor = new DpViaCorridorWorkspaceViewModel(
            _bridge.EngineSession,
            _presentation,
            debugWindowProvider: () => this);
        CorridorView.DataContext = _corridor;
        _overlay = new LiveOverlayToolViewModel(_bridge, _presentation);
        OverlayView.ViewModel = _overlay;
        _overlay.NavigateToExplorerRequested += (_, _) =>
        {
            ShowTool("explorer");
            try
            {
                ExplorerView.OpenSection(WorkbenchSection.Inspect);
            }
            catch (InvalidOperationException error)
            {
                StatusText.Text = "Board Explorer is not attached: " + error.Message;
            }
        };
        _review = new ShareReviewToolViewModel(_bridge, _presentation);
        ReviewView.ViewModel = _review;
        ExplorerView.StateChanged += (_, _) =>
        {
            if (!_connecting && ExplorerView.HasUnresolvedEdit)
            {
                StatusText.Text = ExplorerView.StatusMessage;
            }
            UpdateControls();
        };
        CorridorView.BackRequested += (_, _) => ShowTool(null);
        _corridor.PropertyChanged += (_, _) => UpdateControls();
        _bridge.StateChanged += (_, state) =>
        {
            BoardText.Text = state.IsReady ? state.Design : "Allegro Engine showcase";
            if (!_connecting)
            {
                StatusText.Text = state.UnavailableDetail ?? "Connected to Allegro.";
            }
            UpdateControls();
            if (PadstacksView.IsVisible)
            {
                RefreshPadstacksViewAsync();
            }
        };
        _bridge.RouteStateChanged += (_, state) =>
        {
            RoutePhaseText.Text = state.PhaseTitle;
            RouteDetailText.Text = state.Detail;
            UpdateControls();
        };
        _bridge.Faulted += (_, message) =>
        {
            StatusText.Text = message;
            UpdateControls();
        };
        Loaded += async (_, _) => await ConnectAsync();
        Closing += async (_, e) =>
        {
            if (_closeReady)
            {
                return;
            }
            e.Cancel = true;
            if (_closed)
            {
                return;
            }
            _closed = true;
            IsEnabled = false;
            try
            {
                if (!_teardownComplete)
                {
                    await DisposeApplicationAsync();
                }
            }
            finally
            {
                _closeReady = true;
                Close();
            }
        };
        UpdateControls();
    }

    private async Task DisposeApplicationAsync()
    {
        // Local views release first. Failure in one local presentation owner
        // must not skip the shared Engine session's bounded teardown.
        try
        {
            _corridor.Dispose();
        }
        catch (Exception exception)
        {
            ReportDisposalFailure("corridor view model", exception);
        }

        try
        {
            await ExplorerView.DisposeAsync();
        }
        catch (Exception exception)
        {
            ReportDisposalFailure("Engine Workbench", exception);
        }

        try
        {
            OverlayView.Dispose();
        }
        catch (Exception exception)
        {
            ReportDisposalFailure("overlay tool view", exception);
        }

        try
        {
            ReviewView.Dispose();
        }
        catch (Exception exception)
        {
            ReportDisposalFailure("review tool view", exception);
        }

        try
        {
            await _presentation.DisposeAsync();
        }
        catch (Exception exception)
        {
            ReportDisposalFailure("Engine WPF presentation", exception);
        }

        try
        {
            await _bridge.DisposeAsync();
        }
        catch (Exception exception)
        {
            ReportDisposalFailure("Engine session", exception);
        }
    }

    private static void ReportDisposalFailure(string owner, Exception exception) =>
        System.Diagnostics.Trace.TraceWarning(
            "PD Simple could not fully dispose its {0}: {1}",
            owner,
            exception.Message);

    private async Task ConnectAsync()
    {
        if (_connecting || _closed)
        {
            return;
        }
        if (_recoveryTarget is { } recoveryTarget)
        {
            _connecting = true;
            StatusText.Text = "Recovering the faulted Engine session on the selected board…";
            UpdateControls();
            try
            {
                if (recoveryTarget.Kind == EngineSessionTargetKind.LaunchContext)
                {
                    await _bridge.ConnectAsync(recoveryTarget);
                }
                else
                {
                    await _bridge.AttachAsync(recoveryTarget);
                }
                string adoptionNote = AdoptAbandonedRecovery();
                StatusText.Text = RecoveryConnectedMessage(adoptionNote);
            }
            catch (Exception error)
            {
                StatusText.Text = "Recovery unavailable: " + error.Message;
            }
            finally
            {
                _connecting = false;
                if (!_closed)
                {
                    UpdateControls();
                }
            }
            return;
        }
        if (_launchTarget.Target is null)
        {
            StatusText.Text = ConnectionSwitchPolicy.DescribeDiagnostics(
                _launchTarget.Diagnostics,
                "Choose Reconnect / Attach to select an open board, or run pd_simple in Allegro.");
            UpdateControls();
            return;
        }
        _connecting = true;
        StatusText.Text = "Connecting to the selected Allegro Engine target…";
        UpdateControls();
        try
        {
            await _bridge.ConnectAsync(_launchTarget.Target);
            StatusText.Text = _bridge.State.UnavailableDetail ?? "Connected to Allegro.";
        }
        catch (Exception error)
        {
            StatusText.Text = "Connection unavailable: " + error.Message;
        }
        finally
        {
            _connecting = false;
            if (!_closed)
            {
                UpdateControls();
            }
        }
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting || _closed || !_bridge.CanConnect || _corridor.IsBusy || _corridor.IsNavigating ||
            ExplorerView.IsBusy || !ExplorerView.CanClose)
        {
            return;
        }
        _connecting = true;
        UpdateControls();
        try
        {
            bool faulted = _bridge.EngineSession.State.ConnectionState == EngineConnectionState.Faulted;
            var chooser = new BoardConnectionDialog(
                _bridge.CanReconnectCurrent,
                faulted ? null : _bridge.State.Design)
            {
                Owner = this
            };
            if (chooser.ShowDialog() != true || _closed)
            {
                return;
            }
            if (chooser.ReconnectCurrent)
            {
                StatusText.Text = "Refreshing the current connection without replaying operations…";
                await _bridge.ReconnectAsync();
            }
            else if (chooser.SelectedTarget is { } target)
            {
                EngineConnectionAction action = ConnectionSwitchPolicy.SelectAction(
                    _bridge.EngineSession.State,
                    target);
                if (action == EngineConnectionAction.RecoverWithNewWindow)
                {
                    await RecoverWithNewWindowAsync(target);
                    return;
                }
                if (!ExplorerView.CanSwitchSession)
                {
                    StatusText.Text = ExplorerView.SessionRetentionReason ??
                        "Review or recover the current Engine edit before attaching to another board.";
                    ShowTool("explorer");
                    return;
                }
                StatusText.Text = "Verifying the selected Allegro Engine target…";
                await _bridge.AttachAsync(target);
            }
            StatusText.Text = _bridge.State.UnavailableDetail ?? "Connected to Allegro.";
        }
        catch (Exception error)
        {
            StatusText.Text = "Reconnect unavailable: " + error.Message;
        }
        finally
        {
            _connecting = false;
            UpdateControls();
        }
    }

    private async Task RecoverWithNewWindowAsync(EngineSessionTarget target)
    {
        // The faulted Engine session, its presentation, and every same-session
        // view model die with this window. Teardown completes first so terminal
        // outcomes that arrive during disposal are included in the carried
        // evidence; only then is the replacement window created. The window is
        // closed for interaction while the handoff runs, and Close is guarded
        // until the handoff either launches the replacement or fails.
        _closed = true;
        IsEnabled = false;
        StatusText.Text = "Releasing the faulted Engine session before carrying recovery evidence…";
        try
        {
            System.Collections.Generic.IReadOnlyList<EngineUnresolvedOperation> abandoned =
                await RecoveryHandoff.SnapshotAfterTeardownAsync(
                    DisposeApplicationAsync,
                    () => _bridge.EngineSession.UnresolvedOperations);
            _teardownComplete = true;
            var recovery = new MainWindow(target, abandoned);
            recovery.Show();
            if (Application.Current is not null)
            {
                Application.Current.MainWindow = recovery;
            }
        }
        catch (Exception handoffError)
        {
            System.Diagnostics.Trace.TraceError(
                "PD Simple could not hand off recovery to a replacement window: {0}",
                handoffError.Message);
            StatusText.Text = "Recovery handoff failed: " + handoffError.Message;
        }
        finally
        {
            _closeReady = true;
            Close();
        }
    }

    private string AdoptAbandonedRecovery()
    {
        if (_abandonedRecovery.Count == 0)
        {
            return string.Empty;
        }
        try
        {
            _bridge.EngineSession.AdoptUnresolvedOperations(_abandonedRecovery);
            return string.Empty;
        }
        catch (Exception adoptionError)
        {
            // Never present a ready-to-mutate window when carried evidence is
            // lost: record one restriction obligation per affected board so the
            // Engine mutation gate and switch fence restrict this session.
            try
            {
                _bridge.EngineSession.AdoptUnresolvedOperations(
                    RecoveryHandoff.TransferFailureObligations(
                        _abandonedRecovery, DateTimeOffset.UtcNow, adoptionError.Message));
            }
            catch (Exception restrictionError)
            {
                _recoveryBlocked = true;
                UpdateControls();
                return " Recovery evidence could not be transferred " +
                    $"({restrictionError.Message}); restart PD Simple before mutating.";
            }
            UpdateControls();
            return $" Prior uncertain operations could not be adopted ({adoptionError.Message}); " +
                "this session is restricted until the carried evidence is reconciled in Allegro.";
        }
    }

    private string RecoveryConnectedMessage(string adoptionNote)
    {
        string connected = _bridge.State.UnavailableDetail ?? "Connected to Allegro.";
        if (!string.IsNullOrEmpty(adoptionNote))
        {
            return connected + adoptionNote;
        }
        return _abandonedRecovery.Count <= 0
            ? connected
            : $"{connected} The previous session faulted; {_abandonedRecovery.Count} uncertain operation(s) " +
              "were carried into this session for reconciliation. Inspect Allegro before mutating.";
    }

    private void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = WindowScreenshot.CaptureAndCopyPath(this);
            ScreenshotButton.ToolTip =
                $"Saved PNG and copied it to the clipboard:\n{path}";
            StatusText.Text =
                $"Screenshot saved and copied: {Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            ScreenshotButton.ToolTip =
                $"Screenshot failed: {exception.Message}";
            StatusText.Text =
                $"Screenshot failed: {exception.Message}";
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowTool(null);
    private void Explorer_Click(object sender, RoutedEventArgs e) => ShowTool("explorer");
    private void Corridor_Click(object sender, RoutedEventArgs e) => ShowTool("corridor");
    private void Route_Click(object sender, RoutedEventArgs e) => ShowTool("route");
    private void Overlay_Click(object sender, RoutedEventArgs e) => ShowTool("overlay");
    private void Review_Click(object sender, RoutedEventArgs e) => ShowTool("review");

    private void Padstacks_Click(object sender, RoutedEventArgs e)
    {
        ShowTool("padstacks");
        RefreshPadstacksViewAsync();
    }

    private async void RefreshPadstacksViewAsync()
    {
        if (_padstacksRefreshing || _closed)
        {
            return;
        }
        _padstacksRefreshing = true;
        try
        {
            bool live = _bridge.State.IsReady && _bridge.Workspace.IsConnected;
            if (!live || _bridge.IsBusy)
            {
                PadstacksView.ShowScene(null, live);
                if (_bridge.IsBusy)
                {
                    StatusText.Text = "Padstack capture deferred while another Engine operation runs.";
                }
                return;
            }
            LiveDesignScene capture = await _bridge.ReadEngineSceneAsync(
                SceneQuery.CompleteBoard(includeContours: false));
            if (_closed)
            {
                return;
            }
            PadstacksView.ShowScene(capture.Scene, _bridge.Workspace.IsConnected);
        }
        catch (Exception exception)
        {
            PadstacksView.ShowScene(null, _bridge.State.IsReady);
            StatusText.Text = "Padstack capture unavailable: " + exception.Message;
        }
        finally
        {
            _padstacksRefreshing = false;
        }
    }

    private void ShowTool(string? tool)
    {
        HomePanel.Visibility = tool is null ? Visibility.Visible : Visibility.Collapsed;
        ExplorerView.Visibility = tool == "explorer" ? Visibility.Visible : Visibility.Collapsed;
        CorridorView.Visibility = tool == "corridor" ? Visibility.Visible : Visibility.Collapsed;
        RoutePanel.Visibility = tool == "route" ? Visibility.Visible : Visibility.Collapsed;
        PadstacksView.Visibility = tool == "padstacks" ? Visibility.Visible : Visibility.Collapsed;
        OverlayView.Visibility = tool == "overlay" ? Visibility.Visible : Visibility.Collapsed;
        ReviewView.Visibility = tool == "review" ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool TryWidth(out decimal width) => decimal.TryParse(WidthInput.Text,
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture, out width) && width is >= 0.1m and <= 10000m;

    private void Width_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (WidthError is not null)
        {
            UpdateControls();
        }
    }

    private void UpdateControls()
    {
        if (_closed || WidthError is null || _corridor is null)
        {
            return;
        }
        bool valid = TryWidth(out _);
        WidthError.Text = valid ? "" : "Enter a width from 0.1 to 10000 mil (decimal point: .).";
        bool idle = !_connecting && !_bridge.IsBusy && !_corridor.IsBusy && !_corridor.IsNavigating;
        ExplorerView.HostBusy = _bridge.IsBusy || _corridor.IsBusy || _corridor.IsNavigating;
        bool engineIdle = !ExplorerView.IsBusy && ExplorerView.CanClose;
        bool mutationAllowed = engineIdle && ExplorerView.CanStartMutation && !_recoveryBlocked;
        // Current-board reconnect stays available for review/recovery. Attaching
        // another board is checked after the chooser against CanSwitchSession.
        ReconnectButton.IsEnabled = idle && engineIdle && _bridge.CanConnect;
        WidthInput.IsEnabled = idle;
        StartRouteButton.IsEnabled = idle && mutationAllowed && valid && _bridge.CanRoute;
        CancelRouteButton.IsEnabled = _bridge.HasRouteInProgress;
        ClearPickButton.IsEnabled = _bridge.CanClearFirstPick;
        UndoRouteButton.IsEnabled = idle && mutationAllowed && _bridge.CanUndoRoute;
        ExplorerMenuButton.IsEnabled = !_bridge.HasRouteInProgress && !_corridor.IsBusy && !_corridor.IsNavigating;
        CorridorMenuButton.IsEnabled = !_bridge.HasRouteInProgress;
        RouteMenuButton.IsEnabled = !_corridor.IsBusy && !_corridor.IsNavigating;
        OverlayMenuButton.IsEnabled = !_bridge.HasRouteInProgress;
        ReviewMenuButton.IsEnabled = !_bridge.HasRouteInProgress;
        CorridorView.IsEnabled = !_bridge.HasRouteInProgress && !_connecting;
    }

    private async void StartRoute_Click(object sender, RoutedEventArgs e)
    {
        if (!TryWidth(out var width))
        {
            return;
        }
        if (!ExplorerView.CanStartMutation)
        {
            RoutePhaseText.Text = "Blocked by Engine review";
            RouteDetailText.Text = ExplorerView.SessionRetentionReason ??
                "Finish the current Engine operation before starting another mutation.";
            ShowTool("explorer");
            return;
        }
        await RouteActionAsync(async () =>
        {
            var result = await _bridge.RouteAsync(width);
            RoutePhaseText.Text = _bridge.RouteResultWarning is null ? result.State.ToString() : "Review required";
            RouteDetailText.Text = _bridge.RouteResultWarning ?? result.Message;
        });
    }

    private async void ClearPick_Click(object sender, RoutedEventArgs e) =>
        await RouteActionAsync(() => _bridge.ClearFirstPickAsync());

    private async void CancelRoute_Click(object sender, RoutedEventArgs e) =>
        await RouteActionAsync(() => _bridge.CancelRouteAsync());

    private async void UndoRoute_Click(object sender, RoutedEventArgs e)
    {
        if (!ExplorerView.CanStartMutation)
        {
            RoutePhaseText.Text = "Blocked by Engine review";
            RouteDetailText.Text = ExplorerView.SessionRetentionReason ??
                "Resolve the current Engine edit outcome before starting another mutation.";
            ShowTool("explorer");
            return;
        }
        await RouteActionAsync(async () =>
        {
            var result = await _bridge.UndoRouteAsync();
            RoutePhaseText.Text = _bridge.RouteResultWarning is null ? result.State.ToString() : "Review required";
            RouteDetailText.Text = _bridge.RouteResultWarning ?? result.Message;
        });
    }

    private string BridgeVersion()
    {
        string? version = _bridge.EngineSession.GetType().Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? _bridge.EngineSession.GetType().Assembly.GetName().Version?.ToString();
        return version ?? "unknown";
    }

    private async Task RouteActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            RoutePhaseText.Text = "Operation unavailable";
            RouteDetailText.Text = error.Message;
        }
        finally
        {
            if (!_closed)
            {
                UpdateControls();
            }
        }
    }
}
