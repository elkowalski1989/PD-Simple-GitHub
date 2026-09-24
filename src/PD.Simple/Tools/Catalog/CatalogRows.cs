using System.Collections.Immutable;
using System.Text;
using PD.PcbTools;

namespace PD.Simple.Tools.Catalog;

/// <summary>
/// Stable typed display row for one padstack definition. The row keeps the
/// typed <see cref="PadstackDefinitionSummary"/> for selection-sensitive
/// details, usage, planning, and actions, and exposes preformatted display
/// strings so the view never binds anonymous text.
/// </summary>
public sealed record PadstackCatalogRow(PadstackDefinitionSummary Summary)
{
    public string Name => Summary.Name;

    public string Display =>
        $"{Summary.Name}  ·  drill {(Summary.DrillMils?.ToString() ?? "unknown")}" +
        $"  ·  layers {Summary.LayerCount}  ·  use {Summary.TotalUse}";

    public string Detail => DetailText(Summary);

    public string Usage => UsageText(Summary);

    public static string DetailText(PadstackDefinitionSummary summary)
    {
        var text = new StringBuilder();
        text.Append($"Definition {summary.Name}: drill ");
        text.Append(summary.DrillMils?.ToString() ?? "unknown");
        text.Append(" mils, plating ");
        text.Append(summary.Plated is null ? "unknown" : summary.Plated.Value ? "plated" : "non-plated");
        text.Append($", {summary.LayerCount} captured layer pad(s). ");
        text.Append("A definition default pad is not proof of resolved instance copper.");
        return text.ToString();
    }

    public static string UsageText(PadstackDefinitionSummary summary)
    {
        return $"Where-used for {summary.Name} in this capture: " +
            $"{summary.ViaCount} board via(s), {summary.PinCount} board pin(s), " +
            $"{summary.SymbolPinCount} symbol-definition pin(s). " +
            "Revalidate from a fresh capture before any destructive step.";
    }
}

/// <summary>
/// Stable typed display row for one physical-symbol definition. See
/// <see cref="PadstackCatalogRow"/> for the ownership contract.
/// </summary>
public sealed record PhysicalSymbolCatalogRow(PhysicalSymbolDefinitionSummary Summary)
{
    public string Name => Summary.Name;

    public string Display =>
        $"{Summary.Name}  ·  pins {Summary.PinCount}" +
        $"  ·  padstacks {string.Join(", ", Summary.Padstacks.DefaultIfEmpty("none"))}";

    public string Detail => DetailText(Summary);

    public static string DetailText(PhysicalSymbolDefinitionSummary summary)
    {
        var text = new StringBuilder();
        text.Append($"Definition {summary.Name}: {summary.PinCount} captured pin(s); padstacks ");
        text.Append(summary.Padstacks.IsEmpty ? "unknown" : string.Join(", ", summary.Padstacks));
        text.Append(". Source verification against package dimensions and the pin manifest runs on the staged PACKAGE document, not this board capture.");
        return text.ToString();
    }
}

/// <summary>
/// Catalog selection rule, shared by the padstack and physical-symbol views.
/// Refreshes preserve the prior selection by stable definition identity
/// (<see cref="PadstackDefinitionSummary.Name"/>). A prior identity that is
/// absent from the refreshed catalog clears the selection to null so the
/// view prompts for a new pick instead of showing a stale row.
/// </summary>
public static class CatalogSelection
{
    public static string? ResolvePreservedName(string? priorName, IEnumerable<string> currentNames)
    {
        if (priorName is null)
        {
            return null;
        }
        return currentNames.Contains(priorName, StringComparer.Ordinal) ? priorName : null;
    }

    public static PadstackCatalogRow? FindRow(
        IEnumerable<PadstackCatalogRow> rows, string? name)
    {
        if (name is null)
        {
            return null;
        }
        return rows.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.Ordinal));
    }

    public static PhysicalSymbolCatalogRow? FindRow(
        IEnumerable<PhysicalSymbolCatalogRow> rows, string? name)
    {
        if (name is null)
        {
            return null;
        }
        return rows.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.Ordinal));
    }

    public static ImmutableArray<PadstackCatalogRow> BuildRows(
        IEnumerable<PadstackDefinitionSummary> definitions)
    {
        return definitions.Select(definition => new PadstackCatalogRow(definition)).ToImmutableArray();
    }

    public static ImmutableArray<PhysicalSymbolCatalogRow> BuildRows(
        IEnumerable<PhysicalSymbolDefinitionSummary> definitions)
    {
        return definitions.Select(definition => new PhysicalSymbolCatalogRow(definition)).ToImmutableArray();
    }
}
