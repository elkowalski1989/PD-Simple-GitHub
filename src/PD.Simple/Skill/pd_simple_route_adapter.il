;;; Command: native route adapter for pd.simple.controls
;;; Description: Exact managed route lifecycle without the Workflow catalog.
;;; Functions: pdweaStartInteractiveRoute, cancellation, guarded Undo.
;;; Ownership: the maintained route source owns picks and transactions.
;;; Dependencies: the SDK loads only this extension's packaged sibling source.

;; Load exactly once per extension registration, never while checking readiness.
;; The caller has already fenced an active managed route before reaching here.
(unless (pdceLoadSibling "49_route_interactive.il")
  (error "The packaged point-to-point trace owner could not be loaded."))

(procedure (pdweaInteractiveRouteReadyP)
  (prog (pdwea_irrp_registered_result)
    (unless (and (isCallable (quote pdceSessionId))
                 (isCallable (quote axlCmdIsRegistered))
                 (isCallable (quote axlShellPost)))
      (return nil))
    (setq pdwea_irrp_registered_result
      (errset (axlCmdIsRegistered "jm_iroute") t))
    (return
      (and pdwea_irrp_registered_result
           (car pdwea_irrp_registered_result)
           (isCallable (quote ir_set_pending_request))
           (isCallable (quote ir_clear_pending_request))
           (isCallable (quote ir_cancel_managed_request))
           (isCallable (quote ir_clear_first_pick_request))
           (isCallable (quote ir_resolve_route_layer))
           (isCallable (quote ir_run_native_self_test))
           (isCallable (quote ir_undo_last_managed_route))))
  )
)

(procedure (pdweaInteractiveRoutePreflight)
  (prog (pdwea_preflight_result pdwea_preflight_group
         pdwea_preflight_members pdwea_preflight_member
         pdwea_preflight_layer pdwea_preflight_present
         pdwea_preflight_visible pdwea_preflight_unavailable)
    (setq pdwea_preflight_unavailable
      "The native etch-layer lookup is unavailable. Check the Allegro layer settings before retrying.")
    (setq pdwea_preflight_result
      (errset (ir_resolve_route_layer) t))
    (unless pdwea_preflight_result
      (return pdwea_preflight_unavailable))
    (when (stringp (car pdwea_preflight_result))
      (return nil))

    ;; The owner deliberately refuses hidden etch. Diagnose its nil return
    ;; without selecting a different layer or changing native visibility.
    (setq pdwea_preflight_result
      (errset (axlGetParam "paramLayerGroup:ETCH") t))
    (unless (and pdwea_preflight_result (car pdwea_preflight_result))
      (return pdwea_preflight_unavailable))
    (setq pdwea_preflight_group (car pdwea_preflight_result))
    (setq pdwea_preflight_result
      (errset (getq pdwea_preflight_group groupMembers) t))
    (unless (and pdwea_preflight_result
                 (listp (car pdwea_preflight_result))
                 (car pdwea_preflight_result))
      (return pdwea_preflight_unavailable))
    (setq pdwea_preflight_members (car pdwea_preflight_result))
    (foreach pdwea_preflight_member pdwea_preflight_members
      (unless (stringp pdwea_preflight_member)
        (return pdwea_preflight_unavailable))
      (setq pdwea_preflight_layer (strcat "ETCH/" pdwea_preflight_member))
      (setq pdwea_preflight_present
        (errset (axlIsLayer pdwea_preflight_layer) t))
      (setq pdwea_preflight_visible
        (errset (axlIsVisibleLayer pdwea_preflight_layer) t))
      (unless (and pdwea_preflight_present (car pdwea_preflight_present)
                   pdwea_preflight_visible)
        (return pdwea_preflight_unavailable))
      ;; A changed observation is not evidence that every etch layer is hidden.
      (when (car pdwea_preflight_visible)
        (return pdwea_preflight_unavailable)))
    (return
      "No etch layer is visible. Make the intended ETCH layer visible in Allegro, then retry.")
  )
)

(procedure (pdweaStartInteractiveRoute pdwea_sir_width_mils pdwea_sir_context)
  (prog (pdwea_sir_set_result pdwea_sir_post_result)
    (unless (and (numberp pdwea_sir_width_mils)
                 (geqp pdwea_sir_width_mils 0.1)
                 (leqp pdwea_sir_width_mils 10000.0))
      (return (list (quote rejected)
        "Interactive Route requires a valid width.")))
    (unless (pdweaInteractiveRouteReadyP)
      (return (list (quote rejected)
        "The Interactive Route owner could not be loaded from the installed package.")))
    (unless (and (isCallable (quote ir_set_pending_request))
                 (isCallable (quote ir_clear_pending_request))
                 (isCallable (quote ir_cancel_managed_request)))
      (return (list (quote rejected)
        "The Interactive Route owner does not expose its typed lifecycle boundary.")))
    (setq pdwea_sir_set_result
      (errset (ir_set_pending_request pdwea_sir_width_mils
        (list (pdceSessionId pdwea_sir_context)
              (pdceBoardGeneration pdwea_sir_context))
        (sprintf nil "%s:%d" (pdceSessionId pdwea_sir_context)
          (pdceRequestId pdwea_sir_context))) t))
    (unless (and pdwea_sir_set_result (car pdwea_sir_set_result))
      (return (list (quote failed)
        "The Interactive Route owner rejected the validated request.")))
    (setq pdwea_sir_post_result
      (errset (axlShellPost "jm_iroute") t))
    (unless (and pdwea_sir_post_result (car pdwea_sir_post_result))
      (ir_clear_pending_request)
      (return (list (quote failed)
        "Allegro could not queue Interactive Route.")))
    (return (list (quote succeeded)
      "Interactive Route is waiting for its first native pick."))
  )
)

(procedure (pdweaCancelInteractiveRoute)
  (prog (pdwea_cir_result)
    (unless (pdweaInteractiveRouteReadyP)
      (return (list (quote failed)
        "The Interactive Route owner could not be loaded from the installed package.")))
    (unless (isCallable (quote ir_cancel_managed_request))
      (return (list (quote failed)
        "The Interactive Route owner does not expose safe cancellation.")))
    (setq pdwea_cir_result
      (errset (ir_cancel_managed_request) t))
    (if (and pdwea_cir_result (car pdwea_cir_result))
    then
      (return (list (quote cancelled)
        "Interactive Route was cancelled and its native effects were rolled back."))
    else
      (return (list (quote failed)
        "Interactive Route could not prove safe native cancellation.")))
  )
)

(procedure (pdweaClearInteractiveRouteFirstPick)
  (prog (pdwea_cirfp_result)
    (unless (pdweaInteractiveRouteReadyP)
      (return nil))
    (unless (isCallable (quote ir_clear_first_pick_request))
      (return nil))
    (setq pdwea_cirfp_result
      (errset (ir_clear_first_pick_request) t))
    (return (and pdwea_cirfp_result (car pdwea_cirfp_result)))
  )
)

(procedure (pdweaUndoInteractiveRoute pdwea_uir_context)
  (prog (pdwea_uir_result pdwea_uir_value)
    (unless (pdweaInteractiveRouteReadyP)
      (return (list (quote failed)
        "The Interactive Route owner could not be loaded from the installed package.")))
    (unless (isCallable (quote ir_undo_last_managed_route))
      (return (list (quote failed)
        "The Interactive Route owner does not expose guarded route-specific Undo.")))
    (setq pdwea_uir_result
      (errset (ir_undo_last_managed_route
        (list (pdceSessionId pdwea_uir_context)
              (pdceBoardGeneration pdwea_uir_context))) t))
    (unless (and pdwea_uir_result
                 (listp (car pdwea_uir_result))
                 (equal (length (car pdwea_uir_result)) 2))
      (return (list (quote failed)
        "Interactive Route Undo returned an invalid native result.")))
    (setq pdwea_uir_value (car pdwea_uir_result))
    (unless (member (nth 0 pdwea_uir_value)
              (list (quote succeeded) (quote rejected) (quote failed)))
      (return (list (quote failed)
        "Interactive Route Undo returned an unknown native state.")))
    (return pdwea_uir_value)
  )
)

(procedure (pdweaInteractiveRouteRecoveryState pdwea_irrs_context)
  (prog nil
    (unless (isCallable (quote ir_managed_route_recovery_state))
      (return (list nil nil)))
    (return (ir_managed_route_recovery_state
      (list (pdceSessionId pdwea_irrs_context)
            (pdceBoardGeneration pdwea_irrs_context))))
  )
)

(procedure (pdweaRunInteractiveRouteSelfTest pdwea_rirst_width_mils)
  (prog (pdwea_rirst_result pdwea_rirst_value)
    (unless (and (numberp pdwea_rirst_width_mils)
                 (geqp pdwea_rirst_width_mils 0.1)
                 (leqp pdwea_rirst_width_mils 10000.0))
      (return (list (quote rejected)
        "Interactive Route self-test requires a valid width.")))
    (unless (pdweaInteractiveRouteReadyP)
      (return (list (quote rejected)
        "The Interactive Route owner could not be loaded from the installed package.")))
    (unless (isCallable (quote ir_run_native_self_test))
      (return (list (quote rejected)
        "The Interactive Route owner does not expose its native self-test boundary.")))
    (setq pdwea_rirst_result
      (errset (ir_run_native_self_test pdwea_rirst_width_mils) t))
    (unless (and pdwea_rirst_result (listp (car pdwea_rirst_result)))
      (return (list (quote failed)
        "The Interactive Route owner failed during its native self-test.")))
    (setq pdwea_rirst_value (car pdwea_rirst_result))
    (unless
      (or
        (and (equal (length pdwea_rirst_value) 2)
             (member (nth 0 pdwea_rirst_value)
               (list (quote rejected) (quote failed)))
             (stringp (nth 1 pdwea_rirst_value)))
        (and (equal (length pdwea_rirst_value) 15)
             (equal (nth 0 pdwea_rirst_value) (quote succeeded))
             (stringp (nth 1 pdwea_rirst_value))))
      (return (list (quote failed)
        "The Interactive Route owner returned an invalid native self-test result.")))
    (return pdwea_rirst_value)
  )
)
