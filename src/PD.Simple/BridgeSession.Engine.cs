using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.Simple;

public sealed partial class BridgeSession
{
    private AllegroWorkspace? _engineWorkspace;

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
        if (!ReferenceEquals(scene.Scene, scene.Scene) || !scene.IsCurrent)
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

    public static Task SaveEngineSceneAsync(
        string path,
        DesignScene scene,
        bool overwrite,
        CancellationToken cancellationToken = default) =>
        SceneArchive.SaveAsync(path, scene, overwrite: overwrite, cancellationToken: cancellationToken);

    public static Task<DesignScene> OpenEngineSceneAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        SceneArchive.LoadAsync(path, cancellationToken);

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
