using System.Globalization;
using System.Windows;
using PD.Simple.Corridor;

namespace PD.Simple;

public partial class MainWindow : Window
{
    private readonly BridgeSession _bridge = new();
    private readonly DpViaCorridorWorkspaceViewModel _corridor;
    private readonly string? _bridgeDirectory;
    private bool _connecting;
    private bool _closed;
    private bool _closeReady;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _corridor = new DpViaCorridorWorkspaceViewModel(_bridge);
        CorridorView.DataContext = _corridor;
        ExplorerView.Session = _bridge;
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
        _bridge.FocusRequested += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
        };
        var index = Array.IndexOf(args, "--bridge-dir");
        _bridgeDirectory = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
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
            ExplorerView.Session = null;
            _corridor.Dispose();
            try
            {
                await _bridge.DisposeAsync();
            }
            finally
            {
                _closeReady = true;
                Close();
            }
        };
        UpdateControls();
    }

    private async Task ConnectAsync()
    {
        if (_connecting || _closed)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_bridgeDirectory))
        {
            StatusText.Text = "Choose Reconnect / Attach to select an open board, or run pd_simple in Allegro.";
            UpdateControls();
            return;
        }
        _connecting = true;
        StatusText.Text = "Connecting to the packaged Allegro Engine/native operations…";
        UpdateControls();
        try
        {
            await _bridge.ConnectAsync(_bridgeDirectory, this);
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
        if (_connecting || _closed || !_bridge.CanConnect || _corridor.IsBusy || _corridor.IsNavigating)
        {
            return;
        }
        _connecting = true;
        UpdateControls();
        try
        {
            var chooser = new BoardConnectionDialog(_bridge.CanReconnectCurrent, _bridge.State.Design)
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
                await _bridge.ReconnectAsync(this);
            }
            else if (chooser.SelectedCandidate is { } candidate)
            {
                StatusText.Text = "Verifying the selected Allegro board and its Engine/native operations…";
                await _bridge.AttachAsync(candidate, this);
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

    private void Home_Click(object sender, RoutedEventArgs e) => ShowTool(null);
    private void Explorer_Click(object sender, RoutedEventArgs e) => ShowTool("explorer");
    private void Corridor_Click(object sender, RoutedEventArgs e) => ShowTool("corridor");
    private void Route_Click(object sender, RoutedEventArgs e) => ShowTool("route");

    private void ShowTool(string? tool)
    {
        HomePanel.Visibility = tool is null ? Visibility.Visible : Visibility.Collapsed;
        ExplorerView.Visibility = tool == "explorer" ? Visibility.Visible : Visibility.Collapsed;
        CorridorView.Visibility = tool == "corridor" ? Visibility.Visible : Visibility.Collapsed;
        RoutePanel.Visibility = tool == "route" ? Visibility.Visible : Visibility.Collapsed;
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
        ReconnectButton.IsEnabled = idle && _bridge.CanConnect;
        WidthInput.IsEnabled = idle;
        StartRouteButton.IsEnabled = idle && valid && _bridge.CanRoute;
        CancelRouteButton.IsEnabled = _bridge.HasRouteInProgress;
        ClearPickButton.IsEnabled = _bridge.CanClearFirstPick;
        UndoRouteButton.IsEnabled = idle && _bridge.CanUndoRoute;
        ExplorerMenuButton.IsEnabled = !_bridge.HasRouteInProgress && !_corridor.IsBusy && !_corridor.IsNavigating;
        CorridorMenuButton.IsEnabled = !_bridge.HasRouteInProgress;
        RouteMenuButton.IsEnabled = !_corridor.IsBusy && !_corridor.IsNavigating;
        CorridorView.IsEnabled = !_bridge.HasRouteInProgress && !_connecting;
    }

    private async void StartRoute_Click(object sender, RoutedEventArgs e)
    {
        if (!TryWidth(out var width))
        {
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

    private async void UndoRoute_Click(object sender, RoutedEventArgs e) =>
        await RouteActionAsync(async () =>
        {
            var result = await _bridge.UndoRouteAsync();
            RoutePhaseText.Text = _bridge.RouteResultWarning is null ? result.State.ToString() : "Review required";
            RouteDetailText.Text = _bridge.RouteResultWarning ?? result.Message;
        });

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
