# T10 Physical symbols registration fragment (lane E proposal for the coordinator)

Lane E does not edit `MainWindow.*`, the central tool registry, package pins,
or generated registries. Apply the fragment below at integration.

## 1. Tool descriptor (central registry, coordinator-owned)

```csharp
// Proposed registration entry, reusing the frozen ToolDescriptor shape.
new ToolDescriptor(
    ToolId: PD.PcbTools.PhysicalSymbolTool.Registration.ToolId,      // "tools.physical-symbols"
    Title: PD.PcbTools.PhysicalSymbolTool.Registration.Title,        // "Physical symbols"
    Category: PD.PcbTools.PhysicalSymbolTool.Registration.Category,  // "Physical"
    ViewFactory: () => new PD.Simple.Tools.PhysicalSymbols.PhysicalSymbolsView(),
    OfflineMode: PD.PcbTools.PhysicalSymbolTool.Registration.OfflineMode)
```

Availability source: `PhysicalSymbolTool.DescribeActions(scene, isLiveConnected, stagedSymbolName)`.
The view never enables an action whose `Available` is false; the `Reason` and
`NextStep` strings are shown beside the action and in accessibility metadata.

## 2. Sidebar button (MainWindow.xaml, coordinator-owned)

```xml
<!-- Replaces the T10 FutureNavButton placeholder. Keeps position and label. -->
<Button Content="Physical symbols" Command="{Binding OpenPhysicalSymbolsCommand}" AutomationProperties.AutomationId="Nav.PhysicalSymbols"/>
```

Navigation routes through the same registry as its sidebar counterparts, then
calls `PhysicalSymbolsView.ShowScene(currentCaptureOrNull, isLiveConnected)`
and `PhysicalSymbolsView.StageSymbol(currentStagedSymbolNameOrNull)`.
No second Host, no new Engine session, no disposal of another tool's session,
and no silent switch or close of the application PCB.

## 3. Engine package dependency (coordinator-owned pin)

The tool policy (`PD.PcbTools/PhysicalSymbolTool.cs`) compiles against the
frozen 1.13.0-preview.93 contracts (`EnginePhysicalSymbolOperation`,
`EnginePhysicalSymbolCapabilities`). At integration, rebind staging, source
verification, preview/apply, readback, and DRA publication to the new lane E
Engine APIs on Bridge `tools/e` (`EnginePhysicalSymbolExtensionDescriptor`
activation, `EngineSymbolWorkArea` / `RequireStagedDocument`,
`EnginePhysicalSymbolBinding` preview/apply with pin and geometry readback,
`EnginePhysicalSymbolPublisher`) and raise the Engine pin through the normal
coordinator release. PSM compilation stays refused per the documented vendor
limitation. No PD-side canonical model replaces them.
