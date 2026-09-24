using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.Simple;

public sealed partial class BridgeSession
{
    public async Task<LiveDefinitionCatalog> ReadDefinitionCatalogAsync(
        SceneQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireReadyWorkspace();
        return await Workspace.ReadDefinitionCatalogAsync(query, cancellationToken);
    }
}
