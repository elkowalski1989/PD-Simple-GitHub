using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf.Engine;
using CircuitHub.AllegroBridge.Wpf.NativeHost.Engine;

namespace PD.Simple;

public partial class MainWindow
{
    private Grid? _nativeToolsContent;
    private ColumnDefinition? _nativePaneColumn;
    private GridSplitter? _nativePaneSplitter;
    private Border? _nativePane;
    private Border? _nativeHostContainer;
    private TextBlock? _nativeStatusText;
    private Button? _nativePaneUndockButton;
    private Button? _nativePaneRedockButton;
    private EngineIndependentNativeEditorView? _nativeView;
    private DependencyPropertyDescriptor? _nativeMinimumWidthDescriptor;
    private FrameworkElement? _nativeMinimumWidthSource;
    private WorkspaceDocumentIdentity? _nativeUserUndockedDocument;
    private EngineNativePresentationState _nativeLastState =
        EngineNativePresentationState.Unavailable;
    private bool _nativePaneBuilt;
    private bool _nativeEntering;
    private bool _nativeLeaving;
    private bool _nativeTearingDown;

    private bool IsNativeEditorDocked =>
        _nativeView is not null &&
        _nativeView.State.State == EngineNativePresentationState.Attached;

    /// <summary>
    /// Builds the resizable native pane beside all existing tools. The XAML
    /// children stay the same objects in the same window namescope, so every
    /// existing view, navigation target, and test lookup keeps working; they
    /// are only reparented into the tools column. The native pane, splitter,
    /// and host container are created here so MainWindow.xaml keeps its exact
    /// tool structure. Called once after InitializeComponent.
    /// </summary>
    private void EnsureNativePane()
    {
        if (_nativePaneBuilt)
        {
            return;
        }
        _nativePaneBuilt = true;

        var toolsContent = new Grid();
        List<UIElement> existing = ContentRoot.Children.Cast<UIElement>().ToList();
        ContentRoot.Children.Clear();
        foreach (UIElement child in existing)
        {
            toolsContent.Children.Add(child);
        }

        ContentRoot.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
            MinWidth = 320,
        });
        ContentRoot.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
        });
        _nativePaneColumn = new ColumnDefinition
        {
            Width = new GridLength(0),
        };
        ContentRoot.ColumnDefinitions.Add(_nativePaneColumn);

        Grid.SetColumn(toolsContent, 0);
        ContentRoot.Children.Add(toolsContent);
        _nativeToolsContent = toolsContent;

        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xE7, 0xF5)),
            Visibility = Visibility.Collapsed,
            ResizeDirection = GridResizeDirection.Columns,
            IsTabStop = false,
        };
        AutomationProperties.SetAutomationId(splitter, "NativePaneSplitter");
        AutomationProperties.SetName(splitter, "Resize native Allegro editor pane");
        Grid.SetColumn(splitter, 1);
        ContentRoot.Children.Add(splitter);
        _nativePaneSplitter = splitter;

        var undockButton = new Button
        {
            Content = "Undock",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };
        AutomationProperties.SetAutomationId(undockButton, "NativeUndockButton");
        AutomationProperties.SetName(undockButton, "Undock Allegro editor");
        undockButton.Click += NativeDockToggle_Click;

        var redockButton = new Button
        {
            Content = "Redock",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(redockButton, "NativeRedockButton");
        AutomationProperties.SetName(redockButton, "Redock Allegro editor");
        redockButton.Click += NativeDockToggle_Click;

        var header = new DockPanel
        {
            Margin = new Thickness(12, 8, 12, 8),
            LastChildFill = false,
        };
        var title = new TextBlock
        {
            Text = "Allegro editor",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x17, 0x24, 0x3B)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(title, Dock.Left);
        DockPanel.SetDock(undockButton, Dock.Right);
        DockPanel.SetDock(redockButton, Dock.Right);
        header.Children.Add(title);
        header.Children.Add(undockButton);
        header.Children.Add(redockButton);

        var hostContainer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x21, 0x35)),
            Margin = new Thickness(12, 0, 12, 0),
            MinWidth = 200,
            MinHeight = 200,
        };

        var statusText = new TextBlock
        {
            Text = "Allegro docking is unavailable until a board is connected.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x59, 0x70, 0x8E)),
            FontSize = 11,
            Margin = new Thickness(12, 8, 12, 8),
            TextWrapping = TextWrapping.Wrap,
        };

        var paneGrid = new Grid();
        paneGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        paneGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        paneGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(hostContainer, 1);
        Grid.SetRow(statusText, 2);
        paneGrid.Children.Add(header);
        paneGrid.Children.Add(hostContainer);
        paneGrid.Children.Add(statusText);

        var pane = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xE7, 0xF5)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = paneGrid,
        };
        Grid.SetColumn(pane, 2);
        ContentRoot.Children.Add(pane);

        _nativePane = pane;
        _nativeHostContainer = hostContainer;
        _nativeStatusText = statusText;
        _nativePaneUndockButton = undockButton;
        _nativePaneRedockButton = redockButton;

        StateChanged += (_, _) =>
        {
            if (!_closed && !_nativeTearingDown &&
                IsNativeEditorDocked && WindowState == WindowState.Normal)
            {
                WindowState = WindowState.Maximized;
            }
        };
        ContentRoot.SizeChanged += (_, _) => ConstrainNativePaneWidth();
    }

    private async void NativeDockToggle_Click(object sender, RoutedEventArgs e)
    {
        EngineIndependentNativeEditorView? view = _nativeView;
        if (view is not null)
        {
            switch (view.State.State)
            {
                case EngineNativePresentationState.Attached:
                case EngineNativePresentationState.RestorePending:
                    await UndockNativeEditorAsync(userInitiated: true);
                    return;
                case EngineNativePresentationState.Attaching:
                case EngineNativePresentationState.Detaching:
                    return;
            }
        }

        await DockNativeEditorAsync();
    }

    /// <summary>
    /// Reacts to every Engine session change. A lost connection returns the
    /// borrowed editor to the desktop through the adapter; a fresh ready
    /// document docks automatically unless the user explicitly undocked that
    /// same document. The adapter enforces the exact document binding and
    /// owns all restoration state; PD owns only this auto-dock policy, layout,
    /// and status. Runs on the UI thread.
    /// </summary>
    private void OnBridgeStateChangedForNativeDock()
    {
        if (_closed || _nativeTearingDown)
        {
            return;
        }

        EngineSessionSnapshot snapshot = _bridge.EngineSession.State;
        WorkspaceDocumentIdentity? document = snapshot.Document;
        bool ready =
            snapshot.ConnectionState == EngineConnectionState.Ready &&
            document is not null;

        if (!ready || document is null)
        {
            if (_nativeView is not null)
            {
                _ = UndockNativeEditorAsync(userInitiated: false);
            }
            else
            {
                SetNativeStatus("Allegro docking is unavailable until a board is connected.");
            }

            RefreshNativeDockControls();
            return;
        }

        EngineIndependentNativeEditorView? view = _nativeView;
        if (view is not null)
        {
            switch (view.State.State)
            {
                case EngineNativePresentationState.Attached:
                case EngineNativePresentationState.Attaching:
                case EngineNativePresentationState.Detaching:
                    RefreshNativeDockControls();
                    return;
                case EngineNativePresentationState.RestorePending:
                    SetNativeStatus(
                        "Allegro restoration is incomplete: " +
                        view.State.Status +
                        " Use Undock to retry. Allegro was not closed.");
                    RefreshNativeDockControls();
                    return;
            }
        }

        if (IsUserUndocked(document))
        {
            SetNativeStatus(view is not null && HasShellLossDiagnostic(view.State)
                ? view.State.Status
                : "Allegro is undocked. Use Dock Allegro to dock it beside the tools.");
            RefreshNativeDockControls();
            return;
        }

        _ = DockNativeEditorAsync();
    }

    /// <summary>
    /// Docks the exact interactive editor for the current ready Engine
    /// document through the independent Engine-aware shell adapter. PD supplies only
    /// its existing application-owned Engine session; it obtains no HWND or
    /// process identity, constructs no descriptor, and performs no native
    /// registration. Any failure leaves the desktop Allegro usable and reports
    /// the provider's specific reason.
    /// </summary>
    private async Task DockNativeEditorAsync()
    {
        if (_closed || _nativeTearingDown || _nativeEntering || _nativeLeaving)
        {
            return;
        }

        EngineSessionSnapshot snapshot = _bridge.EngineSession.State;
        WorkspaceDocumentIdentity? document = snapshot.Document;
        if (snapshot.ConnectionState != EngineConnectionState.Ready || document is null)
        {
            SetNativeStatus("Allegro docking is unavailable until a board is connected.");
            RefreshNativeDockControls();
            return;
        }

        EngineIndependentNativeEditorView? existing = _nativeView;
        if (existing is not null)
        {
            switch (existing.State.State)
            {
                case EngineNativePresentationState.Attached:
                case EngineNativePresentationState.Attaching:
                case EngineNativePresentationState.Detaching:
                    return;
                case EngineNativePresentationState.RestorePending:
                    ShowNativePaneForDock();
                    SetNativeStatus(
                        "Allegro docking cannot start while restoration is incomplete: " +
                        existing.State.Status +
                        ". Use Undock to retry.");
                    RefreshNativeDockControls();
                    return;
            }
        }

        if (_navigationOnly)
        {
            SetNavigationOnly(false);
        }

        _nativeEntering = true;
        RefreshNativeDockControls();
        ShowNativePaneForDock();
        SetNativeStatus("Docking the connected Allegro editor…");

        try
        {
            if (_nativeView is null)
            {
                var created = new EngineIndependentNativeEditorView(
                    _bridge.EngineSession,
                    new EngineIndependentNativeEditorOptions
                    {
                        // Keep Allegro's editor a top-level window so an abrupt
                        // shell exit cannot destroy its HWND or the open board.
                        Mode = EngineIndependentNativeEditorMode.TopLevel,
                        AllowUnqualifiedEnvironment =
                            Environment.GetEnvironmentVariable("PD_SIMPLE_NATIVE_QUALIFICATION") == "1",
                    });
                _nativeLastState = created.State.State;
                created.StateChanged += OnNativeViewStateChanged;
                _nativeView = created;
                _nativeMinimumWidthSource = created.Provider.View;
                _nativeMinimumWidthDescriptor = DependencyPropertyDescriptor.FromProperty(
                    FrameworkElement.MinWidthProperty, typeof(FrameworkElement));
                _nativeMinimumWidthDescriptor?.AddValueChanged(
                    _nativeMinimumWidthSource, OnNativeMinimumWidthChanged);
                if (_nativeHostContainer is not null)
                {
                    _nativeHostContainer.Child = created;
                }
            }

            UpdateLayout();
            await _nativeView.EnterAsync();
        }
        catch (InvalidOperationException error)
        {
            string reason = _nativeView?.State.Status ?? error.Message;
            SetNativeStatus(
                "Allegro docking is unavailable: " + reason +
                " Allegro remains usable on the desktop.");
            StatusText.Text = "Allegro docking unavailable: " + error.Message;
        }
        catch (Exception error)
        {
            SetNativeStatus(
                "Allegro docking failed: " + error.Message +
                " Allegro remains usable on the desktop.");
            StatusText.Text = "Allegro docking failed: " + error.Message;
        }
        finally
        {
            _nativeEntering = false;
            RefreshNativeDockControls();
        }
    }

    /// <summary>
    /// Returns the borrowed editor to the desktop through the adapter and
    /// hides the pane once restoration completes. A detach that cannot
    /// complete keeps the pane, the view, and the shell's retryable
    /// restoration state instead of discarding them. Never terminates Allegro
    /// and never saves, reloads, or discards its board.
    /// </summary>
    private async Task UndockNativeEditorAsync(bool userInitiated)
    {
        EngineIndependentNativeEditorView? view = _nativeView;
        if (view is null)
        {
            if (userInitiated)
            {
                _nativeUserUndockedDocument = _bridge.EngineSession.State.Document;
                SetNativeStatus("Allegro is undocked. Use Dock Allegro to dock it beside the tools.");
            }

            HideNativePane();
            RefreshNativeDockControls();
            return;
        }

        if (_nativeLeaving || _nativeEntering)
        {
            return;
        }

        if (view.State.State == EngineNativePresentationState.Detaching)
        {
            return;
        }

        _nativeLeaving = true;
        RefreshNativeDockControls();
        try
        {
            await view.LeaveAsync();
            EngineNativePresentationSnapshot state = view.State;
            if (state.State == EngineNativePresentationState.RestorePending)
            {
                ShowNativePaneForDock();
                SetNativeStatus(
                    "Allegro undock did not complete: " +
                    state.Status +
                    " Use Undock to retry. Allegro was not closed.");
                StatusText.Text = "Allegro undock did not complete: " + state.Status;
                return;
            }

            if (userInitiated)
            {
                _nativeUserUndockedDocument = _bridge.EngineSession.State.Document;
                SetNativeStatus("Allegro is undocked. Use Dock Allegro to dock it beside the tools.");
            }

            HideNativePane();
        }
        catch (InvalidOperationException error)
        {
            ShowNativePaneForDock();
            SetNativeStatus(
                "Allegro undock did not complete: " +
                error.Message +
                " Use Undock to retry. Allegro was not closed.");
            StatusText.Text = "Allegro undock did not complete: " + error.Message;
        }
        catch (Exception error)
        {
            ShowNativePaneForDock();
            SetNativeStatus(
                "Allegro undock did not complete: " +
                error.Message +
                " Use Undock to retry. Allegro was not closed.");
            StatusText.Text = "Allegro undock did not complete: " + error.Message;
        }
        finally
        {
            _nativeLeaving = false;
            RefreshNativeDockControls();
        }
    }

    /// <summary>
    /// Observes the adapter's handle-free state. Maximizes PD once the editor
    /// is attached, surfaces the provider's specific reason while unavailable
    /// or restoring, and re-docks after the provider restores a superseded
    /// board. Never selects by title or process list; the provider enforces
    /// the exact binding.
    /// </summary>
    private void OnNativeViewStateChanged(
        object? sender,
        EngineNativePresentationSnapshot snapshot)
    {
        EngineNativePresentationState previous = _nativeLastState;
        _nativeLastState = snapshot.State;
        if (_closed || _nativeTearingDown)
        {
            RefreshNativeDockControls();
            return;
        }

        switch (snapshot.State)
        {
            case EngineNativePresentationState.Attached:
                _nativeUserUndockedDocument = null;
                ShowNativePaneForDock();
                string design = _bridge.EngineSession.State.Document?.Design ?? string.Empty;
                string docked = string.IsNullOrWhiteSpace(design)
                    ? "Allegro is docked beside the tools."
                    : $"Allegro is docked beside the tools: {design}.";
                SetNativeStatus(docked);
                ShowInTaskbar = true;
                if (!IsVisible)
                {
                    Show();
                }
                WindowState = WindowState.Maximized;
                break;
            case EngineNativePresentationState.Attaching:
                ShowNativePaneForDock();
                SetNativeStatus("Docking the connected Allegro editor… " + snapshot.Status);
                break;
            case EngineNativePresentationState.Detaching:
                SetNativeStatus("Returning Allegro to the desktop… " + snapshot.Status);
                break;
            case EngineNativePresentationState.RestorePending:
                ShowNativePaneForDock();
                SetNativeStatus(
                    "Allegro restoration is incomplete: " +
                    snapshot.Status +
                    " Use Undock to retry. Allegro was not closed.");
                StatusText.Text = "Allegro restoration pending: " + snapshot.Status;
                break;
            case EngineNativePresentationState.Detached:
                if (HasShellLossDiagnostic(snapshot))
                {
                    _nativeUserUndockedDocument = _bridge.EngineSession.State.Document;
                    ShowNativePaneForDock();
                    SetNativeStatus(snapshot.Status);
                    StatusText.Text = "Native shell lost: " + snapshot.Status;
                    break;
                }
                if (previous == EngineNativePresentationState.Attached)
                {
                    EngineSessionSnapshot session = _bridge.EngineSession.State;
                    WorkspaceDocumentIdentity? current = session.Document;
                    bool ready =
                        session.ConnectionState == EngineConnectionState.Ready &&
                        current is not null;
                    if (ready && current is not null && !IsUserUndocked(current))
                    {
                        _ = DockNativeEditorAsync();
                        return;
                    }

                    if (!ready)
                    {
                        SetNativeStatus("The board connection ended. Allegro was returned to the desktop.");
                        HideNativePane();
                    }
                    else
                    {
                        SetNativeStatus(snapshot.Status);
                    }
                }
                else
                {
                    SetNativeStatus(snapshot.Status);
                }
                break;
            case EngineNativePresentationState.Unavailable:
                SetNativeStatus(snapshot.Status + " Allegro remains usable on the desktop.");
                EngineSessionSnapshot unavailableSession = _bridge.EngineSession.State;
                bool sessionReady =
                    unavailableSession.ConnectionState == EngineConnectionState.Ready &&
                    unavailableSession.Document is not null;
                if (sessionReady)
                {
                    ShowNativePaneForDock();
                }
                else if (!IsNativeEditorDocked)
                {
                    HideNativePane();
                }
                break;
            case EngineNativePresentationState.Disposed:
                SetNativeStatus("The native editor view was disposed.");
                HideNativePane();
                break;
        }

        RefreshNativeDockControls();
    }

    private static bool HasShellLossDiagnostic(EngineNativePresentationSnapshot snapshot) =>
        snapshot.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code, "native.shell-lost", StringComparison.Ordinal));

    /// <summary>
    /// Vetoes a close or recovery handoff while the borrowed Allegro editor
    /// is still parented. Carries the specific provider reason so the shell
    /// can show it and keep Undock usable for retry. The veto never
    /// terminates or strands the borrowed editor.
    /// </summary>
    private sealed class NativeEditorRestorePendingException : InvalidOperationException
    {
        public NativeEditorRestorePendingException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Cancels a close or recovery handoff that native restoration vetoed
    /// and returns the window to interactive state so Undock stays usable
    /// for retry. The specific provider reason is shown in both the shell and
    /// native statuses. Internal so the shutdown-safety checks can drive
    /// the canceled-close end state without a live Allegro borrow.
    /// </summary>
    internal void CancelCloseForPendingNativeRestore(string action, string reason)
    {
        _closed = false;
        _nativeTearingDown = false;
        IsEnabled = true;
        if (!_activationTimer.IsEnabled)
        {
            _activationTimer.Start();
        }
        if (!IsVisible)
        {
            Show();
        }
        WindowState = WindowState.Maximized;

        ShowNativePaneForDock();
        SetNativeStatus(
            action + ": Allegro could not be returned to the desktop: " +
            reason +
            ". Use Undock to retry. Allegro was not closed.");
        StatusText.Text =
            action + ": Allegro could not be returned to the desktop: " +
            reason + ".";
        RefreshNativeDockControls();
        UpdateControls();
    }

    internal string NativeStatusForChecks => _nativeStatusText?.Text ?? string.Empty;

    internal bool NativePaneVisibleForChecks =>
        _nativePane is not null && _nativePane.Visibility == Visibility.Visible;

    /// <summary>
    /// Shutdown path. Requests provider close before the Engine session is
    /// disposed so the owned registration can still be cleared. A close that
    /// reports it cannot complete keeps the view, its retryable restoration
    /// state, and the Engine session, then vetoes the close with the specific
    /// provider reason instead of releasing a still-borrowed editor.
    /// </summary>
    internal async Task TeardownNativeDockingAsync()
    {
        _nativeTearingDown = true;
        EngineIndependentNativeEditorView? view = _nativeView;
        if (view is null)
        {
            return;
        }

        EngineWorkspaceCloseResult result;
        try
        {
            result = await view.RequestCloseAsync();
        }
        catch (Exception error)
        {
            _nativeTearingDown = false;
            throw new NativeEditorRestorePendingException(error.Message);
        }

        if (!result.CanClose)
        {
            _nativeTearingDown = false;
            string reason = result.Reason ??
                result.NativeState?.ToString() ??
                "restoration is still pending.";
            Trace.TraceWarning(
                "PD Simple canceled shutdown because its native Allegro editor could not be undocked: {0}",
                reason);
            throw new NativeEditorRestorePendingException(reason);
        }

        try
        {
            await view.DisposeAsync();
        }
        catch (Exception error)
        {
            _nativeTearingDown = false;
            throw new NativeEditorRestorePendingException(error.Message);
        }

        view.StateChanged -= OnNativeViewStateChanged;
        if (_nativeMinimumWidthDescriptor is not null && _nativeMinimumWidthSource is not null)
        {
            _nativeMinimumWidthDescriptor.RemoveValueChanged(
                _nativeMinimumWidthSource, OnNativeMinimumWidthChanged);
        }
        _nativeMinimumWidthDescriptor = null;
        _nativeMinimumWidthSource = null;
        if (_nativeHostContainer is not null &&
            ReferenceEquals(_nativeHostContainer.Child, view))
        {
            _nativeHostContainer.Child = null;
        }

        _nativeView = null;
    }

    /// <summary>
    /// A workspace close veto can arrive after the native editor was safely
    /// restored and its view disposed. The still-open window must accept a
    /// fresh dock request against its retained Engine session.
    /// </summary>
    private void ResumeNativeDockingAfterWorkspaceCloseVeto()
    {
        _nativeTearingDown = false;
        if (_nativeView is null)
        {
            _nativeLastState = EngineNativePresentationState.Detached;
            HideNativePane();
            SetNativeStatus("Allegro is on the desktop. Use Dock Allegro to dock it again.");
        }
        RefreshNativeDockControls();
    }

    private void ShowNativePaneForDock()
    {
        bool entering = _nativePane?.Visibility != Visibility.Visible;
        if (entering && _nativePaneColumn is not null)
        {
            // GridSplitter changes both grid columns to pixel widths. Reset
            // both on a new docking entry so a prior extreme drag cannot
            // leave the editor outside the maximized PD window on redock.
            ContentRoot.ColumnDefinitions[0].Width =
                new GridLength(1, GridUnitType.Star);
            _nativePaneColumn.MinWidth = 0;
            // Allegro's native main window has a larger practical minimum
            // width than a WPF tool panel. Give it the dominant share on
            // entry so its menus and side panes fit at common high-DPI sizes;
            // the splitter remains free to rebalance either side.
            _nativePaneColumn.Width = new GridLength(2, GridUnitType.Star);
        }

        if (_nativePaneSplitter is not null)
        {
            _nativePaneSplitter.Visibility = Visibility.Visible;
        }

        if (_nativePane is not null)
        {
            _nativePane.Visibility = Visibility.Visible;
        }

        ConstrainNativePaneWidth();
    }

    private void OnNativeMinimumWidthChanged(object? sender, EventArgs e)
    {
        // The shell reports native size demand after its layout pass. Apply
        // the grid correction later, outside WPF or Win32 sizing callbacks.
        _ = Dispatcher.BeginInvoke(
            new Action(ConstrainNativePaneWidth),
            DispatcherPriority.Loaded);
    }

    private void ConstrainNativePaneWidth()
    {
        if (_closed || _nativeTearingDown ||
            _nativePane?.Visibility != Visibility.Visible ||
            _nativePaneColumn is null ||
            _nativePaneSplitter is null ||
            _nativeHostContainer is null ||
            _nativeView is null)
        {
            return;
        }

        // Bridge exposes the editor's observed native minimum on its WPF
        // view. The application owns the surrounding grid and its margins.
        double editorMinimum = _nativeView.Provider.View.MinWidth;
        double paneMinimum = Math.Ceiling(
            editorMinimum +
            _nativeHostContainer.Margin.Left +
            _nativeHostContainer.Margin.Right +
            _nativePane.BorderThickness.Left +
            _nativePane.BorderThickness.Right + 2);
        if (!double.IsFinite(paneMinimum) || paneMinimum <= 0)
        {
            return;
        }
        _nativePaneColumn.MinWidth = paneMinimum;

        if (!IsNativeEditorDocked || ContentRoot.ActualWidth <= 0)
        {
            return;
        }

        ColumnDefinition toolsColumn = ContentRoot.ColumnDefinitions[0];
        double available = ContentRoot.ActualWidth - _nativePaneSplitter.ActualWidth;
        if (available <= toolsColumn.MinWidth + paneMinimum)
        {
            SetNativeStatus(
                "The display is too narrow for Allegro and the tools at this DPI. " +
                "Use Undock to return Allegro to the desktop.");
            return;
        }

        double toolsWidth = toolsColumn.ActualWidth;
        double constrainedToolsWidth = Math.Clamp(
            toolsWidth, toolsColumn.MinWidth, available - paneMinimum);
        bool fixedColumns = !toolsColumn.Width.IsStar || !_nativePaneColumn.Width.IsStar;
        if (!fixedColumns && toolsWidth <= constrainedToolsWidth + 0.5)
        {
            return;
        }

        // GridSplitter turns both columns into pixel widths. Converting the
        // bounded split back to star weights keeps the editor growing with
        // later window/DPI size changes while honoring its native minimum.
        toolsColumn.Width = new GridLength(constrainedToolsWidth, GridUnitType.Star);
        _nativePaneColumn.Width = new GridLength(
            available - constrainedToolsWidth, GridUnitType.Star);
    }

    private void HideNativePane()
    {
        if (_nativePane is not null)
        {
            _nativePane.Visibility = Visibility.Collapsed;
        }

        if (_nativePaneSplitter is not null)
        {
            _nativePaneSplitter.Visibility = Visibility.Collapsed;
        }

        if (_nativePaneColumn is not null)
        {
            _nativePaneColumn.MinWidth = 0;
            _nativePaneColumn.Width = new GridLength(0);
        }

        ContentRoot.ColumnDefinitions[0].Width =
            new GridLength(1, GridUnitType.Star);
    }

    private void SetNativeStatus(string message)
    {
        if (_nativeStatusText is not null)
        {
            _nativeStatusText.Text = message;
        }
    }

    private void RefreshNativeDockControls()
    {
        if (_nativePaneBuilt &&
            NativeDockToggleButton is not null &&
            _nativePaneUndockButton is not null &&
            _nativePaneRedockButton is not null &&
            _nativePane is not null)
        {
            bool docked = IsNativeEditorDocked;
            bool restorePending =
                _nativeView?.State.State == EngineNativePresentationState.RestorePending;
            bool transitioning =
                _nativeEntering ||
                _nativeLeaving ||
                _nativeView?.State.State is EngineNativePresentationState.Attaching or
                    EngineNativePresentationState.Detaching;
            bool ready = _bridge.State.IsReady;
            NativeDockToggleButton.Content = docked
                ? "Undock Allegro"
                : restorePending ? "Retry restore" : "Dock Allegro";
            NativeDockToggleButton.IsEnabled =
                !_closed && !_connecting && !transitioning && (docked || restorePending || ready);
            NativeDockToggleButton.ToolTip = docked
                ? "Return the Allegro editor to the desktop"
                : restorePending
                    ? "Retry returning the Allegro editor to the desktop"
                    : ready
                        ? "Dock the connected Allegro editor beside PD Simple tools"
                        : "Docking is unavailable until a board is connected";
            _nativePaneUndockButton.Content = restorePending ? "Retry restore" : "Undock";
            _nativePaneUndockButton.Visibility =
                docked || restorePending ? Visibility.Visible : Visibility.Collapsed;
            _nativePaneUndockButton.IsEnabled = !_nativeEntering && !_nativeLeaving;
            _nativePaneRedockButton.Visibility =
                !docked && !restorePending && _nativePane.Visibility == Visibility.Visible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            _nativePaneRedockButton.IsEnabled = !_nativeEntering && !_nativeLeaving && ready;
        }
    }

    private bool IsUserUndocked(WorkspaceDocumentIdentity document) =>
        _nativeUserUndockedDocument is not null &&
        SameDockedBoard(_nativeUserUndockedDocument, document);

    private static bool SameDockedBoard(
        WorkspaceDocumentIdentity first,
        WorkspaceDocumentIdentity second) =>
        string.Equals(first.SessionId, second.SessionId, StringComparison.Ordinal) &&
        first.SessionGeneration == second.SessionGeneration &&
        first.BoardGeneration == second.BoardGeneration &&
        first.ProcessId == second.ProcessId &&
        string.Equals(first.ProtocolVersion, second.ProtocolVersion, StringComparison.Ordinal);
}
