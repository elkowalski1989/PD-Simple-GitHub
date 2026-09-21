# PD Simple autostart (board watchdog)

Lessons learned 2026-09-21. Opening a board auto-launches the companion; no typing.

## Install chain (the only live path)

`installer/pd_simple_loader.il.in`
-> `Build.ps1` copies it into the package
-> `Install.ps1` substitutes `@INSTALL_ROOT@` into `pd_simple_loader.il`
-> `%LocalAppData%\PD-Simple\pd_simple_loader.il`
-> managed block in `%AppData%\SPB_Data\pcbenv\allegro.ilinit` loads it at Allegro startup.

Consequences: a loader change needs rebuild + reinstall + Allegro restart. There is
no other copy of the loader; the template is the single source of truth.

## Design: timer watchdog, not triggers

- No `axlTrigger` usage exists anywhere in either repo; do not invent trigger names.
- The watchdog reuses the resident's proven pattern from
  `pd_allegro_bridge.il`: `(axlUIWTimerAdd nil <ms> nil (quote <callback>))`,
  callback signature `(window timer_id elapsed)`, removal via `axlUIWTimerRemove`.
- Tick (2000 ms): stand down silently if a bridge is already active; wait if no
  design (`axlDBGetDesign`) or native is busy; else one `pdsil_launch` attempt,
  then remove the timer permanently.
- Autostart never switches companions and never prompts. This is safe because
  the check and the launch run synchronously in one UI-thread timer callback
  with no event pumping between them, so the switch-prompt branch is unreachable.
- One launch per Allegro session: deliberately closing the companion does not
  relaunch it. `PD_SIMPLE_NO_AUTOSTART=1` restores manual-only mode.
- `pd_simple` remains for manual control and retry with full messages.

## Verification pattern

1. SKILL syntax: parse the template with the bridge validator
   (`allegro-bridge/tests/validate_resident_skill.py` sanitize/parse/validate_forms).
2. `Build.ps1`, then the staged package's `Install.ps1` directly (never
   `Install.cmd`: it ends in `pause`). Installer self-verifies the payload.
3. Live E2E on the real profile: start owned Allegro
   (`C:\Cadence\SPB_25.1\tools\bin\allegro.exe -product Allegro_performance
   -p <input> -j <run>/allegro.jrl <scratch-board-copy>`), wait for `.brd` in
   the window title, poll up to 3 min for `PD.Simple`, then close both
   gracefully. Scratch script: `C:\e2studio\pd-simple-local-runs\autostart-test.ps1`.
   Result 2026-09-21: PASS (`AUTOSTART OK ... with zero typing`).
