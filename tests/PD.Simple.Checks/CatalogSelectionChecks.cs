using System.Collections.Immutable;
using System.Xml.Linq;
using PD.PcbTools;
using PD.Simple.Tools.Catalog;

namespace PD.Simple;

/// <summary>
/// A1 catalog checks. The padstack and physical-symbol definition lists bind
/// stable typed rows; refreshes preserve the selection by definition identity
/// and a missing prior identity clears it. Runs on plain .NET: the row and
/// selection model is WPF-free, and the XAML row templates are verified
/// structurally. Live WPF selection rendering still needs GUI acceptance.
/// </summary>
internal static class CatalogSelectionChecks
{
    public static int Run()
    {
        int checks = 0;
        checks += CheckPadstackRows();
        checks += CheckPhysicalSymbolRows();
        checks += CheckRowTemplates();
        Console.WriteLine(
            $"PASS: {checks} catalog checks (typed rows, non-first selection, " +
            "identity-preserving refresh, missing-identity clear, row templates).");
        return checks;
    }

    private static int CheckPadstackRows()
    {
        int checks = 0;
        var definitions = new[]
        {
            new PadstackDefinitionSummary("PAD_A", true, 8m, true, 2, 1, 0, 0),
            new PadstackDefinitionSummary("PAD_B", true, 12m, false, 4, 3, 2, 1),
            new PadstackDefinitionSummary("PAD_C", true, null, null, 1, 0, 0, 0),
        };
        ImmutableArray<PadstackCatalogRow> rows = CatalogSelection.BuildRows(definitions);
        Require(rows.Length == 3, "Padstack row build dropped definitions.");
        checks++;

        PadstackCatalogRow selected = CatalogSelection.FindRow(rows, "PAD_B") ??
            throw new InvalidOperationException("Non-first padstack row PAD_B was not selectable.");
        Require(ReferenceEquals(selected.Summary, definitions[1]),
            "The selected padstack row did not carry its typed summary.");
        Require(selected.Name == "PAD_B", "The selected padstack row reported the wrong name.");
        Require(selected.Display.Contains("PAD_B", StringComparison.Ordinal) &&
                selected.Display.Contains("12", StringComparison.Ordinal),
            "The selected padstack row display did not show its own facts.");
        checks += 3;

        Require(selected.Detail.Contains("PAD_B", StringComparison.Ordinal) &&
                selected.Detail.Contains("12", StringComparison.Ordinal) &&
                selected.Detail.Contains("non-plated", StringComparison.Ordinal) &&
                !selected.Detail.Contains("PAD_A", StringComparison.Ordinal),
            "Padstack details did not follow the selected row.");
        Require(selected.Usage.Contains("PAD_B", StringComparison.Ordinal) &&
                selected.Usage.Contains("3 board via(s)", StringComparison.Ordinal) &&
                selected.Usage.Contains("2 board pin(s)", StringComparison.Ordinal) &&
                selected.Usage.Contains("1 symbol-definition pin(s)", StringComparison.Ordinal),
            "Padstack usage did not follow the selected row.");
        checks += 2;

        string withSelection = PadstackTool.DescribeActions(null, false, selected.Name)
            .First(action => action.Title == "Inspect instances").Reason;
        string withoutSelection = PadstackTool.DescribeActions(null, false, null)
            .First(action => action.Title == "Inspect instances").Reason;
        Require(withSelection != withoutSelection &&
                withoutSelection.Contains("Select a definition first", StringComparison.Ordinal),
            "Padstack action availability did not distinguish a selected row from no selection.");
        checks++;

        var refreshed = new[]
        {
            new PadstackDefinitionSummary("PAD_A", true, 8m, true, 2, 1, 0, 0),
            new PadstackDefinitionSummary("PAD_B", true, 14m, false, 4, 5, 2, 1),
            new PadstackDefinitionSummary("PAD_D", true, 10m, true, 2, 0, 0, 0),
        };
        string? preserved = CatalogSelection.ResolvePreservedName(
            "PAD_B", refreshed.Select(definition => definition.Name));
        Require(preserved == "PAD_B", "A refresh dropped a still-present selection identity.");
        ImmutableArray<PadstackCatalogRow> refreshedRows = CatalogSelection.BuildRows(refreshed);
        PadstackCatalogRow reselected = CatalogSelection.FindRow(refreshedRows, preserved) ??
            throw new InvalidOperationException("The preserved padstack identity did not reselect.");
        Require(reselected.Usage.Contains("5 board via(s)", StringComparison.Ordinal),
            "The preserved selection did not resolve to the refreshed row data.");
        checks += 2;

        var removed = new[]
        {
            new PadstackDefinitionSummary("PAD_A", true, 8m, true, 2, 1, 0, 0),
            new PadstackDefinitionSummary("PAD_C", true, null, null, 1, 0, 0, 0),
        };
        Require(CatalogSelection.ResolvePreservedName("PAD_B",
                removed.Select(definition => definition.Name)) is null,
            "A missing prior identity did not clear the selection.");
        Require(CatalogSelection.FindRow(CatalogSelection.BuildRows(removed), "PAD_B") is null,
            "A removed definition remained selectable.");
        Require(CatalogSelection.ResolvePreservedName(null,
                removed.Select(definition => definition.Name)) is null,
            "A null prior selection resolved to a row.");
        checks += 3;
        return checks;
    }

    private static int CheckPhysicalSymbolRows()
    {
        int checks = 0;
        var definitions = new[]
        {
            new PhysicalSymbolDefinitionSummary("SYM_A", 4, ["PAD_A"]),
            new PhysicalSymbolDefinitionSummary("SYM_B", 8, ["PAD_A", "PAD_B"]),
            new PhysicalSymbolDefinitionSummary("SYM_C", 0, []),
        };
        ImmutableArray<PhysicalSymbolCatalogRow> rows = CatalogSelection.BuildRows(definitions);
        Require(rows.Length == 3, "Physical-symbol row build dropped definitions.");
        checks++;

        PhysicalSymbolCatalogRow selected = CatalogSelection.FindRow(rows, "SYM_B") ??
            throw new InvalidOperationException("Non-first symbol row SYM_B was not selectable.");
        Require(ReferenceEquals(selected.Summary, definitions[1]),
            "The selected symbol row did not carry its typed summary.");
        Require(selected.Detail.Contains("SYM_B", StringComparison.Ordinal) &&
                selected.Detail.Contains("8 captured pin(s)", StringComparison.Ordinal) &&
                selected.Detail.Contains("PAD_A, PAD_B", StringComparison.Ordinal) &&
                !selected.Detail.Contains("SYM_A", StringComparison.Ordinal),
            "Symbol details did not follow the selected row.");
        checks += 2;

        var refreshed = new[]
        {
            new PhysicalSymbolDefinitionSummary("SYM_B", 10, ["PAD_A", "PAD_B"]),
            new PhysicalSymbolDefinitionSummary("SYM_D", 2, ["PAD_C"]),
        };
        string? preserved = CatalogSelection.ResolvePreservedName(
            "SYM_B", refreshed.Select(definition => definition.Name));
        Require(preserved == "SYM_B", "A refresh dropped a still-present symbol identity.");
        PhysicalSymbolCatalogRow reselected =
            CatalogSelection.FindRow(CatalogSelection.BuildRows(refreshed), preserved) ??
            throw new InvalidOperationException("The preserved symbol identity did not reselect.");
        Require(reselected.Detail.Contains("10 captured pin(s)", StringComparison.Ordinal),
            "The preserved symbol selection did not resolve to the refreshed row data.");
        Require(CatalogSelection.ResolvePreservedName("SYM_B", ["SYM_A"]) is null,
            "A missing prior symbol identity did not clear the selection.");
        checks += 3;
        return checks;
    }

    private static int CheckRowTemplates()
    {
        int checks = 0;
        string root = FindRepositoryRoot();
        CheckListTemplate(
            Path.Combine(root, "src", "PD.Simple", "Tools", "Padstacks", "PadstacksView.xaml"),
            "PadDefinitionList");
        checks++;
        CheckListTemplate(
            Path.Combine(root, "src", "PD.Simple", "Tools", "PhysicalSymbols", "PhysicalSymbolsView.xaml"),
            "SymDefinitionList");
        checks++;
        return checks;
    }

    private static void CheckListTemplate(string xamlPath, string listName)
    {
        XDocument xaml = XDocument.Load(xamlPath);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement list = xaml.Descendants(ns + "ListBox").FirstOrDefault(element =>
            string.Equals((string?)element.Attribute(x + "Name"), listName, StringComparison.Ordinal)) ??
            throw new InvalidOperationException($"List '{listName}' is missing from {xamlPath}.");
        bool bindsDisplay = list.Descendants(ns + "TextBlock").Any(element =>
            string.Equals((string?)element.Attribute(ns + "Text"), "{Binding Display}", StringComparison.Ordinal) ||
            string.Equals((string?)element.Attribute("Text"), "{Binding Display}", StringComparison.Ordinal));
        Require(bindsDisplay,
            $"List '{listName}' does not format its typed rows through a Display template.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.targets")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "PD.Simple")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Catalog check failed: " + message);
        }
    }
}
