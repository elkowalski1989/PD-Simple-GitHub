using System.Windows.Controls;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf.Engine;

namespace PD.Simple.Tools.EngineWorkspace;

/// <summary>
/// Optional Engine workspace page. It borrows the application's one shared
/// Engine session and hosts the corridor screening tool through the standard
/// registered-tool path with standard parameter editors. It creates no
/// session, connects nothing implicitly, and registers no native provider:
/// native Allegro docking stays owned by the shell's native integration.
/// Close order is workspace first (honoring its close veto), then the
/// application-owned session last; the session is never disposed here.
/// </summary>
public partial class EngineWorkspacePage : UserControl, IAsyncDisposable
{
    private EngineWorkspaceView? _workspace;
    private bool _disposed;

    public EngineWorkspacePage()
    {
        InitializeComponent();
    }

    public bool IsAttached => _workspace is not null;

    public AllegroEngineSession? Session => _workspace?.Session;

    public string StatusMessage =>
        _workspace?.StatusMessage ?? "The Engine workspace is not attached.";

    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Borrows the application-owned Engine session and registers the
    /// corridor screening tool. Performs no connect, attach, or acquisition.
    /// </summary>
    public void Attach(AllegroEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_workspace is not null)
        {
            throw new InvalidOperationException(
                "The Engine workspace page is already attached.");
        }

        var workspace = new EngineWorkspaceView();
        workspace.AttachSession(session);
        if (!ReferenceEquals(workspace.Session, session))
        {
            throw new InvalidOperationException(
                "The Engine workspace did not retain the supplied session.");
        }

        workspace.RegisterTool(CorridorWorkspaceRegistration.Create());
        workspace.SelectTool(CorridorWorkspaceTool.ToolId);
        workspace.StatusChanged += Workspace_StatusChanged;
        _workspace = workspace;
        WorkspaceHost.Content = workspace;
    }

    /// <summary>
    /// Requests workspace close: drains local runs, reports whether closing
    /// is safe, and on success releases workspace resources. Never disposes
    /// the borrowed session. A refusal retains everything for a retry.
    /// </summary>
    public ValueTask<EngineWorkspaceCloseResult> RequestCloseAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_workspace is not { } workspace)
        {
            return ValueTask.FromResult(EngineWorkspaceCloseResult.Ready);
        }

        return workspace.RequestCloseAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EngineWorkspaceView? workspace = _workspace;
        _workspace = null;
        if (workspace is null)
        {
            return;
        }

        workspace.StatusChanged -= Workspace_StatusChanged;
        await workspace.DisposeAsync();
        WorkspaceHost.Content = null;
    }

    private void Workspace_StatusChanged(object? sender, string status) =>
        StatusChanged?.Invoke(this, status);
}
