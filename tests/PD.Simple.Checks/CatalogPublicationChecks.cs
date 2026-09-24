using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;
using PD.Simple.Tools.Catalog;

namespace PD.Simple;

/// <summary>
/// C1 catalog publication checks. Each catalog reads only the data families
/// its fact pipeline consumes; a capture completed after a board or session
/// switch is rejected rather than published under the new connection; and a
/// scoped read with incomplete coverage never renders as a complete catalog.
/// Native acquisition honoring still needs Allegro acceptance.
/// </summary>
internal static class CatalogPublicationChecks
{
    public static int Run()
    {
        int checks = 0;
        checks += CheckQueries();
        checks += CheckFence();
        checks += CheckCoverage();
        checks += CheckFactParity();
        Console.WriteLine(
            $"PASS: {checks} catalog publication checks (scoped queries, freshness fence, coverage, fact parity).");
        return checks;
    }

    private static int CheckQueries()
    {
        int checks = 0;
        SceneQuery padstacks = CatalogPublication.PadstackCatalogQuery();
        Require(padstacks.Kind == SceneReadKind.CompleteBoard,
            "The padstack catalog query left the CompleteBoard native path.");
        Require(padstacks.Families.SequenceEqual(CatalogPublication.RequiredPadstackFamilies),
            "The padstack catalog query does not request exactly its audited families.");
        Require(CatalogPublication.RequiredPadstackFamilies.SequenceEqual(
                [DataFamily.Padstacks, DataFamily.Symbols]),
            "The padstack family set changed without a re-audit.");
        Require(!padstacks.IncludeContours, "The padstack catalog query requests contours.");
        checks += 4;

        SceneQuery symbols = CatalogPublication.PhysicalSymbolCatalogQuery();
        Require(symbols.Kind == SceneReadKind.CompleteBoard,
            "The symbol catalog query left the CompleteBoard native path.");
        Require(symbols.Families.SequenceEqual([DataFamily.Symbols]),
            "The symbol catalog query requests more than the Symbols family.");
        Require(!symbols.IncludeContours, "The symbol catalog query requests contours.");
        Require(!symbols.Equals(padstacks), "The two catalog queries are identical.");
        checks += 4;
        return checks;
    }

    private static int CheckFence()
    {
        int checks = 0;
        var boardA = new WorkspaceDocumentIdentity(
            "session-a", 1, 7, null, @"C:\boards\conformance-a.brd", "25");
        var boardAGeneration2 = new WorkspaceDocumentIdentity(
            "session-a", 1, 8, null, @"C:\boards\conformance-a.brd", "25");
        var boardB = new WorkspaceDocumentIdentity(
            "session-b", 1, 7, null, @"C:\boards\conformance-b.brd", "25");

        Require(CatalogPublication.AcceptsPublication(boardA, boardA, true, boardA),
            "A steady capture was rejected.");
        checks++;

        Require(!CatalogPublication.AcceptsPublication(boardA, boardAGeneration2, true, boardAGeneration2),
            "A delayed read completed after a document switch was published under the new connection.");
        Require(!CatalogPublication.AcceptsPublication(boardA, boardA, true, boardAGeneration2),
            "A capture was published after the board moved to a new generation.");
        Require(!CatalogPublication.AcceptsPublication(boardA, boardB, true, boardB),
            "A delayed read was published under a renamed board.");
        Require(!CatalogPublication.AcceptsPublication(boardA, boardA, true, boardB),
            "A capture was published after switching to a renamed board.");
        checks += 4;

        Require(!CatalogPublication.AcceptsPublication(boardA, boardA, false, boardA),
            "A superseded capture was published.");
        Require(!CatalogPublication.AcceptsPublication(boardA, boardA, true, null),
            "A capture was published with no live document.");
        Require(!CatalogPublication.AcceptsPublication(null, boardA, true, boardA),
            "A capture with no requested document was published.");
        Require(CatalogPublication.AcceptsPublication(boardAGeneration2, boardAGeneration2, true, boardAGeneration2),
            "A steady capture on the second generation was rejected.");
        checks += 4;
        return checks;
    }

    private static int CheckCoverage()
    {
        int checks = 0;
        SceneData data = CatalogData();
        DesignScene scoped = CatalogScene(
            CatalogPublication.PadstackCatalogQuery(),
            CoverageFor(CatalogPublication.RequiredPadstackFamilies),
            data);
        Require(CatalogPublication.HasCompleteCoverage(
                scoped, CatalogPublication.RequiredPadstackFamilies, out string? scopedReason) &&
            scopedReason is null,
            "A complete scoped capture failed its own coverage check.");
        Require(CatalogPublication.HasCompleteCoverage(
                scoped, CatalogPublication.RequiredSymbolFamilies, out _) &&
            !CatalogPublication.HasCompleteCoverage(
                scoped, [.. CatalogPublication.RequiredPadstackFamilies, DataFamily.Routes], out string? extraReason) &&
            extraReason is not null && extraReason.Contains("Routes", StringComparison.Ordinal),
            "Coverage did not distinguish requested families from unrequested ones.");
        checks += 2;

        DesignScene partial = CatalogScene(
            CatalogPublication.PadstackCatalogQuery(),
            CoverageFor(
                [DataFamily.Symbols],
                partial: [DataFamily.Padstacks]),
            data);
        Require(!CatalogPublication.HasCompleteCoverage(
                partial, CatalogPublication.RequiredPadstackFamilies, out string? partialReason) &&
            partialReason is not null && partialReason.Contains("Padstacks", StringComparison.Ordinal),
            "A partial Padstacks family passed as a complete catalog.");
        checks++;

        DesignScene missing = CatalogScene(
            CatalogPublication.PhysicalSymbolCatalogQuery(),
            CoverageFor([]),
            CatalogData());
        Require(!CatalogPublication.HasCompleteCoverage(
                missing, CatalogPublication.RequiredSymbolFamilies, out string? missingReason) &&
            missingReason is not null && missingReason.Contains("Symbols", StringComparison.Ordinal),
            "A missing Symbols family passed as a complete catalog.");
        checks++;
        return checks;
    }

    private static int CheckFactParity()
    {
        int checks = 0;
        SceneData data = CatalogData();
        DesignScene full = CatalogScene(
            SceneQuery.CompleteBoard() with
            {
                Families = CatalogFixtureFamilies,
            },
            CoverageFor(CatalogFixtureFamilies),
            data);

        var scoped = new EngineDefinitionCatalog("catalog-scalar-fixture",
            data.Symbols.Select(symbol => new EngineSymbolCatalogDefinition(symbol.Name,
                symbol.Pins.Select(pin => new EngineSymbolCatalogPin(
                    pin.Number, pin.Padstack, pin.Position, pin.Rotation)).ToImmutableArray())).ToImmutableArray(),
            [
                new("VIA_C1_A", 10m, true, ["ETCH/TOP_C1"], 1, 1, 1),
                new("VIA_C1_B", null, null, [], 0, 0, 1),
            ],
            [new("symbols", true, 2, null), new("symbol_pins", true, 2, null),
             new("padstacks", true, 2, null), new("padstack_uses", true, 2, null)]);

        ImmutableArray<PadstackDefinitionSummary> scopedPadstacks =
            CatalogPublication.PadstackSummaries(scoped);
        ImmutableArray<PadstackDefinitionSummary> fullPadstacks =
            PadstackTool.SummarizeDefinitions(full);
        Require(scopedPadstacks.Length == 2 && scopedPadstacks.SequenceEqual(fullPadstacks),
            "The scoped padstack query changed the definition facts.");
        PadstackDefinitionSummary via = scopedPadstacks.Single(item => item.Name == "VIA_C1_A");
        Require(via is { DrillMils: 10m, Plated: true, LayerCount: 1, ViaCount: 1, PinCount: 1, SymbolPinCount: 1 },
            "The scoped padstack facts do not carry drill, plating, layer, and use counts.");
        checks += 2;

        ImmutableArray<PhysicalSymbolDefinitionSummary> scopedSymbols =
            CatalogPublication.SymbolSummaries(scoped);
        ImmutableArray<PhysicalSymbolDefinitionSummary> fullSymbols =
            PhysicalSymbolTool.SummarizeDefinitions(full);
        Require(scopedSymbols.Length == 2 &&
            scopedSymbols.Length == fullSymbols.Length &&
            scopedSymbols.Zip(fullSymbols).All(pair =>
                pair.First.Name == pair.Second.Name &&
                pair.First.PinCount == pair.Second.PinCount &&
                pair.First.HasPins == pair.Second.HasPins &&
                pair.First.Padstacks.SequenceEqual(pair.Second.Padstacks)),
            "The scoped symbol query changed the definition facts.");
        PhysicalSymbolDefinitionSummary symbol = scopedSymbols.Single(item => item.Name == "CASE_C1");
        Require(symbol is { PinCount: 2, HasPins: true } &&
            symbol.Padstacks.SequenceEqual(["VIA_C1_A", "VIA_C1_B"]),
            "The scoped symbol facts do not carry pin and padstack names.");
        checks += 2;

        Require(scoped.IsComplete && scoped.Padstacks.All(definition => !definition.HasLayerGeometry),
            "The metadata catalog manufactured definition layer geometry.");
        Require(!(scoped with { Coverage = [new("padstack_uses", false, 1, "partial")] }).IsComplete,
            "Partial scalar usage passed as a complete definition catalog.");
        Require(PadstackTool.DescribeActions(null, true, "VIA_C1_A", scoped)
                .Single(action => action.ActionId == PadstackToolActions.InspectDefinitions).Available &&
            PhysicalSymbolTool.DescribeActions(null, true, null, scoped)
                .Single(action => action.ActionId == PhysicalSymbolToolActions.InspectDefinitions).Available,
            "The scoped query changed catalog action availability.");
        checks += 3;

        DesignScene empty = CatalogScene(
            SceneQuery.CompleteBoard() with
            {
                Families = CatalogFixtureFamilies,
            },
            CoverageFor(CatalogFixtureFamilies),
            new SceneData());
        Require(PadstackTool.SummarizeDefinitions(empty).IsEmpty &&
            PhysicalSymbolTool.SummarizeDefinitions(empty).IsEmpty,
            "The empty-catalog negative control returned definitions.");
        checks++;
        return checks;
    }

    private static SceneData CatalogData()
    {
        var top = new LayerId("ETCH/TOP_C1");
        var bottom = new LayerId("ETCH/BOTTOM_C1");
        return new SceneData
        {
            Nets = [new(new("net:c1gnd"), "GND_C1", 2)],
            Components =
            [
                new(new("component:c1u7"), "U7", "CASE_C1", "PART", null, 1, new(2, 2), new(0), "placed", false),
            ],
            Pins = [new(new("pin:c1u7"), "U7", "3", "GND_C1", new(2, 2), "VIA_C1_A")],
            Layers = [new(top, 0, false, true, true), new(bottom, 1, false, true, false)],
            Padstacks =
            [
                new(new("padstack:c1a"), "VIA_C1_A", new(10), true,
                    [new(top, "regular", PolygonGeometry.Rectangle(new(new(-8, -8), new(8, 8))))]),
                new(new("padstack:c1b"), "VIA_C1_B", null, null, []),
            ],
            Copper =
            [
                new(new("copper:c1pin"), CopperKind.Pin, "GND_C1", top,
                    new(new(0, 0), new(4, 4)), null, null, null, [], null, [],
                    new("U7", "3", "VIA_C1_A", new(2, 2), true, [top, bottom], []), null),
                new(new("copper:c1via"), CopperKind.Via, "GND_C1", null,
                    new(new(10, 10), new(14, 14)), null, null,
                    new("VIA_C1_A", new(12, 12), [top, bottom], [top, bottom], "none", true),
                    [], null, []),
            ],
            Symbols =
            [
                new(new("symbol:c1case"), "CASE_C1",
                    [new("1", "VIA_C1_A", new(0, 0), new(0)),
                        new("2", "VIA_C1_B", new(10, 0), new(0))],
                    []),
                new(new("symbol:c1empty"), "PKG_EMPTY_C1", [], []),
            ],
        };
    }

    private static ImmutableArray<DataFamily> CatalogFixtureFamilies { get; } =
    [
        DataFamily.Nets,
        DataFamily.Components,
        DataFamily.Pins,
        DataFamily.Layers,
        DataFamily.Copper,
        DataFamily.Symbols,
        DataFamily.Padstacks,
    ];

    private static CoverageReport CoverageFor(
        IEnumerable<DataFamily> available,
        IEnumerable<DataFamily>? partial = null)
    {
        var availableSet = available.ToHashSet();
        var partialSet = (partial ?? []).ToHashSet();
        return new CoverageReport(Enum.GetValues<DataFamily>().Select(family =>
            partialSet.Contains(family)
                ? new FamilyCoverage(family, DataAvailability.Available, DataCompleteness.Partial,
                    GeometryFidelity.Unknown, ["The catalog fixture models this family as partial."])
                : availableSet.Contains(family)
                    ? new FamilyCoverage(family, DataAvailability.Available,
                        DataCompleteness.CompleteForRequestedScope, GeometryFidelity.AnalyticPrimitive, [])
                    : FamilyCoverage.NotRequested(family)));
    }

    private static DesignScene CatalogScene(SceneQuery query, CoverageReport coverage, SceneData data) =>
        new(new(Guid.NewGuid(), DateTimeOffset.UtcNow,
                new("catalog-fixture", "1", true, "synthetic-data-only")),
            new(DocumentKind.PcbBoard, "catalog", "mils", 2, new(new(0, 0), new(100, 100))),
            query, coverage, ProjectToQuery(query, coverage, data));

    private static SceneData ProjectToQuery(
        SceneQuery query,
        CoverageReport coverage,
        SceneData data)
    {
        bool Includes(DataFamily family) =>
            query.Families.Contains(family) &&
            coverage[family].Availability == DataAvailability.Available;
        CopperReadScope? copperScope = Includes(DataFamily.Copper)
            ? new CopperReadScope(
                query.CopperKinds,
                query.CopperKinds.Select(kind => new CopperKindCoverage(
                    kind,
                    DataAvailability.Available,
                    DataCompleteness.CompleteForRequestedScope,
                    GeometryFidelity.AnalyticPrimitive,
                    [])).ToImmutableArray())
            : null;
        return data with
        {
            Nets = Includes(DataFamily.Nets) ? data.Nets : [],
            Components = Includes(DataFamily.Components) ? data.Components : [],
            Pins = Includes(DataFamily.Pins) ? data.Pins : [],
            Xnets = Includes(DataFamily.Connectivity) ? data.Xnets : [],
            DifferentialPairs = Includes(DataFamily.Connectivity) ? data.DifferentialPairs : [],
            Layers = Includes(DataFamily.Layers) ? data.Layers : [],
            Copper = Includes(DataFamily.Copper) ? data.Copper : [],
            Modules = Includes(DataFamily.Modules) ? data.Modules : [],
            Symbols = Includes(DataFamily.Symbols) ? data.Symbols : [],
            Padstacks = Includes(DataFamily.Padstacks) ? data.Padstacks : [],
            Constraints = Includes(DataFamily.Constraints) ? data.Constraints : [],
            Drc = Includes(DataFamily.Drc) ? data.Drc : [],
            Properties = Includes(DataFamily.Properties) ? data.Properties : [],
            CopperScope = copperScope,
            BoardGeometry = Includes(DataFamily.BoardGeometry) ? data.BoardGeometry : null,
            Stackup = Includes(DataFamily.Stackup) ? data.Stackup : null,
            RouteBranches = Includes(DataFamily.Routes) ? data.RouteBranches : [],
            RoutePaths = Includes(DataFamily.Routes) ? data.RoutePaths : [],
            Contacts = Includes(DataFamily.Contacts) ? data.Contacts : [],
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Catalog publication check failed: " + message);
        }
    }
}
