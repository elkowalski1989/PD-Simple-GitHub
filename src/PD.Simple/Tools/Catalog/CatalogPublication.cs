using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.Tools.Catalog;

/// <summary>
/// Catalog acquisition scope and publication fence. Each catalog page reads
/// only the data families its fact pipeline consumes (audited against the
/// PD tools and the Engine padstack inspection they call), and a completed
/// capture is published only while it still belongs to the live document.
/// Definition catalogs use Engine's sealed, streaming metadata acquisition.
/// CompleteBoard describes coherent document scope, not copper materialization.
/// </summary>
public static class CatalogPublication
{
    /// <summary>
    /// Definition metadata and scalar usage are one native observation. Engine
    /// streams usage counts without retaining board pins or copper objects.
    /// </summary>
    public static ImmutableArray<DataFamily> RequiredPadstackFamilies { get; } =
        [
            DataFamily.Padstacks,
            DataFamily.Symbols,
        ];

    /// <summary>
    /// Families read by the Physical Symbols page: symbol names, pin counts,
    /// and per-pin padstack names.
    /// </summary>
    public static ImmutableArray<DataFamily> RequiredSymbolFamilies { get; } =
        [DataFamily.Symbols];

    public static SceneQuery PadstackCatalogQuery() =>
        SceneQuery.CompleteBoard(includeContours: false) with
        {
            Families = RequiredPadstackFamilies,
        };

    public static SceneQuery PhysicalSymbolCatalogQuery() =>
        SceneQuery.CompleteBoard(includeContours: false) with
        {
            Families = RequiredSymbolFamilies,
        };

    public static ImmutableArray<PadstackDefinitionSummary> PadstackSummaries(
        EngineDefinitionCatalog catalog) => catalog.Padstacks.Select(definition => new PadstackDefinitionSummary(
            definition.Name, true, definition.DrillMils, definition.Plated, definition.Layers.Length,
            definition.BoardViaCount, definition.BoardPinCount, definition.SymbolPinCount)).ToImmutableArray();

    public static ImmutableArray<PhysicalSymbolDefinitionSummary> SymbolSummaries(
        EngineDefinitionCatalog catalog) => catalog.Symbols.Select(definition => new PhysicalSymbolDefinitionSummary(
            definition.Name, definition.Pins.Length, definition.Pins.Select(pin => pin.Padstack)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray())).ToImmutableArray();

    /// <summary>
    /// Freshness fence for catalog publication. A capture completed after a
    /// board or session switch is rejected: the requested, completed, and
    /// live document identities must all agree, and the completed capture
    /// must still be current. A delayed read therefore cannot publish an
    /// old catalog under a new connection.
    /// </summary>
    public static bool AcceptsPublication(
        WorkspaceDocumentIdentity? requested,
        WorkspaceDocumentIdentity completed,
        bool completedIsCurrent,
        WorkspaceDocumentIdentity? current)
    {
        if (requested is null || current is null || !completedIsCurrent)
        {
            return false;
        }
        return requested.Equals(current) && completed.Equals(current);
    }

    /// <summary>
    /// True when every required family arrived complete in the capture.
    /// A scoped read that returns partial or missing families must not
    /// render as a complete catalog.
    /// </summary>
    public static bool HasCompleteCoverage(
        DesignScene scene,
        IEnumerable<DataFamily> required,
        out string? missingReason)
    {
        ArgumentNullException.ThrowIfNull(scene);
        string[] missing = required
            .Where(family => !scene.Coverage[family].IsComplete)
            .Select(family => family.ToString())
            .ToArray();
        if (missing.Length == 0)
        {
            missingReason = null;
            return true;
        }
        missingReason = "The capture is missing complete " +
            string.Join(", ", missing) + " data.";
        return false;
    }
}
