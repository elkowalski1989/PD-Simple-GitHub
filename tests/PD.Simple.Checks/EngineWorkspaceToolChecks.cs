using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Interactions;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Engine.Tools;
using PD.PcbTools;
using PD.Simple.Tools.EngineWorkspace;

namespace PD.Simple;

/// <summary>
/// Focused checks for the one-tool Engine workspace migration: the corridor
/// screening registration, its validation and acquisition parity with the
/// existing <see cref="CorridorAnalyzer"/> policy, and the finding mapping.
/// Runs on plain .NET; WPF hosting is covered by the drawing checks.
/// </summary>
internal static class EngineWorkspaceToolChecks
{
    public static int Run()
    {
        int checks = 0;
        checks += CheckRegistrationContract();
        checks += CheckDefaultsAndOptions();
        checks += CheckValidationParity();
        checks += CheckPlanParity();
        checks += CheckFindingMapping();
        checks += CheckSyntheticBoardBehavior();
        checks += CheckRecipes();
        Console.WriteLine(
            $"PASS: {checks} Engine workspace corridor-registration checks " +
            "(contract, validation, acquisition, mapping, recipes).");
        return checks;
    }

    private static int CheckRegistrationContract()
    {
        int checks = 0;
        EngineToolRegistration<CorridorOptions> registration =
            CorridorWorkspaceRegistration.Create();
        ToolDescriptor descriptor = registration.Descriptor;
        Require(
            descriptor.Id == "pd.corridor-screening" &&
            descriptor.Name == "DP via corridor screening" &&
            descriptor.Version == new Version(1, 0) &&
            descriptor.Effect == ToolEffect.ReadOnly,
            "The corridor workspace tool identity is wrong.");
        checks++;
        Require(
            descriptor.Documents is [{ } kind] && kind == DocumentKind.PcbBoard,
            "The corridor workspace tool must screen PCB boards only.");
        checks++;
        Require(
            descriptor.Requirements.Select(item => item.Family).ToHashSet().SetEquals(
            [
                DataFamily.Nets,
                DataFamily.Modules,
                DataFamily.Layers,
                DataFamily.Copper,
            ]) &&
            descriptor.Requirements.All(item => !item.Complete),
            "The descriptor must require available (possibly partial) corridor families; " +
            "completeness is judged by analyzer warnings, not admission.");
        checks++;
        Require(
            registration.Selection.Kind == ToolSelectionKind.None,
            "Corridor screening is whole-board; it must not require a selection.");
        checks++;
        Require(
            registration.Parameters.Select(item => item.Key).ToArray() is
                ["marginMils", "module", "includeUnused", "pairPolicy"],
            "The corridor parameter set drifted.");
        checks++;
        ToolParameterDescriptor margin = registration.Parameters[0];
        Require(
            margin.Kind == ToolParameterKind.Decimal &&
            margin.Effect == ToolParameterEffect.LocalAnalysis &&
            margin.MinimumDecimal == 0m && margin.MaximumDecimal == 50m,
            "The margin parameter must stay a 0–50 mils local-analysis decimal.");
        checks++;
        ToolParameterDescriptor module = registration.Parameters[1];
        Require(
            module.Kind == ToolParameterKind.String &&
            module.Effect == ToolParameterEffect.Acquisition &&
            module.MaximumStringLength == 64,
            "The module parameter must stay a 64-character acquisition string.");
        checks++;
        ToolParameterDescriptor unused = registration.Parameters[2];
        Require(
            unused.Kind == ToolParameterKind.Boolean &&
            unused.Effect == ToolParameterEffect.LocalAnalysis,
            "The unused-pairs parameter must stay a local-analysis boolean.");
        checks++;
        ToolParameterDescriptor policy = registration.Parameters[3];
        Require(
            policy.Kind == ToolParameterKind.Choice &&
            policy.Effect == ToolParameterEffect.Acquisition &&
            policy.Choices.Select(item => item.Value).ToArray() is
                ["suffix-compat", "declared-pairs"],
            "The pair-policy parameter must stay an acquisition choice of the two known policies.");
        checks++;
        Require(
            registration.Presentation.ResultColumns.ToArray() is
                ["id", "pair", "aggressor", "layer", "risk"] &&
            registration.Presentation.AnnotationStyle == "standard",
            "The corridor result presentation drifted.");
        checks++;
        return checks;
    }

    private static int CheckDefaultsAndOptions()
    {
        int checks = 0;
        EngineToolRegistration<CorridorOptions> registration =
            CorridorWorkspaceRegistration.Create();
        CorridorOptions defaults = registration.CreateDefaultOptionsValue();
        Require(
            defaults.MarginMils == 0 &&
            defaults.ModuleName is null &&
            !defaults.IncludeUnused &&
            defaults.PairPolicy == CorridorPairPolicy.SuffixCompat,
            "Workspace defaults must match the existing corridor defaults.");
        checks++;
        var updated = (CorridorOptions)registration.SetOptionsValueBoxed(defaults, "marginMils", 5m);
        Require(
            updated.MarginMils == 5 && defaults.MarginMils == 0,
            "Boxed margin assignment mutated shared options.");
        checks++;
        var cleared = (CorridorOptions)registration.SetOptionsValueBoxed(
            updated with { ModuleName = "M1" }, "module", string.Empty);
        Require(
            cleared.ModuleName is null,
            "An empty module filter must normalize to no filter.");
        checks++;
        var blanked = (CorridorOptions)registration.SetOptionsValueBoxed(defaults, "module", "   ");
        Require(
            blanked.ModuleName is null,
            "A whitespace module filter must normalize to no filter.");
        checks++;
        var declared = (CorridorOptions)registration.SetOptionsValueBoxed(
            defaults, "pairPolicy", "declared-pairs");
        Require(
            declared.PairPolicy == CorridorPairPolicy.DeclaredPairs,
            "The declared-pairs choice did not map to its policy.");
        checks++;
        RequireThrows<ArgumentException>(
            () => CorridorWorkspaceRegistration.FromChoiceValue("mystery"),
            "An unknown pair-policy choice was accepted.");
        checks++;
        Require(
            CorridorWorkspaceRegistration.ToDecimalMargin(double.NaN) == 51m &&
            CorridorWorkspaceRegistration.ToDecimalMargin(double.PositiveInfinity) == 51m &&
            CorridorWorkspaceRegistration.ToDecimalMargin(2.5) == 2.5m,
            "Non-representable margins must fail validation instead of throwing from the binding.");
        checks++;
        Require(
            registration.IsPresentationOnlyChange(defaults, defaults with { }) &&
            !registration.IsPresentationOnlyChange(defaults, updated) &&
            registration.OptionsEqual(defaults, defaults with { }),
            "Options equality misclassifies execution-affecting changes.");
        checks++;
        return checks;
    }

    private static int CheckValidationParity()
    {
        int checks = 0;
        EngineToolRegistration<CorridorOptions> registration =
            CorridorWorkspaceRegistration.Create();
        CorridorOptions defaults = registration.CreateDefaultOptionsValue();
        Require(
            registration.ValidateOptions(defaults, ToolSelection.None, null)
                .All(problem => problem.Severity != EngineDiagnosticSeverity.Error),
            "Default options failed workspace validation.");
        checks++;
        Require(
            registration.ValidateOptions(
                    defaults with { MarginMils = 51 }, ToolSelection.None, null)
                .Any(problem => problem.Severity == EngineDiagnosticSeverity.Error),
            "A 51-mil margin passed workspace validation.");
        checks++;
        Require(
            registration.ValidateOptions(
                    defaults with { MarginMils = double.NaN }, ToolSelection.None, null)
                .Any(problem => problem.Severity == EngineDiagnosticSeverity.Error),
            "A non-numeric margin passed workspace validation.");
        checks++;
        Require(
            registration.ValidateOptions(
                    defaults with { ModuleName = new string('M', 65) }, ToolSelection.None, null)
                .Any(problem => problem.Severity == EngineDiagnosticSeverity.Error),
            "A 65-character module filter passed workspace validation.");
        checks++;
        DesignScene board = EngineExamples.CreateBoard("Workspace validation board");
        Require(
            registration.ValidateOptions(defaults, ToolSelection.None, board)
                .All(problem => problem.Severity != EngineDiagnosticSeverity.Error),
            "Default options failed validation against the adopted capture.");
        checks++;
        ImmutableArray<EngineDiagnostic> missingModule = registration.ValidateOptions(
            defaults with { ModuleName = "MISSING_MODULE_XQ" }, ToolSelection.None, board);
        Require(
            missingModule.Any(problem =>
                problem.Severity == EngineDiagnosticSeverity.Error &&
                problem.Message.Contains("MISSING_MODULE_XQ", StringComparison.Ordinal)),
            "An unknown module was not rejected against the adopted capture.");
        checks++;
        Require(
            registration.ValidateOptions(
                    defaults with { PairPolicy = CorridorPairPolicy.DeclaredPairs },
                    ToolSelection.None,
                    board)
                .All(problem => problem.Severity != EngineDiagnosticSeverity.Error),
            "Declared-pairs discovery failed validation on a capture carrying Connectivity.");
        checks++;
        DesignScene noConnectivity = WithoutConnectivity(board);
        Require(
            registration.ValidateOptions(
                    defaults with { PairPolicy = CorridorPairPolicy.DeclaredPairs },
                    ToolSelection.None,
                    noConnectivity)
                .Any(problem =>
                    problem.Severity == EngineDiagnosticSeverity.Error &&
                    problem.Message.Contains("Connectivity", StringComparison.Ordinal)),
            "Declared-pairs discovery passed validation without Connectivity data.");
        checks++;
        Require(
            registration.ValidateOptions(
                    defaults,
                    ToolSelection.ForRegion(new DesignBounds(new(0, 0), new(10, 10))),
                    null)
                .Any(problem => problem.Severity == EngineDiagnosticSeverity.Error),
            "A region selection passed validation for a selection-free tool.");
        checks++;
        return checks;
    }

    private static int CheckPlanParity()
    {
        int checks = 0;
        EngineToolRegistration<CorridorOptions> registration =
            CorridorWorkspaceRegistration.Create();
        CorridorOptions defaults = registration.CreateDefaultOptionsValue();
        var document = new WorkspaceDocumentIdentity("workspace-plan", 3, 9, 4242, "plan.brd", "PD_V25");
        ToolAcquisitionPlan live = registration.Plan(
            new(defaults, ToolSelection.None, document, LiveAcquisitionAvailable: true));
        SceneQuery expected = CorridorAnalyzer.CreateSceneQuery(null, CorridorPairPolicy.SuffixCompat);
        Require(
            live.Source == ToolAcquisitionSource.LiveSceneQuery &&
            live.ExpectedDocument == document &&
            QueriesMatch(live.Query, expected),
            "The live workspace plan does not reuse the audited corridor query.");
        checks++;
        Require(
            live.Requirements.Select(item => item.Family).ToHashSet().SetEquals(
            [
                DataFamily.Nets,
                DataFamily.Modules,
                DataFamily.Layers,
                DataFamily.Copper,
            ]),
            "The live suffix plan requires the wrong families.");
        checks++;
        ToolAcquisitionPlan retained = registration.Plan(
            new(defaults, ToolSelection.None, null, LiveAcquisitionAvailable: false));
        Require(
            retained.Source == ToolAcquisitionSource.RetainedCapture &&
            retained.ExpectedDocument is null &&
            retained.OfflineSourceIdentity == "pd-corridor-retained-capture" &&
            retained.HistoricalDataAcceptable &&
            QueriesMatch(retained.Query, expected),
            "The retained workspace plan does not reuse the audited corridor query.");
        checks++;
        CorridorOptions declared = defaults with { PairPolicy = CorridorPairPolicy.DeclaredPairs };
        ToolAcquisitionPlan declaredLive = registration.Plan(
            new(declared, ToolSelection.None, document, LiveAcquisitionAvailable: true));
        SceneQuery declaredQuery = CorridorAnalyzer.CreateSceneQuery(null, CorridorPairPolicy.DeclaredPairs);
        Require(
            QueriesMatch(declaredLive.Query, declaredQuery) &&
            declaredLive.Query.Families.Contains(DataFamily.Connectivity) &&
            declaredLive.Requirements.Any(item => item.Family == DataFamily.Connectivity),
            "The declared-pairs plan does not acquire Connectivity.");
        checks++;
        ToolAcquisitionPlan second = registration.Plan(
            new(defaults, ToolSelection.None, document, LiveAcquisitionAvailable: true));
        Require(
            second.Source == live.Source &&
            second.Description == live.Description &&
            QueriesMatch(second.Query, live.Query) &&
            second.Requirements.SequenceEqual(live.Requirements),
            "Workspace planning is not deterministic.");
        checks++;
        DesignScene board = EngineExamples.CreateBoard("Workspace plan assessment");
        Require(
            retained.Assess(board, null, candidateIsFreshLive: false).Eligible,
            "The retained plan rejects a complete synthetic capture: " +
            string.Join(" ", retained.Assess(board, null, false).Reasons));
        checks++;
        return checks;
    }

    private static int CheckFindingMapping()
    {
        int checks = 0;
        DesignScene board = EngineExamples.CreateBoard("Workspace mapping board");
        var finding = new CorridorFinding(
            "crossing-1", "DP0", "GND", "cline_segment", "ETCH/INNER1",
            "power", "MEDIUM", new(140, 180), new(140, 200), new(145, 190),
            1.5, 4.0, 10.0, 0, 1, 2, "ETCH/INNER1");
        var scan = new CorridorScan(
            board,
            CorridorWorkspaceRegistration.CreateDefaultOptions(),
            PairCount: 1,
            CorridorCount: 1,
            [finding],
            CoverageWarnings: ["Engine trace coverage is partial for this capture. No clear conclusion is permitted."]);
        ToolResult result = CorridorWorkspaceTool.MapScan(scan);
        Require(
            result.Findings is [{ } mapped] &&
            mapped.Id == "crossing-1" &&
            mapped.Message == "Pair DP0: MEDIUM power -- cline_segment 'GND' on ETCH/INNER1 " +
                "at 1.50 mils (width source ETCH/INNER1)." &&
            mapped.Layer == new LayerId("ETCH/INNER1") &&
            mapped.Involved.IsEmpty &&
            mapped.Annotation is { } annotation &&
            annotation.Role == AnnotationRole.Finding &&
            annotation.Geometry is PointGeometry point &&
            point.Position == new DesignPoint(145, 190),
            "The corridor finding projection lost engineering content.");
        checks++;
        Require(
            result.Annotations.CaptureId == board.Identity.CaptureId &&
            result.Annotations.Items.Contains(result.Findings[0].Annotation!),
            "The finding annotation is not a member of the result capture scene.");
        checks++;
        Require(
            result.Diagnostics.Any(item =>
                item.Severity == EngineDiagnosticSeverity.Warning &&
                item.Code == "pd.corridor.coverage" &&
                item.Message.Contains("No clear conclusion is permitted.", StringComparison.Ordinal)) &&
            result.Diagnostics.Any(item =>
                item.Code == "pd.corridor.summary" &&
                item.Message.Contains("1 pair(s)", StringComparison.Ordinal) &&
                item.Message.Contains("reference-screening-v1", StringComparison.Ordinal)) &&
            result.Diagnostics.All(item =>
                !item.Message.Contains("No problems found", StringComparison.Ordinal)),
            "Coverage warnings or the policy summary did not reach the result diagnostics.");
        checks++;

        // Changed identifiers map identically; nothing keys on the fixture names.
        var renamed = finding with
        {
            Id = "crossing-7",
            PairName = "DP9_ZQX",
            AggressorNet = "VCCX_AUX",
            Layer = "ETCH/INNER2",
            WidthSourceLayer = "ETCH/INNER2",
        };
        var renamedScan = new CorridorScan(
            board,
            CorridorWorkspaceRegistration.CreateDefaultOptions(),
            1,
            1,
            [renamed],
            []);
        ToolResult renamedResult = CorridorWorkspaceTool.MapScan(renamedScan);
        Require(
            renamedResult.Findings is [{ } renamedMapped] &&
            renamedMapped.Message == "Pair DP9_ZQX: MEDIUM power -- cline_segment 'VCCX_AUX' " +
                "on ETCH/INNER2 at 1.50 mils (width source ETCH/INNER2)." &&
            renamedMapped.Layer == new LayerId("ETCH/INNER2"),
            "The finding projection depends on fixture identifiers.");
        checks++;

        // Negative control: empty findings with warnings stay review-required,
        // never a clean result.
        var emptyScan = new CorridorScan(
            board,
            CorridorWorkspaceRegistration.CreateDefaultOptions(),
            0,
            0,
            [],
            ["Pad and antipad measurements are unavailable for pair DP0 on ETCH/INNER1; " +
             "its width uses the reference estimate."]);
        ToolResult empty = CorridorWorkspaceTool.MapScan(emptyScan);
        Require(
            empty.Findings.IsEmpty &&
            empty.Annotations.Items.IsEmpty &&
            empty.Diagnostics.Any(item => item.Severity == EngineDiagnosticSeverity.Warning),
            "An empty partial scan was projected as a clean result.");
        checks++;

        var unattributed = finding with { Layer = "  " };
        var unattributedScan = new CorridorScan(
            board,
            CorridorWorkspaceRegistration.CreateDefaultOptions(),
            1,
            1,
            [unattributed],
            []);
        Require(
            CorridorWorkspaceTool.MapScan(unattributedScan).Findings[0].Layer is null,
            "A blank finding layer was not left unattributed.");
        checks++;
        return checks;
    }

    private static int CheckSyntheticBoardBehavior()
    {
        int checks = 0;
        DesignScene board = EngineExamples.CreateBoard("Workspace behavior board");
        CorridorOptions options = CorridorWorkspaceRegistration.CreateDefaultOptions();
        CorridorScan scan = CorridorAnalyzer.Analyze(board, options);
        var tool = new CorridorWorkspaceTool();
        ToolResult result = SceneToolRunner.RunAsync(
            tool,
            new ToolContext(board),
            options).AsTask().GetAwaiter().GetResult();
        Require(
            result.Findings.Select(item => item.Id).SequenceEqual(
                scan.Findings.Select(item => item.Id)) &&
            result.Annotations.CaptureId == board.Identity.CaptureId &&
            result.Annotations.Items.Length == Math.Min(scan.Findings.Count, 10_000),
            "The workspace tool diverged from CorridorAnalyzer on the retained fixture.");
        checks++;
        Require(
            result.Diagnostics.Count(item => item.Code == "pd.corridor.coverage") ==
                scan.CoverageWarnings.Count &&
            result.Diagnostics.Any(item => item.Code == "pd.corridor.summary"),
            "The workspace tool dropped analyzer coverage evidence on the retained fixture.");
        checks++;
        return checks;
    }

    private static int CheckRecipes()
    {
        int checks = 0;
        var catalog = new ToolCatalog();
        EngineToolRegistration<CorridorOptions> registration =
            CorridorWorkspaceRegistration.Create();
        catalog.Register(registration);
        CorridorOptions defaults = registration.CreateDefaultOptionsValue();
        var saved = (CorridorOptions)registration.SetOptionsValueBoxed(defaults, "marginMils", 2.5m);
        saved = (CorridorOptions)registration.SetOptionsValueBoxed(saved, "includeUnused", true);
        ToolRecipe recipe = registration.CaptureRecipe(
            saved, "Saved screening", ToolSelection.None, true, "standard");
        var restored = (CorridorOptions)recipe.ApplyTo(catalog);
        Require(
            registration.OptionsEqual(restored, saved),
            "A saved corridor recipe did not restore its options.");
        checks++;
        ImmutableArray<EngineDiagnostic> viaRecipe = registration.ValidateOptions(
            restored, ToolSelection.None, null);
        ImmutableArray<EngineDiagnostic> viaProgram = registration.ValidateOptions(
            saved, ToolSelection.None, null);
        Require(
            viaRecipe.Length == viaProgram.Length,
            "Recipe and programmatic validation diverge.");
        checks++;
        Require(
            registration.IsPresentationOnlyChange(
                saved,
                (CorridorOptions)registration.SetOptionsValueBoxed(saved, "marginMils", 2.5m)) &&
            !registration.IsPresentationOnlyChange(
                saved,
                (CorridorOptions)registration.SetOptionsValueBoxed(saved, "marginMils", 3m)),
            "Presentation-only detection misclassifies margin edits.");
        checks++;
        var bad = new ToolRecipe(
            ToolRecipeDocument.CurrentSchemaVersion,
            "pd.corridor-screening",
            new Version(1, 0),
            "Bad screening",
            ImmutableDictionary.Create<string, ToolRecipeValue>(StringComparer.Ordinal)
                .Add("marginMils", new(ToolParameterKind.Decimal, "999"))
                .Add("mystery", new(ToolParameterKind.String, "\"x\"")),
            new(ToolSelectionKind.None, []),
            new(ResultsExpanded: false, AnnotationStyle: "standard"));
        RequireThrows<InvalidDataException>(
            () => bad.ApplyTo(catalog),
            "An invalid corridor recipe was applied.");
        checks++;
        var stale = new ToolRecipe(
            ToolRecipeDocument.CurrentSchemaVersion,
            "pd.corridor-screening",
            new Version(0, 9),
            "Stale screening",
            ImmutableDictionary.Create<string, ToolRecipeValue>(StringComparer.Ordinal),
            new(ToolSelectionKind.None, []),
            new(ResultsExpanded: false, AnnotationStyle: "standard"));
        RequireThrows<InvalidDataException>(
            () => stale.ApplyTo(catalog),
            "A stale corridor recipe without a migration was applied.");
        checks++;
        return checks;
    }

    private static DesignScene WithoutConnectivity(DesignScene board)
    {
        var coverage = new CoverageReport(board.Coverage.Families.Select(family =>
            family.Family == DataFamily.Connectivity
                ? new FamilyCoverage(
                    DataFamily.Connectivity,
                    DataAvailability.NotRequested,
                    DataCompleteness.Partial)
                : family));
        SceneData data = board.Data with { Xnets = [], DifferentialPairs = [] };
        return new DesignScene(
            new(Guid.NewGuid(), DateTimeOffset.UtcNow, board.Identity.Provenance),
            board.Document,
            board.Query,
            coverage,
            data);
    }

    private static bool QueriesMatch(SceneQuery first, SceneQuery second) =>
        first.Kind == second.Kind &&
        first.Families.SequenceEqual(second.Families) &&
        first.CopperKinds.SequenceEqual(second.CopperKinds) &&
        first.IncludeContours == second.IncludeContours &&
        first.MaximumObjects == second.MaximumObjects &&
        first.Module == second.Module;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Engine workspace tool check failed: " + message);
        }
    }

    private static void RequireThrows<TException>(Func<object?> action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("Engine workspace tool check failed: " + message);
    }
}
