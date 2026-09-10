;; ==========================================================================
;; Command: jm_iroute
;; Description: One-shot Interactive Route configured by the PD WPF client
;; Functions: ir_main, ir_cancel, ir_done, ir_pick_and_route,
;;            ir_run_native_self_test
;; ==========================================================================
;;
;; The C# PD Workflow Engine owns all configurable UI. This SKILL command owns
;; only the native Allegro interaction: resolve a route layer, pick two board
;; objects with typed SDK interaction feedback, create one H-first route, and
;; commit or roll back the database transaction. No Allegro form or popup is
;; created.
;; ==========================================================================

;; --- Top-level reload cleanup (guard for allegro.ilinit safety) ---
(let (_ir_design_guard)
  (setq _ir_design_guard nil)
  (errset (setq _ir_design_guard (axlDBGetDesign)) t)
  (when _ir_design_guard
    (errset (progn
      (axlClearDynamics)
      (axlClearSelSet)
      (axlFlushDisplay)
    ) t)
  )
)

;; --- Global variables (unique prefix to avoid scope collisions) ---
(setq ir_done_flag nil)
(setq ir_mark nil)
(setq ir_fill_mode nil)
(setq ir_drc_enabled nil)
(setq ir_route_count 0)
(setq ir_saved_find_enabled nil)
(setq ir_saved_find_buttons nil)
(setq ir_pending_width_mils nil)
(setq ir_pending_identity nil)
(setq ir_request_identity nil)
(setq ir_pending_cancelled nil)
(setq ir_request_active nil)
(setq ir_runtime_state_saved nil)
(setq ir_first_pick_clear_requested nil)
(setq ir_selection_ordinal 0)
(setq ir_viewport_sequence 0)
(setq ir_last_viewport_box nil)
(setq ir_hover_timer nil)
(setq ir_hover_disallowed_point nil)
(setq ir_hover_failed nil)
(setq ir_last_managed_route nil)
(setq ir_request_committed nil)
(setq ir_request_recovery_available nil)

;; --- Command Registration ---
(axlCmdRegister "jm_iroute" (quote ir_main) ?cmdType "interactive"
  ?doneCmd (quote ir_done) ?cancelCmd (quote ir_cancel) ?undo t)

;; ==========================================================================
;; Typed request/lifecycle handoff owned by the route command
;; ==========================================================================
(procedure (ir_recovery_binding_p ir_rbp_binding)
  (prog nil
    (return (and (listp ir_rbp_binding) (equal (length ir_rbp_binding) 2)
                 (stringp (car ir_rbp_binding))
                 (greaterp (strlen (car ir_rbp_binding)) 0)
                 (leqp (strlen (car ir_rbp_binding)) 200)
                 (integerp (cadr ir_rbp_binding))
                 (greaterp (cadr ir_rbp_binding) 0)))))

(procedure (ir_set_pending_request ir_spr_width_mils ir_spr_binding ir_spr_token)
  (prog nil
    (when (or ir_pending_width_mils ir_pending_cancelled ir_request_active)
      (return nil))
    (unless (and (numberp ir_spr_width_mils)
                 (geqp ir_spr_width_mils 0.1)
                 (leqp ir_spr_width_mils 10000.0)
                 (ir_recovery_binding_p ir_spr_binding)
                 (stringp ir_spr_token) (greaterp (strlen ir_spr_token) 0)
                 (leqp (strlen ir_spr_token) 256))
      (return nil))
    (setq ir_pending_width_mils (float ir_spr_width_mils))
    (setq ir_pending_identity (list ir_spr_binding ir_spr_token))
    (setq ir_request_committed nil)
    (setq ir_request_recovery_available nil)
    (return t)
  )
)

(procedure (ir_clear_pending_request)
  (prog nil
    (setq ir_pending_width_mils nil)
    (setq ir_pending_identity nil)
    (setq ir_pending_cancelled nil)
    (return t)
  )
)

(procedure (ir_take_pending_request)
  (prog (ir_tpr_width)
    (setq ir_tpr_width ir_pending_width_mils)
    (setq ir_request_identity ir_pending_identity)
    (setq ir_pending_width_mils nil)
    (setq ir_pending_identity nil)
    (return ir_tpr_width)
  )
)

(procedure (ir_begin_interaction ir_bi_managed)
  (prog nil
    (setq ir_request_active (if ir_bi_managed then t else nil))
    (setq ir_request_committed nil)
    (setq ir_request_recovery_available nil)
    (return t)
  )
)

(procedure (ir_report_interaction_progress)
  (prog nil
    (unless ir_request_active (return t))
    (unless (and (isCallable (quote pdwecInteractiveRouteActiveP))
                 (pdwecInteractiveRouteActiveP)
                 (isCallable (quote pdwecInteractiveRouteProgress)))
      (return nil))
    (return (pdwecInteractiveRouteProgress))
  )
)

(procedure (ir_finish_interaction ir_fi_state ir_fi_message)
  (prog (ir_fi_managed ir_fi_result)
    (ir_stop_hover_observation)
    (setq ir_fi_managed ir_request_active)
    (setq ir_request_active nil)
    (unless ir_fi_managed (return t))
    (unless (isCallable (quote pdwecInteractiveRouteFinished))
      (return nil))
    (setq ir_fi_result
      (pdwecInteractiveRouteFinished ir_fi_state ir_fi_message))
    (unless ir_fi_result
      (axlMsgPut
        "ERROR: Interactive Route could not publish its terminal SDK lifecycle result.\n"))
    (return ir_fi_result)
  )
)

(procedure (ir_cancel_managed_request)
  (prog nil
    (when ir_pending_width_mils
      (setq ir_pending_width_mils nil)
      (setq ir_pending_identity nil)
      (setq ir_pending_cancelled t)
      (return t))
    (when ir_request_active
      ;; The managed cancellation caller owns the terminal receipt. Transfer
      ;; completion delivery before native teardown; direct native Cancel still
      ;; uses ir_cancel with its ordinary completion callback intact.
      (setq ir_request_active nil)
      (return (ir_cancel)))
    (return nil)
  )
)

(procedure (ir_point_p ir_pp_value)
  (prog nil
    (return
      (and (listp ir_pp_value)
           (geqp (length ir_pp_value) 2)
           (numberp (nth 0 ir_pp_value))
           (numberp (nth 1 ir_pp_value))))
  )
)

(procedure (ir_net_name_from_owner ir_nnfo_owner)
  (prog (ir_nnfo_net_result ir_nnfo_net ir_nnfo_name_result ir_nnfo_name)
    (unless ir_nnfo_owner (return nil))
    (setq ir_nnfo_net_result (errset (getq ir_nnfo_owner net) t))
    (unless ir_nnfo_net_result (return nil))
    (setq ir_nnfo_net (car ir_nnfo_net_result))
    (unless ir_nnfo_net (return nil))
    (setq ir_nnfo_name_result (errset (getq ir_nnfo_net name) t))
    (unless ir_nnfo_name_result (return nil))
    (setq ir_nnfo_name (car ir_nnfo_name_result))
    (return
      (if (and (stringp ir_nnfo_name)
               (greaterp (strlen ir_nnfo_name) 0))
      then ir_nnfo_name else nil))
  )
)

(procedure (ir_object_net_name ir_onn_object)
  (prog (ir_onn_name ir_onn_result ir_onn_branch ir_onn_parent)
    (setq ir_onn_result (errset (getq ir_onn_object objType) t))
    (when (and ir_onn_result
               (stringp (car ir_onn_result))
               (equal (lowerCase (car ir_onn_result)) "net"))
      (setq ir_onn_result (errset (getq ir_onn_object name) t))
      (when (and ir_onn_result
                 (stringp (car ir_onn_result))
                 (greaterp (strlen (car ir_onn_result)) 0))
        (return (car ir_onn_result))))

    (setq ir_onn_name (ir_net_name_from_owner ir_onn_object))
    (when ir_onn_name (return ir_onn_name))

    (setq ir_onn_result (errset (getq ir_onn_object branch) t))
    (when ir_onn_result
      (setq ir_onn_branch (car ir_onn_result))
      (setq ir_onn_name (ir_net_name_from_owner ir_onn_branch))
      (when ir_onn_name (return ir_onn_name)))

    (setq ir_onn_result (errset (getq ir_onn_object parent) t))
    (when ir_onn_result
      (setq ir_onn_parent (car ir_onn_result))
      (setq ir_onn_name (ir_net_name_from_owner ir_onn_parent))
      (when ir_onn_name (return ir_onn_name))
      (setq ir_onn_result (errset (getq ir_onn_parent branch) t))
      (when ir_onn_result
        (setq ir_onn_branch (car ir_onn_result))
        (setq ir_onn_name (ir_net_name_from_owner ir_onn_branch))
        (when ir_onn_name (return ir_onn_name))))
    (return nil)
  )
)

(procedure (ir_design_units)
  (prog (ir_du_result ir_du_value)
    (setq ir_du_result (errset (axlDBGetDesignUnits) t))
    (unless ir_du_result (return nil))
    (setq ir_du_value (car ir_du_result))
    (unless (and (listp ir_du_value)
                 (stringp (car ir_du_value)))
      (return nil))
    (return (lowerCase (car ir_du_value)))
  )
)

(procedure (ir_mils_to_design ir_mtd_mils)
  (prog (ir_mtd_units)
    (setq ir_mtd_units (ir_design_units))
    (cond
      ((member ir_mtd_units (list "mil" "mils"))
        (return (float ir_mtd_mils)))
      ((member ir_mtd_units (list "mm" "millimeter" "millimeters"))
        (return (times (float ir_mtd_mils) 0.0254)))
      (t (return nil)))
  )
)

(procedure (ir_design_to_mils ir_dtm_value)
  (prog (ir_dtm_units)
    (unless (numberp ir_dtm_value) (return nil))
    (setq ir_dtm_units (ir_design_units))
    (cond
      ((member ir_dtm_units (list "mil" "mils"))
        (return (float ir_dtm_value)))
      ((member ir_dtm_units (list "mm" "millimeter" "millimeters"))
        (return (quotient (float ir_dtm_value) 0.0254)))
      (t (return nil)))
  )
)

;; Allegro stores coordinates and widths at database precision. A 1e-9 user-
;; unit comparison is below that precision in both supported unit systems and
;; catches any material native readback mismatch without broadening geometry.
(procedure (ir_number_equal_p ir_nep_left ir_nep_right)
  (prog nil
    (return
      (and (numberp ir_nep_left)
           (numberp ir_nep_right)
           (leqp
             (abs (difference (float ir_nep_left) (float ir_nep_right)))
             0.000000001)))
  )
)

(procedure (ir_point_equal_p ir_pep_left ir_pep_right)
  (prog nil
    (return
      (and (ir_point_p ir_pep_left)
           (ir_point_p ir_pep_right)
           (ir_number_equal_p (nth 0 ir_pep_left) (nth 0 ir_pep_right))
           (ir_number_equal_p (nth 1 ir_pep_left) (nth 1 ir_pep_right))))
  )
)

(procedure (ir_distinct_endpoint_p ir_dep_candidate ir_dep_disallowed)
  (prog nil
    (return
      (and (ir_point_p ir_dep_candidate)
           (or (null ir_dep_disallowed)
               (not (ir_point_equal_p
                      ir_dep_candidate ir_dep_disallowed)))))
  )
)

;; ==========================================================================
;; Choose the current etch layer without opening an Allegro form.
;; Use the active etch layer only when it is visible. Otherwise use the first
;; visible etch layer in stack order. Never create on a hidden etch layer.
;; ==========================================================================
(procedure (ir_resolve_route_layer)
  (prog (ir_rrl_active_result ir_rrl_active ir_rrl_group_result
         ir_rrl_group ir_rrl_members_result ir_rrl_members ir_rrl_member
         ir_rrl_full ir_rrl_active_full ir_rrl_visible_result
         ir_rrl_visible_layers)
    (setq ir_rrl_active_result (errset (axlGetActiveLayer) t))
    (setq ir_rrl_active
      (if (and ir_rrl_active_result
               (stringp (car ir_rrl_active_result)))
      then (car ir_rrl_active_result) else nil))
    (setq ir_rrl_group_result
      (errset (axlGetParam "paramLayerGroup:ETCH") t))
    (unless (and ir_rrl_group_result (car ir_rrl_group_result))
      (return nil))
    (setq ir_rrl_group (car ir_rrl_group_result))
    (setq ir_rrl_members_result
      (errset (getq ir_rrl_group groupMembers) t))
    (unless (and ir_rrl_members_result
                 (listp (car ir_rrl_members_result)))
      (return nil))
    (setq ir_rrl_members (car ir_rrl_members_result))
    (setq ir_rrl_visible_layers nil)
    (foreach ir_rrl_member ir_rrl_members
      (when (stringp ir_rrl_member)
        (setq ir_rrl_full (strcat "ETCH/" ir_rrl_member))
        (setq ir_rrl_visible_result
          (errset (axlIsVisibleLayer ir_rrl_full) t))
        (when (and ir_rrl_visible_result (car ir_rrl_visible_result))
          (setq ir_rrl_visible_layers
            (append1 ir_rrl_visible_layers ir_rrl_full))
          (when (and ir_rrl_active
                     (or (equal (lowerCase ir_rrl_active)
                                (lowerCase ir_rrl_member))
                         (equal (lowerCase ir_rrl_active)
                                (lowerCase ir_rrl_full))))
            (setq ir_rrl_active_full ir_rrl_full)))))
    (when ir_rrl_active_full (return ir_rrl_active_full))
    (when ir_rrl_visible_layers
      (return (car ir_rrl_visible_layers)))
    (return nil)
  )
)

;; ==========================================================================
;; Restore initialized SKILL filter state; native commands own their UI filters.
;; ==========================================================================
(procedure (ir_restore_find_filter)
  (prog (ir_rff_set_result ir_rff_enabled_result ir_rff_buttons_result
         ir_rff_restored)
    (unless (and (isCallable (quote axlSetFindFilter))
                 (isCallable (quote axlGetFindFilter)))
      (return nil))
    (setq ir_rff_set_result
      (errset
        (axlSetFindFilter
          ?enabled
            (if ir_saved_find_enabled
            then (append (list "NOALL") ir_saved_find_enabled)
            else (list "NOALL"))
          ?onButtons
            (if ir_saved_find_buttons
            then (append (list "NOALL") ir_saved_find_buttons)
            else (list "NOALL")))
        t))
    ;; Allegro's executable example defines the getter mapping: no argument
    ;; returns ?enabled and t returns ?onButtons. Keep those independent states
    ;; in their matching fields.
    (setq ir_rff_enabled_result (errset (axlGetFindFilter) t))
    (setq ir_rff_buttons_result (errset (axlGetFindFilter t) t))
    (setq ir_rff_restored
      (and ir_rff_set_result (car ir_rff_set_result)
           ir_rff_enabled_result
           ir_rff_buttons_result
           (equal (car ir_rff_buttons_result) ir_saved_find_buttons)
           ;; Both getters are nil before any SKILL filter exists. They are
           ;; not a snapshot of the native command's Find panel. In that case
           ;; NOALL must leave no selectable SKILL types; initialized filters
           ;; still require exact enabled/options AND button restoration.
           (or (and (null ir_saved_find_enabled)
                    (null ir_saved_find_buttons))
               (equal (car ir_rff_enabled_result) ir_saved_find_enabled))))
    (unless ir_rff_restored
      (axlMsgPut
        (sprintf nil
          "Interactive Route Find-filter diagnostic: setter=%L; expected enabled=%L, onButtons=%L; observed enabled=%L, onButtons=%L.\n"
          ir_rff_set_result ir_saved_find_enabled ir_saved_find_buttons
          ir_rff_enabled_result ir_rff_buttons_result)))
    (return ir_rff_restored)
  )
)

(procedure (ir_restore_runtime_state)
  (prog (ir_rrs_find_restored ir_rrs_fill_result ir_rrs_drc_result)
    (ir_stop_hover_observation)
    (unless ir_runtime_state_saved (return t))
    (axlClearDynamics)
    (axlClearSelSet)
    (setq ir_rrs_find_restored (ir_restore_find_filter))
    (setq ir_rrs_fill_result
      (errset
        (axlDBControl (quote dynamicFillMode) ir_fill_mode)
        t))
    (setq ir_rrs_drc_result
      (errset
        (axlDBControl (quote drcEnable) ir_drc_enabled)
        t))
    (setq ir_runtime_state_saved nil)
    (setq ir_first_pick_clear_requested nil)
    (setq ir_selection_ordinal 0)
    (unless ir_rrs_find_restored
      (axlMsgPut
        "WARNING: Interactive Route could not restore the prior Find filter.\n"))
    (unless ir_rrs_fill_result
      (axlMsgPut
        "WARNING: Interactive Route could not restore dynamic-fill state.\n"))
    (unless ir_rrs_drc_result
      (axlMsgPut
        "WARNING: Interactive Route could not restore DRC state.\n"))
    (axlFlushDisplay)
    (return (and ir_rrs_find_restored ir_rrs_fill_result ir_rrs_drc_result))
  )
)

(procedure (ir_enable_supported_objects)
  (prog (ir_eso_result)
    (setq ir_eso_result
      (errset
        (axlSetFindFilter
          ?enabled
            (list "NOALL" "COMPONENTS" "SYMBOLS" "NETS" "PINS"
                  "VIAS" "CLINES" "CLINESEGS" "LINES"
                  "NOEQUIVLOGIC")
          ?onButtons
            (list "NOALL" "COMPONENTS" "SYMBOLS" "NETS" "PINS"
                  "VIAS" "CLINES" "CLINESEGS" "LINES"))
        t))
    (return (and ir_eso_result (car ir_eso_result)))
  )
)

;; ==========================================================================
;; Start and mark the one-shot route transaction
;; ==========================================================================
(procedure (ir_prepare_transaction)
  (prog (ir_pt_start_result ir_pt_mark_result ir_pt_rollback_result)
    (when (null ir_mark)
      (setq ir_pt_start_result (errset (axlDBTransactionStart) t))
      (unless (and ir_pt_start_result
                   (numberp (car ir_pt_start_result)))
        (return nil))
      (setq ir_mark (car ir_pt_start_result)))
    (setq ir_pt_mark_result
      (errset (axlDBTransactionMark ir_mark) t))
    (unless (and ir_pt_mark_result (car ir_pt_mark_result))
      (setq ir_pt_rollback_result
        (errset (axlDBTransactionRollback ir_mark) t))
      (unless (and ir_pt_rollback_result (car ir_pt_rollback_result))
        (axlMsgPut
          "ERROR: Interactive Route could not mark or roll back its transaction; inspect the board.\n"))
      (setq ir_mark nil)
      (return nil))
    (return t)
  )
)

;; ==========================================================================
;; Revert a route that Allegro rejected after the per-route mark
;; ==========================================================================
(procedure (ir_revert_failed_route)
  (prog (ir_rfr_result)
    (unless ir_mark (return nil))
    (setq ir_rfr_result
      (errset (axlDBTransactionOops ir_mark) t))
    (unless (and ir_rfr_result (car ir_rfr_result))
      (axlMsgPut
        "ERROR: Interactive Route could not undo a failed route; inspect the board.\n"))
    (return (and ir_rfr_result (car ir_rfr_result)))
  )
)

(procedure (ir_rollback_transaction)
  (prog (ir_rt_result)
    (unless ir_mark (return t))
    (setq ir_rt_result
      (errset (axlDBTransactionRollback ir_mark) t))
    (setq ir_mark nil)
    (return (and ir_rt_result (car ir_rt_result)))
  )
)

(procedure (ir_event_point ir_ep_event)
  (prog (ir_ep_result ir_ep_point)
    (setq ir_ep_result (errset (getq ir_ep_event xySnap) t))
    (setq ir_ep_point
      (if (and ir_ep_result (ir_point_p (car ir_ep_result)))
      then (car ir_ep_result) else nil))
    (unless ir_ep_point
      (setq ir_ep_result (errset (getq ir_ep_event xy) t))
      (setq ir_ep_point
        (if (and ir_ep_result (ir_point_p (car ir_ep_result)))
        then (car ir_ep_result) else nil)))
    (unless ir_ep_point (return nil))
    (return
      (list (float (nth 0 ir_ep_point))
            (float (nth 1 ir_ep_point))))
  )
)

;; Resolve exactly one object under the pointer through the command's current
;; supported-object Find filter. Empty board points remain invalid.
(procedure (ir_supported_object_at_point ir_soap_xy)
  (prog (ir_soap_result ir_soap_selection)
    (unless (ir_point_p ir_soap_xy) (return nil))
    (setq ir_soap_result
      (errset (axlGetSelSet (axlSingleSelectPoint ir_soap_xy)) t))
    (unless ir_soap_result
      (axlClearSelSet)
      (return (quote failed)))
    (setq ir_soap_selection (car ir_soap_result))
    (axlClearSelSet)
    (unless (and (listp ir_soap_selection) ir_soap_selection)
      (return nil))
    (return (car ir_soap_selection))
  )
)

(procedure (ir_supported_object_kind ir_sok_object)
  (prog (ir_sok_result ir_sok_type ir_sok_parent ir_sok_parent_type)
    (unless ir_sok_object (return "none"))
    (setq ir_sok_result (errset (getq ir_sok_object objType) t))
    (unless (and ir_sok_result (stringp (car ir_sok_result)))
      (return "none"))
    (setq ir_sok_type (lowerCase (car ir_sok_result)))
    (case ir_sok_type
      ("component" (return "component"))
      ("symbol" (return "symbol"))
      ("net" (return "net"))
      ("pin" (return "pin"))
      ("via" (return "via"))
      ("path" (return "cline"))
      ("cline" (return "cline"))
      ("segment" (return "cline_segment"))
      ("clineseg" (return "cline_segment"))
      ("cline segment" (return "cline_segment"))
      ("line"
        (setq ir_sok_result (errset (getq ir_sok_object parent) t))
        (when ir_sok_result (setq ir_sok_parent (car ir_sok_result)))
        (when ir_sok_parent
          (setq ir_sok_result (errset (getq ir_sok_parent objType) t))
          (when (and ir_sok_result (stringp (car ir_sok_result)))
            (setq ir_sok_parent_type (lowerCase (car ir_sok_result)))))
        (return
          (if (member ir_sok_parent_type (list "path" "cline"))
          then "cline_segment" else "line")))
      (t (return "none")))
  )
)

(procedure (ir_current_viewport)
  (prog (ir_cv_result ir_cv_box ir_cv_lower ir_cv_upper ir_cv_units)
    (setq ir_cv_result (errset (axlWindowBoxGet) t))
    (unless (and ir_cv_result
                 (listp (car ir_cv_result))
                 (geqp (length (car ir_cv_result)) 2))
      (return nil))
    (setq ir_cv_box (car ir_cv_result))
    (setq ir_cv_lower (nth 0 ir_cv_box))
    (setq ir_cv_upper (nth 1 ir_cv_box))
    (setq ir_cv_units (ir_design_units))
    (unless (and (ir_point_p ir_cv_lower) (ir_point_p ir_cv_upper)
                 (stringp ir_cv_units))
      (return nil))
    (return
      (list
        (float (nth 0 ir_cv_lower)) (float (nth 1 ir_cv_lower))
        (float (nth 0 ir_cv_upper)) (float (nth 1 ir_cv_upper))
        ir_cv_units))
  )
)

(procedure (ir_publish_feedback ir_pf_kind ir_pf_pointer_state
             ir_pf_object_kind ir_pf_point ir_pf_viewport
             ir_pf_ordinal ir_pf_reason @optional (ir_pf_capture nil))
  (prog nil
    (unless ir_request_active (return t))
    (unless (and (isCallable (quote pdwecInteractiveRouteActiveP))
                 (isCallable (quote pdwecInteractiveRouteFeedback))
                 (pdwecInteractiveRouteActiveP))
      (return nil))
    (return
      (pdwecInteractiveRouteFeedback
        ir_pf_kind ir_pf_pointer_state ir_pf_object_kind ir_pf_point
        ir_pf_viewport ir_pf_ordinal ir_pf_reason ir_pf_capture))
  )
)

(procedure (ir_publish_viewport_if_changed)
  (prog (ir_pvic_viewport ir_pvic_event)
    (setq ir_pvic_viewport (ir_current_viewport))
    (unless ir_pvic_viewport (return nil))
    (when (equal ir_pvic_viewport ir_last_viewport_box) (return t))
    (setq ir_last_viewport_box ir_pvic_viewport)
    (setq ir_viewport_sequence (add1 ir_viewport_sequence))
    (setq ir_pvic_event
      (append ir_pvic_viewport (list ir_viewport_sequence)))
    (return
      (ir_publish_feedback
        "viewport_changed" "unknown" "none" nil ir_pvic_event nil ""))
  )
)

;; Hover evidence is a fresh native observation, not a cached MOVE verdict.
;; Cadence processes UI timers inside axlEnterEvent, including while stationary.
;; One 50 ms producer matches the bridge's interactive service cadence; MOVE
;; cannot add a second stream. Queue and delivery time remain in the SDK age.
;; No DBID survives the callback. The existing SDK retains its identity,
;; foreground, viewport, pointer-alignment and freshness checks unchanged.
(procedure (ir_observe_hover_pointer)
  (prog (ir_ohp_capture ir_ohp_point ir_ohp_object ir_ohp_kind ir_ohp_state)
    (setq ir_ohp_capture
      (and (isCallable (quote pdwecInteractiveRouteBeginObservation))
           (pdwecInteractiveRouteBeginObservation)))
    (unless (ir_publish_viewport_if_changed) (return nil))
    (setq ir_ohp_point (axlCursorGet nil))
    (unless (ir_point_p ir_ohp_point) (return nil))
    (setq ir_ohp_object (ir_supported_object_at_point ir_ohp_point))
    (when (equal ir_ohp_object (quote failed)) (return nil))
    (setq ir_ohp_kind (ir_supported_object_kind ir_ohp_object))
    (setq ir_ohp_state
      (if (and ir_ohp_object (nequal ir_ohp_kind "none")
               (ir_distinct_endpoint_p
                 ir_ohp_point ir_hover_disallowed_point))
      then "valid" else "invalid"))
    (return
      (ir_publish_feedback
        "pointer" ir_ohp_state ir_ohp_kind
        (append ir_ohp_point (list (ir_design_units)))
        nil nil "" ir_ohp_capture))))

(procedure (ir_stop_hover_observation)
  (prog (ir_sho_timer ir_sho_result)
    (setq ir_sho_timer ir_hover_timer)
    ;; Fence any already queued callback before removing its timer.
    (setq ir_hover_timer nil)
    (setq ir_hover_disallowed_point nil)
    (when ir_sho_timer
      (setq ir_sho_result (errset (axlUIWTimerRemove ir_sho_timer) t))
      (unless (and ir_sho_result (car ir_sho_result))
        (axlMsgPut "WARNING: Interactive Route hover timer removal failed; its callback is fenced.\n")))
    (return t)))

(procedure (ir_hover_timer_callback ir_htc_window ir_htc_timer ir_htc_elapsed)
  (prog (ir_htc_result)
    (unless (eq ir_htc_timer ir_hover_timer) (return nil))
    (unless (and ir_request_active (not ir_done_flag)
                 (member ir_selection_ordinal (list 1 2))
                 (pdwecInteractiveRouteActiveP))
      (ir_stop_hover_observation)
      (return nil))
    (setq ir_htc_result (errset (ir_observe_hover_pointer) t))
    (unless (and ir_htc_result (car ir_htc_result))
      (setq ir_hover_failed t)
      (ir_stop_hover_observation)
      (axlMsgPut "ERROR: Interactive Route could not acquire fresh hover evidence; no further picks will be applied.\n"))
    (return t)))

(procedure (ir_clear_first_pick_request)
  (prog (ir_cfpr_wake)
    (unless (and ir_request_active (equal ir_selection_ordinal 2))
      (return nil))
    (setq ir_first_pick_clear_requested t)
    ;; A flag alone leaves axlEnterEvent waiting and discards the next pick
    ;; merely to observe that flag. Wake this wait without cancelling the route;
    ;; the event loop consumes the clear request before inspecting returned data.
    (setq ir_cfpr_wake (errset (axlCancelEnterFun) t))
    (unless (and ir_cfpr_wake (car ir_cfpr_wake))
      (setq ir_first_pick_clear_requested nil)
      (return nil))
    (return t)
  )
)

;; Return (clicked_xy net_name object_kind), 'cleared, 'failed, or nil on
;; cancel. A second pick at the accepted first-pick coordinate is rejected
;; before mutation. Route geometry and native highlight geometry remain absent.
(procedure (ir_pick_object ir_po_prompt ir_po_ordinal ir_po_disallowed_point)
  (prog (ir_po_result ir_po_timer_result)
    (ir_stop_hover_observation)
    (setq ir_hover_failed nil)
    (setq ir_hover_disallowed_point ir_po_disallowed_point)
    (when ir_request_active
      (setq ir_po_timer_result
        (errset (axlUIWTimerAdd nil 50 nil (quote ir_hover_timer_callback)) t))
      (unless (and ir_po_timer_result (car ir_po_timer_result))
        (axlMsgPut "ERROR: Interactive Route could not start native hover observation.\n")
        (return (quote failed)))
      (setq ir_hover_timer (car ir_po_timer_result)))
    ;; Stop on all normal/error returns; cancellation also fences the timer
    ;; through ir_restore_runtime_state before leaving the native event loop.
    (setq ir_po_result
      (errset (ir_pick_object_events ir_po_prompt ir_po_ordinal ir_po_disallowed_point) t))
    (ir_stop_hover_observation)
    (return (if ir_po_result then (car ir_po_result) else (quote failed)))))

(procedure (ir_pick_object_events ir_po_prompt ir_po_ordinal ir_po_disallowed_point)
  (prog (ir_po_event_mask ir_po_event_result ir_po_event ir_po_type_result
         ir_po_event_type ir_po_point ir_po_waiting
         ir_po_obj ir_po_xy ir_po_net_name ir_po_kind
         ir_po_point_with_units ir_po_capture)
    (axlMsgPut ir_po_prompt)
    (unless (ir_publish_viewport_if_changed)
      (return (quote failed)))
    (setq ir_po_event_mask
      (list (quote PICK) (quote PICK_EXTEND) (quote PICK_TOGGLE)
            (quote MOVE)))
    (setq ir_po_waiting t)
    (while ir_po_waiting
      (setq ir_po_event_result
        ;; The one-time message above owns the prompt. Re-supplying it here
        ;; prints it again after every MOVE event on large boards.
        (errset (axlEnterEvent ir_po_event_mask nil nil) t))
      (when ir_done_flag (return nil))
      (when ir_hover_failed (return (quote failed)))
      (when ir_first_pick_clear_requested
        (setq ir_first_pick_clear_requested nil)
        (return (quote cleared)))
      (unless (and ir_po_event_result (car ir_po_event_result))
        (axlMsgPut
          "ERROR: Interactive Route could not receive the Allegro pick event.\n")
        (return (quote failed)))
      (setq ir_po_event (car ir_po_event_result))
      (setq ir_po_type_result (errset (getq ir_po_event type) t))
      (setq ir_po_event_type
        (if ir_po_type_result then (car ir_po_type_result) else nil))
      ;; The barrier precedes native point/object inspection, not publication.
      ;; It bounds the current hit-test at the event's point, not the OS event's
      ;; creation time. The GUI must still match the live canvas and pointer.
      (setq ir_po_capture
        (and ir_request_active
             (isCallable (quote pdwecInteractiveRouteBeginObservation))
             (pdwecInteractiveRouteBeginObservation)))
      (unless (ir_publish_viewport_if_changed)
        (return (quote failed)))
      (cond
        ((member ir_po_event_type
                 (list (quote PICK) (quote PICK_EXTEND)
                       (quote PICK_TOGGLE)))
          (setq ir_po_point (ir_event_point ir_po_event))
          (setq ir_po_obj
            (if ir_po_point
            then (ir_supported_object_at_point ir_po_point) else nil))
          (when (equal ir_po_obj (quote failed))
            (return (quote failed)))
          (setq ir_po_kind (ir_supported_object_kind ir_po_obj))
          (setq ir_po_point_with_units
            (if ir_po_point
            then (append ir_po_point (list (ir_design_units))) else nil))
          (cond
            ((not (ir_distinct_endpoint_p
                    ir_po_point ir_po_disallowed_point))
              (unless
                (ir_publish_feedback
                  "selection_rejected" "invalid" ir_po_kind
                  ir_po_point_with_units nil ir_po_ordinal
                  "same_location" ir_po_capture)
                (return (quote failed)))
              (axlMsgPut
                "Interactive Route: Choose a different endpoint.\n")
              (axlUIWBeep))
            ((and ir_po_obj (nequal ir_po_kind "none"))
              (unless
                (ir_publish_feedback
                  "selection_accepted" "valid" ir_po_kind
                  ir_po_point_with_units nil ir_po_ordinal "" ir_po_capture)
                (return (quote failed)))
              (setq ir_po_xy ir_po_point)
              (setq ir_po_waiting nil))
            (t
              (unless
                (ir_publish_feedback
                  "selection_rejected" "invalid" "none"
                  ir_po_point_with_units nil ir_po_ordinal
                  "unsupported_object" ir_po_capture)
                (return (quote failed)))
              (axlMsgPut
                "Interactive Route: Select a supported board object.\n")
              (axlUIWBeep))))
        ((or (equal ir_po_event_type (quote DONE))
             (equal ir_po_event_type (quote CANCEL)))
          (return nil))))
    (unless (and ir_po_xy ir_po_obj)
      (return (quote failed)))
    (setq ir_po_net_name (ir_object_net_name ir_po_obj))
    (return (list ir_po_xy ir_po_net_name ir_po_kind))
  )
)

;; ==========================================================================
;; Create one H-first route after both picks. No dynamic route preview exists.
;; ==========================================================================
(procedure (ir_axis_aligned_p ir_aap_start ir_aap_end)
  (prog nil
    (return
      (or (equal (fix (times 10000 (nth 0 ir_aap_start)))
                 (fix (times 10000 (nth 0 ir_aap_end))))
          (equal (fix (times 10000 (nth 1 ir_aap_start)))
                 (fix (times 10000 (nth 1 ir_aap_end))))))
  )
)

(procedure (ir_create_route ir_cr_start_xy ir_cr_end_xy ir_cr_width
                            ir_cr_layer ir_cr_net)
  (prog (ir_cr_axis_aligned ir_cr_mid_xy ir_cr_path_result ir_cr_path_id
         ir_cr_line_result ir_cr_end_result ir_cr_create_result)
    (setq ir_cr_axis_aligned
      (ir_axis_aligned_p ir_cr_start_xy ir_cr_end_xy))
    (if ir_cr_axis_aligned
    then
      (setq ir_cr_create_result
        (errset
          (axlDBCreateLine
            (list ir_cr_start_xy ir_cr_end_xy)
            ir_cr_width ir_cr_layer ir_cr_net)
          t))
    else
      (setq ir_cr_mid_xy
        (list (nth 0 ir_cr_end_xy) (nth 1 ir_cr_start_xy)))
      (setq ir_cr_path_result
        (errset (axlPathStart (list ir_cr_start_xy) ir_cr_width) t))
      (unless (and ir_cr_path_result (car ir_cr_path_result))
        (return nil))
      (setq ir_cr_path_id (car ir_cr_path_result))
      (setq ir_cr_line_result
        (errset
          (axlPathLine ir_cr_path_id ir_cr_width ir_cr_mid_xy)
          t))
      (unless (and ir_cr_line_result (car ir_cr_line_result))
        (return nil))
      (setq ir_cr_end_result
        (errset
          (axlPathLine ir_cr_path_id ir_cr_width ir_cr_end_xy)
          t))
      (unless (and ir_cr_end_result (car ir_cr_end_result))
        (return nil))
      (setq ir_cr_create_result
        (errset
          (axlDBCreatePath ir_cr_path_id ir_cr_layer ir_cr_net)
          t)))
    ;; Both creation APIs return one nested collection of created dbids;
    ;; errset adds the outer result list. Return the one path dbid, never the
    ;; collection itself, so callers can perform exact native readback.
    (return
      (and ir_cr_create_result
           (listp (car ir_cr_create_result))
           (listp (car (car ir_cr_create_result)))
           (equal (length (car (car ir_cr_create_result))) 1)
           (car (car (car ir_cr_create_result)))))
  )
)

;; ==========================================================================
;; Rollback-only native route self-test
;; --------------------------------------------------------------------------
;; This diagnostic is intentionally non-interactive. It derives two distinct
;; points from the current design extents, creates through ir_create_route,
;; reads the native line back, and rolls the transaction back before returning.
;; It contains no board, filename, coordinate, or fixture-specific identity.
;; ==========================================================================
(procedure (ir_self_test_points ir_stp_width)
  (prog (ir_stp_result ir_stp_extents ir_stp_lower ir_stp_upper
         ir_stp_x_span ir_stp_y_span ir_stp_min_span
         ir_stp_start ir_stp_end)
    ;; The design dbid owns the drawing extents in its bBox.  axlDBGetExtents
    ;; does not return that two-point box consistently across Allegro releases.
    (setq ir_stp_result
      (errset (getq (axlDBGetDesign) bBox) t))
    (unless (and ir_stp_result
                 (listp (car ir_stp_result))
                 (equal (length (car ir_stp_result)) 2))
      (return nil))
    (setq ir_stp_extents (car ir_stp_result))
    (setq ir_stp_lower (nth 0 ir_stp_extents))
    (setq ir_stp_upper (nth 1 ir_stp_extents))
    (unless (and (ir_point_p ir_stp_lower) (ir_point_p ir_stp_upper))
      (return nil))
    (setq ir_stp_x_span
      (difference (nth 0 ir_stp_upper) (nth 0 ir_stp_lower)))
    (setq ir_stp_y_span
      (difference (nth 1 ir_stp_upper) (nth 1 ir_stp_lower)))
    (setq ir_stp_min_span (times ir_stp_width 8.0))
    ;; Exercise the same two-segment H route used by non-axis-aligned live
    ;; picks. The prior axis-aligned fixture could not detect path regressions.
    (unless (and (greaterp ir_stp_x_span ir_stp_min_span)
                 (greaterp ir_stp_y_span ir_stp_min_span))
      (return nil))
    (setq ir_stp_start
      (list
        (plus (nth 0 ir_stp_lower) (times ir_stp_x_span 0.375))
        (plus (nth 1 ir_stp_lower) (times ir_stp_y_span 0.45))))
    (setq ir_stp_end
      (list
        (plus (nth 0 ir_stp_lower) (times ir_stp_x_span 0.625))
        (plus (nth 1 ir_stp_lower) (times ir_stp_y_span 0.55))))
    (unless (and (ir_point_p ir_stp_start)
                 (ir_point_p ir_stp_end)
                 (null (ir_point_equal_p ir_stp_start ir_stp_end)))
      (return nil))
    (return (list ir_stp_start ir_stp_end))
  )
)

(procedure (ir_two_segment_terminals ir_tst_first ir_tst_second)
  (prog (ir_tst_a ir_tst_b ir_tst_c ir_tst_d)
    (unless (and (listp ir_tst_first)
                 (equal (length ir_tst_first) 2)
                 (ir_point_p (nth 0 ir_tst_first))
                 (ir_point_p (nth 1 ir_tst_first))
                 (listp ir_tst_second)
                 (equal (length ir_tst_second) 2)
                 (ir_point_p (nth 0 ir_tst_second))
                 (ir_point_p (nth 1 ir_tst_second)))
      (return nil))
    (setq ir_tst_a (nth 0 ir_tst_first))
    (setq ir_tst_b (nth 1 ir_tst_first))
    (setq ir_tst_c (nth 0 ir_tst_second))
    (setq ir_tst_d (nth 1 ir_tst_second))
    (cond
      ((ir_point_equal_p ir_tst_a ir_tst_c)
        (return (list ir_tst_b ir_tst_d)))
      ((ir_point_equal_p ir_tst_a ir_tst_d)
        (return (list ir_tst_b ir_tst_c)))
      ((ir_point_equal_p ir_tst_b ir_tst_c)
        (return (list ir_tst_a ir_tst_d)))
      ((ir_point_equal_p ir_tst_b ir_tst_d)
        (return (list ir_tst_a ir_tst_c)))
      (t (return nil)))
  )
)

(procedure (ir_line_readback ir_lr_created)
  (prog (ir_lr_object ir_lr_result ir_lr_type ir_lr_segment
         ir_lr_segments ir_lr_layer ir_lr_parent ir_lr_width
         ir_lr_start_end ir_lr_segment_start_end ir_lr_segment_width
         ir_lr_segment_layer ir_lr_geometry ir_lr_points ir_lr_net)
    (setq ir_lr_object ir_lr_created)
    (unless ir_lr_object (return nil))
    (setq ir_lr_result (errset (getq ir_lr_object objType) t))
    (unless (and ir_lr_result (stringp (car ir_lr_result)))
      (return nil))
    (setq ir_lr_type (lowerCase (car ir_lr_result)))
    (cond
      ((equal ir_lr_type "line")
        (setq ir_lr_segments (list ir_lr_object)))
      ((equal ir_lr_type "path")
        (setq ir_lr_result (errset (getq ir_lr_object segments) t))
        (unless (and ir_lr_result
                     (listp (car ir_lr_result))
                     (member (length (car ir_lr_result)) (list 1 2)))
          (return nil))
        (setq ir_lr_segments (car ir_lr_result)))
      (t (return nil)))
    (setq ir_lr_segment (car ir_lr_segments))
    (setq ir_lr_result (errset (getq ir_lr_object layer) t))
    (when (and ir_lr_result (stringp (car ir_lr_result)))
      (setq ir_lr_layer (car ir_lr_result)))
    (unless ir_lr_layer
      (setq ir_lr_result (errset (getq ir_lr_segment layer) t))
      (when (and ir_lr_result (stringp (car ir_lr_result)))
        (setq ir_lr_layer (car ir_lr_result))))
    (unless ir_lr_layer
      (setq ir_lr_result (errset (getq ir_lr_segment parent) t))
      (when (and ir_lr_result (car ir_lr_result))
        (setq ir_lr_parent (car ir_lr_result))
        (setq ir_lr_result (errset (getq ir_lr_parent layer) t))
        (when (and ir_lr_result (stringp (car ir_lr_result)))
          (setq ir_lr_layer (car ir_lr_result)))))
    (foreach ir_lr_segment ir_lr_segments
      ;; Only straight segments have a complete identity in this route owner.
      ;; An arc with the same terminals is not the line that PD created.
      (setq ir_lr_result (errset (getq ir_lr_segment objType) t))
      (unless (and ir_lr_result (equal (car ir_lr_result) "line"))
        (return nil))
      (setq ir_lr_result (errset (getq ir_lr_segment startEnd) t))
      (unless (and ir_lr_result (listp (car ir_lr_result))
                   (equal (length (car ir_lr_result)) 2)
                   (ir_point_p (nth 0 (car ir_lr_result)))
                   (ir_point_p (nth 1 (car ir_lr_result))))
        (return nil))
      (setq ir_lr_points (car ir_lr_result))
      (setq ir_lr_result (errset (getq ir_lr_segment width) t))
      (unless (and ir_lr_result (numberp (car ir_lr_result)))
        (return nil))
      (setq ir_lr_segment_width (car ir_lr_result))
      (if ir_lr_width
      then
        (unless (ir_number_equal_p ir_lr_width ir_lr_segment_width)
          (return nil))
      else
        (setq ir_lr_width ir_lr_segment_width))
      (setq ir_lr_result (errset (getq ir_lr_segment layer) t))
      (when (and ir_lr_result (stringp (car ir_lr_result)))
        (setq ir_lr_segment_layer (car ir_lr_result))
        (if ir_lr_layer
        then
          (unless (equal (lowerCase ir_lr_layer)
                         (lowerCase ir_lr_segment_layer))
            (return nil))
        else
          (setq ir_lr_layer ir_lr_segment_layer)))
      ;; Copy every segment's coordinates and width, not just terminal points
      ;; or the first width. No mutable database list enters the Undo receipt.
      (setq ir_lr_geometry
        (cons (list
          (list (nth 0 (nth 0 ir_lr_points)) (nth 1 (nth 0 ir_lr_points)))
          (list (nth 0 (nth 1 ir_lr_points)) (nth 1 (nth 1 ir_lr_points)))
          ir_lr_segment_width ir_lr_layer)
          ir_lr_geometry)))
    (unless (and ir_lr_width (stringp ir_lr_layer)) (return nil))
    (if (equal (length ir_lr_segments) 1)
    then
      (setq ir_lr_result (errset (getq (car ir_lr_segments) startEnd) t))
      (unless (and ir_lr_result
                   (listp (car ir_lr_result))
                   (equal (length (car ir_lr_result)) 2)
                   (ir_point_p (nth 0 (car ir_lr_result)))
                   (ir_point_p (nth 1 (car ir_lr_result))))
        (return nil))
      (setq ir_lr_start_end (car ir_lr_result))
    else
      (setq ir_lr_result (errset (getq (nth 0 ir_lr_segments) startEnd) t))
      (unless ir_lr_result (return nil))
      (setq ir_lr_segment_start_end (car ir_lr_result))
      (setq ir_lr_result (errset (getq (nth 1 ir_lr_segments) startEnd) t))
      (unless ir_lr_result (return nil))
      (setq ir_lr_start_end
        (ir_two_segment_terminals
          ir_lr_segment_start_end (car ir_lr_result)))
      (unless ir_lr_start_end (return nil)))
    (setq ir_lr_result (errset (getq ir_lr_object net) t))
    (unless ir_lr_result (return nil))
    (setq ir_lr_net (car ir_lr_result))
    (when ir_lr_net
      (setq ir_lr_result (errset (getq ir_lr_net name) t))
      (unless (and ir_lr_result (stringp (car ir_lr_result)))
        (return nil))
      (setq ir_lr_net (car ir_lr_result)))
    (return
      (list ir_lr_object ir_lr_width ir_lr_layer ir_lr_start_end
            (length ir_lr_segments) (reverse ir_lr_geometry) ir_lr_net))
  )
)

(procedure (ir_route_readback_matches_p ir_rrm_readback ir_rrm_start
             ir_rrm_end ir_rrm_width ir_rrm_layer ir_rrm_segment_count)
  (prog (ir_rrm_points)
    (unless (and (listp ir_rrm_readback)
                 (equal (length ir_rrm_readback) 7))
      (return nil))
    (setq ir_rrm_points (nth 3 ir_rrm_readback))
    (return
      (and (ir_number_equal_p ir_rrm_width (nth 1 ir_rrm_readback))
           (stringp (nth 2 ir_rrm_readback))
           (equal (lowerCase ir_rrm_layer)
                  (lowerCase (nth 2 ir_rrm_readback)))
           (equal (nth 4 ir_rrm_readback) ir_rrm_segment_count)
           (or
             (and (ir_point_equal_p ir_rrm_start (nth 0 ir_rrm_points))
                  (ir_point_equal_p ir_rrm_end (nth 1 ir_rrm_points)))
             (and (ir_point_equal_p ir_rrm_start (nth 1 ir_rrm_points))
                  (ir_point_equal_p ir_rrm_end (nth 0 ir_rrm_points))))))
  )
)

;; A command-local DBID cannot identify a later Undo target. Stamp only the
;; newly created route inside its creation transaction, then retain scalars.
;; The SDK owns session/generation identity; Allegro owns fresh object lookup.
(procedure (ir_stamp_route_identity ir_sri_object ir_sri_token)
  (prog (ir_sri_result ir_sri_dict)
    (setq ir_sri_result (errset
      (progn
        (unless (and (stringp ir_sri_token) (greaterp (strlen ir_sri_token) 0)
                     (leqp (strlen ir_sri_token) 256))
          (error "Invalid route recovery identity."))
        (setq ir_sri_dict (axlDBGetPropDictEntry "PD_INTERACTIVE_ROUTE_ID"))
        (unless ir_sri_dict
          (setq ir_sri_dict
            (axlDBCreatePropDictEntry "PD_INTERACTIVE_ROUTE_ID" "STRING" t)))
        ;; PropDict.readOnly describes the database object's attributes, not
        ;; whether axlDBAddProp can attach its property. Native attachment and
        ;; exact value readback below establish that capability.
        (unless (and ir_sri_dict (equal (getq ir_sri_dict dataType) "STRING")
                     (null (getq (getq ir_sri_object prop) PD_INTERACTIVE_ROUTE_ID)))
          (error "Route identity property is unavailable or already occupied."))
        (unless (axlDBAddProp (list ir_sri_object)
                  (list "PD_INTERACTIVE_ROUTE_ID" ir_sri_token))
          (error "Allegro did not attach route identity."))
        (unless (axlDBRefreshId ir_sri_object)
          (error "Route identity refresh failed."))
        (equal (getq (getq ir_sri_object prop) PD_INTERACTIVE_ROUTE_ID)
               ir_sri_token)) t))
    (return (and ir_sri_result (car ir_sri_result)))))

(procedure (ir_find_route_identity ir_fri_token ir_fri_net_name)
  (prog (ir_fri_result ir_fri_net ir_fri_branches ir_fri_branch
         ir_fri_object ir_fri_matches ir_fri_count)
    ;; A failed or over-bound enumeration is not evidence of absence. Find
    ;; settings and visibility do not participate in this database traversal.
    (setq ir_fri_result (errset
      (progn
        (unless (and (axlDBGetDesign) (stringp ir_fri_token)
                     (greaterp (strlen ir_fri_token) 0))
          (error "Route identity lookup is unavailable."))
        (if ir_fri_net_name
        then
          (setq ir_fri_net (axlDBFindByName (quote net) ir_fri_net_name))
          (when ir_fri_net
            (unless (axlDBRefreshId ir_fri_net) (error "Route net refresh failed."))
            (setq ir_fri_branches (getq ir_fri_net branches)))
        else
          (setq ir_fri_branches (axlDBGetLonelyBranches)))
        (unless (listp ir_fri_branches) (error "Invalid route branches."))
        (setq ir_fri_count 0)
        (foreach ir_fri_branch ir_fri_branches
          (setq ir_fri_count (add1 ir_fri_count))
          (when (greaterp ir_fri_count 50000) (error "Route lookup exceeded its bound."))
          (unless (axlDBRefreshId ir_fri_branch) (error "Route branch refresh failed."))
          (foreach ir_fri_object (getq ir_fri_branch children)
            (setq ir_fri_count (add1 ir_fri_count))
            (when (greaterp ir_fri_count 50000) (error "Route lookup exceeded its bound."))
            (when (member (getq ir_fri_object objType) (list "path" "line"))
              (unless (axlDBRefreshId ir_fri_object) (error "Route object refresh failed."))
              (when (equal (getq (getq ir_fri_object prop) PD_INTERACTIVE_ROUTE_ID)
                           ir_fri_token)
                (unless (member ir_fri_object ir_fri_matches)
                  (setq ir_fri_matches (cons ir_fri_object ir_fri_matches)))))))
        ir_fri_matches) t))
    (return (if ir_fri_result then (car ir_fri_result) else (quote failed)))))

;; The C# Undo action never invokes Allegro's global undo. Re-resolve the
;; tagged route in the same SDK binding and revalidate every stored scalar.
(procedure (ir_forget_last_managed_route)
  (prog nil
    (setq ir_last_managed_route nil)
    (return t)
  )
)

(procedure (ir_remember_last_managed_route ir_rlmr_readback ir_rlmr_binding ir_rlmr_token)
  (prog (ir_rlmr_result ir_rlmr_units)
    (unless (and (listp ir_rlmr_readback)
                 (equal (length ir_rlmr_readback) 7)
                 (ir_recovery_binding_p ir_rlmr_binding)
                 (stringp ir_rlmr_token))
      (return nil))
    (setq ir_rlmr_result (errset
      (getq (getq (car ir_rlmr_readback) prop) PD_INTERACTIVE_ROUTE_ID) t))
    (setq ir_rlmr_units (ir_design_units))
    (unless (and ir_rlmr_result (equal (car ir_rlmr_result) ir_rlmr_token)
                 ir_rlmr_units)
      (return nil))
    ;; The readback owner already copied segment coordinates and widths.
    ;; Deliberately replace its transient object handle with nil.
    (setq ir_last_managed_route
      (list ir_rlmr_binding (cons nil (cdr ir_rlmr_readback))
            ir_rlmr_token ir_rlmr_units))
    (return t)
  )
)

(procedure (ir_route_after_state_matches_p ir_rasm_current ir_rasm_expected)
  (prog nil
    ;; Exact ordered segments (including the bend), width, layer and net.
    ;; Unknown or changed after-state must never authorize deletion.
    (return (and (listp ir_rasm_current) (listp ir_rasm_expected)
                 (equal (length ir_rasm_current) 7)
                 (equal (length ir_rasm_expected) 7)
                 (equal (cdr ir_rasm_current) (cdr ir_rasm_expected))))
  )
)

(procedure (ir_managed_route_recovery_state ir_mrrs_binding)
  (prog nil
    (return (list ir_request_committed
      (and ir_request_recovery_available ir_last_managed_route
           (ir_recovery_binding_p ir_mrrs_binding)
           (equal (car ir_last_managed_route) ir_mrrs_binding))))
  )
)

(procedure (ir_record_committed_route ir_rcr_readback ir_rcr_exact
             ir_rcr_managed ir_rcr_display_ok ir_rcr_binding ir_rcr_token)
  (prog nil
    (setq ir_request_committed t)
    (setq ir_request_recovery_available
      (and ir_rcr_managed ir_rcr_exact
           (ir_remember_last_managed_route ir_rcr_readback ir_rcr_binding ir_rcr_token)))
    (if (and ir_rcr_exact ir_rcr_display_ok
             (or (null ir_rcr_managed) ir_request_recovery_available))
    then (return (quote succeeded))
    else (return (quote failed)))
  )
)

(procedure (ir_object_removed_p ir_orp_object)
  (prog (ir_orp_refresh_all_result ir_orp_refresh_result
         ir_orp_attributes_result)
    (unless ir_orp_object (return nil))
    (setq ir_orp_refresh_all_result (errset (axlDBRefreshId nil) t))
    (unless (and ir_orp_refresh_all_result
                 (car ir_orp_refresh_all_result))
      (return nil))
    (setq ir_orp_refresh_result
      (errset (axlDBRefreshId ir_orp_object) t))
    (unless ir_orp_refresh_result (return nil))
    (when (null (car ir_orp_refresh_result)) (return t))
    ;; Used only before the creating command returns. Across command exits,
    ;; an invalid handle is not removal proof; Undo uses fresh tagged lookup.
    (setq ir_orp_attributes_result
      (errset (getq (car ir_orp_refresh_result) ?) t))
    (return
      (and ir_orp_attributes_result
           (null (car ir_orp_attributes_result))))
  )
)

(procedure (ir_undo_last_managed_route ir_ulmr_binding)
  (prog (ir_ulmr_record ir_ulmr_design ir_ulmr_expected ir_ulmr_points
         ir_ulmr_refresh_result ir_ulmr_current ir_ulmr_start_result
         ir_ulmr_token ir_ulmr_mark_result ir_ulmr_delete_result
         ir_ulmr_removed_before_commit ir_ulmr_commit_result
         ir_ulmr_rollback_result ir_ulmr_removed_after_commit
         ir_ulmr_visible_result ir_ulmr_flush_result ir_ulmr_remaining)
    (when (or ir_pending_width_mils ir_pending_cancelled
              ir_request_active ir_mark)
      (return (list (quote rejected)
        "Interactive Route undo refused while another route operation is active.")))
    (unless (and ir_last_managed_route
                 (listp ir_last_managed_route)
                 (equal (length ir_last_managed_route) 4))
      (return (list (quote rejected)
        "There is no verified PD-created route available to undo.")))
    (unless (and (axlDBGetDesign) (axlOKToProceed))
      (return (list (quote rejected)
        "Interactive Route undo requires one idle open board.")))
    (setq ir_ulmr_record ir_last_managed_route)
    (setq ir_ulmr_design (nth 0 ir_ulmr_record))
    (setq ir_ulmr_expected (nth 1 ir_ulmr_record))
    (unless (and (ir_recovery_binding_p ir_ulmr_binding)
                 (equal ir_ulmr_design ir_ulmr_binding)
                 (equal (nth 3 ir_ulmr_record) (ir_design_units))
                 (listp ir_ulmr_expected)
                 (equal (length ir_ulmr_expected) 7))
      (ir_forget_last_managed_route)
      (return (list (quote rejected)
        "The verified route belongs to a different board session.")))
    (setq ir_ulmr_refresh_result
      (ir_find_route_identity (nth 2 ir_ulmr_record) (nth 6 ir_ulmr_expected)))
    (unless (and (listp ir_ulmr_refresh_result)
                 (equal (length ir_ulmr_refresh_result) 1))
      (ir_forget_last_managed_route)
      (return (list (quote rejected)
        "The verified route is missing, ambiguous, or unavailable.")))
    (setq ir_ulmr_current
      (ir_line_readback (car ir_ulmr_refresh_result)))
    (setq ir_ulmr_points (nth 3 ir_ulmr_expected))
    (unless
      (and (listp ir_ulmr_points)
           (equal (length ir_ulmr_points) 2)
           (ir_route_readback_matches_p
             ir_ulmr_current
             (nth 0 ir_ulmr_points) (nth 1 ir_ulmr_points)
             (nth 1 ir_ulmr_expected) (nth 2 ir_ulmr_expected)
             (nth 4 ir_ulmr_expected))
           (ir_route_after_state_matches_p ir_ulmr_current ir_ulmr_expected))
      (ir_forget_last_managed_route)
      (return (list (quote rejected)
        "The route changed after creation, so PD left the board untouched.")))

    (setq ir_ulmr_start_result (errset (axlDBTransactionStart) t))
    (unless (and ir_ulmr_start_result
                 (numberp (car ir_ulmr_start_result)))
      (return (list (quote failed)
        "Interactive Route undo could not start its transaction.")))
    (setq ir_ulmr_token (car ir_ulmr_start_result))
    (setq ir_ulmr_mark_result
      (errset (axlDBTransactionMark ir_ulmr_token) t))
    (unless (and ir_ulmr_mark_result (car ir_ulmr_mark_result))
      (setq ir_ulmr_rollback_result
        (errset (axlDBTransactionRollback ir_ulmr_token) t))
      (return (list (quote failed)
        "Interactive Route undo could not mark its transaction.")))
    (setq ir_ulmr_delete_result
      (errset (axlDeleteObject (car ir_ulmr_refresh_result)) t))
    (setq ir_ulmr_remaining
      (ir_find_route_identity (nth 2 ir_ulmr_record) (nth 6 ir_ulmr_expected)))
    (setq ir_ulmr_removed_before_commit
      (and ir_ulmr_delete_result (car ir_ulmr_delete_result)
           (listp ir_ulmr_remaining) (null ir_ulmr_remaining)))
    (unless ir_ulmr_removed_before_commit
      (setq ir_ulmr_rollback_result
        (errset (axlDBTransactionRollback ir_ulmr_token) t))
      (return (list (quote failed)
        "Interactive Route undo could not prove deletion and rolled back.")))
    (setq ir_ulmr_commit_result
      (errset (axlDBTransactionCommit ir_ulmr_token) t))
    (unless (and ir_ulmr_commit_result (car ir_ulmr_commit_result))
      (setq ir_ulmr_rollback_result
        (errset (axlDBTransactionRollback ir_ulmr_token) t))
      (return (list (quote failed)
        "Interactive Route undo could not commit and attempted rollback.")))
    (setq ir_ulmr_remaining
      (ir_find_route_identity (nth 2 ir_ulmr_record) (nth 6 ir_ulmr_expected)))
    (setq ir_ulmr_removed_after_commit
      (and (listp ir_ulmr_remaining) (null ir_ulmr_remaining)))
    (setq ir_ulmr_visible_result (errset (axlVisibleUpdate t) t))
    (setq ir_ulmr_flush_result (errset (axlFlushDisplay) t))
    (ir_forget_last_managed_route)
    (unless (and ir_ulmr_removed_after_commit
                 ir_ulmr_visible_result (car ir_ulmr_visible_result)
                 ir_ulmr_flush_result (car ir_ulmr_flush_result))
      (return (list (quote failed)
        "The route was deleted, but native post-undo verification did not complete.")))
    (return (list (quote succeeded)
      "The exact verified Interactive Route was removed transactionally."))
  )
)

(procedure (ir_verify_guarded_undo_self_test
             ir_vgust_start ir_vgust_end ir_vgust_width ir_vgust_layer)
  (prog (ir_vgust_start_result ir_vgust_token ir_vgust_mark_result
         ir_vgust_created ir_vgust_readback ir_vgust_commit_result
         ir_vgust_refresh_all_result ir_vgust_refresh_created_result
         ir_vgust_post_readback ir_vgust_undo_result ir_vgust_changed
         ir_vgust_corner ir_vgust_state ir_vgust_binding ir_vgust_identity)
    (when ir_last_managed_route
      (return (list (quote rejected)
        "Guarded-undo self-test refused to replace a user route receipt.")))
    (setq ir_vgust_identity (sprintf nil "%L" (gensym)))
    (setq ir_vgust_binding (list ir_vgust_identity 1))
    (setq ir_vgust_start_result (errset (axlDBTransactionStart) t))
    (unless (and ir_vgust_start_result
                 (numberp (car ir_vgust_start_result)))
      (return (list (quote failed)
        "Guarded-undo self-test could not start its transaction.")))
    (setq ir_vgust_token (car ir_vgust_start_result))
    (setq ir_vgust_mark_result
      (errset (axlDBTransactionMark ir_vgust_token) t))
    (unless (and ir_vgust_mark_result (car ir_vgust_mark_result))
      (errset (axlDBTransactionRollback ir_vgust_token) t)
      (return (list (quote failed)
        "Guarded-undo self-test could not mark its transaction.")))
    (setq ir_vgust_created
      (ir_create_route
        ir_vgust_start ir_vgust_end ir_vgust_width ir_vgust_layer nil))
    (when ir_vgust_created
      (setq ir_vgust_readback (ir_line_readback ir_vgust_created)))
    (unless
      (ir_route_readback_matches_p
        ir_vgust_readback ir_vgust_start ir_vgust_end ir_vgust_width
        ir_vgust_layer 2)
      (errset (axlDBTransactionRollback ir_vgust_token) t)
      (return (list (quote failed)
        "Guarded-undo self-test route readback failed before commit.")))
    (unless (ir_stamp_route_identity ir_vgust_created ir_vgust_identity)
      (errset (axlDBTransactionRollback ir_vgust_token) t)
      (return (list (quote failed)
        "Guarded-undo self-test could not attach native identity.")))
    (setq ir_vgust_commit_result
      (errset (axlDBTransactionCommit ir_vgust_token) t))
    (unless (and ir_vgust_commit_result (car ir_vgust_commit_result))
      (errset (axlDBTransactionRollback ir_vgust_token) t)
      (return (list (quote failed)
        "Guarded-undo self-test route commit failed.")))
    (setq ir_vgust_refresh_all_result (errset (axlDBRefreshId nil) t))
    (setq ir_vgust_refresh_created_result
      (errset (axlDBRefreshId ir_vgust_created) t))
    (when (and ir_vgust_refresh_created_result
               (car ir_vgust_refresh_created_result))
      (setq ir_vgust_post_readback
        (ir_line_readback (car ir_vgust_refresh_created_result))))
    (unless
      (and ir_vgust_refresh_all_result (car ir_vgust_refresh_all_result)
           (ir_route_readback_matches_p
             ir_vgust_post_readback ir_vgust_start ir_vgust_end
             ir_vgust_width ir_vgust_layer 2)
           (ir_remember_last_managed_route
             ir_vgust_post_readback ir_vgust_binding ir_vgust_identity))
      (return (list (quote failed)
        "Guarded-undo self-test could not arm exact post-commit identity.")))
    ;; A live matching route does not excuse a different SDK session or board
    ;; generation. These rejected requests must leave the native route intact.
    (foreach ir_vgust_changed
      (list (list (strcat ir_vgust_identity "-other") 1)
            (list ir_vgust_identity 2))
      (setq ir_vgust_undo_result (ir_undo_last_managed_route ir_vgust_changed))
      (unless (and (equal (car ir_vgust_undo_result) (quote rejected))
                   (ir_route_after_state_matches_p
                     (ir_line_readback ir_vgust_created) ir_vgust_post_readback)
                   (ir_remember_last_managed_route
                     ir_vgust_post_readback ir_vgust_binding ir_vgust_identity))
        (return (list (quote failed)
          "Guarded Undo did not preserve the route for a stale SDK binding."))))
    ;; Exercise the actual Undo admission with changed after-state evidence.
    ;; The current object stays untouched; equality must reject either side
    ;; changing its interior corner or net, even with identical terminals.
    (setq ir_vgust_corner
      (list (plus (nth 0 ir_vgust_start) ir_vgust_width)
            (plus (nth 1 ir_vgust_start) ir_vgust_width)))
    (setq ir_vgust_changed
      (list (nth 0 ir_vgust_post_readback) (nth 1 ir_vgust_post_readback)
            (nth 2 ir_vgust_post_readback) (nth 3 ir_vgust_post_readback)
            (nth 4 ir_vgust_post_readback)
            (list (list ir_vgust_start ir_vgust_corner ir_vgust_width ir_vgust_layer)
                  (list ir_vgust_corner ir_vgust_end ir_vgust_width ir_vgust_layer))
            (nth 6 ir_vgust_post_readback)))
    (ir_remember_last_managed_route ir_vgust_changed ir_vgust_binding ir_vgust_identity)
    (setq ir_vgust_undo_result (ir_undo_last_managed_route ir_vgust_binding))
    (unless (and (equal (car ir_vgust_undo_result) (quote rejected))
                 (ir_line_readback (nth 0 ir_vgust_post_readback)))
      (return (list (quote failed)
        "Guarded Undo did not preserve geometry after a changed-corner receipt.")))
    (setq ir_vgust_changed
      (append (list (nth 0 ir_vgust_post_readback) (nth 1 ir_vgust_post_readback)
                    (nth 2 ir_vgust_post_readback) (nth 3 ir_vgust_post_readback)
                    (nth 4 ir_vgust_post_readback) (nth 5 ir_vgust_post_readback))
              (list "changed-net-negative-control")))
    (ir_remember_last_managed_route ir_vgust_changed ir_vgust_binding ir_vgust_identity)
    (setq ir_vgust_undo_result (ir_undo_last_managed_route ir_vgust_binding))
    (unless (and (equal (car ir_vgust_undo_result) (quote rejected))
                 (ir_line_readback (nth 0 ir_vgust_post_readback)))
      (return (list (quote failed)
        "Guarded Undo did not preserve geometry after a changed-net receipt.")))
    ;; Inject failure at the completion decision, not in database creation.
    (setq ir_vgust_state
      (ir_record_committed_route ir_vgust_post_readback t t nil
        ir_vgust_binding ir_vgust_identity))
    (unless (and (equal ir_vgust_state (quote failed))
                 (nth 0 (ir_managed_route_recovery_state ir_vgust_binding))
                 (nth 1 (ir_managed_route_recovery_state ir_vgust_binding)))
      (return (list (quote failed)
        "Post-commit display failure lost its exact recovery receipt.")))
    (setq ir_vgust_undo_result (ir_undo_last_managed_route ir_vgust_binding))
    (return ir_vgust_undo_result)
  )
)

(procedure (ir_restore_self_test_controls ir_rstc_fill ir_rstc_drc)
  (prog (ir_rstc_fill_result ir_rstc_drc_result
         ir_rstc_fill_verify_result ir_rstc_drc_verify_result)
    (setq ir_rstc_fill_result
      (errset (axlDBControl (quote dynamicFillMode) ir_rstc_fill) t))
    (setq ir_rstc_drc_result
      (errset (axlDBControl (quote drcEnable) ir_rstc_drc) t))
    (unless (and ir_rstc_fill_result ir_rstc_drc_result)
      (return nil))
    ;; Setting each control to its saved value a second time returns the
    ;; current value and therefore proves the first restore took effect.
    (setq ir_rstc_fill_verify_result
      (errset (axlDBControl (quote dynamicFillMode) ir_rstc_fill) t))
    (setq ir_rstc_drc_verify_result
      (errset (axlDBControl (quote drcEnable) ir_rstc_drc) t))
    (return
      (and ir_rstc_fill_verify_result
           (equal (car ir_rstc_fill_verify_result) ir_rstc_fill)
           ir_rstc_drc_verify_result
           (equal (car ir_rstc_drc_verify_result) ir_rstc_drc)))
  )
)

(procedure (ir_run_native_self_test ir_rnst_width_mils)
  (prog (ir_rnst_design_result ir_rnst_ok_result ir_rnst_width ir_rnst_layer
         ir_rnst_points ir_rnst_start ir_rnst_end ir_rnst_fill_result
         ir_rnst_drc_result ir_rnst_fill_before ir_rnst_drc_before
         ir_rnst_start_result ir_rnst_token ir_rnst_mark_result
         ir_rnst_created ir_rnst_readback ir_rnst_observed_width
         ir_rnst_observed_layer ir_rnst_observed_points
         ir_rnst_width_verified ir_rnst_layer_verified
         ir_rnst_endpoints_verified ir_rnst_rollback_result
         ir_rnst_controls_restored ir_rnst_flush_result ir_rnst_refresh_result
         ir_rnst_removed_attributes_result
         ir_rnst_rollback_restored ir_rnst_observed_width_mils
         ir_rnst_start_x_mils ir_rnst_start_y_mils
         ir_rnst_end_x_mils ir_rnst_end_y_mils
         ir_rnst_guarded_undo_result)
    (unless (and (numberp ir_rnst_width_mils)
                 (geqp ir_rnst_width_mils 0.1)
                 (leqp ir_rnst_width_mils 10000.0))
      (return (list (quote rejected)
        "Interactive Route self-test requires a valid width.")))
    (when (or ir_pending_width_mils ir_pending_cancelled
              ir_request_active ir_mark ir_last_managed_route)
      (return (list (quote rejected)
        "Interactive Route self-test refused while another route operation is active.")))
    (setq ir_rnst_design_result (errset (axlDBGetDesign) t))
    (setq ir_rnst_ok_result (errset (axlOKToProceed) t))
    (unless (and ir_rnst_design_result (car ir_rnst_design_result)
                 ir_rnst_ok_result (car ir_rnst_ok_result))
      (return (list (quote rejected)
        "Interactive Route self-test requires one idle open board.")))
    (setq ir_rnst_width (ir_mils_to_design ir_rnst_width_mils))
    (setq ir_rnst_layer (ir_resolve_route_layer))
    (unless (and ir_rnst_width (greaterp ir_rnst_width 0.0)
                 (stringp ir_rnst_layer))
      (return (list (quote rejected)
        "Interactive Route self-test could not resolve width and etch layer.")))
    (setq ir_rnst_points (ir_self_test_points ir_rnst_width))
    (unless ir_rnst_points
      (return (list (quote rejected)
        "Interactive Route self-test could not derive two distinct board points.")))
    (setq ir_rnst_start (nth 0 ir_rnst_points))
    (setq ir_rnst_end (nth 1 ir_rnst_points))

    (setq ir_rnst_fill_result
      (errset (axlDBControl (quote dynamicFillMode) nil) t))
    (setq ir_rnst_drc_result
      (errset (axlDBControl (quote drcEnable) nil) t))
    (unless (and ir_rnst_fill_result ir_rnst_drc_result)
      (when ir_rnst_fill_result
        (errset
          (axlDBControl (quote dynamicFillMode)
            (car ir_rnst_fill_result)) t))
      (when ir_rnst_drc_result
        (errset
          (axlDBControl (quote drcEnable) (car ir_rnst_drc_result)) t))
      (return (list (quote failed)
        "Interactive Route self-test could not preserve native controls.")))
    (setq ir_rnst_fill_before (car ir_rnst_fill_result))
    (setq ir_rnst_drc_before (car ir_rnst_drc_result))

    (setq ir_rnst_start_result (errset (axlDBTransactionStart) t))
    (unless (and ir_rnst_start_result
                 (numberp (car ir_rnst_start_result)))
      (setq ir_rnst_controls_restored
        (ir_restore_self_test_controls
          ir_rnst_fill_before ir_rnst_drc_before))
      (return (list (quote failed)
        "Interactive Route self-test could not start its rollback transaction.")))
    (setq ir_rnst_token (car ir_rnst_start_result))
    (setq ir_rnst_mark_result
      (errset (axlDBTransactionMark ir_rnst_token) t))
    (unless (and ir_rnst_mark_result (car ir_rnst_mark_result))
      (setq ir_rnst_rollback_result
        (errset (axlDBTransactionRollback ir_rnst_token) t))
      (setq ir_rnst_controls_restored
        (ir_restore_self_test_controls
          ir_rnst_fill_before ir_rnst_drc_before))
      (return (list (quote failed)
        "Interactive Route self-test could not mark its rollback transaction.")))

    (setq ir_rnst_created
      (ir_create_route
        ir_rnst_start ir_rnst_end ir_rnst_width ir_rnst_layer nil))
    (when ir_rnst_created
      (setq ir_rnst_readback (ir_line_readback ir_rnst_created)))
    (when ir_rnst_readback
      (setq ir_rnst_observed_width (nth 1 ir_rnst_readback))
      (setq ir_rnst_observed_layer (nth 2 ir_rnst_readback))
      (setq ir_rnst_observed_points (nth 3 ir_rnst_readback))
      (setq ir_rnst_width_verified
        (ir_number_equal_p ir_rnst_width ir_rnst_observed_width))
      (setq ir_rnst_layer_verified
        (equal (lowerCase ir_rnst_layer)
               (lowerCase ir_rnst_observed_layer)))
      (setq ir_rnst_endpoints_verified
        (and
          (equal (nth 4 ir_rnst_readback) 2)
          (or
            (and (ir_point_equal_p ir_rnst_start
                   (nth 0 ir_rnst_observed_points))
                 (ir_point_equal_p ir_rnst_end
                   (nth 1 ir_rnst_observed_points)))
            (and (ir_point_equal_p ir_rnst_start
                   (nth 1 ir_rnst_observed_points))
                 (ir_point_equal_p ir_rnst_end
                   (nth 0 ir_rnst_observed_points)))))))

    (setq ir_rnst_rollback_result
      (errset (axlDBTransactionRollback ir_rnst_token) t))
    (setq ir_rnst_controls_restored
      (ir_restore_self_test_controls
        ir_rnst_fill_before ir_rnst_drc_before))
    ;; Refresh the database after rollback before asking whether the created
    ;; dbid still resolves. Allegro otherwise may retain a stale cached dbid.
    (setq ir_rnst_flush_result
      (errset (axlDBRefreshId nil) t))
    (when ir_rnst_created
      (setq ir_rnst_refresh_result
        (errset (axlDBRefreshId ir_rnst_created) t)))
    ;; Allegro 25.1 refreshes a deleted object as dbid:removed rather than nil.
    ;; The special ? attribute query is nil only after that dbid is out of
    ;; scope; keep it wrapped so an API error cannot be accepted as deletion.
    (when (and ir_rnst_refresh_result (car ir_rnst_refresh_result))
      (setq ir_rnst_removed_attributes_result
        (errset (getq (car ir_rnst_refresh_result) ?) t)))
    (setq ir_rnst_rollback_restored
      (and ir_rnst_created
           ir_rnst_rollback_result
           (car ir_rnst_rollback_result)
           ir_rnst_flush_result
           (car ir_rnst_flush_result)
           ir_rnst_refresh_result
           (or (null (car ir_rnst_refresh_result))
               (and ir_rnst_removed_attributes_result
                    (null (car ir_rnst_removed_attributes_result))))))
    (cond
      ((null ir_rnst_created)
        (return (list (quote failed)
          "Interactive Route self-test could not create its rollback-only route.")))
      ((null ir_rnst_readback)
        (return (list (quote failed)
          "Interactive Route self-test could not read back the created route.")))
      ((null ir_rnst_width_verified)
        (return (list (quote failed)
          "Interactive Route self-test width readback did not match exactly.")))
      ((null ir_rnst_layer_verified)
        (return (list (quote failed)
          "Interactive Route self-test layer readback did not match exactly.")))
      ((null ir_rnst_endpoints_verified)
        (return (list (quote failed)
          "Interactive Route self-test endpoint readback did not match exactly.")))
      ((null ir_rnst_rollback_restored)
        (return
          (list (quote failed)
            (sprintf nil
              "Interactive Route self-test rollback proof failed (rollback=%L, refreshAll=%L, refreshCreated=%L)."
              ir_rnst_rollback_result ir_rnst_flush_result
              ir_rnst_refresh_result))))
      ((null ir_rnst_controls_restored)
        (return (list (quote failed)
          "Interactive Route self-test could not prove native controls were restored."))))

    (setq ir_rnst_guarded_undo_result
      (ir_verify_guarded_undo_self_test
        ir_rnst_start ir_rnst_end ir_rnst_width ir_rnst_layer))
    (unless (and (listp ir_rnst_guarded_undo_result)
                 (equal (nth 0 ir_rnst_guarded_undo_result)
                        (quote succeeded)))
      (return
        (list (quote failed)
          (if (and (listp ir_rnst_guarded_undo_result)
                   (stringp (nth 1 ir_rnst_guarded_undo_result)))
          then (nth 1 ir_rnst_guarded_undo_result)
          else "Interactive Route guarded-undo verification failed."))))

    (setq ir_rnst_observed_width_mils
      (ir_design_to_mils ir_rnst_observed_width))
    (setq ir_rnst_start_x_mils
      (ir_design_to_mils (nth 0 ir_rnst_start)))
    (setq ir_rnst_start_y_mils
      (ir_design_to_mils (nth 1 ir_rnst_start)))
    (setq ir_rnst_end_x_mils
      (ir_design_to_mils (nth 0 ir_rnst_end)))
    (setq ir_rnst_end_y_mils
      (ir_design_to_mils (nth 1 ir_rnst_end)))
    (unless (and ir_rnst_observed_width_mils ir_rnst_start_x_mils
                 ir_rnst_start_y_mils ir_rnst_end_x_mils
                 ir_rnst_end_y_mils)
      (return (list (quote failed)
        "Interactive Route self-test could not publish canonical mil evidence.")))
    (return
      (list (quote succeeded)
        "Interactive Route self-test verified rollback and exact guarded undo."
        (float ir_rnst_width_mils)
        ir_rnst_observed_width_mils
        ir_rnst_start_x_mils ir_rnst_start_y_mils
        ir_rnst_end_x_mils ir_rnst_end_y_mils
        t t t t t t t))
  )
)

(procedure (ir_pick_and_route ir_pr_layer ir_pr_width)
  (prog (ir_pr_start ir_pr_end ir_pr_start_xy ir_pr_end_xy ir_pr_start_net
         ir_pr_end_net ir_pr_net ir_pr_created ir_pr_readback
         ir_pr_expected_segment_count ir_pr_waiting_for_end)
    (setq ir_pr_waiting_for_end nil)
    (while (null ir_pr_waiting_for_end)
      (setq ir_selection_ordinal 1)
      (setq ir_pr_start
        (ir_pick_object
          "Interactive Route: Pick the first supported object..." 1 nil))
      (when (equal ir_pr_start (quote failed))
        (return (quote failed)))
      (when (or ir_done_flag (null ir_pr_start))
        (return (quote cancelled)))
      (setq ir_pr_start_xy (nth 0 ir_pr_start))
      (setq ir_pr_start_net (nth 1 ir_pr_start))
      (setq ir_selection_ordinal 2)
      (unless (ir_report_interaction_progress)
        (axlMsgPut
          "ERROR: Interactive Route could not publish its second-pick lifecycle phase.\n")
        (return (quote failed)))
      (setq ir_pr_end
        (ir_pick_object
          "Interactive Route: Pick the second supported object..."
          2 ir_pr_start_xy))
      (if (equal ir_pr_end (quote cleared))
      then
        (setq ir_pr_start nil)
        (setq ir_pr_start_xy nil)
        (setq ir_pr_start_net nil)
      else
        (setq ir_pr_waiting_for_end t)))
    (when (equal ir_pr_end (quote failed))
      (return (quote failed)))
    (when (or ir_done_flag (null ir_pr_end))
      (return (quote cancelled)))
    (setq ir_pr_end_xy (nth 0 ir_pr_end))
    (setq ir_pr_end_net (nth 1 ir_pr_end))
    (cond
      ((and ir_pr_start_net ir_pr_end_net
            (equal ir_pr_start_net ir_pr_end_net))
        (setq ir_pr_net ir_pr_start_net))
      ((and ir_pr_start_net ir_pr_end_net)
        (setq ir_pr_net nil)
        (axlMsgPut
          "Interactive Route: Endpoints are on different nets; creating the requested unassigned etch line.\n"))
      (ir_pr_start_net
        (setq ir_pr_net ir_pr_start_net))
      (ir_pr_end_net
        (setq ir_pr_net ir_pr_end_net))
      (t
        (setq ir_pr_net nil)))
    (unless (ir_prepare_transaction)
      (axlMsgPut
        "ERROR: Interactive Route could not start its board transaction.\n")
      (axlUIWBeep)
      (return (quote failed)))
    (setq ir_pr_created
      (ir_create_route
        ir_pr_start_xy ir_pr_end_xy ir_pr_width ir_pr_layer ir_pr_net))
    (unless ir_pr_created
      (ir_revert_failed_route)
      (axlMsgPut
        "ERROR: Interactive Route could not create the requested route.\n")
      (axlUIWBeep)
      (return (quote failed)))
    (setq ir_pr_readback (ir_line_readback ir_pr_created))
    (setq ir_pr_expected_segment_count
      (if (ir_axis_aligned_p ir_pr_start_xy ir_pr_end_xy)
      then 1 else 2))
    (unless
      (ir_route_readback_matches_p
        ir_pr_readback ir_pr_start_xy ir_pr_end_xy ir_pr_width ir_pr_layer
        ir_pr_expected_segment_count)
      (ir_revert_failed_route)
      (axlMsgPut
        "ERROR: Interactive Route native readback did not match the requested route; the route was rolled back.\n")
      (axlUIWBeep)
      (return (quote failed)))
    (when ir_request_active
      (unless (and (ir_recovery_binding_p (car ir_request_identity))
                   (ir_stamp_route_identity ir_pr_created (cadr ir_request_identity)))
        (ir_revert_failed_route)
        (axlMsgPut "ERROR: Interactive Route could not attach recovery identity; route rolled back.\n")
        (return (quote failed))))
    (setq ir_route_count 1)
    (return (list (quote created) ir_pr_created ir_pr_start_xy ir_pr_end_xy
                  ir_pr_expected_segment_count))
  )
)

;; ==========================================================================
;; Main entry point: consume the C# width, perform one two-pick route, and end.
;; ==========================================================================
(procedure (ir_main @rest ir_m_option)
  (prog (ir_m_request ir_m_managed
         ir_m_find_enabled_result ir_m_find_buttons_result
         ir_m_fill_result ir_m_drc_result ir_m_width_mils ir_m_width
         ir_m_layer ir_m_route_result ir_m_created ir_m_commit_result
         ir_m_post_readback ir_m_post_readback_ok ir_m_refresh_all_result
         ir_m_refresh_created_result ir_m_restore_result
         ir_m_layer_visible_result ir_m_visible_result ir_m_flush_result
         ir_m_rollback_ok ir_m_completion_state)
    (setq ir_done_flag nil)
    (setq ir_route_count 0)
    (setq ir_mark nil)
    (setq ir_runtime_state_saved nil)
    (setq ir_first_pick_clear_requested nil)
    (setq ir_selection_ordinal 0)
    (setq ir_viewport_sequence 0)
    (setq ir_last_viewport_box nil)
    (ir_stop_hover_observation)
    (setq ir_hover_failed nil)
    (when ir_pending_cancelled
      (setq ir_pending_cancelled nil)
      (setq ir_done_flag t)
      (axlMsgPut
        "Interactive Route: Cancelled before the first native pick.\n")
      (return))
    (setq ir_m_request (ir_take_pending_request))
    (if ir_m_request
    then
      (setq ir_m_width_mils ir_m_request)
      (setq ir_m_managed t)
    else
      (setq ir_m_width_mils 5.0)
      (setq ir_m_managed nil))
    (ir_begin_interaction ir_m_managed)
    (setq ir_m_width (ir_mils_to_design ir_m_width_mils))
    (unless (and ir_m_width (greaterp ir_m_width 0.0))
      (ir_finish_interaction (quote failed)
        "Interactive Route could not convert the C# trace width to the current design units.")
      (axlMsgPut
        "ERROR: Interactive Route cannot convert the C# trace width to the current design units.\n")
      (return))
    (setq ir_m_layer (ir_resolve_route_layer))
    (unless ir_m_layer
      (ir_finish_interaction (quote failed)
        "Interactive Route could not resolve an etch layer.")
      (axlMsgPut
        "ERROR: Interactive Route could not resolve an etch layer.\n")
      (return))

    (setq ir_m_find_enabled_result (errset (axlGetFindFilter) t))
    (setq ir_m_find_buttons_result (errset (axlGetFindFilter t) t))
    (unless (and ir_m_find_enabled_result ir_m_find_buttons_result)
      (ir_finish_interaction (quote failed)
        "Interactive Route could not preserve the prior Find filter.")
      (axlMsgPut
        "ERROR: Interactive Route could not read the prior Find filter.\n")
      (return))
    (setq ir_saved_find_enabled (car ir_m_find_enabled_result))
    (setq ir_saved_find_buttons (car ir_m_find_buttons_result))
    (setq ir_m_fill_result
      (errset (axlDBControl (quote dynamicFillMode) nil) t))
    (setq ir_m_drc_result
      (errset (axlDBControl (quote drcEnable) nil) t))
    (unless (and ir_m_fill_result ir_m_drc_result)
      (when ir_m_fill_result
        (axlDBControl (quote dynamicFillMode) (car ir_m_fill_result)))
      (when ir_m_drc_result
        (axlDBControl (quote drcEnable) (car ir_m_drc_result)))
      (unless (ir_restore_find_filter)
        (axlMsgPut
          "WARNING: Interactive Route could not restore the prior Find filter.\n"))
      (ir_finish_interaction (quote failed)
        "Interactive Route could not prepare native board controls.")
      (axlMsgPut
        "ERROR: Interactive Route could not prepare native board controls.\n")
      (return))
    (setq ir_fill_mode (car ir_m_fill_result))
    (setq ir_drc_enabled (car ir_m_drc_result))
    (setq ir_runtime_state_saved t)

    (unless (ir_enable_supported_objects)
      (axlMsgPut
        "ERROR: Interactive Route could not enable its supported-object Find filter.\n")
      (axlUIWBeep)
      (ir_restore_runtime_state)
      (setq ir_done_flag t)
      (ir_finish_interaction (quote failed)
        "Interactive Route could not enable its supported-object Find filter.")
      (return))

    (axlMsgPut
      (sprintf nil
        "Interactive Route: C# width %.4g mil, layer %s. Windows feedback identifies valid picks; route geometry stays hidden until pick two.\n"
        ir_m_width_mils ir_m_layer))
    (setq ir_m_route_result (ir_pick_and_route ir_m_layer ir_m_width))
    (when ir_done_flag (return))
    (cond
      ((and (listp ir_m_route_result)
            (equal (car ir_m_route_result) (quote created)))
        (setq ir_m_created (nth 1 ir_m_route_result))
        (setq ir_m_commit_result
          (errset (axlDBTransactionCommit ir_mark) t))
        (if (and ir_m_commit_result (car ir_m_commit_result))
        then
          (setq ir_mark nil)
          (setq ir_request_committed t)
          ;; Restoration includes fallible display calls. Keep following the
          ;; committed-state path even if one raises, so exact recovery can
          ;; still be read back and retained independently of that failure.
          (setq ir_m_restore_result (errset (ir_restore_runtime_state) t))
          (setq ir_m_restore_result
            (and ir_m_restore_result (car ir_m_restore_result)))
          (setq ir_m_refresh_all_result (errset (axlDBRefreshId nil) t))
          (setq ir_m_refresh_created_result
            (errset (axlDBRefreshId ir_m_created) t))
          (when (and ir_m_refresh_created_result
                     (car ir_m_refresh_created_result))
            (setq ir_m_post_readback
              (ir_line_readback (car ir_m_refresh_created_result))))
          (setq ir_m_post_readback_ok
            (and ir_m_refresh_all_result (car ir_m_refresh_all_result)
                 (ir_route_readback_matches_p
                   ir_m_post_readback
                   (nth 2 ir_m_route_result) (nth 3 ir_m_route_result)
                   ir_m_width ir_m_layer (nth 4 ir_m_route_result))))
          (setq ir_m_layer_visible_result
            (errset (axlIsVisibleLayer ir_m_layer) t))
          (setq ir_m_visible_result (errset (axlVisibleUpdate t) t))
          (setq ir_m_flush_result (errset (axlFlushDisplay) t))
          (setq ir_m_completion_state
            (ir_record_committed_route
              ir_m_post_readback ir_m_post_readback_ok ir_m_managed
              (and ir_m_restore_result ir_m_layer_visible_result
                   (car ir_m_layer_visible_result) ir_m_visible_result
                   (car ir_m_visible_result) ir_m_flush_result
                   (car ir_m_flush_result))
              (car ir_request_identity) (cadr ir_request_identity)))
          (setq ir_done_flag t)
          (if (equal ir_m_completion_state (quote succeeded))
          then
            (ir_finish_interaction (quote succeeded)
              "Interactive Route created route 1, verified exact post-commit readback, and armed route-specific Undo.")
            (axlMsgPut
              (if ir_request_recovery_available
              then "Interactive Route: Created route 1 and armed exact route-specific Undo.\n"
              else "Interactive Route: Created route 1 and verified exact native readback.\n"))
          else
            (ir_finish_interaction (quote failed)
              (if ir_request_recovery_available
              then "The route committed with verified recovery, but restoration or display verification failed. Route-specific Undo remains available."
              else "The route committed but exact recovery could not be verified. Inspect the board and use Allegro Ctrl+Z before saving."))
            (axlMsgPut
              (if ir_request_recovery_available
              then "ERROR: Route committed; display/restoration verification failed. Route-specific Undo remains available.\n"
              else "ERROR: Route committed without verified recovery. Inspect the board and use Allegro Ctrl+Z before saving.\n"))
            (axlUIWBeep))
        else
          (setq ir_m_rollback_ok (ir_rollback_transaction))
          (ir_restore_runtime_state)
          (setq ir_done_flag t)
          (ir_finish_interaction (quote failed)
            (if ir_m_rollback_ok
            then "Interactive Route commit failed; the route was rolled back."
            else "Interactive Route commit and rollback could not be verified; inspect the board before saving."))
          (if ir_m_rollback_ok
          then
            (axlMsgPut
              "ERROR: Interactive Route commit failed; the route was rolled back.\n")
          else
            (axlMsgPut
              "ERROR: Interactive Route commit and rollback failed; inspect the board.\n"))
          (axlUIWBeep)))
      ((equal ir_m_route_result (quote rejected))
        (ir_restore_runtime_state)
        (setq ir_done_flag t)
        (ir_finish_interaction (quote rejected)
          "Interactive Route rejected the selected endpoints."))
      ((equal ir_m_route_result (quote failed))
        (setq ir_m_rollback_ok (ir_rollback_transaction))
        (ir_restore_runtime_state)
        (setq ir_done_flag t)
        (ir_finish_interaction (quote failed)
          (if ir_m_rollback_ok
          then "Interactive Route could not create the requested route and rolled back."
          else "Interactive Route creation and rollback could not be verified; inspect the board before saving."))
        (unless ir_m_rollback_ok
          (axlMsgPut
            "ERROR: Interactive Route could not verify rollback; inspect the board.\n")))
      (t
        (ir_cancel)))
  )
)

;; The native Done action means cancel while a pick is active. A successfully
;; created one-shot route commits and exits automatically after the second pick.
(procedure (ir_done)
  (prog nil
    (return (ir_cancel))
  )
)

(procedure (ir_cancel)
  (prog (ir_c_rollback_ok ir_c_restore_result ir_c_cancel_ok ir_c_message)
    (when ir_done_flag (return t))
    (setq ir_c_rollback_ok (ir_rollback_transaction))
    ;; A cancelled terminal result requires both rollback and restoration.
    ;; A fallible display/restore call must not strand the native enter loop.
    (setq ir_c_restore_result (errset (ir_restore_runtime_state) t))
    (setq ir_c_cancel_ok
      (and ir_c_rollback_ok ir_c_restore_result (car ir_c_restore_result)))
    (setq ir_c_message
      (cond
        ((null ir_c_rollback_ok)
          "Interactive Route cancel could not verify rollback; inspect the board before saving.")
        ((null ir_c_cancel_ok)
          "Interactive Route rolled back, but could not restore native runtime settings; inspect Allegro's Find filter, dynamic-fill and DRC settings before continuing.")
        (t "Interactive Route was cancelled; no route was created.")))
    (setq ir_done_flag t)
    (ir_finish_interaction
      (if ir_c_cancel_ok then (quote cancelled) else (quote failed))
      ir_c_message)
    (axlMsgPut
      (strcat (if ir_c_cancel_ok then "" else "ERROR: ") ir_c_message "\n"))
    (axlCancelEnterFun)
    (return ir_c_cancel_ok)
  )
)
