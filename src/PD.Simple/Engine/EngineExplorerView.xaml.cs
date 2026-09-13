using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Interactions;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Wpf;
using CircuitHub.AllegroBridge.Wpf.Engine;

namespace PD.Simple.Engine;

/// <summary>
/// Typed host for the reusable Engine workbench. PD supplies its already-selected
/// Engine workspace and review capture; the Workbench never discovers or owns a
/// second Allegro connection.
/// </summary>
public partial class EngineExplorerView : UserControl, IAsyncDisposable
{
    private readonly EngineWorkbenchView _workbench;
    private BridgeSession? _session;
    private bool _disposed;

    public EngineExplorerView()
    {
        InitializeComponent();
        _workbench = new EngineWorkbenchView
        {
            WorkspaceProvider = ProvideWorkspaceAsync,
            ReviewCaptureProvider = ProvideReviewCaptureAsync,
        };
        WorkbenchHost.Content = _workbench;
        _workbench.BusyChanged += Workbench_StateChanged;
        _workbench.SceneChanged += Workbench_StateChanged;
        _workbench.StatusChanged += Workbench_StatusChanged;
        RefreshState();
    }

    private void Workbench_StateChanged(object? sender, EventArgs args)
    {
        RefreshState();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Workbench_StatusChanged(object? sender, string status)
    {
        RefreshState();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public BridgeSession? Session
    {
        get => _session;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_session, value))
            {
                return;
            }
            if (_session is not null)
            {
                _session.StateChanged -= Session_StateChanged;
            }
            _session = value;
            if (_session is not null)
            {
                _session.StateChanged += Session_StateChanged;
            }
            RefreshState();
        }
    }

    public bool IsBusy => _workbench.IsBusy;
    public bool HasUnresolvedEdit => _workbench.HasUnresolvedEdit;
    public bool CanClose => _workbench.CanClose;
    public bool CanSwitchNativeSession => _workbench.CanSwitchNativeSession;
    public bool CanStartNativeMutation => !IsBusy && CanSwitchNativeSession;
    public string? NativeSessionRetentionReason => _workbench.NativeSessionRetentionReason;
    public string StatusMessage => _workbench.StatusMessage;

    public event EventHandler? StateChanged;

    private ValueTask<AllegroWorkspace> ProvideWorkspaceAsync(CancellationToken token) =>
        (_session ?? throw new InvalidOperationException("Connect PD Simple to an Allegro board first."))
            .GetEngineWorkspaceAsync(token);

    private ValueTask<AllegroReviewFrame> ProvideReviewCaptureAsync(
        LiveDesignScene scene,
        AnnotationScene annotations,
        CancellationToken token) =>
        (_session ?? throw new InvalidOperationException("Connect PD Simple to an Allegro board first."))
            .CaptureEngineReviewAsync(scene, annotations, token);

    private void Session_StateChanged(object? sender, SimpleSessionState state) =>
        Dispatcher.BeginInvoke(RefreshState);

    private void RefreshState()
    {
        if (_disposed)
        {
            return;
        }
        _workbench.LiveAvailable = _session?.HasLiveNativeSession == true;
        _workbench.HostBusy = _session?.IsBusy == true;
        _workbench.RefreshHostState();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        if (_session is not null)
        {
            _session.StateChanged -= Session_StateChanged;
        }
        _session = null;
        _workbench.BusyChanged -= Workbench_StateChanged;
        _workbench.SceneChanged -= Workbench_StateChanged;
        _workbench.StatusChanged -= Workbench_StatusChanged;
        await _workbench.DisposeAsync();
        _disposed = true;
    }
}
