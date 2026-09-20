using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf.Engine;

namespace PD.Simple.Engine;

/// <summary>
/// Hosts the reusable Workbench over the application's one shared Engine/WPF
/// presentation. The Workbench borrows both owners and never creates or
/// disposes another Engine session.
/// </summary>
public partial class EngineExplorerView : UserControl, IAsyncDisposable
{
    private EngineWorkbenchView? _workbench;
    private bool _hostBusy;
    private bool _disposed;

    public EngineExplorerView()
    {
        InitializeComponent();
    }

    public AllegroEngineSession? Session => _workbench?.Session;

    public bool HostBusy
    {
        get => _hostBusy;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hostBusy == value)
            {
                return;
            }
            _hostBusy = value;
            RefreshState();
        }
    }

    public bool IsBusy => _workbench?.IsBusy == true;

    public bool HasUnresolvedEdit => _workbench?.HasUnresolvedEdit == true;

    public bool CanClose => _workbench?.CanClose ?? true;

    public bool CanSwitchSession => _workbench?.CanSwitchNativeSession == true;

    public bool CanStartMutation => !IsBusy && CanSwitchSession;

    public string? SessionRetentionReason =>
        _workbench?.NativeSessionRetentionReason;

    public string StatusMessage =>
        _workbench?.StatusMessage ?? "Engine Workbench presentation is not attached.";

    public event EventHandler? StateChanged;

    public void AttachPresentation(EngineWpfPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_workbench is not null)
        {
            throw new InvalidOperationException(
                "The Engine Workbench presentation is already attached.");
        }

        var workbench = new EngineWorkbenchView(presentation);
        if (!ReferenceEquals(workbench.Session, presentation.Session))
        {
            throw new InvalidOperationException(
                "The Engine Workbench did not retain the supplied presentation session.");
        }

        _workbench = workbench;
        WorkbenchHost.Content = workbench;
        workbench.BusyChanged += Workbench_StateChanged;
        workbench.SceneChanged += Workbench_StateChanged;
        workbench.StatusChanged += Workbench_StatusChanged;
        RefreshState();
    }

    /// <summary>
    /// Thin PD forwarding over the shared Engine Workbench's public section
    /// navigation. Routes sidebar tool entries to the already-attached
    /// Workbench without creating another session or touching internals.
    /// </summary>
    public void OpenSection(WorkbenchSection section, ObjectFamily? family = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_workbench is null)
        {
            throw new InvalidOperationException(
                "The shared Engine presentation is not attached.");
        }

        _workbench.OpenSection(section, family);
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

    private void RefreshState()
    {
        if (_disposed || _workbench is not { } workbench)
        {
            return;
        }

        workbench.HostBusy = _hostBusy;
        workbench.RefreshHostState();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        EngineWorkbenchView? workbench = _workbench;
        _workbench = null;
        if (workbench is null)
        {
            return;
        }

        workbench.BusyChanged -= Workbench_StateChanged;
        workbench.SceneChanged -= Workbench_StateChanged;
        workbench.StatusChanged -= Workbench_StatusChanged;
        await workbench.DisposeAsync();
        WorkbenchHost.Content = null;
    }
}
