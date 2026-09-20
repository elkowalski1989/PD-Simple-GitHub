using System.Collections.Immutable;
using System.Xml.Linq;
using CircuitHub.AllegroBridge.Engine.Analysis;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.Simple;

/// <summary>
/// Lane A (tools 1, 2, 3, 8) checks. Runs on plain .NET without Allegro:
/// sidebar wiring is verified from XAML source, the thin Workbench
/// forwarding from PD source contract, and request/analysis behavior through
/// public offline Engine APIs only. Live native gates remain NOT_EXECUTED
/// without a licensed Allegro session.
/// </summary>
internal static class LaneAToolsChecks
{
    public static int Run()
    {
        int checks = 0;
        checks += CheckSidebarWiring();
        checks += CheckForwardingContract();
        checks += CheckQualifiedGeometry();
        checks += CheckExplicitRequestScope();
        Console.WriteLine(
            $"PASS: {checks} Lane A tool checks (Crossing review, Geometry inspector, " +
            "Pick/measure/ruler, Captured scenes navigation, forwarding, qualified " +
            "offline geometry, explicit request scope).");
        return checks;
    }

    private static int CheckSidebarWiring()
    {
        string xamlPath = Path.Combine(
            FindRepositoryRoot(), "src", "PD.Simple", "MainWindow.xaml");
        XDocument xaml = XDocument.Load(xamlPath);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var buttons = xaml.Descendants(ns + "Button").ToList();
        Require(buttons.Count > 0, "MainWindow.xaml declares no sidebar buttons.");

        (string Name, string Click)[] wired =
        [
            ("CrossingMenuButton", "Crossing_Click"),
            ("InspectorMenuButton", "Inspector_Click"),
            ("MeasureMenuButton", "Measure_Click"),
            ("ScenesMenuButton", "Scenes_Click"),
        ];
        foreach ((string name, string click) in wired)
        {
            XElement button = buttons.FirstOrDefault(b =>
                string.Equals((string?)b.Attribute(x + "Name"), name, StringComparison.Ordinal)) ??
                throw new InvalidOperationException(
                    $"Lane A button '{name}' is missing from MainWindow.xaml.");
            Require(
                string.Equals((string?)button.Attribute("Click"), click, StringComparison.Ordinal),
                $"Lane A button '{name}' does not route to '{click}'.");
            Require(
                ((string?)button.Attribute("Style") ?? string.Empty).Contains("NavButton") &&
                !((string?)button.Attribute("Style") ?? string.Empty).Contains("FutureNavButton"),
                $"Lane A button '{name}' still uses the disabled placeholder style.");
            Require(
                button.ToString().Contains("AutomationProperties.Name", StringComparison.Ordinal),
                $"Lane A button '{name}' has no accessible name.");
        }

        string[] laneALabels = ["Crossing review", "Geometry inspector", "Pick / measure", "Captured scenes"];
        var placeholders = buttons
            .Where(b => ((string?)b.Attribute("Style") ?? string.Empty).Contains("FutureNavButton"))
            .Select(b => (string?)b.Attribute("Content") ?? b.Value)
            .ToList();
        foreach (string label in laneALabels)
        {
            Require(
                placeholders.All(text => !text.Contains(label, StringComparison.Ordinal)),
                $"Lane A tool '{label}' is still a disabled placeholder.");
        }
        string[] otherLaneLabels =
        [
            "Placement handles", "Via / route editing", "Live overlay tools",
            "Share / review view", "Constraints / DRC", "Physical symbols",
            "Padstacks", "Manufacturing",
        ];
        Require(
            placeholders.Count <= otherLaneLabels.Length,
            $"Expected at most the {otherLaneLabels.Length} other-lane placeholders, found {placeholders.Count}.");
        foreach (string text in placeholders)
        {
            Require(
                otherLaneLabels.Any(label => text.Contains(label, StringComparison.Ordinal)),
                $"Unknown sidebar placeholder '{text}'.");
        }

        return wired.Length + 2;
    }

    private static int CheckForwardingContract()
    {
        string root = FindRepositoryRoot();
        string forwarding = File.ReadAllText(Path.Combine(
            root, "src", "PD.Simple", "Engine", "EngineExplorerView.xaml.cs"));
        Require(
            forwarding.Contains(
                "public void OpenSection(WorkbenchSection section, ObjectFamily? family = null)",
                StringComparison.Ordinal),
            "EngineExplorerView does not expose the thin OpenSection forwarding method.");
        Require(
            forwarding.Contains("_workbench.OpenSection(section, family)", StringComparison.Ordinal),
            "EngineExplorerView.OpenSection does not delegate to the shared Workbench.");
        Require(
            forwarding.Contains("InvalidOperationException", StringComparison.Ordinal),
            "EngineExplorerView.OpenSection hides a missing Workbench instead of reporting it.");

        string navigation = File.ReadAllText(Path.Combine(
            root, "src", "PD.Simple", "MainWindow.xaml.cs"));
        Require(
            navigation.Contains("ExplorerView.OpenSection", StringComparison.Ordinal) &&
            navigation.Contains("ShowWorkbenchSection", StringComparison.Ordinal),
            "MainWindow does not route Lane A tools through central Workbench navigation.");
        string[] sections = ["WorkbenchSection.Crossings", "WorkbenchSection.Inspect", "WorkbenchSection.Measure", "WorkbenchSection.Coverage"];
        foreach (string section in sections)
        {
            Require(
                navigation.Contains(section, StringComparison.Ordinal),
                $"MainWindow never navigates to {section}.");
        }
        int forwardingCount = forwarding.Split(
            "public void OpenSection", StringSplitOptions.None).Length - 1;
        Require(
            forwardingCount == 1,
            $"Expected exactly one OpenSection forwarding method, found {forwardingCount}.");
        // Scope the forbidden-mechanism scan to the Lane A code: the
        // OpenSection forwarding body (the file's pre-existing
        // AttachPresentation legitimately constructs the one shared
        // Workbench) and MainWindow's central navigation.
        int bodyStart = forwarding.IndexOf("public void OpenSection", StringComparison.Ordinal);
        int nextPublic = forwarding.IndexOf("\n    public ", bodyStart + 1, StringComparison.Ordinal);
        int nextPrivate = forwarding.IndexOf("\n    private ", bodyStart + 1, StringComparison.Ordinal);
        int bodyEnd = new[] { nextPublic, nextPrivate }
            .Where(index => index > bodyStart)
            .DefaultIfEmpty(forwarding.Length)
            .Min();
        string forwardingBody = forwarding.Substring(bodyStart, bodyEnd - bodyStart);
        string navigationBody = navigation.Substring(
            navigation.IndexOf("private void ShowWorkbenchSection", StringComparison.Ordinal));
        string[] forbiddenMechanisms = ["BindingFlags.NonPublic", "GetField(", ".GetMethod(", "RaiseEvent(", "PerformClick", "new AllegroEngineSession", "new EngineWorkbenchView"];
        foreach (string forbidden in forbiddenMechanisms)
        {
            Require(
                !navigationBody.Contains(forbidden, StringComparison.Ordinal) &&
                !forwardingBody.Contains(forbidden, StringComparison.Ordinal),
                $"Lane A navigation uses a forbidden mechanism: {forbidden}.");
        }

        return 6;
    }

    private static int CheckQualifiedGeometry()
    {
        // Mil/mm equivalence (inspector local/board coordinates).
        Length thousandMils = Length.FromMillimeters(25.4m);
        Require(thousandMils.Mils == 1000m, "25.4 mm did not equal 1000 mil.");
        DesignPoint origin = DesignPoint.From(0m, 0m, LengthUnit.Mils);
        DesignPoint milMark = DesignPoint.From(1000m, 0m, LengthUnit.Mils);
        Require(
            origin.DistanceTo(milMark).Mils == 1000m,
            "Point-to-point ruler distance disagrees with expected geometry.");

        var kernel = new GeometryKernel();
        var lower = new LineGeometry(
            DesignPoint.From(0m, 0m, LengthUnit.Mils),
            DesignPoint.From(100m, 0m, LengthUnit.Mils));
        var upper = new LineGeometry(
            DesignPoint.From(0m, 10m, LengthUnit.Mils),
            DesignPoint.From(100m, 10m, LengthUnit.Mils));
        DistanceResult gap = kernel.Distance(lower, upper);
        Require(gap.Distance.Mils == 10m, "Parallel-line gap is not the expected 10 mil.");
        Require(gap.ErrorBound.Mils >= 0m, "Qualified distance carries a negative error bound.");

        // Known crossing geometry: diagonals meet at (5, 5).
        var rising = new LineGeometry(
            DesignPoint.From(0m, 0m, LengthUnit.Mils),
            DesignPoint.From(10m, 10m, LengthUnit.Mils));
        var falling = new LineGeometry(
            DesignPoint.From(0m, 10m, LengthUnit.Mils),
            DesignPoint.From(10m, 0m, LengthUnit.Mils));
        IntersectionResult crossing = kernel.Intersect(rising, falling);
        Require(
            crossing.Relation == GeometricRelation.Intersects,
            "Known line crossing was not reported as an intersection.");
        Require(
            crossing.Qualification == RelationQualification.ExactLinear,
            "Linear crossing lost its exact qualification.");
        Require(
            !crossing.Witnesses.IsDefaultOrEmpty,
            "Crossing witnesses are missing instead of explicit.");
        DesignPoint witness = crossing.Witnesses[0];
        Require(
            Math.Abs(witness.X - 5m) <= 0.001m && Math.Abs(witness.Y - 5m) <= 0.001m,
            $"Crossing witness ({witness.X}, {witness.Y}) is not the expected (5, 5).");

        // Missing crossing stays explicit: parallel lines are disjoint, never
        // an empty success.
        IntersectionResult disjoint = kernel.Intersect(lower, upper);
        Require(
            disjoint.Relation == GeometricRelation.Disjoint,
            "Parallel lines were not reported disjoint.");

        // Ruler length: 3-4-5 triangle hypotenuse is exactly 5000 mil = 127 mm.
        var hypotenuse = new LineGeometry(
            DesignPoint.From(0m, 0m, LengthUnit.Mils),
            DesignPoint.From(3000m, 4000m, LengthUnit.Mils));
        GeometryLengthResult ruler = kernel.MeasureLength(hypotenuse);
        Require(ruler.Length.Mils == 5000m, "Ruler length is not the expected 5000 mil.");
        Require(
            ruler.Length.In(LengthUnit.Millimeters) == 127m,
            "Ruler length does not convert to the expected 127 mm.");

        // Zero-length edge input is measured as zero, not rejected silently.
        var degenerate = new LineGeometry(origin, origin);
        Require(
            kernel.MeasureLength(degenerate).Length.Mils == 0m,
            "Zero-length input was not measured as zero.");

        return 9;
    }

    private static int CheckExplicitRequestScope()
    {
        // Crossing request carries its explicit rule/layer/corridor/representation.
        var corridor = new LineGeometry(
            DesignPoint.From(0m, 0m, LengthUnit.Mils),
            DesignPoint.From(100m, 100m, LengthUnit.Mils));
        var query = new CrossingQuery(
            "lane-a-test",
            new LayerId("ETCH/S04"),
            corridor,
            ImmutableArray<string>.Empty,
            CrossingRepresentation.TraceCenterline,
            true,
            false,
            100,
            10_000,
            GeometryPolicy.Default);
        Require(query.RuleId == "lane-a-test", "Crossing request lost its rule identity.");
        Require(query.Layer == new LayerId("ETCH/S04"), "Crossing request lost its layer scope.");
        Require(
            query.Representation == CrossingRepresentation.TraceCenterline,
            "Crossing request lost its declared representation.");
        Require(
            ReferenceEquals(query.Corridor, corridor),
            "Crossing request does not retain its explicit corridor scope.");

        // Capture request carries explicit region/families/budget; contours
        // stay off unless the caller opts in.
        var sceneQuery = new SceneQuery
        {
            Region = new DesignBounds
            {
                Minimum = DesignPoint.From(0m, 0m, LengthUnit.Mils),
                Maximum = DesignPoint.From(1000m, 1000m, LengthUnit.Mils),
            },
            Families = ImmutableArray.Create(DataFamily.Copper, DataFamily.Nets),
            MaximumObjects = 1000,
        };
        Require(sceneQuery.Region.HasValue, "Capture request lost its explicit region.");
        Require(
            sceneQuery.Region!.Value.Minimum == DesignPoint.From(0m, 0m, LengthUnit.Mils),
            "Capture region minimum moved.");
        Require(
            sceneQuery.Families.Contains(DataFamily.Copper),
            "Capture request lost its explicit family scope.");
        Require(sceneQuery.MaximumObjects == 1000, "Capture request lost its object budget.");
        Require(!sceneQuery.IncludeContours, "Contour detail must be an explicit opt-in.");

        return 7;
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
        throw new InvalidOperationException("Could not locate the PD Simple repository root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Lane A check failed: " + message);
        }
    }
}
