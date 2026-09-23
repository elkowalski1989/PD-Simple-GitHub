namespace PD.Simple.Corridor;

/// <summary>
/// WPF-free corridor findings exploration over the complete in-memory
/// finding set: risk/layer/search filtering, layer derivation, paging, and
/// selection identity. The workspace view model delegates to these pure
/// functions so the same behavior is verifiable on plain .NET. Filtering
/// always runs before paging, and the UI page holds only its own rows.
/// </summary>
public static class DpViaCorridorFindingExplorer
{
    public const string AllRisks = "All risks";

    public const string AllLayers = "All layers";

    public static List<DpViaCorridorFinding> ApplyFilters(
        IEnumerable<DpViaCorridorFinding> findings,
        string riskFilter,
        string layerFilter,
        string searchText)
    {
        string query = searchText.Trim();
        var filtered = new List<DpViaCorridorFinding>();
        foreach (DpViaCorridorFinding finding in findings)
        {
            if (riskFilter != AllRisks && finding.Risk != riskFilter)
            {
                continue;
            }
            if (layerFilter != AllLayers && finding.Layer != layerFilter)
            {
                continue;
            }
            if (query.Length > 0 && !MatchesSearch(finding, query))
            {
                continue;
            }
            filtered.Add(finding);
        }
        return filtered;
    }

    public static bool MatchesSearch(DpViaCorridorFinding finding, string query) =>
        finding.PairName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        finding.AggressorNet.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        finding.Layer.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        finding.Category.Contains(query, StringComparison.OrdinalIgnoreCase);

    public static List<string> DeriveLayers(IEnumerable<DpViaCorridorFinding> findings) =>
        findings
            .Select(finding => finding.Layer)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(layer => layer, StringComparer.Ordinal)
            .ToList();

    public static int PageCount(int filteredCount, int pageSize) =>
        Math.Max(1, (filteredCount + pageSize - 1) / pageSize);

    public static List<DpViaCorridorFinding> GetPage(
        IReadOnlyList<DpViaCorridorFinding> filtered, int page, int pageSize)
    {
        int clamped = Math.Clamp(page, 1, PageCount(filtered.Count, pageSize));
        return filtered
            .Skip((clamped - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    /// <summary>
    /// Selection identity across paging and filtering. A prior identity on
    /// the current page stays selected; a prior identity still in the
    /// filtered set but on another page is preserved; a prior identity that
    /// left the filtered set (or no prior identity) selects the first row of
    /// the current page, or nothing when the page is empty.
    /// </summary>
    public static string? ResolveSelectedId(
        IReadOnlyList<DpViaCorridorFinding> filtered,
        IReadOnlyList<DpViaCorridorFinding> pageItems,
        string? priorId)
    {
        if (priorId is not null && pageItems.Any(item => item.Id == priorId))
        {
            return priorId;
        }
        if (priorId is null || !filtered.Any(item => item.Id == priorId))
        {
            return pageItems.FirstOrDefault()?.Id;
        }
        return priorId;
    }
}
