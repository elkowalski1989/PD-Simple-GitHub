;;; Command: pd.simple.controls (SDK custom extension entry)
;;; Description: DP via corridor and two-object point-to-point trace controls.
;;; Functions: DPVC run/zoom, route start/cancel/clear/Undo and native self-test.
;;; Ownership: native owners perform geometry; Allegro Bridge SDK transports it.
;;; Dependencies: only content-bound sibling sources in this extension package.

;; PD Simple and Workflow share the maintained native owner namespaces. They
;; must use separate Allegro sessions; never replace an active route context.
(when (and (boundp 'pdwec__interactive_route_context)
           pdwec__interactive_route_context)
  (error "Finish or cancel the active route before loading PD Simple."))

(unless (pdceLoadSibling "pd_simple_route_adapter.il")
  (error "The PD Simple route adapter could not be loaded."))

(unless (pdceLoadSibling "pd_dp_via_corridor.il")
  (error "The DP via corridor typed adapter could not be loaded."))

(setq pdwec__interactive_route_context nil)

(procedure (pdwec__publish
             pdwec_pub_context pdwec_pub_action
             pdwec_pub_state pdwec_pub_message)
  (prog (pdwec_pub_state_text pdwec_pub_json)
    (unless (and
              (member pdwec_pub_state
                (list (quote succeeded) (quote rejected)
                      (quote failed) (quote cancelled)))
              (stringp pdwec_pub_message))
      (return (list (quote failed)
        "A resident workflow control returned an invalid result.")))
    (setq pdwec_pub_state_text
      (case pdwec_pub_state
        (succeeded "succeeded")
        (rejected "rejected")
        (cancelled "cancelled")
        (t "failed")))
    (setq pdwec_pub_json
      (sprintf nil
        "{\"schema\":\"pd-workflow-control-result-v1\",\"action\":\"%s\",\"boardGeneration\":%d,\"state\":\"%s\"}"
        pdwec_pub_action
        (pdceBoardGeneration pdwec_pub_context)
        pdwec_pub_state_text))
    (unless (pdceSetResultJson pdwec_pub_context pdwec_pub_json)
      (return (list (quote failed)
        "The typed workflow-control result could not be published.")))
    (return (list pdwec_pub_state pdwec_pub_message))
  )
)

(procedure (pdwec__resident_result
             pdwec_rr_context pdwec_rr_action pdwec_rr_call)
  (prog (pdwec_rr_value)
    (unless (and pdwec_rr_call
                 (listp (car pdwec_rr_call))
                 (equal (length (car pdwec_rr_call)) 2))
      (return (list (quote failed)
        "The resident workflow-control adapter failed.")))
    (setq pdwec_rr_value (car pdwec_rr_call))
    (return
      (pdwec__publish
        pdwec_rr_context pdwec_rr_action
        (nth 0 pdwec_rr_value) (nth 1 pdwec_rr_value)))
  )
)

(procedure (pdwec__interactive_route_result_json
             pdwec_irrj_context pdwec_irrj_state)
  (prog (pdwec_irrj_state_text pdwec_irrj_recovery)
    (setq pdwec_irrj_state_text
      (case pdwec_irrj_state
        (succeeded "succeeded")
        (rejected "rejected")
        (cancelled "cancelled")
        (t "failed")))
    (setq pdwec_irrj_recovery
      (pdweaInteractiveRouteRecoveryState pdwec_irrj_context))
    (return
      (sprintf nil
        "{\"schema\":\"pd-workflow-control-result-v1\",\"action\":\"interactive-route\",\"boardGeneration\":%d,\"state\":\"%s\",\"committed\":%s,\"recoveryAvailable\":%s}"
        (pdceBoardGeneration pdwec_irrj_context)
        pdwec_irrj_state_text
        (if (nth 0 pdwec_irrj_recovery) then "true" else "false")
        (if (nth 1 pdwec_irrj_recovery) then "true" else "false")))
  )
)

(procedure (pdwecInteractiveRouteActiveP)
  (prog nil
    (return (if pdwec__interactive_route_context then t else nil))
  )
)

(procedure (pdwecInteractiveRouteProgress)
  (prog (pdwec_irp_context)
    (setq pdwec_irp_context pdwec__interactive_route_context)
    (unless pdwec_irp_context (return nil))
    (return
      (pdceDeferredProgress
        pdwec_irp_context "second_endpoint"
        "Pick the second supported object on the board." 2 2))
  )
)

(procedure (pdwecInteractiveRouteBeginObservation)
  (prog nil
    (return
      (and pdwec__interactive_route_context
           (isCallable (quote pdceBeginFeedbackObservation))
           (pdceBeginFeedbackObservation pdwec__interactive_route_context))))
)

(procedure (pdwecInteractiveRouteFeedback
             pdwec_irf_kind pdwec_irf_pointer_state
             pdwec_irf_object_kind pdwec_irf_point
             pdwec_irf_viewport pdwec_irf_ordinal pdwec_irf_reason
             @optional (pdwec_irf_capture nil))
  (prog (pdwec_irf_context)
    (setq pdwec_irf_context pdwec__interactive_route_context)
    (unless pdwec_irf_context (return nil))
    (return
      ;; Without a capture handle retain the published SDK's existing arity.
      ;; Its feedback remains untimed, not falsely fresh or incompatible.
      (apply (quote pdceDeferredFeedback)
        (append
          (list pdwec_irf_context pdwec_irf_kind pdwec_irf_pointer_state
            pdwec_irf_object_kind pdwec_irf_point pdwec_irf_viewport
            pdwec_irf_ordinal pdwec_irf_reason)
          (if pdwec_irf_capture then (list pdwec_irf_capture) else nil))))
  )
)

(procedure (pdwec_interactive_route_action_handler
             pdwec_irah_context pdwec_irah_action)
  (prog (pdwec_irah_clear_result)
    (unless (eq pdwec_irah_context pdwec__interactive_route_context)
      (return (list (quote failed) "action_stale"
        "The Interactive Route action no longer matches the active request.")))
    (unless (equal pdwec_irah_action "clear_first_pick")
      (return (list (quote rejected) "action_unknown"
        "The requested Interactive Route action is not supported.")))
    (unless (and (pdweaInteractiveRouteReadyP)
                 (isCallable (quote pdweaClearInteractiveRouteFirstPick)))
      (return (list (quote failed) "clear_unavailable"
        "The native route owner cannot clear its first pick.")))
    (setq pdwec_irah_clear_result
      (errset (pdweaClearInteractiveRouteFirstPick) t))
    (unless (and pdwec_irah_clear_result
                 (car pdwec_irah_clear_result))
      (return (list (quote failed) "clear_failed"
        "The native route owner did not clear its first pick.")))
    (unless
      (pdceDeferredFeedback
        pdwec_irah_context "selection_cleared" "unknown" "none"
        nil nil nil "clear_first_pick")
      (return (list (quote failed) "feedback_unavailable"
        "The cleared first pick could not be published.")))
    (unless
      (pdceDeferredProgress
        pdwec_irah_context "first_endpoint"
        "Pick the first supported object on the board." 1 2)
      (return (list (quote failed) "progress_unavailable"
        "Interactive Route could not return to its first-pick phase.")))
    (return (list (quote succeeded) ""
      "The first Interactive Route pick was cleared."))
  )
)

(procedure (pdwecInteractiveRouteFinished pdwec_irf_state pdwec_irf_message)
  (prog (pdwec_irf_context pdwec_irf_json pdwec_irf_result)
    (setq pdwec_irf_context pdwec__interactive_route_context)
    (unless (and pdwec_irf_context
                 (member pdwec_irf_state
                   (list (quote succeeded) (quote rejected)
                         (quote failed) (quote cancelled)))
                 (stringp pdwec_irf_message))
      (return nil))
    (setq pdwec_irf_json
      (pdwec__interactive_route_result_json
        pdwec_irf_context pdwec_irf_state))
    (if (pdceSetResultJson pdwec_irf_context pdwec_irf_json)
    then
      (setq pdwec_irf_result
        (case pdwec_irf_state
          (succeeded
            (pdceDeferredSucceed pdwec_irf_context pdwec_irf_message))
          (rejected
            (pdceDeferredReject pdwec_irf_context
              "interactive_route_rejected" pdwec_irf_message "" ""))
          (cancelled
            (pdceDeferredCancel pdwec_irf_context pdwec_irf_message))
          (t
            (pdceDeferredFail pdwec_irf_context
              "interactive_route_failed" pdwec_irf_message "" ""))))
    else
      (setq pdwec_irf_result
        (pdceDeferredFail pdwec_irf_context
          "interactive_route_result_invalid"
          "Interactive Route could not publish its typed native result."
          "" "")))
    (when pdwec_irf_result
      (setq pdwec__interactive_route_context nil))
    (return pdwec_irf_result)
  )
)

(procedure (pdwec_route_preflight_rejected pdwec_rpr_context pdwec_rpr_message)
  (prog (pdwec_rpr_json)
    ;; This proof is emitted only before pending native state or shell dispatch.
    ;; Never infer it from an ordinary failed/cancelled route completion.
    (setq pdwec_rpr_json
      (sprintf nil
        "{\"schema\":\"pd-workflow-control-result-v1\",\"action\":\"interactive-route\",\"boardGeneration\":%d,\"state\":\"rejected\",\"committed\":false,\"recoveryAvailable\":false,\"routePreflightRejected\":true}"
        (pdceBoardGeneration pdwec_rpr_context)))
    (unless (pdceSetResultJson pdwec_rpr_context pdwec_rpr_json)
      (return (list 'failed
        "The route preflight rejection could not be published.")))
    (return (list 'rejected pdwec_rpr_message))
  )
)

(procedure (pdwec_interactive_route_handler pdwec_ir_context)
  (prog (pdwec_ir_width pdwec_ir_width_number pdwec_ir_start_result
         pdwec_ir_start_value pdwec_ir_cancel_result pdwec_ir_deferred
         pdwec_ir_preflight_message pdwec_ir_cancel_json)
    (when (pdceCancellationRequested pdwec_ir_context)
      ;; This request has not dispatched anything. Publish no-edit proof only
      ;; when the loaded native owner also confirms no active or pending work.
      ;; Do not read a previous completed request's recovery globals here.
      (when (and (null pdwec__interactive_route_context)
                 (boundp 'ir_request_active) (null ir_request_active)
                 (boundp 'ir_pending_width_mils) (null ir_pending_width_mils)
                 (boundp 'ir_pending_cancelled) (null ir_pending_cancelled)
                 (boundp 'ir_mark) (null ir_mark)
                 (boundp 'ir_runtime_state_saved) (null ir_runtime_state_saved))
        (setq pdwec_ir_cancel_json
          (sprintf nil
            "{\"schema\":\"pd-workflow-control-result-v1\",\"action\":\"interactive-route\",\"boardGeneration\":%d,\"state\":\"cancelled\",\"committed\":false,\"recoveryAvailable\":false}"
            (pdceBoardGeneration pdwec_ir_context)))
        (unless (pdceSetResultJson pdwec_ir_context pdwec_ir_cancel_json)
          (return (list 'failed
            "The pre-dispatch route cancellation result could not be published."))))
      (return (list (quote cancelled)
        "Interactive Route was cancelled before native picks.")))
    (setq pdwec_ir_width
      (pdceArgument pdwec_ir_context "width_mils"))
    (unless (and (stringp pdwec_ir_width)
                 (null pdwec__interactive_route_context)
                 (pdweaInteractiveRouteReadyP))
      (return (list (quote rejected)
        "Interactive Route is unavailable or malformed.")))
    (setq pdwec_ir_width_number (atof pdwec_ir_width))
    (unless (and (numberp pdwec_ir_width_number)
                 (geqp pdwec_ir_width_number 0.1)
                 (leqp pdwec_ir_width_number 10000.0))
      (return (list (quote rejected)
        "Interactive Route width must be between 0.1 and 10000 mils.")))
    (when (or ir_request_active ir_pending_width_mils ir_pending_cancelled ir_mark)
      (return (list 'rejected
        "Finish or cancel the active native route before starting another.")))
    (setq pdwec_ir_preflight_message (pdweaInteractiveRoutePreflight))
    (when pdwec_ir_preflight_message
      (return (pdwec_route_preflight_rejected
        pdwec_ir_context pdwec_ir_preflight_message)))
    (setq pdwec__interactive_route_context pdwec_ir_context)
    (setq pdwec_ir_start_result
      (errset (pdweaStartInteractiveRoute pdwec_ir_width_number pdwec_ir_context) t))
    (unless (and pdwec_ir_start_result
                 (listp (car pdwec_ir_start_result))
                 (equal (length (car pdwec_ir_start_result)) 2))
      (setq pdwec__interactive_route_context nil)
      (return (list (quote failed)
        "The resident could not start Interactive Route.")))
    (setq pdwec_ir_start_value (car pdwec_ir_start_result))
    (unless (equal (nth 0 pdwec_ir_start_value) (quote succeeded))
      (setq pdwec__interactive_route_context nil)
      (return pdwec_ir_start_value))
    (setq pdwec_ir_deferred
      (pdceDefer pdwec_ir_context "first_endpoint"
        "Pick the first supported object on the board." 1 2))
    (unless pdwec_ir_deferred
      (setq pdwec_ir_cancel_result
        (errset (pdweaCancelInteractiveRoute) t))
      (setq pdwec__interactive_route_context nil)
      (return (list (quote failed)
        "Interactive Route could not enter its deferred native lifecycle.")))
    (return pdwec_ir_deferred)
  )
)

(procedure (pdwec_interactive_route_cancel_handler pdwec_irc_context)
  (prog (pdwec_irc_result pdwec_irc_json)
    (unless (eq pdwec_irc_context pdwec__interactive_route_context)
      (return (list (quote failed)
        "Interactive Route cancellation does not match the active request.")))
    ;; Fence the native cancel callback before asking the route owner to stop;
    ;; this cancellation handler alone publishes the terminal cancel receipt.
    (setq pdwec__interactive_route_context nil)
    (unless (and (pdweaInteractiveRouteReadyP)
                 (isCallable (quote pdweaCancelInteractiveRoute)))
      (return (list (quote failed)
        "Interactive Route does not expose safe native cancellation.")))
    (setq pdwec_irc_result
      (errset (pdweaCancelInteractiveRoute) t))
    (unless (and pdwec_irc_result
                 (listp (car pdwec_irc_result))
                 (equal (length (car pdwec_irc_result)) 2)
                 (equal (nth 0 (car pdwec_irc_result)) (quote cancelled)))
      (return (list (quote failed)
        "Interactive Route cancellation failed inside its native owner.")))
    ;; The SDK keeps this context active until it captures the handler result.
    ;; The ordinary completion callback was fenced above, so this handler must
    ;; publish the same native recovery evidence after confirmed cancellation.
    (setq pdwec_irc_json
      (pdwec__interactive_route_result_json pdwec_irc_context 'cancelled))
    (unless (pdceSetResultJson pdwec_irc_context pdwec_irc_json)
      (return (list 'failed
        "Interactive Route was cancelled, but its typed result could not be published.")))
    (return (car pdwec_irc_result))
  )
)

(procedure (pdwec_interactive_route_self_test_handler pdwec_irst_context)
  (prog (pdwec_irst_width pdwec_irst_width_number pdwec_irst_result
         pdwec_irst_value pdwec_irst_json)
    (when (pdceCancellationRequested pdwec_irst_context)
      (return (list (quote cancelled)
        "Interactive Route self-test was cancelled before native execution.")))
    (setq pdwec_irst_width
      (pdceArgument pdwec_irst_context "width_mils"))
    (unless (and (stringp pdwec_irst_width)
                 (pdweaInteractiveRouteReadyP)
                 (isCallable (quote pdweaRunInteractiveRouteSelfTest)))
      (return (list (quote rejected)
        "Interactive Route self-test is unavailable or malformed.")))
    (setq pdwec_irst_width_number (atof pdwec_irst_width))
    (unless (and (numberp pdwec_irst_width_number)
                 (geqp pdwec_irst_width_number 0.1)
                 (leqp pdwec_irst_width_number 10000.0))
      (return (list (quote rejected)
        "Interactive Route self-test width must be between 0.1 and 10000 mils.")))
    (setq pdwec_irst_result
      (errset
        (pdweaRunInteractiveRouteSelfTest pdwec_irst_width_number)
        t))
    (unless (and pdwec_irst_result
                 (listp (car pdwec_irst_result)))
      (return (list (quote failed)
        "The resident Interactive Route self-test adapter failed.")))
    (setq pdwec_irst_value (car pdwec_irst_result))
    (when (and (equal (length pdwec_irst_value) 2)
               (member (nth 0 pdwec_irst_value)
                 (list (quote rejected) (quote failed)))
               (stringp (nth 1 pdwec_irst_value)))
      (return pdwec_irst_value))
    (unless (and
              (equal (length pdwec_irst_value) 15)
              (equal (nth 0 pdwec_irst_value) (quote succeeded))
              (stringp (nth 1 pdwec_irst_value))
              (numberp (nth 2 pdwec_irst_value))
              (numberp (nth 3 pdwec_irst_value))
              (numberp (nth 4 pdwec_irst_value))
              (numberp (nth 5 pdwec_irst_value))
              (numberp (nth 6 pdwec_irst_value))
              (numberp (nth 7 pdwec_irst_value))
              (nth 8 pdwec_irst_value)
              (nth 9 pdwec_irst_value)
              (nth 10 pdwec_irst_value)
              (nth 11 pdwec_irst_value)
              (nth 12 pdwec_irst_value)
              (nth 13 pdwec_irst_value)
              (nth 14 pdwec_irst_value))
      (return (list (quote failed)
        "The resident Interactive Route self-test returned incomplete evidence.")))
    (setq pdwec_irst_json
      (sprintf nil
        "{\"schema\":\"pd-interactive-route-self-test-v1\",\"action\":\"interactive-route-self-test\",\"boardGeneration\":%d,\"requestedWidthMil\":%.12f,\"observedWidthMil\":%.12f,\"startXMil\":%.12f,\"startYMil\":%.12f,\"endXMil\":%.12f,\"endYMil\":%.12f,\"routeCreated\":true,\"exactWidth\":true,\"exactLayer\":true,\"exactEndpoints\":true,\"rollbackRestored\":true,\"controlsRestored\":true,\"guardedUndoRestored\":true}"
        (pdceBoardGeneration pdwec_irst_context)
        (nth 2 pdwec_irst_value) (nth 3 pdwec_irst_value)
        (nth 4 pdwec_irst_value) (nth 5 pdwec_irst_value)
        (nth 6 pdwec_irst_value) (nth 7 pdwec_irst_value)))
    (unless (pdceSetResultJson pdwec_irst_context pdwec_irst_json)
      (return (list (quote failed)
        "The typed Interactive Route self-test evidence could not be published.")))
    (return
      (list (quote succeeded) (nth 1 pdwec_irst_value)))
  )
)

(procedure (pdwec_interactive_route_undo_handler pdwec_iru_context)
  (prog (pdwec_iru_result)
    (when (pdceCancellationRequested pdwec_iru_context)
      (return (list (quote cancelled)
        "Interactive Route Undo was cancelled before native verification.")))
    (unless (isCallable (quote pdweaUndoInteractiveRoute))
      (return (list (quote rejected)
        "Guarded Interactive Route Undo is unavailable.")))
    (setq pdwec_iru_result
      (errset (pdweaUndoInteractiveRoute pdwec_iru_context) t))
    (return
      (pdwec__resident_result
        pdwec_iru_context "interactive-route-undo" pdwec_iru_result))
  )
)

(pdceRegisterCommand
  "pd.simple.controls.dp-via-corridor"
  "DP via corridor"
  "Checks foreign copper between differential-pair vias without changing the board."
  "SI Checks"
  "analysis"
  (quote pddpvc_run_handler)
  (list
    (list "margin_mils" "Extra corridor margin" "decimal" "scalar"
          "" "mils" "" "" t 0
          "Extra margin, 0 to 50 mils; the WPF default is 0." nil)
    (list "module_filter" "Module boundary" "text" "scalar"
          "" "" "" "" nil 64
          "Optional exact Allegro module name; blank checks all pairs." nil)
    (list "include_unused" "Include unused pairs" "boolean" "scalar"
          "false" "" "" "" t 0
          "Include NC/NU differential pairs." nil)
    (list "report_path" "Report output" "text" "scalar"
          "" "" "" "" t 1024
          "New .rpt output path selected by PD; existing outputs are refused." nil))
  "pd-dp-via-corridor-result-v1")

(pdceRegisterCommand
  "pd.simple.controls.dp-via-corridor-zoom"
  "Show native DP via crossing"
  "Revalidates a published crossing and centers its current Allegro view."
  "SI Checks"
  "display"
  (quote pddpvc_zoom_handler)
  (list
    (list "report_path" "Completed analysis report" "text" "scalar"
          "" "" "" "" t 1024
          "Exact report path returned by the current corridor analysis." nil)
    (list "finding_id" "Published crossing" "text" "scalar"
          "" "" "" "" t 12
          "Exact crossing-N identifier returned by that analysis." nil))
  "pd-dp-via-corridor-zoom-v1")

(pdceRegisterCommand
  "pd.simple.controls.interactive-route"
  "Start Interactive Route"
  "Queues one repository-owned two-pick route with its validated C# width."
  "PD Simple"
  "routing"
  (quote pdwec_interactive_route_handler)
  (list
    (list "width_mils" "Trace width" "decimal" "scalar"
          "" "mils" "" "" t 0
          "Width applied to the one route created after two valid picks." nil))
  "pd-workflow-control-result-v1"
  "deferred"
  (quote pdwec_interactive_route_cancel_handler)
  (list
    (list "clear_first_pick" (list "second_endpoint")))
  (quote pdwec_interactive_route_action_handler))

(pdceRegisterCommand
  "pd.simple.controls.interactive-route-self-test"
  "Verify Interactive Route natively"
  "Discovers board points and verifies exact native readback, rollback, and guarded route-specific Undo."
  "PD Simple"
  "modify"
  (quote pdwec_interactive_route_self_test_handler)
  (list
    (list "width_mils" "Trace width" "decimal" "scalar"
          "" "mils" "" "" t 0
          "Width for native rollback and guarded-Undo verification." nil))
  "pd-interactive-route-self-test-v1")

(pdceRegisterCommand
  "pd.simple.controls.interactive-route-undo"
  "Undo verified Interactive Route"
  "Revalidates and transactionally removes only the exact last route created by PD."
  "PD Simple"
  "modify"
  (quote pdwec_interactive_route_undo_handler)
  nil
  "pd-workflow-control-result-v1")
