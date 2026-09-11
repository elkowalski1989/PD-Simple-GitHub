using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Wpf.Engine;

namespace PD.Simple.Engine;

/// <summary>
/// PD Simple hosts the reusable Engine workbench instead of maintaining a second
/// explorer implementation. The application supplies its already-selected live
/// session; the workbench owns no discovery, licensing, or second transport.
/// </summary>
public partial class EngineExplorerView : UserControl, IAsyncDisposable
{
    private BridgeSession? _session;
    private bool _disposed;

    public EngineExplorerView()
    {
        InitializeComponent();
        Workbench.WorkspaceProvider = token =>
            (_session ?? throw new InvalidOperationException("Connect PD Simple to an Allegro board first."))
                .GetEngineWorkspaceAsync(token);
        Workbench.ReviewCaptureProvider = (scene, annotations, token) =>
            (_session ?? throw new InvalidOperationException("Connect PD Simple to an Allegro board first."))
                .CaptureEngineReviewAsync(scene, annotations, token);
        Workbench.BusyChanged += Workbench_StateChanged;
        Workbench.SceneChanged += Workbench_StateChanged;
        Workbench.StatusChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        RefreshState();
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

    public bool IsBusy => Workbench.IsBusy;
    public bool HasUnresolvedEdit => Workbench.HasUnresolvedEdit;
    public bool CanClose => Workbench.CanClose;
    public string StatusMessage => Workbench.StatusMessage;

    public event EventHandler? StateChanged;

    public void OpenSection(WorkbenchSection section, ObjectFamily? family = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Workbench.OpenSection(section, family);
    }

    private void Session_StateChanged(object? sender, SimpleSessionState state) =>
        Dispatcher.BeginInvoke(RefreshState);

    private void Workbench_StateChanged(object? sender, EventArgs args)
    {
        RefreshState();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshState()
    {
        if (_disposed || Workbench is null)
        {
            return;
        }
        Workbench.LiveAvailable = _session?.HasLiveNativeSession == true;
        Workbench.HostBusy = _session?.IsBusy == true;
        Workbench.RefreshHostState();
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
        await Workbench.DisposeAsync();
        _disposed = true;
    }
}
