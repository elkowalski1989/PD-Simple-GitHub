using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Interactions;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Windows;
using CircuitHub.AllegroBridge.Wpf;

namespace PD.Simple;

public sealed partial class BridgeSession
{
    private AllegroWorkspace? _engineWorkspace;

    /// <summary>
    /// Returns the high-level Engine facade over this application's already
    /// selected Allegro session. It never discovers or opens a second board.
    /// </summary>
    public ValueTask<AllegroWorkspace> GetEngineWorkspaceAsync(
        CancellationToken cancellationToken = default) =>
        RequireEngineWorkspaceAsync(cancellationToken);

    /// <summary>
    /// Acquire an immutable Engine scene through this application's already
    /// selected Allegro session. Native cost remains explicit at this call.
    /// </summary>
    public async Task<LiveDesignScene> ReadEngineSceneAsync(
        SceneQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AllegroWorkspace workspace = await RequireEngineWorkspaceAsync(cancellationToken);
        return await workspace.ReadAsync(query, cancellationToken);
    }

    public async Task EngineHighlightAsync(
        LiveDesignScene scene,
        SceneObjectReference target,
        CancellationToken cancellationToken = default)
    {
        AllegroWorkspace workspace = await RequireEngineWorkspaceAsync(cancellationToken);
        if (!scene.IsCurrent)
        {
            throw new InvalidOperationException("Acquire a fresh live Engine scene before highlighting Allegro.");
        }
        await workspace.Display.HighlightAsync(scene, target, cancellationToken);
    }

    public async Task EngineZoomAsync(
        LiveDesignScene scene,
        SceneObjectReference target,
        CancellationToken cancellationToken = default)
    {
        AllegroWorkspace workspace = await RequireEngineWorkspaceAsync(cancellationToken);
        if (!scene.IsCurrent)
        {
            throw new InvalidOperationException("Acquire a fresh live Engine scene before navigating Allegro.");
        }
        await workspace.Display.ZoomAsync(scene, target, cancellationToken);
    }

    /// <summary>
    /// Captures only the currently bound Allegro application and composes the
    /// workbench annotations into that same historical viewport. This is a
    /// review/export image, not a promise that a third-party recorder includes
    /// a separate live overlay window.
    /// </summary>
    public async ValueTask<AllegroReviewFrame> CaptureEngineReviewAsync(
        LiveDesignScene scene,
        AnnotationScene annotations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(annotations);
        var session = RequireSession();
        AllegroDesktopBinding desktop = _desktop is { IsValid: true } current
            ? current
            : throw new InvalidOperationException("The selected Allegro desktop is unavailable.");

        scene.RequireCurrent();
        if (!desktop.IsCurrentFor(session.Binding))
        {
            throw new InvalidOperationException("The selected Allegro window changed. Reconnect or reacquire before review capture.");
        }

        AllegroCanvasPixelCapture capture = await AllegroCanvasCapture.CaptureAsync(
            session, desktop, cancellationToken);
        scene.RequireCurrent();
        if (!desktop.IsCurrentFor(session.Binding))
        {
            throw new InvalidOperationException("Allegro changed while the review image was being captured.");
        }

        if (_dispatcher.CheckAccess())
        {
            return AllegroReviewFrame.Compose(capture, scene.Scene, annotations);
        }
        return await _dispatcher.InvokeAsync(() => AllegroReviewFrame.Compose(capture, scene.Scene, annotations));
    }

    public static async Task SaveEngineSceneAsync(
        string path,
        DesignScene scene,
        bool overwrite,
        CancellationToken cancellationToken = default) =>
        await SceneArchive.SaveAsync(path, scene, overwrite: overwrite, cancellationToken: cancellationToken);

    public static async Task<DesignScene> OpenEngineSceneAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        await SceneArchive.LoadAsync(path, cancellationToken);

    private async ValueTask<AllegroWorkspace> RequireEngineWorkspaceAsync(
        CancellationToken cancellationToken)
    {
        var session = RequireSession();
        AllegroWorkspace? current = _engineWorkspace;
        if (current is null || !current.IsConnected ||
            current.Document.SessionId != session.Binding.SessionId ||
            current.Document.SessionGeneration != session.Binding.SessionGeneration ||
            current.Document.BoardGeneration != session.Binding.BoardGeneration ||
            current.Document.ProcessId != session.Binding.ProcessId)
        {
            current = await AllegroWorkspace.OpenAsync(session, cancellationToken);
            _engineWorkspace = current;
        }
        return current;
    }
}
