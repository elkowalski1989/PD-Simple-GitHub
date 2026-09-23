# PD Simple autostart (board watchdog)

Lessons learned 2026-09-21. Opening a board establishes the companion connection
without putting another window in the operator's way. The existing **AI
Workflow > Open AI Workflow** menu/toolbar command (or `pd_simple`) is the
explicit request that shows and focuses PD Simple.

## Install chain (the only live path)

`installer/pd_simple_loader.il.in`
-> `Build.ps1` copies it into the package
-> `Install.ps1` substitutes `@INSTALL_ROOT@` into `pd_simple_loader.il`
-> `%LocalAppData%\PD-Simple\pd_simple_loader.il`
-> managed block in `%AppData%\SPB_Data\pcbenv\allegro.ilinit` loads it at Allegro startup.

Consequences: a loader change needs rebuild + reinstall + Allegro restart. There is
no other copy of the loader; the template is the single source of truth.

The installer also retires the older **PD AI Workflow loader** block from
`allegro.ilinit`. Its menu is deliberately retained, while `pd_engine` is
registered as an alias for the maintained PD Simple launcher. The retired
startup block is saved inside the installation and restored by uninstall. This
prevents two board-open owners from racing and prevents an older executable
from reopening. Never add a second board-open trigger for the same companion.

## Delivery lesson: a source fix is not the live fix

For an authorized live-fix request, editing source and producing a successful
repository build are intermediate results. The work is complete only after all
of these steps succeed:

1. Run `Build.ps1` so the managed installer payload contains the change.
2. Run the staged package's `Install.ps1`; do not substitute a loose executable.
3. Compare the packaged and `%LocalAppData%\PD-Simple\app\PD.Simple.exe`
   SHA-256 hashes to prove Allegro will launch the new binary.
4. Verify the managed block in `allegro.ilinit` and the installed loader still
   target `%LocalAppData%\PD-Simple`.
5. Exercise the changed behavior against the installed executable, not only the
   repository `bin` output.
6. State and perform the required Allegro restart when authorized; otherwise
   identify it as the exact remaining operator action.

Unless the user explicitly asks for source-only delivery, never report the
board-open workflow as fixed while its managed installation still contains the
previous executable.

## Design: timer watchdog, not triggers

- No `axlTrigger` usage exists anywhere in either repo; do not invent trigger names.
- The watchdog reuses the resident's proven pattern from
  `pd_allegro_bridge.il`: `(axlUIWTimerAdd nil <ms> nil (quote <callback>))`,
  callback signature `(window timer_id elapsed)`, removal via `axlUIWTimerRemove`.
- Tick (2000 ms): stand down silently if a bridge is already active; reset the
  readiness count while there is no design (`axlDBGetDesign`) or native is
  busy. A board must then remain present and independently idle for ten
  consecutive ticks (twenty seconds) before one `pdsil_launch "hidden"`
  attempt removes the timer permanently. `axlDBGetDesign` alone is not a
  post-open readiness signal: on large boards it becomes non-nil before
  compatibility conversion and other native initialization have settled.
- Autostart never switches companions and never prompts. This is safe because
  the check and the launch run synchronously in one UI-thread timer callback
  with no event pumping between them, so the switch-prompt branch is unreachable.
- One automatic launch per Allegro session: deliberately closing the companion
  does not reopen it until the operator explicitly invokes AI Workflow or
  `pd_simple`. `PD_SIMPLE_NO_AUTOSTART=1` restores manual-only mode.
- The hidden process is loaded and connected, but has no visible window or
  taskbar entry. A later `pd_engine`/AI Workflow or `pd_simple` request uses the
  SDK focus event plus the PD Simple-owned `pd-simple-focus.signal` to restore
  that same process. The separate file is required because the Bridge host
  consumes its standard `focus.signal`. PD Simple deletes its private signal as
  the acknowledgement: a disconnected live window then reconnects its Engine
  session, while an unacknowledged signal causes the launcher to start one new
  visible companion against the existing resident board session.
- Session ownership must use the exact resident SDK field
  `pdab__bridge_dir`. Do not invent or carry an older name such as
  `pdab__session_dir`: that makes every activation look foreign, prompts for an
  unnecessary takeover, and strands the replacement window disconnected.
- The header reconnect/attach action stays available for a disconnected or
  faulted Engine session. Workbench close/switch fences apply only while a live
  ready session exists; they must never remove the recovery path itself.
- `pd_simple` remains for manual control and retry with full messages.

## Verification pattern

1. SKILL syntax: parse the template with the bridge validator
   (`allegro-bridge/tests/validate_resident_skill.py` sanitize/parse/validate_forms).
2. `Build.ps1`, then the staged package's `Install.ps1` directly (never
   `Install.cmd`: it ends in `pause`). Installer self-verifies the payload.
3. Installer migration E2E with isolated paths: seed an `allegro.ilinit` with
   both managed blocks, install, prove only the PD Simple block remains and the
   packaged loader owns both `pd_simple` and `pd_engine`, then uninstall and
   prove the retired AI Workflow block is restored.
4. Live E2E on the real profile: start owned Allegro
   (`C:\Cadence\SPB_25.1\tools\bin\allegro.exe -product Allegro_performance
   -p <input> -j <run>/allegro.jrl <scratch-board-copy>`), wait for `.brd` in
   the window title, poll up to 3 min for `PD.Simple`, then close both
   gracefully. Scratch script: `C:\e2studio\pd-simple-local-runs\autostart-test.ps1`.
   The process should appear with no visible/taskbar window. Invoke the AI
   Workflow button and confirm the same PID becomes visible and focused.
