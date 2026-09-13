using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Interactions;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.Simple;

public sealed partial class BridgeSession
{
    /// <summary>
    /// Returns the stable workspace owned by this application's Engine session.
    /// No second session or workspace is created.
    /// </summary>
    public ValueTask<AllegroWorkspace> GetEngineWorkspaceAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireReadyWorkspace();
        return ValueTask.FromResult(Workspace);
    }

    /// <summary>
    /// Acquire an immutable Engine scene through this application's already
    /// selected Allegro session. Native cost remains explicit at this call.
    /// </summary>
    public async Task<LiveDesignScene> ReadEngineSceneAsync(
        SceneQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireReadyWorkspace();
        return await Workspace.ReadAsync(query, cancellationToken);
    }

    public async Task EngineHighlightAsync(
        LiveDesignScene scene,
        SceneObjectReference target,
        CancellationToken cancellationToken = default)
    {
        RequireReadyWorkspace();
        if (!scene.IsCurrent)
        {
            throw new InvalidOperationException("Acquire a fresh live Engine scene before highlighting Allegro.");
        }
        await Workspace.Display.HighlightAsync(scene, target, cancellationToken);
    }

    public async Task EngineZoomAsync(
        LiveDesignScene scene,
        SceneObjectReference target,
        CancellationToken cancellationToken = default)
    {
        RequireReadyWorkspace();
        if (!scene.IsCurrent)
        {
            throw new InvalidOperationException("Acquire a fresh live Engine scene before navigating Allegro.");
        }
        await Workspace.Display.ZoomAsync(scene, target, cancellationToken);
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

    private void RequireReadyWorkspace()
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        if (EngineSession.State.ConnectionState != EngineConnectionState.Ready ||
            !Workspace.IsConnected)
        {
            throw new InvalidOperationException(
                State.UnavailableDetail ??
                "A ready Allegro Engine session is required.");
        }
    }
}
