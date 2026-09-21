using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Exploration;

namespace PD.Simple.Tools.Analysis;

/// <summary>
/// Lane A registration fragment for the coordinator-owned tool registry.
/// This file declares descriptors only; it does not register anything
/// globally. Section names match <c>WorkbenchSection</c> members by name;
/// the coordinator adopts these entries (target sections, view types,
/// offline support) into MainWindow and the central registry without Lane A
/// touching shared files. The descriptor stays UI-framework free on purpose.
/// </summary>
public sealed record LaneAToolDescriptor(
    string ToolId,
    string Title,
    string Section,
    ObjectFamily? Family,
    string ViewTypeName,
    bool OpensOffline,
    string OfflineNote)
{
    public static ImmutableArray<LaneAToolDescriptor> All { get; } =
        ImmutableArray.Create(
            new LaneAToolDescriptor(
                "T01", "Crossing review", "Crossings", null,
                "PD.Simple.Tools.Analysis.CrossingReviewView",
                true,
                "Opens offline; Run requires an attached capture with complete copper scope."),
            new LaneAToolDescriptor(
                "T02", "Geometry inspector", "Inspect", null,
                "PD.Simple.Tools.Analysis.GeometryInspectorView",
                true,
                "Opens offline; search and inspect run locally on the attached capture."),
            new LaneAToolDescriptor(
                "T08", "Captured scenes", "Coverage", null,
                "PD.Simple.Tools.Captures.CapturedScenesView",
                true,
                "Opens offline; archive open, replay, and export run without live authority."));
}
