# P0-A PD Engine-boundary handoff

## Identity

- Base: `d501212805384400c17bfe220be46a05be172a55`
- Branch: `p0/pd-engine-boundary`
- Lane implementation commit: `53e746f1d6a87f38dc724b0cc071c324c8f165b6`
- Branch head: the commit containing this handoff; use the pushed branch identity
  reported by the lane owner because a commit cannot embed its own object ID.

## Completed

- Replaced the migration allowlist with a zero-seam boundary over all maintained
  production, test, and sample C# plus project package/assembly references.
- Added an emitted-assembly dependency gate for `PD.PcbTools.dll` and
  `PD.Simple.dll`; both must directly consume Engine, neither may directly bind a
  lower SDK/runtime layer, and reusable `PD.PcbTools` may not bind WPF.
- Removed excluded legacy lower-SDK product files
  `src/PD.Simple/SimpleToolExtension.cs` and
  `src/PD.Simple/InteractiveRouteRecovery.cs` and the excluded Board Explorer
  `MainWindow*` implementation. Their exact prior contents remain recoverable at
  base `d501212805384400c17bfe220be46a05be172a55`; no compiled behavior used them.
- Removed the now-stale compile/page exclusions and updated product, package,
  contribution, dependency, acceptance, and Board Explorer documentation to the
  Engine-first package-only boundary.
- Preserved source-only native comparison material under `src/PD.Simple/Skill`.
  No native SKILL, geometry/model/archive, PCB DTO, or composition-sample ownership
  was changed.

## Verification

All results below are from the P0-A worktrees.

- `python3 scripts/check-engine-boundary.py`
  — passed: zero ordinary lower-SDK source/package/assembly seams across 43
  maintained C# files and 12 production/test/sample projects.
- `/home/emilekowalski/.dotnet/dotnet run --project tests/PD.EngineBoundaryChecks/PD.EngineBoundaryChecks.csproj -c Release --no-launch-profile`
  — emitted `PD.PcbTools` and `PD.Simple` passed with zero direct lower-SDK
  dependencies; reusable policy remained WPF-free.
- `/home/emilekowalski/.dotnet/dotnet build src/PD.Simple/PD.Simple.csproj -c Release --nologo -m:1 -tl:off`
  — package-only build passed with 0 warnings and 0 errors.
- `/home/emilekowalski/.dotnet/dotnet run --project tests/PD.Simple.Checks/PD.Simple.Checks.csproj -c Release --no-launch-profile`
  — 35 target-selection/state/busy/uncertainty/recovery/diagnostic checks, public
  Engine discovery/session lifetime, and route-completion evidence checks passed.
- `/home/emilekowalski/.dotnet/dotnet run --project tests/PD.PcbTools.Checks/PD.PcbTools.Checks.csproj -c Release --no-launch-profile`
  — 70 Engine routing-policy, corridor, coverage, classification, and fresh
  navigation-witness checks passed; no Allegro or GUI was executed.
- `/mnt/c/PROGRA~1/dotnet/dotnet.exe run --project 'C:\e2studio\pd-simple-p0a\tests\PD.Simple.DrawingChecks\PD.Simple.DrawingChecks.csproj' -c Release --no-launch-profile`
  — PD policy and canonical drawing ownership passed with Engine/WPF owning
  capture, review, live projection, and disposal.
- `/home/emilekowalski/.dotnet/dotnet /mnt/c/e2studio/allegro-bridge-p0a/tools/EngineConformance/bin/Release/net10.0/EngineConformance.dll check-assembly --assembly /mnt/c/e2studio/pd-simple-p0a/src/PD.Simple/bin/Release/net10.0-windows/PD.Simple.dll --role ordinary` and the same command with assembly `/mnt/c/e2studio/pd-simple-p0a/src/PD.PcbTools/bin/Release/net10.0/PD.PcbTools.dll`
  — both emitted ordinary application assemblies passed independently.
- `/mnt/c/PROGRA~1/dotnet/dotnet.exe run --project 'C:\e2studio\allegro-bridge-p0a\tests\AllegroBridge.PackageDeliveryChecks\AllegroBridge.PackageDeliveryChecks.csproj' -c Release --no-launch-profile -- run --bridge-root 'C:\e2studio\allegro-bridge-p0a' --pd-root 'C:\e2studio\pd-simple-p0a' --candidate-dir 'C:\Users\EMILEKOWALSKI\Desktop\boards\PD-Simple-GitHub\packages' --version 1.13.0-preview.16 --isolation-root 'C:\e2studio\p0a-package-delivery-20260913-r2' --conformance-tool 'C:\e2studio\allegro-bridge-p0a\tools\EngineConformance\bin\Release\net10.0\EngineConformance.dll' --dotnet 'C:\Program Files\dotnet\dotnet.exe'`
  — all 12 consumers passed build, compiled-boundary, delivery, publish, and
  declared offline runtime checks. Evidence:
  `C:\e2studio\p0a-package-delivery-20260913-r2\compiled-dependencies.json`.

This establishes the PD portion of P0-A01 and the real-consumer/package portion
of P0-A03. P0-A02 and P0-A04 are recorded in the matching Bridge handoff.

## Composition gates

- P0-X09 passed locally in the package gate's headless Engine/Core consumers;
  Windows drawing rasterization was checked separately.
- P0-X12 has local proof for the real PD assemblies, direct lower-layer source and
  project rejection, source/project isolation, and package-only delivery. The
  copied-renderer negative control and P0-D module remain integration gates.
- P0-X01 has only P0-A architecture guidance/current primitive use. The required
  two new recipes belong to P0-D/P0-I.
- P0-X02 through P0-X08, P0-X10, and P0-X11 are reserved for P0-I against the
  merged canonical/native/composition work.

## Limitations and P0-I integration

No licensed Allegro, native mutation, native geometry, screen sharing, or
disposable-board qualification was performed in this lane. Existing historical
Preview.16 native evidence is not reclassified as P0-A evidence. The current
local package manifest identifies Bridge source commit
`e601f0f08207338dce9a4ea9eff6ff675b4383a9`; it does not identify the later
integrated P0 source.

P0-I must merge both P0-A branches with P0-B/P0-C/P0-D, regenerate the exact
four-package and Host/resident generation from the clean integrated commit, rerun
the package-only PD build, emitted-assembly checks, all listed PD regression
checks and every sample through the fresh isolation gate, then record P0-X01
through P0-X12. Native/GUI verification must use that exact generation and a
disposable board; no result here qualifies native geometry or the complete P0
product.
