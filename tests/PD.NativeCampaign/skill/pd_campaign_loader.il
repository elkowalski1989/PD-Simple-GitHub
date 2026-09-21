;;; PD native-campaign Allegro bootstrap (tests/PD.NativeCampaign/skill).
;;; Loaded via the campaign replay script. Establishes the worktree resident
;;; set (observer + extensions + bridge, in that order), then registers a
;;; board-open trigger that starts the bridge in control mode once a design is
;;; open. Mirrors the installed PD Simple launch sequence with worktree paths
;;; supplied through PD_CAMPAIGN_RESIDENT_DIR and
;;; CIRCUITHUB_ALLEGRO_BRIDGE_CONTROL_UI_EXE.

(unless (boundp (quote pdc__start_timer))
  (setq pdc__start_timer nil))

(procedure (pdc__resident_file pdc_rf_name)
  (prog (pdc_rf_dir)
    (setq pdc_rf_dir (getShellEnvVar "PD_CAMPAIGN_RESIDENT_DIR"))
    (unless (and (stringp pdc_rf_dir) (nequal pdc_rf_dir ""))
      (return nil))
    (return (strcat pdc_rf_dir "/" pdc_rf_name))
  )
)

(procedure (pdc__load_residents)
  (prog (pdc_lr_files pdc_lr_file pdc_lr_result pdc_lr_ok)
    (setq pdc_lr_files
      (list (pdc__resident_file "pd_constraint_observer.il")
            (pdc__resident_file "pd_custom_extensions.il")
            (pdc__resident_file "pd_allegro_bridge.il")))
    (setq pdc_lr_ok t)
    (foreach pdc_lr_file pdc_lr_files
      (unless (and (stringp pdc_lr_file) (isFile pdc_lr_file))
        (axlMsgPut
          (strcat "PD campaign: resident file missing: "
                  (if (stringp pdc_lr_file) then pdc_lr_file else "<no dir>") "\n"))
        (setq pdc_lr_ok nil))
      (when pdc_lr_ok
        (setq pdc_lr_result (errset (load pdc_lr_file) t))
        (unless (and pdc_lr_result (car pdc_lr_result))
          (axlMsgPut (strcat "PD campaign: could not load " pdc_lr_file "\n"))
          (setq pdc_lr_ok nil))))
    (unless (and pdc_lr_ok
                 (isCallable (quote pdab_start))
                 (isCallable (quote pdab_stop))
                 (isCallable (quote pdceResetSession)))
      (axlMsgPut "PD campaign: resident set incomplete; bridge start refused.\n")
      (return nil))
    (return t)
  )
)

(procedure (pdc__timer_callback pdc_tc_window pdc_tc_timer pdc_tc_elapsed)
  (prog (pdc_tc_result)
    (setq pdc__start_timer nil)
    (unless (axlDBGetDesign)
      (return t))
    (when (and (boundp (quote pdab__timer)) pdab__timer)
      (return t))
    (setq pdc_tc_result (errset (pdab_start "control") t))
    (unless (and pdc_tc_result
                 (car pdc_tc_result)
                 (boundp (quote pdab__timer))
                 pdab__timer)
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
  (prog (pdc_rg_result pdc_rg_ui)
    (unless (and (isCallable (quote axlTriggerSet))
                 (isCallable (quote axlUIWTimerAdd)))
      (printf "PD campaign: this Allegro release lacks trigger/timer APIs.\n")
      (return nil))
    (setq pdc_rg_ui
      (getShellEnvVar "CIRCUITHUB_ALLEGRO_BRIDGE_CONTROL_UI_EXE"))
    (unless (and (stringp pdc_rg_ui)
                 (nequal pdc_rg_ui "")
                 (isFile pdc_rg_ui))
      (printf "PD campaign: control UI executable is not configured.\n")
      (return nil))
    (unless (pdc__load_residents)
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
