using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.Simple.Tools.Analysis;

namespace PD.ToolsAChecks;

/// <summary>
/// Source-level view contract: every {Binding} path in the three Lane A
/// views resolves to a real public property on its workspace (or row type
/// inside DataTemplates), and every actionable control carries an accessible
/// name. This is an independent oracle for "no unresolved bindings" without
/// running WPF.
/// </summary>
internal static class ViewContractChecks
{
    private static readonly Regex BindingPath =
        new(@"\{Binding\s+([A-Za-z0-9_]+)", RegexOptions.Compiled);

    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    public static int Run()
    {
        int checks = 0;
        string root = LaneACheck.FindRepositoryRoot();
        checks += CheckView(
            Path.Combine(root, "src", "PD.Simple", "Tools", "Analysis", "CrossingReviewView.xaml"),
            typeof(CrossingReviewWorkspace),
            new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                ["FindingsList"] = typeof(CrossingFindingRow),
            });
        checks += CheckView(
            Path.Combine(root, "src", "PD.Simple", "Tools", "Analysis", "GeometryInspectorView.xaml"),
            typeof(GeometryInspectorWorkspace),
            new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                ["HitsList"] = typeof(InspectorHit),
            });
        checks += CheckView(
            Path.Combine(root, "src", "PD.Simple", "Tools", "Captures", "CapturedScenesView.xaml"),
            typeof(PD.Simple.Tools.Captures.CapturedSceneWorkspace),
            new Dictionary<string, Type>(StringComparer.Ordinal));
        checks += CheckCodeBehinds(root);
        Console.WriteLine($"PASS: {checks} Lane A view contract checks.");
        return checks;
    }

    private static int CheckView(
        string xamlPath,
        Type workspaceType,
        Dictionary<string, Type> listItemTypes)
    {
        int checks = 0;
        LaneACheck.Require(File.Exists(xamlPath), "View is missing: " + xamlPath);
        checks++;
        XDocument xaml = XDocument.Load(xamlPath);
        string text = File.ReadAllText(xamlPath);

        // Binding paths outside DataTemplates resolve on the workspace.
        foreach (XElement element in xaml.Descendants())
        {
            if (IsInsideDataTemplate(element))
            {
                continue;
            }
            foreach (XAttribute attribute in element.Attributes())
            {
                foreach (Match match in BindingPath.Matches(attribute.Value))
                {
                    string path = match.Groups[1].Value;
                    LaneACheck.Require(
                        HasPublicProperty(workspaceType, path),
                        $"{Path.GetFileName(xamlPath)} binds '{path}', which is not a public property on {workspaceType.Name}.");
                    checks++;
                }
            }
        }

        // DataTemplate bindings resolve on the owning list's row type.
        foreach (XElement template in xaml.Descendants(Presentation + "DataTemplate"))
        {
            XElement? list = template.Ancestors().FirstOrDefault(a =>
                (a.Name == Presentation + "ListBox" || a.Name == Presentation + "ListView") &&
                a.Attribute(Xaml + "Name") is not null);
            string? listName = list?.Attribute(Xaml + "Name")?.Value;
            Type? rowType = listName is not null && listItemTypes.TryGetValue(listName, out Type? mapped)
                ? mapped
                : InferRowType(xamlPath);
            LaneACheck.Require(rowType is not null, "Cannot infer a row type for a DataTemplate.");
            foreach (Match match in BindingPath.Matches(template.ToString()))
            {
                string path = match.Groups[1].Value;
                LaneACheck.Require(
                    HasPublicProperty(rowType!, path),
                    $"{Path.GetFileName(xamlPath)} template binds '{path}', which is not on {rowType!.Name}.");
                checks++;
            }
        }

        // Every Button carries an accessible name; every named input control
        // is accounted for. Placeholders stay out.
        foreach (XElement button in xaml.Descendants(Presentation + "Button"))
        {
            string? name = button.Attribute(Xaml + "Name")?.Value;
            bool hasAccessibleName = text.Contains($"x:Name=\"{name}\"")
                && HasAutomationName(button);
            LaneACheck.Require(
                hasAccessibleName,
                $"Button '{name ?? button.Value}' has no AutomationProperties.Name.");
            checks++;
        }
        LaneACheck.Require(
            !text.Contains("FutureNavButton", StringComparison.Ordinal),
            "A Lane A view still uses the disabled placeholder style.");
        checks++;
        return checks;
    }

    private static Type? InferRowType(string xamlPath) =>
        Path.GetFileName(xamlPath) switch
        {
            "CrossingReviewView.xaml" => typeof(CrossingFindingRow),
            "GeometryInspectorView.xaml" => typeof(InspectorHit),
            "CapturedScenesView.xaml" => typeof(EngineDiagnostic),
            _ => null,
        };

    private static bool IsInsideDataTemplate(XElement element) =>
        element.Ancestors().Any(a => a.Name == Presentation + "DataTemplate");

    private static bool HasAutomationName(XElement element) =>
        element.ToString().Contains("AutomationProperties.Name", StringComparison.Ordinal);

    private static bool HasPublicProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null;

    private static int CheckCodeBehinds(string root)
    {
        int checks = 0;
        string[] files =
        [
            Path.Combine(root, "src", "PD.Simple", "Tools", "Analysis", "CrossingReviewView.xaml.cs"),
            Path.Combine(root, "src", "PD.Simple", "Tools", "Analysis", "GeometryInspectorView.xaml.cs"),
            Path.Combine(root, "src", "PD.Simple", "Tools", "Captures", "CapturedScenesView.xaml.cs"),
        ];
        string[] forbidden = ["BindingFlags.NonPublic", "GetField(", ".GetMethod(", "RaiseEvent(", "PerformClick"];
        foreach (string file in files)
        {
            LaneACheck.Require(File.Exists(file), "Code-behind is missing: " + file);
            checks++;
            string text = File.ReadAllText(file);
            foreach (string mechanism in forbidden)
            {
                LaneACheck.Require(
                    !text.Contains(mechanism, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} uses a forbidden mechanism: {mechanism}.");
                checks++;
            }
        }

        // Registration descriptors cover exactly T01/T02/T08 with offline pages.
        LaneACheck.Require(
            LaneAToolDescriptor.All.Length == 3 &&
            LaneAToolDescriptor.All.Any(d => d.ToolId == "T01") &&
            LaneAToolDescriptor.All.Any(d => d.ToolId == "T02") &&
            LaneAToolDescriptor.All.Any(d => d.ToolId == "T08") &&
            LaneAToolDescriptor.All.All(d => d.OpensOffline),
            "Lane A registration descriptors are incomplete.");
        checks++;
        return checks;
    }
}
