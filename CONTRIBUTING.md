# Working on PD-Simple

PD-Simple is a Windows/.NET 10 example with two tools: DP via corridor review
and two-pick point-to-point routing. Keep it independent of the full Workflow
Engine and use the packaged Bridge SDK for native integration.

## Build and check

1. Use the included SDK package and reference PDF described in `packages/README.md`.
2. Run the build and both check projects listed in the root README.
3. Use `Build.cmd` when installer and source archives are needed.

Use `.editorconfig` for maintained source. Keep coordinates, board/session
identity, native mutation, and guarded Undo in their existing owners. Do not
replace SDK projection with cached screen coordinates or identify known boards
in production code to make a test pass.

## Native verification

Use disposable board copies in a licensed Allegro session. Never use a teammate's
working board as a mutation fixture or save test changes over an original board.

- Corridor changes: check setup, findings, filters, native navigation, report,
  raw/annotated PNG export, and alignment in both the preview and live canvas.
- Drawing changes: check focus loss, partial/full coverage, resize, minimize and
  restore; graphics must clear when the SDK's native evidence is unavailable.
- Routing changes: verify width, two real picks, native feedback, Cancel, Clear
  first pick, committed-route readback, and route-specific Undo. Hidden ETCH
  layers must reject the start before mutation.
- Installer changes: use isolated install/startup paths; verify preservation of
  unrelated entries, reinstallation, payload rejection, rollback, and uninstall.

Report compilation, automated checks, and native GUI results separately. A queued
input message or successful API call does not alone prove a rendered result.

## Native cleanup candidates

Use `pd_simple` and its SDK-bound commands as the supported application entry
points. The retained checker also registers the older
`dp_via_corridor_check` and `dp_via_corridor_restore` commands. The former calls
`dpvc_main`, which depends on `si_tools_cleanup_all` from the original environment;
that helper is not bundled here. Do not use those legacy commands in a standalone
installation or copy them into a new SDK example.

A future report-only extraction should retire those registrations and remove
unused native form/display helpers while preserving the managed checker,
navigation, reports, and routing recovery. Because shared analysis state is
interleaved with those branches, that cleanup requires native regression, not
just a successful C# build. The publication scrub does not change native behavior.

## Review before publishing

Inspect the staged diff and filenames. Do not commit board files, logs, credentials,
signing material, dependency caches, or generated setup/source archives. The
explicit SDK package in `packages/` is the bundled binary dependency; the adjacent
PDF is its reference documentation. Review the intended repository contents
before pushing to `github.ibm.com`.
