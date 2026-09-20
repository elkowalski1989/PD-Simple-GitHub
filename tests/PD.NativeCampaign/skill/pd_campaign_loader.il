;;; PD native-campaign Allegro bootstrap (tests/PD.NativeCampaign/skill).
;;; Loads AFTER pd_allegro_bridge.il via the campaign replay script. Registers
;;; a board-open trigger that starts the resident bridge in control mode once
;;; a design is open. Mirrors the installed AIWorkflow auto-open shape
;;; (trigger -> UI timer deferral -> start) with worktree paths supplied by
;;; the campaign through shell environment (CIRCUITHUB_ALLEGRO_BRIDGE_CONTROL_UI_EXE).
;;; No autostart at load when no design is open; no menus or forms.

(unless (boundp (quote pdc__start_timer))
  (setq pdc__start_timer nil))

(procedure (pdc__timer_callback pdc_tc_window pdc_tc_timer pdc_tc_elapsed)
  (prog (pdc_tc_result)
    (setq pdc__start_timer nil)
    (unless (and (isCallable (quote pdab_start)) (axlDBGetDesign))
      (return t))
    ;; Already running: nothing to do (pdab_start would just report it).
    (when (and (boundp (quote pdab__timer)) pdab__timer)
      (return t))
    (setq pdc_tc_result (errset (pdab_start "control") t))
    (unless (and pdc_tc_result (car pdc_tc_result))
      (axlMsgPut "PD campaign: resident bridge start did not complete.\n"))
    (return t)
  )
)

(procedure (pdc__open_trigger pdc_ot_open_info)
  (prog (pdc_ot_timer_result)
    (unless pdc__start_timer
      (setq pdc_ot_timer_result
        (errset
          (axlUIWTimerAdd nil 500 t
            (quote pdc__timer_callback))
          t))
      (when (and pdc_ot_timer_result (car pdc_ot_timer_result))
        (setq pdc__start_timer (car pdc_ot_timer_result))))
    (unless pdc__start_timer
      (axlMsgPut "PD campaign: bridge-start timer could not be armed.\n"))
    (return t)
  )
)

(procedure (pdc__register)
  (prog (pdc_rg_result)
    (unless (and (isCallable (quote axlTriggerSet))
                 (isCallable (quote axlUIWTimerAdd)))
      (printf "PD campaign: this Allegro release lacks trigger/timer APIs.\n")
      (return nil))
    (unless (isCallable (quote pdab_start))
      (printf "PD campaign: resident not loaded; load pd_allegro_bridge.il first.\n")
      (return nil))
    (setq pdc_rg_result
      (errset
        (axlTriggerSet (quote open) (quote pdc__open_trigger))
        t))
    (unless (and pdc_rg_result (car pdc_rg_result))
      (printf "PD campaign: open-trigger registration failed.\n")
      (return nil))
    (return t)
  )
)

(when (pdc__register)
  ;; Catch a board whose open completed before this loader ran.
  (when (axlDBGetDesign)
    (pdc__open_trigger nil)))
(printf "PD campaign loader ready. Resident bridge starts on board open.\n")
