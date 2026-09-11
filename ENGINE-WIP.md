# Allegro engine consumer checkpoint

Date: 2026-09-11

**Status: earlier committed consumer baseline preserved; newer uncommitted engine integration was not recovered.**

This checkpoint branches from `567405e8c16501dae7368f0fd8014a295a71c140` on `work/pcb-developer-experience`. All existing application, PCB tool module, Explorer, examples, tests and package references are retained unchanged. This commit adds this status note only. It does not claim that new engine integration source was pushed.

## Related SDK checkpoint

The corresponding branch in `elkowalski1989/allegro-bridge` is `wip/allegro-engine-20260911`, with preservation commit `30284c12c8e34c06fb916cb2be3081668ce1e493`.

That SDK checkpoint preserves six exported source files and two earlier logs. Its `ENGINE-WIP.md` documents the exact recovered files, their hashes, missing dependencies and build limitations. SDK implementation source remains in its own repository and is not copied here.

## Missing work

The preceding development session's complete uncommitted working directories were not retained in the current execution environment. No newer PD-Simple/Explorer engine integration files survived as exported attachments. Available source archives predate that engine work. They are not substitutes for the missing changes.

Consequently the newer live/offline scene integration, engine-backed Explorer changes and other consumer edits described in the preceding progress report are NOT present in this checkpoint. The original source must be recovered from a separately retained complete workspace, or deliberately reimplemented as new work. Do not present reconstruction as recovery of the old implementation.

## Verification and use

No build, test, native Allegro operation, recording/sharing check, installation or runtime package publication was performed for this preservation commit. Historical results apply only to their original tested versions. The partial SDK checkpoint is not a matching runtime package, and it is not a buildable replacement for the existing package references.

Do not change package references or substitute loose SDK assemblies merely to consume the recovered fragments. Complete the SDK dependency graph and supported package handoff before integrating the engine. Keep the existing native identity, authorization, mutation outcome and recovery rules intact.

Neither `main` nor the existing work branch is reset or force-pushed by this checkpoint.
