# T11 Padstacks registration fragment (lane F proposal for the coordinator)

Lane F does not edit `MainWindow.*`, the central tool registry, package pins,
or generated registries. Apply the fragment below at integration.

## 1. Tool descriptor (central registry, coordinator-owned)

```csharp
// Proposed registration entry, reusing the frozen ToolDescriptor shape.
new ToolDescriptor(
    ToolId: PD.PcbTools.PadstackTool.Registration.ToolId,      // "tools.padstacks"
    Title: PD.PcbTools.PadstackTool.Registration.Title,        // "Padstacks"
    Category: PD.PcbTools.PadstackTool.Registration.Category,  // "Physical"
    ViewFactory: () => new PD.Simple.Tools.Padstacks.PadstacksView(),
    OfflineMode: PD.PcbTools.PadstackTool.Registration.OfflineMode)
```

Availability source: `PadstackTool.DescribeActions(scene, isLiveConnected, selectedDefinition)`.
The view never enables an action whose `Available` is false; the `Reason` and
`NextStep` strings are shown beside the action and in accessibility metadata.

## 2. Sidebar button (MainWindow.xaml, coordinator-owned)

```xml
<!-- Replaces the T11 FutureNavButton placeholder. Keeps position and label. -->
<Button Content="Padstacks" Command="{Binding OpenPadstacksCommand}" AutomationProperties.AutomationId="Nav.Padstacks"/>
```

Navigation routes through the same registry as its sidebar counterparts, then
calls `PadstacksView.ShowScene(currentCaptureOrNull, isLiveConnected)`.
No second Host, no new Engine session, no disposal of another tool's session.

## 3. Engine package dependency (coordinator-owned pin)

The view and `PD.PcbTools/PadstackTool.cs` compile against the frozen
1.13.0-preview.93 contracts. At integration, rebind the view's
definition/instance/compare/where-used display to the new lane F Engine APIs
(`EnginePadstackInspection`, `EnginePadstackWorkflows`, `EnginePadstackLibrary`
in `CircuitHub.AllegroBridge.Engine.Live`) and raise the Engine pin through
the normal coordinator release. No PD-side canonical model replaces them.
