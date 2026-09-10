;; ==========================================================================
;; Command: pd.simple.controls.dp-via-corridor (SDK adapter)
;; Description: Read-only DP via corridor analysis, reports, and navigation.
;; Functions: dpvc_run_report_only, dpvc_run, dpvc_check_corridor,
;;   dpvc_navigation_clear, dpvc_navigation_publish, dpvc_navigation_zoom.
;; ==========================================================================

;; The SDK adapter loads the packaged categories and owns command registration.
;; Report settings are dynamically scoped by dpvc_run_report_only. The only
;; persistent checker state is a private, scalar navigation snapshot; discard
;; it on source reload without touching the native design or display.
(setq dpvc__navigation_cache nil)

;; -----------------------------------------------------------------------
;; Absolute value utility (SKILL has no built-in abs for flonum)
;; -----------------------------------------------------------------------
(procedure (dpvc_abs x)
  (if (lessp x 0.0) then (times -1.0 x) else x)
)

;; -----------------------------------------------------------------------
;; Query actual via COPPER pad radius from padstack definition
;; Returns radius in design units, or nil if pad cannot be queried.
;;
;; The pad object does NOT have a 'width' attribute (per DB model table
;; 2-13: pad attrs are bBox, figure, figureName, layer, type, offset,
;; sides, inside, radius, corners). Using (getq pad width) would either
;; return nil or pick up a stale dynamic-scope variable, causing the
;; corridor to be too wide.
;;
;; Correct approach: get the REGULAR (copper) pad via axlDBGetPad, then
;; compute radius from bBox. For a circular pad centered at the origin,
;; bBox = ((-r -r) (r r)), so radius = (maxX - minX) / 2.
;;
;; Layer attempts (per axlDBGetPad docs):
;;   1. "ETCH/<layer>" — standard format for named layers
;;   2. bare "<layer>"  — also accepted for etch layers
;;   3. 'internal       — default internal layer (for through-hole vias)
;;
;; IMPORTANT: we request pad_type "REGULAR" which returns the copper pad.
;; "ANTI" would return the clearance pad (anti-pad), which is larger and
;; would make the corridor incorrectly wide.
;; -----------------------------------------------------------------------
(procedure (dpvc_get_pad_radius via_dbid layer_name)
  (prog (dpvc_gpr_pad dpvc_gpr_bbox dpvc_gpr_rad
         dpvc_gpr_tries dpvc_gpr_lyr dpvc_gpr_fig)
    ;; Build ordered list of layer names to try
    ;; ETCH/ prefix first (documented canonical format), then bare, then internal
    (setq dpvc_gpr_tries
      (list (strcat "ETCH/" layer_name)
            layer_name
            (quote internal)))

    (foreach dpvc_gpr_lyr dpvc_gpr_tries
      (setq dpvc_gpr_pad nil)
      (errset
        (setq dpvc_gpr_pad
          (axlDBGetPad via_dbid dpvc_gpr_lyr "REGULAR")) t)
      (when dpvc_gpr_pad
        ;; Verify this is actually a REGULAR pad (not ANTI/THERMAL)
        (setq dpvc_gpr_fig nil)
        (errset (setq dpvc_gpr_fig (getq dpvc_gpr_pad type)) t)
        (when (and dpvc_gpr_fig
                   (nequal (upperCase dpvc_gpr_fig) "REGULAR"))
          ;; Got wrong pad type -- skip this layer attempt
          (setq dpvc_gpr_pad nil)
        )
        (when dpvc_gpr_pad
          ;; Compute radius from bBox: for circular pad at origin,
          ;; bBox = ((-r -r) (r r)), so diameter = maxX - minX
          (setq dpvc_gpr_bbox nil)
          (errset
            (setq dpvc_gpr_bbox (getq dpvc_gpr_pad bBox)) t)
          (when (and dpvc_gpr_bbox
                     (listp dpvc_gpr_bbox)
                     (geqp (length dpvc_gpr_bbox) 2))
            (setq dpvc_gpr_rad
              (quotient
                (difference (nth 0 (nth 1 dpvc_gpr_bbox))
                            (nth 0 (nth 0 dpvc_gpr_bbox)))
                2.0))
            (when (greaterp dpvc_gpr_rad 0.0)
              (return dpvc_gpr_rad))
          )
        )
      )
    )
    (return nil)
  )
)

;; -----------------------------------------------------------------------
;; Query via ANTI-PAD (clearance) radius from padstack definition
;; Returns radius in design units, or nil if anti-pad cannot be queried.
;;
;; The anti-pad defines the void in the reference plane copper. The EM
;; coupling zone between differential pair vias extends to the anti-pad
;; boundary, not just the copper pad boundary. Using the anti-pad radius
;; for the corridor width gives a more physically accurate exclusion zone.
;;
;; Same layer-attempt logic as dpvc_get_pad_radius, but passes "ANTI"
;; as the pad type to axlDBGetPad instead of "REGULAR".
;; -----------------------------------------------------------------------
(procedure (dpvc_get_antipad_radius via_dbid layer_name)
  (prog (dpvc_gapr_pad dpvc_gapr_bbox dpvc_gapr_rad
         dpvc_gapr_tries dpvc_gapr_lyr dpvc_gapr_fig)
    ;; Build ordered list of layer names to try
    (setq dpvc_gapr_tries
      (list (strcat "ETCH/" layer_name)
            layer_name
            (quote internal)))

    (foreach dpvc_gapr_lyr dpvc_gapr_tries
      (setq dpvc_gapr_pad nil)
      (errset
        (setq dpvc_gapr_pad
          (axlDBGetPad via_dbid dpvc_gapr_lyr "ANTI")) t)
      (when dpvc_gapr_pad
        ;; Verify this is actually an ANTI pad
        (setq dpvc_gapr_fig nil)
        (errset (setq dpvc_gapr_fig (getq dpvc_gapr_pad type)) t)
        (when (and dpvc_gapr_fig
                   (nequal (upperCase dpvc_gapr_fig) "ANTI"))
          ;; Got wrong pad type -- skip this layer attempt
          (setq dpvc_gapr_pad nil)
        )
        (when dpvc_gapr_pad
          ;; Compute radius from bBox: for circular anti-pad at origin,
          ;; bBox = ((-r -r) (r r)), so diameter = maxX - minX
          (setq dpvc_gapr_bbox nil)
          (errset
            (setq dpvc_gapr_bbox (getq dpvc_gapr_pad bBox)) t)
          (when (and dpvc_gapr_bbox
                     (listp dpvc_gapr_bbox)
                     (geqp (length dpvc_gapr_bbox) 2))
            (setq dpvc_gapr_rad
              (quotient
                (difference (nth 0 (nth 1 dpvc_gapr_bbox))
                            (nth 0 (nth 0 dpvc_gapr_bbox)))
                2.0))
            (when (greaterp dpvc_gapr_rad 0.0)
              (return dpvc_gapr_rad))
          )
        )
      )
    )
    (return nil)
  )
)

;; -----------------------------------------------------------------------
;; Helper: check if a net name should be ignored as a violating net
;; GND nets are always ignored (ground stitching between diff pair vias
;; is beneficial for return current, not a violation).
;; Power nets (VDD, VCC, VSS, PWR, +prefix) are always ignored (DC rails
;; do not create SI risk in the corridor).
;; NU_ (Not Used) and NC (No Connect) nets are always ignored.
;; -----------------------------------------------------------------------
(procedure (dpvc_is_ignored_net net_name_upper)
  (cond
    ;; Always ignore ground nets (anchored to avoid matching e.g. BACKGROUND)
    ;; Matches: GND, GND_xxx, GND0, xxx_GND, xxx_GND_xxx, */GND, DGND, AGND, PGND
    ;; NOTE: SKILL rexMatchp does NOT support \| alternation outside \(...\)
    ;; groups. Use separate rexMatchp calls joined by 'or' instead.
    ((or (rexMatchp "^GND$" net_name_upper)
         (rexMatchp "^GND[_0-9]" net_name_upper)
         (rexMatchp "_GND$" net_name_upper)
         (rexMatchp "_GND[_0-9]" net_name_upper)
         (rexMatchp "/GND" net_name_upper)
         (rexMatchp "^DGND" net_name_upper)
         (rexMatchp "^AGND" net_name_upper)
         (rexMatchp "^PGND" net_name_upper)) t)
    ;; Always ignore pull-down, pull-up, and no-connect nets
    ((rexMatchp "^PD_" net_name_upper) t)
    ((rexMatchp "^PU_" net_name_upper) t)
    ;; NC nets: match "NC" exactly, "NC_xxx", or "NC0" through "NC9"
    ;; Do NOT use bare "^NC" -- it is too broad (matches NCLK, NCS, etc.)
    ((or (rexMatchp "^NC$" net_name_upper)
         (rexMatchp "^NC_" net_name_upper)
         (rexMatchp "^NC[0-9]" net_name_upper)) t)
    ;; Always ignore not-used nets (NU_ prefix)
    ((rexMatchp "^NU_" net_name_upper) t)
    ;; Always ignore power nets (VDD, VCC, VSS, PWR, POWER, +prefix)
    ;; DC rails do not create SI risk in the diff pair corridor
    ((or (rexMatchp "^VDD" net_name_upper)
         (rexMatchp "^VCC" net_name_upper)
         (rexMatchp "^VSS" net_name_upper)
         (rexMatchp "^PWR" net_name_upper)
         (rexMatchp "^POWER" net_name_upper)) t)
    ;; +prefix power nets (e.g., +3.3V, +5V, +1.8V)
    ;; CRITICAL: Use [+] character class for literal plus sign.
    ;; "^\\+" produces BRE ^\+ which means "one or more of preceding"
    ;; with NO preceding atom -- undefined behavior that can match ALL
    ;; strings in some regex engines, silently filtering every net.
    ((rexMatchp "^[+]" net_name_upper) t)
    (t nil)
  )
)

;; -----------------------------------------------------------------------
;; Classify a net by signal category and risk level
;; Returns: (list "CATEGORY" "RISK") for the given UPPER-CASED net name.
;; Matches against dpvc_signal_categories in priority order (first match wins).
;; -----------------------------------------------------------------------
(procedure (dpvc_classify_net net_name_upper)
  (prog (dpvc_cn_result)
    (when dpvc_signal_categories
      (foreach dpvc_cn_entry dpvc_signal_categories
        (when (and (null dpvc_cn_result)
                   (rexMatchp (nth 0 dpvc_cn_entry) net_name_upper))
          (setq dpvc_cn_result
            (list (nth 1 dpvc_cn_entry) (nth 3 dpvc_cn_entry)))
        )
      )
    )
    (if dpvc_cn_result
    then (return dpvc_cn_result)
    else (return (list dpvc_default_category dpvc_default_risk)))
  )
)

;; -----------------------------------------------------------------------
;; Compute the higher of two risk levels
;; Ordering: CRITICAL > MEDIUM > LOW
;; Returns the more severe risk string.
;; -----------------------------------------------------------------------
(procedure (dpvc_max_risk risk_a risk_b)
  (prog (dpvc_mr_map dpvc_mr_va dpvc_mr_vb)
    ;; Map risk strings to numeric values for comparison
    (setq dpvc_mr_map
      (list (list "CRITICAL" 3) (list "MEDIUM" 2) (list "LOW" 1)))
    (setq dpvc_mr_va 0)
    (setq dpvc_mr_vb 0)
    (foreach dpvc_mr_e dpvc_mr_map
      (when (equal (nth 0 dpvc_mr_e) risk_a)
        (setq dpvc_mr_va (nth 1 dpvc_mr_e)))
      (when (equal (nth 0 dpvc_mr_e) risk_b)
        (setq dpvc_mr_vb (nth 1 dpvc_mr_e)))
    )
    (if (geqp dpvc_mr_va dpvc_mr_vb)
    then (return risk_a)
    else (return risk_b))
  )
)

;; -----------------------------------------------------------------------
;; Convert risk string to numeric sort key (higher risk = lower number
;; so that CRITICAL sorts first)
;; -----------------------------------------------------------------------
(procedure (dpvc_risk_sort_key risk_str)
  (cond
    ((equal risk_str "CRITICAL") 0)
    ((equal risk_str "MEDIUM")   1)
    ((equal risk_str "LOW")      2)
    (t                           3)
  )
)

;; -----------------------------------------------------------------------
;; Deduplicate violation list: collapse multiple cline_segment detections
;; from the same foreign net crossing the same corridor on the same layer
;; into a single violation with the minimum distance (closest approach).
;;
;; From an SI perspective, one net crossing one corridor on one layer is
;; ONE violation regardless of how many trace segments compose the routing
;; path. Without dedup, a net routed with 5 segments through a corridor
;; produces 5 violations in the report, inflating counts and noise.
;;
;; Dedup key: (aggressor_net, dp_name, layer, P_via_xy, N_via_xy)
;; For each unique key, keep only the record with the smallest distance
;; to the P-N centerline (closest approach = worst case coupling).
;;
;; Parameters:
;;   viols  — list of full violation records (dp_name prepended)
;;            Record: (dp_name p_xy n_xy p_layers n_layers
;;                     layer net_name obj_type obj_dbid dist hw hl
;;                     intrusion_xy)
;;            indices: 0      1    2    3        4
;;                     5     6        7        8     9    10 11
;;                     12
;;
;; Returns: deduplicated violation list (same record format)
;; -----------------------------------------------------------------------
(procedure (dpvc_dedup_violations viols)
  (prog (dpvc_dd_best dpvc_dd_result
         dpvc_dd_vr dpvc_dd_key dpvc_dd_net dpvc_dd_dp
         dpvc_dd_lyr dpvc_dd_pxy dpvc_dd_nxy dpvc_dd_dist
         dpvc_dd_existing dpvc_dd_entry)

    (when (null viols) (return nil))

    ;; Assoc list: (key_string best_violation_record) pairs
    ;; assoc finds existing entries by key in O(n), which is fine
    ;; for the typical violation count (tens to low hundreds).
    (setq dpvc_dd_best nil)

    (foreach dpvc_dd_vr viols
      ;; Build dedup key from: aggressor net, dp name, layer, P via XY, N via XY
      (setq dpvc_dd_net (nth 6 dpvc_dd_vr))    ;; aggressor net name
      (setq dpvc_dd_dp  (nth 0 dpvc_dd_vr))    ;; diff pair name
      (setq dpvc_dd_lyr (nth 5 dpvc_dd_vr))    ;; etch layer
      (setq dpvc_dd_pxy (nth 1 dpvc_dd_vr))    ;; P via XY
      (setq dpvc_dd_nxy (nth 2 dpvc_dd_vr))    ;; N via XY
      (setq dpvc_dd_dist (nth 9 dpvc_dd_vr))   ;; distance to P-N axis

      (setq dpvc_dd_key
        (sprintf nil "%s|%s|%s|%.2f,%.2f|%.2f,%.2f"
          dpvc_dd_net dpvc_dd_dp dpvc_dd_lyr
          (nth 0 dpvc_dd_pxy) (nth 1 dpvc_dd_pxy)
          (nth 0 dpvc_dd_nxy) (nth 1 dpvc_dd_nxy)))

      ;; Look up existing entry for this key
      (setq dpvc_dd_existing (assoc dpvc_dd_key dpvc_dd_best))

      (if dpvc_dd_existing
      then
        ;; Key exists: keep the record with smaller distance
        (when (lessp dpvc_dd_dist (nth 9 (nth 1 dpvc_dd_existing)))
          ;; Replace with closer violation
          (setq dpvc_dd_best
            (foreach mapcar dpvc_dd_entry dpvc_dd_best
              (if (equal (nth 0 dpvc_dd_entry) dpvc_dd_key)
              then (list dpvc_dd_key dpvc_dd_vr)
              else dpvc_dd_entry)))
        )
      else
        ;; New key: add entry
        (setq dpvc_dd_best
          (cons (list dpvc_dd_key dpvc_dd_vr) dpvc_dd_best))
      )
    )

    ;; Extract just the violation records (drop the key strings)
    (setq dpvc_dd_result
      (foreach mapcar dpvc_dd_entry dpvc_dd_best
        (nth 1 dpvc_dd_entry)))

    ;; Reverse to maintain original encounter order
    (return (reverse dpvc_dd_result))
  )
)

;; -----------------------------------------------------------------------
;; Managed candidate discovery does not use the selection service. All net
;; branches AND unnetted branches are included; no board-size truncation.
;; Each cached row is (dbid minX minY maxX maxY), in native design units.
;; The original corridor narrowphase remains the sole violation classifier.
;; -----------------------------------------------------------------------
(procedure (dpvc_managed_layer_key dpvc_mlk_layer)
  (prog ()
    (unless (and (stringp dpvc_mlk_layer) (greaterp (strlen dpvc_mlk_layer) 0))
      (error "Managed corridor discovery found an unreadable layer."))
    (setq dpvc_mlk_layer (upperCase dpvc_mlk_layer))
    (return (if (and (greaterp (strlen dpvc_mlk_layer) 5)
                    (equal (substring dpvc_mlk_layer 1 5) "ETCH/"))
      then (substring dpvc_mlk_layer 6) else dpvc_mlk_layer))))

(procedure (dpvc_managed_candidate_row dpvc_mcr_object)
  (prog (dpvc_mcr_box dpvc_mcr_point dpvc_mcr_value)
    (unless (axlDBRefreshId dpvc_mcr_object)
      (error "Managed corridor discovery found a retired copper object."))
    (setq dpvc_mcr_box (getq dpvc_mcr_object bBox))
    (unless (and (listp dpvc_mcr_box) (equal (length dpvc_mcr_box) 2))
      (error "Managed corridor discovery found an unreadable copper bound."))
    (foreach dpvc_mcr_point dpvc_mcr_box
      (unless (and (listp dpvc_mcr_point) (equal (length dpvc_mcr_point) 2))
        (error "Managed corridor discovery found an invalid copper bound."))
      (foreach dpvc_mcr_value dpvc_mcr_point
        (unless (and (numberp dpvc_mcr_value)
                     (lessp (abs dpvc_mcr_value) 1e9))
          (error "Managed corridor discovery found a non-finite copper bound."))))
    (unless (and (leqp (caar dpvc_mcr_box) (caadr dpvc_mcr_box))
                 (leqp (cadar dpvc_mcr_box) (cadadr dpvc_mcr_box)))
      (error "Managed corridor discovery found a reversed copper bound."))
    (return (list dpvc_mcr_object (caar dpvc_mcr_box) (cadar dpvc_mcr_box)
      (caadr dpvc_mcr_box) (cadadr dpvc_mcr_box)))))

(procedure (dpvc_collect_managed_candidates dpvc_cmc_design)
  (prog (dpvc_cmc_nets dpvc_cmc_net dpvc_cmc_branches dpvc_cmc_branch
         dpvc_cmc_children dpvc_cmc_child dpvc_cmc_type dpvc_cmc_segments
         dpvc_cmc_segment dpvc_cmc_layer dpvc_cmc_row dpvc_cmc_layers
         dpvc_cmc_vias dpvc_cmc_seen_branches dpvc_cmc_seen_objects)
    (unless (axlDBRefreshId dpvc_cmc_design)
      (error "Managed corridor discovery cannot refresh the design."))
    (setq dpvc_cmc_nets (getq dpvc_cmc_design nets))
    (unless (listp dpvc_cmc_nets)
      (error "Managed corridor discovery cannot enumerate design nets."))
    (setq dpvc_cmc_branches (axlDBGetLonelyBranches))
    (unless (listp dpvc_cmc_branches)
      (error "Managed corridor discovery cannot enumerate unnetted branches."))
    ;; Own the list spine; never modify lists owned by the Allegro database.
    (setq dpvc_cmc_branches (append dpvc_cmc_branches nil))
    (foreach dpvc_cmc_net dpvc_cmc_nets
      (unless (axlDBRefreshId dpvc_cmc_net)
        (error "Managed corridor discovery found a retired net."))
      (unless (listp (getq dpvc_cmc_net branches))
        (error "Managed corridor discovery cannot enumerate a net's branches."))
      (foreach dpvc_cmc_branch (getq dpvc_cmc_net branches)
        (setq dpvc_cmc_branches (cons dpvc_cmc_branch dpvc_cmc_branches))))
    (setq dpvc_cmc_layers (makeTable "dpvc_managed_layers" nil))
    (setq dpvc_cmc_seen_branches (makeTable "dpvc_managed_branches" nil))
    (setq dpvc_cmc_seen_objects (makeTable "dpvc_managed_objects" nil))
    (foreach dpvc_cmc_branch dpvc_cmc_branches
      (unless (arrayref dpvc_cmc_seen_branches dpvc_cmc_branch)
        (setarray dpvc_cmc_seen_branches dpvc_cmc_branch t)
        (unless (axlDBRefreshId dpvc_cmc_branch)
          (error "Managed corridor discovery found a retired branch."))
        (setq dpvc_cmc_children (getq dpvc_cmc_branch children))
        (unless (listp dpvc_cmc_children)
          (error "Managed corridor discovery cannot enumerate branch copper."))
        (foreach dpvc_cmc_child dpvc_cmc_children
          (unless (axlDBRefreshId dpvc_cmc_child)
            (error "Managed corridor discovery found a retired branch object."))
          (setq dpvc_cmc_type (getq dpvc_cmc_child objType))
          (unless (stringp dpvc_cmc_type)
            (error "Managed corridor discovery cannot identify a branch object."))
          (when (and (equal dpvc_cmc_type "via")
                     (not (arrayref dpvc_cmc_seen_objects dpvc_cmc_child)))
            (setarray dpvc_cmc_seen_objects dpvc_cmc_child t)
            (setq dpvc_cmc_vias
              (cons (dpvc_managed_candidate_row dpvc_cmc_child) dpvc_cmc_vias)))
          ;; isEtch distinguishes CLINES from graphical LINES. The original
          ;; CLINESEGS filter targets both straight and curved cline segments.
          (when (and (equal dpvc_cmc_type "path") (getq dpvc_cmc_child isEtch))
            (setq dpvc_cmc_segments (getq dpvc_cmc_child segments))
            (unless (listp dpvc_cmc_segments)
              (error "Managed corridor discovery cannot enumerate cline segments."))
            (foreach dpvc_cmc_segment dpvc_cmc_segments
              (unless (arrayref dpvc_cmc_seen_objects dpvc_cmc_segment)
                (setq dpvc_cmc_row (dpvc_managed_candidate_row dpvc_cmc_segment))
                (unless (member (getq dpvc_cmc_segment objType) '("line" "arc"))
                  (error "Managed corridor discovery found an unsupported cline segment."))
                (setq dpvc_cmc_layer
                  (dpvc_managed_layer_key (getq dpvc_cmc_segment layer)))
                (setarray dpvc_cmc_seen_objects dpvc_cmc_segment t)
                (setarray dpvc_cmc_layers dpvc_cmc_layer
                  (cons dpvc_cmc_row (arrayref dpvc_cmc_layers dpvc_cmc_layer)))))))))
    (return (list dpvc_cmc_layers dpvc_cmc_vias))))

(procedure (dpvc_managed_candidates_in_box dpvc_mcb_rows dpvc_mcb_box)
  (prog (dpvc_mcb_row dpvc_mcb_result)
    (foreach dpvc_mcb_row dpvc_mcb_rows
      (when (and (leqp (nth 1 dpvc_mcb_row) (caadr dpvc_mcb_box))
                 (geqp (nth 3 dpvc_mcb_row) (caar dpvc_mcb_box))
                 (leqp (nth 2 dpvc_mcb_row) (cadadr dpvc_mcb_box))
                 (geqp (nth 4 dpvc_mcb_row) (cadar dpvc_mcb_box)))
        (setq dpvc_mcb_result (cons (car dpvc_mcb_row) dpvc_mcb_result))))
    (return dpvc_mcb_result)))

;; -----------------------------------------------------------------------
;; Main analysis routine
;; -----------------------------------------------------------------------
(procedure (dpvc_run)
  (prog (dpvc_ag_console dpvc_aggr_summary dpvc_all_etch dpvc_all_modules
         dpvc_all_nets dpvc_all_viols dpvc_an_acat dpvc_an_arsk dpvc_an_orsk
         dpvc_annotated dpvc_base dpvc_check_lyrs dpvc_d_mils dpvc_d_mm
         dpvc_dd_after dpvc_dd_before dpvc_design dpvc_design_name
         dpvc_dp_counts dpvc_dp_name dpvc_dp_viols
         dpvc_ex dpvc_full_rec dpvc_idx dpvc_layer_map dpvc_lyr_counts
         dpvc_margin_du dpvc_module_bbox dpvc_nname dpvc_nname_exp
         dpvc_nnet dpvc_own_names dpvc_p_nets dpvc_pairs dpvc_pn_pairs
         dpvc_pname dpvc_pnet dpvc_r_dbid dpvc_r_dist dpvc_r_dp dpvc_r_lyr
         dpvc_r_net dpvc_r_nlyrs dpvc_r_nxy dpvc_r_plyrs dpvc_r_pxy
         dpvc_r_type dpvc_risk_crit dpvc_risk_low dpvc_risk_med dpvc_rpt
         dpvc_seen_objs dpvc_target_mod dpvc_to_mils dpvc_to_mm dpvc_total_dp
         dpvc_total_vp dpvc_units dpvc_v1 dpvc_v2 dpvc_vl dpvc_vnum
         dpvc_vp_viols dpvc_vr dpvc_hr_dist dpvc_hr_num dpvc_hr_vr
         dpvc_mr_dist dpvc_mr_num dpvc_mr_vr dpvc_ag_num dpvc_ag_detail_list
         dpvc_ag_dps_sorted dpvc_ag_dp_found dpvc_ag_dp_cx dpvc_ag_dp_cy
         dpvc_ag_scan_dp dpvc_ag_scan_net dpvc_ag_scan_nxy dpvc_ag_scan_pxy
         dpvc_ag_scan_vr dpvc_cum_action dpvc_cum_brk_dps dpvc_cum_brk_lyr
         dpvc_cum_brk_lyrs dpvc_cum_brk_min dpvc_cum_brk_net
         dpvc_cum_brk_viols dpvc_cum_crit_dp dpvc_cum_d dpvc_cum_dp_cnt
         dpvc_cum_dp_entry dpvc_cum_dp_groups dpvc_cum_dp_min
         dpvc_cum_dp_name dpvc_cum_hdr_fmt dpvc_cum_k dpvc_cum_ndps
         dpvc_cum_net dpvc_cum_ok dpvc_cum_record dpvc_cum_review
         dpvc_cum_rework dpvc_cum_row_fmt dpvc_cum_s11 dpvc_cum_s11_single
         dpvc_cum_sep dpvc_cum_sum_xt dpvc_cum_t_rise dpvc_cum_total_x
         dpvc_cum_v_swing dpvc_cum_viols dpvc_cum_xt_single
         dpvc__managed_candidates)
    (dpvc_navigation_clear)
    (setq dpvc__completed_result nil)
    (setq dpvc__run_error "The corridor analysis did not complete.")
    (setq dpvc__dat_path nil)
    ;; Discovery uses the database directly and never changes the display.
    ;; The report-only entry verifies display and selection state afterward.

    (printf "\n========================================\n")
    (printf "  DP Via Corridor Check\n")
    (printf "========================================\n")

    ;; ---------------------------------------------------------------
    ;; Design information
    ;; ---------------------------------------------------------------
    (setq dpvc_design (axlDBGetDesign))
    (setq dpvc_units (nth 0 (axlDBGetDesignUnits)))
    (setq dpvc_design_name (axlCurrentDesign))
    (unless (and dpvc_design
                 (member dpvc_units (list "mils" "millimeters")))
      (setq dpvc__run_error "An open design in mils or millimeters is required.")
      (return nil))

    ;; Conversion factors for report (always report both mils and mm)
    (if (equal dpvc_units "millimeters")
    then
      (setq dpvc_to_mils (quotient 1.0 0.0254))
      (setq dpvc_to_mm 1.0)
    else
      (setq dpvc_to_mils 1.0)
      (setq dpvc_to_mm 0.0254)
    )

    ;; ---------------------------------------------------------------
    ;; Build etch layer list and index map
    ;; ---------------------------------------------------------------
    (setq dpvc_all_etch
      (getq (axlGetParam "paramLayerGroup:ETCH") groupMembers))

    ;; Map: layer_name -> stackup position (0 = top)
    (setq dpvc_layer_map nil)
    (setq dpvc_idx 0)
    (foreach dpvc_lyr dpvc_all_etch
      (setq dpvc_layer_map
        (cons (list dpvc_lyr dpvc_idx) dpvc_layer_map))
      (setq dpvc_idx (plus dpvc_idx 1))
    )

    ;; Inner signal layers only — exclude TOP and BOTTOM
    ;; Surface layers are skipped because dogbone fan-outs at BGA breakout
    ;; create unavoidable foreign-net crossings that cannot be rerouted
    (setq dpvc_check_lyrs
      (setof d dpvc_all_etch
        (and (null (axlIsLayerNegative (strcat "ETCH/" d)))
             (nequal (upperCase d) "TOP")
             (nequal (upperCase d) "BOTTOM"))))

    ;; ---------------------------------------------------------------
    ;; Check if backdrill has been performed on this design.
    ;; axlBackdrillGet('status) returns:
    ;;   nil         — error (treat as no backdrill)
    ;;   'notStarted — backdrill not performed
    ;;   't          — backdrill done and up to date
    ;;   'odd        — backdrill done but out of date
    ;; When backdrill is active ('t or 'odd), we narrow each via's
    ;; layer span to only the layers where copper remains post-drill.
    ;; This prevents false positives on layers where the via barrel
    ;; has been removed (no copper = no EM coupling).
    ;; ---------------------------------------------------------------
    (setq dpvc__has_backdrill nil)
    (let (dpvc_bd_status)
      (setq dpvc_bd_status nil)
      (errset (setq dpvc_bd_status
        (axlBackdrillGet (quote status))) t)
      (when (or (equal dpvc_bd_status t)
                (equal dpvc_bd_status (quote odd)))
        (setq dpvc__has_backdrill t))
    )

    (printf "  Design:           %s.brd\n" dpvc_design_name)
    (printf "  Units:            %s\n" dpvc_units)
    (printf "  Corridor:         pad-to-pad zone + %.1f mil margin\n"
      dpvc__pad_margin)
    (printf "  Checking %d inner layer(s) (TOP/BOTTOM excluded)\n"
      (length dpvc_check_lyrs))
    (printf "  Backdrill:        %s\n"
      (if dpvc__has_backdrill
        then "active (layer spans narrowed to post-drill copper)"
        else "not detected (using original via spans)"))
    ;; ---------------------------------------------------------------
    ;; Discover differential pairs by _P/_N net name convention
    ;; Scan all nets, find pairs where base_P and base_N both exist
    ;; ---------------------------------------------------------------
    (setq dpvc_all_nets (getq dpvc_design nets))
    (setq dpvc__managed_candidates (dpvc_collect_managed_candidates dpvc_design))
    (setq dpvc_p_nets nil)
    (setq dpvc_pn_pairs nil)

    ;; First pass: collect all nets ending with _P
    (foreach dpvc_net dpvc_all_nets
      (setq dpvc_nname (getq dpvc_net name))
      (when (and dpvc_nname (greaterp (strlen dpvc_nname) 2))
        (when (equal (upperCase (substring dpvc_nname
                (plus (strlen dpvc_nname) -1) 2)) "_P")
          (setq dpvc_p_nets (cons dpvc_net dpvc_p_nets))
        )
      )
    )

    ;; Second pass: for each _P net, find matching _N net
    (foreach dpvc_pnet dpvc_p_nets
      (setq dpvc_pname (getq dpvc_pnet name))
      ;; Build the _N name: replace trailing _P with _N
      (setq dpvc_base (substring dpvc_pname 1
        (difference (strlen dpvc_pname) 2)))
      (setq dpvc_nname_exp (strcat dpvc_base "_N"))
      ;; Search for the matching _N net
      (setq dpvc_nnet nil)
      (foreach dpvc_net dpvc_all_nets
        (when (and (null dpvc_nnet) (getq dpvc_net name))
          (when (equal (upperCase (getq dpvc_net name))
                       (upperCase dpvc_nname_exp))
            (setq dpvc_nnet dpvc_net)
          )
        )
      )
      (when dpvc_nnet
        ;; Skip NC_/NU_ diff pairs unless user opted to include them.
        ;; These are unused differential pairs with no active signal --
        ;; checking their corridors produces noise in the report.
        (if (and (null dpvc__include_unused)
                 (or (rexMatchp "^NC_" (upperCase dpvc_base))
                     (rexMatchp "^NC[0-9]" (upperCase dpvc_base))
                     (rexMatchp "^NU_" (upperCase dpvc_base))))
        then
          nil  ;; skip this pair
        else
          ;; Store as (pair_base_name p_net_dbid n_net_dbid)
          (setq dpvc_pn_pairs
            (cons (list dpvc_base dpvc_pnet dpvc_nnet)
              dpvc_pn_pairs))
        )
      )
    )

    (if (null dpvc_pn_pairs)
    then
      (printf "  No differential pairs found (_P/_N naming convention).\n")
    )

    ;; ---------------------------------------------------------------
    ;; Optional module boundary filter
    ;; ---------------------------------------------------------------
    (setq dpvc_module_bbox nil)
    (if (and (stringp dpvc__module_name)
             (greaterp (strlen dpvc__module_name) 0))
    then
      (setq dpvc_all_modules (getq dpvc_design module))
      (setq dpvc_target_mod nil)
      (foreach dpvc_m dpvc_all_modules
        (when (equal (upperCase (getq dpvc_m name))
                     (upperCase dpvc__module_name))
          (setq dpvc_target_mod dpvc_m))
      )
      (if (null dpvc_target_mod)
      then
        (setq dpvc__run_error
          (sprintf nil "Module '%s' was not found; no analysis was run."
            dpvc__module_name))
        (return nil)
      else
        (setq dpvc_module_bbox (getq dpvc_target_mod bBox))
        (when (null dpvc_module_bbox)
          (setq dpvc__run_error "The selected module has no usable boundary.")
          (return nil))
        (printf "  Filtering to module: %s\n" dpvc__module_name)
      )
    )

    (printf "  Found %d differential pair(s) by _P/_N naming%s\n"
      (length dpvc_pn_pairs)
      (if dpvc__include_unused
        then ""
        else " (NC/NU pairs excluded)"))

    ;; ---------------------------------------------------------------
    ;; Warn if BACKDRILL_MAX_PTH_STUB properties exist but backdrill
    ;; has not been performed. This means the design has backdrill
    ;; constraints defined but the actual backdrill operation was not
    ;; run, so via spans may be wider than the designer intended.
    ;; ---------------------------------------------------------------
    (setq dpvc__bd_stub_warn_count 0)
    (when (null dpvc__has_backdrill)
      (let (dpvc_bsw_count dpvc_bsw_seen)
        (setq dpvc_bsw_count 0)
        (setq dpvc_bsw_seen nil) ;; track checked net names to avoid double-counting
        (foreach dpvc_bsw_rec dpvc_pn_pairs
          ;; Each record is (base_name p_net_dbid n_net_dbid)
          ;; Check both P and N nets for the property
          (foreach dpvc_bsw_net (list (nth 1 dpvc_bsw_rec) (nth 2 dpvc_bsw_rec))
            (when (and dpvc_bsw_net
                       (null (member (getq dpvc_bsw_net name) dpvc_bsw_seen)))
              (setq dpvc_bsw_seen
                (cons (getq dpvc_bsw_net name) dpvc_bsw_seen))
              (let (dpvc_bsw_prop)
                (setq dpvc_bsw_prop nil)
                (errset (setq dpvc_bsw_prop
                  (getq (getq dpvc_bsw_net prop)
                    BACKDRILL_MAX_PTH_STUB)) t)
                (when dpvc_bsw_prop
                  (setq dpvc_bsw_count (plus dpvc_bsw_count 1)))
              )
            )
          )
        )
        (setq dpvc__bd_stub_warn_count dpvc_bsw_count)
        (when (greaterp dpvc_bsw_count 0)
          (printf "  WARNING: BACKDRILL_MAX_PTH_STUB properties found on %d net(s)\n" dpvc_bsw_count)
          (printf "           but backdrill has not been performed. Corridor checks\n")
          (printf "           use full via spans. Run Manufacture > Backdrill for\n")
          (printf "           accurate post-backdrill analysis.\n")
        )
      )
    )

    ;; ---------------------------------------------------------------
    ;; Main analysis loop: iterate over all differential pairs
    ;; ---------------------------------------------------------------
    (setq dpvc_all_viols nil)         ;; accumulate all violation records
    (setq dpvc_dp_counts nil)         ;; (dp_name count) pairs
    (setq dpvc_lyr_counts nil)        ;; (layer count) pairs
    (setq dpvc_total_dp 0)            ;; diff pairs actually checked
    (setq dpvc_total_vp 0)            ;; total via pairs analyzed
    (setq dpvc_seen_objs nil)         ;; dedup: track seen violation objs

    ;; Database candidates are independent of visibility and Find Filter.
    ;; Keep analysis errors separate from report-output failures.
    (setq dpvc__analysis_error nil)
    (setq dpvc__analysis_error
      (errset (progn

    (foreach dpvc_dp_rec dpvc_pn_pairs
      (setq dpvc_dp_name (nth 0 dpvc_dp_rec))
      (setq dpvc_pnet (nth 1 dpvc_dp_rec))
      (setq dpvc_nnet (nth 2 dpvc_dp_rec))

      ;; Build the set of own-pair net names (for exclusion)
      (setq dpvc_own_names
        (list (upperCase (getq dpvc_pnet name))
              (upperCase (getq dpvc_nnet name))))

      ;; Collect vias from each net (wrap in list for dpvc_get_net_vias)
      (setq dpvc_v1 (dpvc_get_net_vias (list dpvc_pnet)))
      (setq dpvc_v2 (dpvc_get_net_vias (list dpvc_nnet)))
      ;; Apply module bounding box filter if active
      (when dpvc_module_bbox
        (setq dpvc_v1
          (setof dpvc_fv dpvc_v1
            (dpvc_point_in_bbox (getq dpvc_fv xy)
              dpvc_module_bbox)))
        (setq dpvc_v2
          (setof dpvc_fv dpvc_v2
            (dpvc_point_in_bbox (getq dpvc_fv xy)
              dpvc_module_bbox)))
      )

      ;; Only process if both sides have vias
      (when (and dpvc_v1 dpvc_v2)
        ;; Pair up P/N vias by proximity (greedy nearest-neighbor)
        (setq dpvc_pairs (dpvc_pair_vias dpvc_v1 dpvc_v2))
        (setq dpvc_total_dp (plus dpvc_total_dp 1))
        (setq dpvc_total_vp
          (plus dpvc_total_vp (length dpvc_pairs)))

        ;; Convert pad margin from mils to design units (constant for all pairs)
        (setq dpvc_margin_du
          (if (equal dpvc_units "millimeters")
          then (times dpvc__pad_margin 0.0254)
          else dpvc__pad_margin))

        ;; Check each via pair corridor
        (setq dpvc_dp_viols nil)
        (foreach dpvc_vp dpvc_pairs
          (setq dpvc_vp_viols
            (dpvc_check_corridor
              (nth 0 dpvc_vp)   ;; P via
              (nth 1 dpvc_vp)   ;; N via
              dpvc_own_names
              dpvc_check_lyrs
              dpvc_all_etch
              dpvc_layer_map
              dpvc_margin_du
              dpvc_seen_objs))
          (when dpvc_vp_viols
            (foreach dpvc_vr dpvc_vp_viols
              ;; Prepend diff pair name to the violation record
              (setq dpvc_full_rec
                (cons dpvc_dp_name dpvc_vr))
              ;; Private via references follow the 13 report fields.
              ;; Dedup/sorting carry them with the winning record.
              (setq dpvc_full_rec
                (append dpvc_full_rec
                  (list (nth 0 dpvc_vp) (nth 1 dpvc_vp))))
              (setq dpvc_dp_viols
                (cons dpvc_full_rec dpvc_dp_viols))
              ;; Track per-layer counts
              (setq dpvc_vl (nth 4 dpvc_vr))
              (setq dpvc_ex
                (exists dpvc_lc dpvc_lyr_counts
                  (equal (nth 0 dpvc_lc) dpvc_vl)))
              (if dpvc_ex
              then
                (setq dpvc_lyr_counts
                  (foreach mapcar dpvc_lc dpvc_lyr_counts
                    (if (equal (nth 0 dpvc_lc) dpvc_vl)
                    then
                      (list (nth 0 dpvc_lc)
                        (plus (nth 1 dpvc_lc) 1))
                    else dpvc_lc)))
              else
                (setq dpvc_lyr_counts
                  (cons (list dpvc_vl 1) dpvc_lyr_counts))
              )
              ;; Track seen objects for dedup (obj_dbid at index 7)
              (setq dpvc_seen_objs
                (cons (nth 7 dpvc_vr) dpvc_seen_objs))
            )
          )
        )
        ;; Accumulate
        (when dpvc_dp_viols
          (setq dpvc_all_viols
            (append dpvc_dp_viols dpvc_all_viols))
          (setq dpvc_dp_counts
            (cons (list dpvc_dp_name
                    (length dpvc_dp_viols))
              dpvc_dp_counts))
        )
      ) ;; close when (and dpvc_v1 dpvc_v2)
    ) ;; close foreach dpvc_dp_rec

    ) t)) ;; close progn + errset + setq wrapper around analysis loop

    ;; If the analysis loop errored out, report and bail
    (when (null dpvc__analysis_error)
      (printf "  ERROR: Analysis loop failed; no completed result.\n")
      (setq dpvc__run_error "The corridor analysis failed; no completed result is available.")
      (return nil)
    )

    ;; ---------------------------------------------------------------
    ;; Deduplicate violations: one net crossing one corridor on one
    ;; layer = one violation, regardless of how many cline_segments
    ;; compose the routing path. Keeps the record with the minimum
    ;; distance (closest approach = worst case). This must happen
    ;; BEFORE annotation, report, .dat file, and visualization so
    ;; all downstream consumers see correct deduplicated counts.
    ;; ---------------------------------------------------------------
    (let (dpvc_dd_before dpvc_dd_after)
      (setq dpvc_dd_before (length dpvc_all_viols))
      (setq dpvc_all_viols (dpvc_dedup_violations dpvc_all_viols))
      (setq dpvc_dd_after (length dpvc_all_viols))
      (when (greaterp dpvc_dd_before dpvc_dd_after)
        (printf "  Dedup:          %d raw -> %d unique (collapsed %d duplicate segment%s)\n"
          dpvc_dd_before dpvc_dd_after
          (difference dpvc_dd_before dpvc_dd_after)
          (if (equal (difference dpvc_dd_before dpvc_dd_after) 1)
            then "" else "s"))
      )

      ;; Rebuild per-DP and per-layer counts from deduped violations
      (setq dpvc_dp_counts nil)
      (setq dpvc_lyr_counts nil)
      (foreach dpvc_dd_vr dpvc_all_viols
        ;; Per-DP count
        (let (dpvc_dd_dp_name dpvc_dd_dp_ex)
          (setq dpvc_dd_dp_name (nth 0 dpvc_dd_vr))
          (setq dpvc_dd_dp_ex
            (exists dpvc_dd_dc dpvc_dp_counts
              (equal (nth 0 dpvc_dd_dc) dpvc_dd_dp_name)))
          (if dpvc_dd_dp_ex
          then
            (setq dpvc_dp_counts
              (foreach mapcar dpvc_dd_dc dpvc_dp_counts
                (if (equal (nth 0 dpvc_dd_dc) dpvc_dd_dp_name)
                then (list (nth 0 dpvc_dd_dc)
                       (plus (nth 1 dpvc_dd_dc) 1))
                else dpvc_dd_dc)))
          else
            (setq dpvc_dp_counts
              (cons (list dpvc_dd_dp_name 1) dpvc_dp_counts))
          )
        )
        ;; Per-layer count
        (let (dpvc_dd_lyr dpvc_dd_lyr_ex)
          (setq dpvc_dd_lyr (nth 5 dpvc_dd_vr))
          (setq dpvc_dd_lyr_ex
            (exists dpvc_dd_lc dpvc_lyr_counts
              (equal (nth 0 dpvc_dd_lc) dpvc_dd_lyr)))
          (if dpvc_dd_lyr_ex
          then
            (setq dpvc_lyr_counts
              (foreach mapcar dpvc_dd_lc dpvc_lyr_counts
                (if (equal (nth 0 dpvc_dd_lc) dpvc_dd_lyr)
                then (list (nth 0 dpvc_dd_lc)
                       (plus (nth 1 dpvc_dd_lc) 1))
                else dpvc_dd_lc)))
          else
            (setq dpvc_lyr_counts
              (cons (list dpvc_dd_lyr 1) dpvc_lyr_counts))
          )
        )
      )
    )

    ;; ---------------------------------------------------------------
    ;; Classify each violation and build sorted list
    ;; Risk is based on aggressor only -- the aggressor's switching
    ;; characteristics determine the real SI risk.  A slow GPIO
    ;; crossing a fast diff pair is LOW risk.
    ;; Annotate: (viol_rec aggr_cat aggr_risk overall_risk)
    ;; We do NOT change existing indices (visualization depends on 0-11).
    ;; Instead, build a parallel annotation list and a sorted index.
    ;; ---------------------------------------------------------------
    (setq dpvc_annotated nil)  ;; list of (viol_rec aggr_cat aggr_risk overall_risk)
    (setq dpvc_risk_crit 0)
    (setq dpvc_risk_med 0)
    (setq dpvc_risk_low 0)

    (foreach dpvc_vr dpvc_all_viols
      (let (dpvc_an_net dpvc_an_aggr dpvc_an_overall)
        ;; Classify the violating (aggressor) net
        (setq dpvc_an_net (nth 6 dpvc_vr))
        (setq dpvc_an_aggr (dpvc_classify_net dpvc_an_net))

        ;; Overall risk = aggressor risk directly
        (setq dpvc_an_overall (nth 1 dpvc_an_aggr))

        ;; Count by risk level
        (cond
          ((equal dpvc_an_overall "CRITICAL")
            (setq dpvc_risk_crit (plus dpvc_risk_crit 1)))
          ((equal dpvc_an_overall "MEDIUM")
            (setq dpvc_risk_med (plus dpvc_risk_med 1)))
          (t
            (setq dpvc_risk_low (plus dpvc_risk_low 1)))
        )

        ;; Build annotated record:
        ;; (original_viol_rec aggr_cat aggr_risk overall_risk)
        (setq dpvc_annotated
          (cons (list dpvc_vr
                  (nth 0 dpvc_an_aggr) (nth 1 dpvc_an_aggr)
                  dpvc_an_overall)
            dpvc_annotated))
      )
    )
    ;; Reverse to restore original order before sorting
    (setq dpvc_annotated (reverse dpvc_annotated))

    ;; Sort: by risk (HIGH first), then by distance to P-N axis (closest first)
    (setq dpvc_annotated
      (sort dpvc_annotated
        (lambda (dpvc_sa dpvc_sb)
          (let (dpvc_sa_rk dpvc_sb_rk dpvc_sa_dist dpvc_sb_dist)
            (setq dpvc_sa_rk (dpvc_risk_sort_key (nth 3 dpvc_sa)))
            (setq dpvc_sb_rk (dpvc_risk_sort_key (nth 3 dpvc_sb)))
            (if (nequal dpvc_sa_rk dpvc_sb_rk)
            then (lessp dpvc_sa_rk dpvc_sb_rk)
            else
              ;; Same risk: sort by distance (closer = more severe = first)
              (setq dpvc_sa_dist (nth 9 (nth 0 dpvc_sa)))
              (setq dpvc_sb_dist (nth 9 (nth 0 dpvc_sb)))
              (lessp dpvc_sa_dist dpvc_sb_dist)
            )
          )
        )
      )
    )

    ;; ---------------------------------------------------------------
    ;; Build per-aggressor net summary from annotated violations
    ;; ---------------------------------------------------------------
    ;; Each entry in dpvc_aggr_summary:
    ;; (net_name corridor_count violation_count category risk
    ;;  min_distance layer_list dp_name_list)
    (setq dpvc_aggr_summary nil)

    (foreach dpvc_ag_rec dpvc_annotated
      (let (dpvc_ag_vr dpvc_ag_net dpvc_ag_dp dpvc_ag_cat
            dpvc_ag_risk dpvc_ag_lyr dpvc_ag_dist dpvc_ag_entry)
        ;; Extract fields from annotated record
        (setq dpvc_ag_vr   (nth 0 dpvc_ag_rec))
        (setq dpvc_ag_net  (nth 6 dpvc_ag_vr))
        (setq dpvc_ag_dp   (nth 0 dpvc_ag_vr))
        (setq dpvc_ag_cat  (nth 1 dpvc_ag_rec))
        (setq dpvc_ag_risk (nth 2 dpvc_ag_rec))
        (setq dpvc_ag_lyr  (nth 5 dpvc_ag_vr))
        (setq dpvc_ag_dist (nth 9 dpvc_ag_vr))

        ;; Look up existing entry by net name
        (setq dpvc_ag_entry (assoc dpvc_ag_net dpvc_aggr_summary))

        (if dpvc_ag_entry
        then
          ;; Update existing entry
          ;; Increment violation count (index 2)
          (setq dpvc_aggr_summary
            (foreach mapcar dpvc_ag_e dpvc_aggr_summary
              (if (equal (nth 0 dpvc_ag_e) dpvc_ag_net)
              then
                (list
                  (nth 0 dpvc_ag_e)         ;; net_name
                  ;; corridor_count: recomputed below
                  (nth 1 dpvc_ag_e)
                  ;; violation_count: increment
                  (plus (nth 2 dpvc_ag_e) 1)
                  (nth 3 dpvc_ag_e)         ;; category
                  (nth 4 dpvc_ag_e)         ;; risk
                  ;; min_distance: keep smaller
                  (if (lessp dpvc_ag_dist (nth 5 dpvc_ag_e))
                    then dpvc_ag_dist
                    else (nth 5 dpvc_ag_e))
                  ;; layer_list: add if not already present
                  (if (member dpvc_ag_lyr (nth 6 dpvc_ag_e))
                    then (nth 6 dpvc_ag_e)
                    else (cons dpvc_ag_lyr (nth 6 dpvc_ag_e)))
                  ;; dp_name_list: add if not already present
                  (if (member dpvc_ag_dp (nth 7 dpvc_ag_e))
                    then (nth 7 dpvc_ag_e)
                    else (cons dpvc_ag_dp (nth 7 dpvc_ag_e)))
                )
              else dpvc_ag_e)))
        else
          ;; Create new entry
          (setq dpvc_aggr_summary
            (cons (list dpvc_ag_net
                    1                        ;; corridor_count (placeholder)
                    1                        ;; violation_count
                    dpvc_ag_cat              ;; category
                    dpvc_ag_risk             ;; risk
                    dpvc_ag_dist             ;; min_distance
                    (list dpvc_ag_lyr)       ;; layer_list
                    (list dpvc_ag_dp))       ;; dp_name_list
              dpvc_aggr_summary))
        )
      )
    )

    ;; Finalize corridor_count = length of unique dp_name_list
    (setq dpvc_aggr_summary
      (foreach mapcar dpvc_ag_e dpvc_aggr_summary
        (list
          (nth 0 dpvc_ag_e)                  ;; net_name
          (length (nth 7 dpvc_ag_e))         ;; corridor_count
          (nth 2 dpvc_ag_e)                  ;; violation_count
          (nth 3 dpvc_ag_e)                  ;; category
          (nth 4 dpvc_ag_e)                  ;; risk
          (nth 5 dpvc_ag_e)                  ;; min_distance
          (nth 6 dpvc_ag_e)                  ;; layer_list
          (nth 7 dpvc_ag_e)                  ;; dp_name_list
        )
      )
    )

    ;; Compute action flag for each entry
    ;; Returns: (net_name corr viols cat risk min_dist layers dps action)
    (setq dpvc_aggr_summary
      (foreach mapcar dpvc_ag_e dpvc_aggr_summary
        (let (dpvc_ag_act dpvc_ag_rk dpvc_ag_cc)
          (setq dpvc_ag_rk (nth 4 dpvc_ag_e))
          (setq dpvc_ag_cc (nth 1 dpvc_ag_e))
          (cond
            ;; REWORK: (CRITICAL and corridors>=3) or corridors>=6
            ((or (and (equal dpvc_ag_rk "CRITICAL")
                      (geqp dpvc_ag_cc 3))
                 (geqp dpvc_ag_cc 6))
              (setq dpvc_ag_act "REWORK"))
            ;; REVIEW: CRITICAL, or (MEDIUM and corridors>=3), or corridors>=4
            ((or (equal dpvc_ag_rk "CRITICAL")
                 (and (equal dpvc_ag_rk "MEDIUM")
                      (geqp dpvc_ag_cc 3))
                 (geqp dpvc_ag_cc 4))
              (setq dpvc_ag_act "REVIEW"))
            (t
              (setq dpvc_ag_act "--"))
          )
          (append dpvc_ag_e (list dpvc_ag_act))
        )
      )
    )

    ;; Sort: risk level first (CRITICAL > MEDIUM > LOW),
    ;; then corridor_count descending, then violation_count descending
    (setq dpvc_aggr_summary
      (sort dpvc_aggr_summary
        (lambda (dpvc_ag_a dpvc_ag_b)
          (let (dpvc_ag_ark dpvc_ag_brk)
            (setq dpvc_ag_ark (dpvc_risk_sort_key (nth 4 dpvc_ag_a)))
            (setq dpvc_ag_brk (dpvc_risk_sort_key (nth 4 dpvc_ag_b)))
            (cond
              ;; Different risk: CRITICAL (0) < MEDIUM (1) < LOW (2) sort key
              ((nequal dpvc_ag_ark dpvc_ag_brk)
                (lessp dpvc_ag_ark dpvc_ag_brk))
              ;; Same risk: more corridors first
              ((nequal (nth 1 dpvc_ag_a) (nth 1 dpvc_ag_b))
                (greaterp (nth 1 dpvc_ag_a) (nth 1 dpvc_ag_b)))
              ;; Same corridors: more violations first
              (t (greaterp (nth 2 dpvc_ag_a) (nth 2 dpvc_ag_b)))
            )
          )
        )
      )
    )

    ;; ---------------------------------------------------------------
    ;; Generate report file
    ;; ---------------------------------------------------------------
    (setq dpvc__run_error
      "The analysis completed, but the report could not be written completely.")
    (setq dpvc_rpt (outfile dpvc__report_path))
    (setq dpvc__report_port dpvc_rpt)
    (if (null dpvc_rpt)
    then
      (printf "  ERROR: Cannot open report file: %s\n"
        dpvc__report_path)
      (setq dpvc__run_error "The report output could not be opened.")
      (return nil)
    )

    ;; Header
    (fprintf dpvc_rpt
      "============================================================\n")
    (fprintf dpvc_rpt
      " Differential Pair Via Corridor Check Report\n")
    (fprintf dpvc_rpt
      "============================================================\n\n")
    (fprintf dpvc_rpt "Design:           %s.brd\n" dpvc_design_name)
    (fprintf dpvc_rpt "Date:             %s\n" (getCurrentTime))
    (fprintf dpvc_rpt "User:             %s\n" (getLogin))
    (fprintf dpvc_rpt "Design Units:     %s\n" dpvc_units)
    (fprintf dpvc_rpt "Corridor:         pad-to-pad zone + %.1f mil margin\n"
      dpvc__pad_margin)
    (if (and (stringp dpvc__module_name)
             (greaterp (strlen dpvc__module_name) 0))
    then
      (fprintf dpvc_rpt "Module Filter:    %s\n" dpvc__module_name)
    else
      (fprintf dpvc_rpt "Module Filter:    (all)\n")
    )
    (fprintf dpvc_rpt "Layers Checked:   inner signal layers only (TOP/BOTTOM excluded)\n")
    (fprintf dpvc_rpt "Backdrill:        %s\n"
      (if dpvc__has_backdrill
        then "active (backdrilled layers excluded from corridor checks)"
        else "not detected (using original via layer spans)"))
    (when (greaterp dpvc__bd_stub_warn_count 0)
      (fprintf dpvc_rpt "WARNING:          BACKDRILL_MAX_PTH_STUB properties found on %d net(s)\n"
        dpvc__bd_stub_warn_count)
      (fprintf dpvc_rpt "                  but backdrill has not been performed. Corridor checks use\n")
      (fprintf dpvc_rpt "                  full via spans. Run Manufacture > Backdrill for accurate\n")
      (fprintf dpvc_rpt "                  post-backdrill analysis.\n")
    )
    (fprintf dpvc_rpt "Nets Ignored:     GND, power (VDD/VCC/VSS/PWR/+prefix), NC, NU, PD, PU\n")
    (fprintf dpvc_rpt "NC/NU Diff Pairs: %s\n"
      (if dpvc__include_unused
        then "included (user option)"
        else "skipped (unused)"))
    (fprintf dpvc_rpt "Categories:       %s\n"
      (if dpvc_signal_categories
        then (sprintf nil "%d rules loaded" (length dpvc_signal_categories))
        else "not loaded (risk classification disabled)"))
    (fprintf dpvc_rpt "\n")
    (fprintf dpvc_rpt "This report identifies foreign-net conductors (traces or vias)\n")
    (fprintf dpvc_rpt "that pass through the protected zone between differential pair\n")
    (fprintf dpvc_rpt "via transitions. A foreign conductor in this zone degrades\n")
    (fprintf dpvc_rpt "differential impedance, injects asymmetric crosstalk, and\n")
    (fprintf dpvc_rpt "disrupts return current paths -- effects that compound at\n")
    (fprintf dpvc_rpt "high data rates. Each detection is called a \"violation\".\n\n")
    (fprintf dpvc_rpt "Diff Pairs Checked:  %d\n" dpvc_total_dp)
    (fprintf dpvc_rpt "Via Pairs Analyzed:  %d\n" dpvc_total_vp)
    (fprintf dpvc_rpt "Total Violations:    %d (CRITICAL: %d, MEDIUM: %d, LOW: %d)\n\n"
      (length dpvc_all_viols) dpvc_risk_crit dpvc_risk_med dpvc_risk_low)

    ;; Report body: summaries first, then quick-reference, then details
    (if dpvc_all_viols
    then
      ;; ---------------------------------------------------------------
      ;; Aggressor Net Summary (most actionable -- first)
      ;; ---------------------------------------------------------------
      (when dpvc_aggr_summary
        (fprintf dpvc_rpt
          "============================================================\n")
        (fprintf dpvc_rpt
          " SUMMARY: Aggressor Net Analysis\n")
        (fprintf dpvc_rpt
          "============================================================\n\n")
        (fprintf dpvc_rpt "This section flips the perspective: instead of asking which\n")
        (fprintf dpvc_rpt "diff pairs are affected, it asks which FOREIGN nets are the\n")
        (fprintf dpvc_rpt "worst offenders -- the nets whose routing crosses through\n")
        (fprintf dpvc_rpt "the most diff pair via corridors.\n")
        (fprintf dpvc_rpt "\n")
        (fprintf dpvc_rpt "  Pairs Crossed: How many different DP via corridors this net\n")
        (fprintf dpvc_rpt "  intrudes into. A net crossing many corridors is a systematic\n")
        (fprintf dpvc_rpt "  routing problem, not an isolated conflict.\n")
        (fprintf dpvc_rpt "\n")
        (fprintf dpvc_rpt "  Intrusions: Total detections across all corridors and layers.\n")
        (fprintf dpvc_rpt "  A via barrel passing through one corridor on 5 inner layers\n")
        (fprintf dpvc_rpt "  counts as 5 intrusions, because it couples on every layer.\n")
        (fprintf dpvc_rpt "  More layers = more parasitic capacitance and more reference\n")
        (fprintf dpvc_rpt "  plane disruption.\n")
        (fprintf dpvc_rpt "\n")
        (fprintf dpvc_rpt "  Closest: Minimum distance to any P-N centerline across all\n")
        (fprintf dpvc_rpt "  intrusions. 0.0 = dead center (worst case coupling).\n")
        (fprintf dpvc_rpt "\n")
        (fprintf dpvc_rpt "  Action:\n")
        (fprintf dpvc_rpt "    REWORK = this net almost certainly needs rerouting to a\n")
        (fprintf dpvc_rpt "             different layer or channel\n")
        (fprintf dpvc_rpt "    REVIEW = investigate this net's routing path for possible\n")
        (fprintf dpvc_rpt "             improvement\n")
        (fprintf dpvc_rpt "    --     = acceptable, no action needed\n")
        (fprintf dpvc_rpt "\n")
        (fprintf dpvc_rpt "  Physics note: each intrusion adds parasitic capacitance\n")
        (fprintf dpvc_rpt "  (typically 8-60 fF depending on geometry) and disrupts the\n")
        (fprintf dpvc_rpt "  reference plane return current path for the victim pair.\n")
        (fprintf dpvc_rpt "  Multiple intrusions on the same corridor compound linearly.\n")
        (fprintf dpvc_rpt "\n")

        ;; Table header
        (fprintf dpvc_rpt
          " #  Risk      DPs  Intrs  Closest  Action  %-22s %s\n"
          "Net" "Category")
        (fprintf dpvc_rpt
          "--- -------- ----- ------ -------- ------- %-22s %s\n"
          "----------------------" "----------")

        ;; Table rows
        (setq dpvc_ag_num 0)
        (foreach dpvc_ag_row dpvc_aggr_summary
          (setq dpvc_ag_num (plus dpvc_ag_num 1))
          ;; Record: (net corr viols cat risk min_dist layers dps action)
          (fprintf dpvc_rpt
            "%3d %-8s %5d %6d %8.1f %-7s %-22s %s\n"
            dpvc_ag_num
            (nth 4 dpvc_ag_row)                       ;; risk
            (nth 1 dpvc_ag_row)                       ;; corridor_count
            (nth 2 dpvc_ag_row)                       ;; violation_count
            (times (nth 5 dpvc_ag_row) dpvc_to_mils)  ;; min_distance in mils
            (nth 8 dpvc_ag_row)                       ;; action
            (nth 0 dpvc_ag_row)                       ;; net_name
            (nth 3 dpvc_ag_row))                      ;; category
        )

        ;; Detail lines for REWORK/REVIEW aggressors
        (setq dpvc_ag_detail_list
          (setof dpvc_ag_d dpvc_aggr_summary
            (or (equal (nth 8 dpvc_ag_d) "REWORK")
                (equal (nth 8 dpvc_ag_d) "REVIEW"))))

        (when dpvc_ag_detail_list
          (fprintf dpvc_rpt
            "\n  ---  Per-Aggressor Detail (REWORK / REVIEW nets)  ---\n\n")
          (fprintf dpvc_rpt "For each aggressor net flagged as REWORK or REVIEW, this lists\n")
          (fprintf dpvc_rpt "the specific DP corridors it crosses. Use this to locate the\n")
          (fprintf dpvc_rpt "routing conflicts in your layout editor and plan reroutes.\n\n")
          (foreach dpvc_ag_d dpvc_ag_detail_list
            ;; Sort the DP name list alphabetically for readability
            (setq dpvc_ag_dps_sorted
              (sort (mapcar (lambda (x) x) (nth 7 dpvc_ag_d))
                (lambda (dpvc_ag_s1 dpvc_ag_s2)
                  (lessp (strcmp dpvc_ag_s1 dpvc_ag_s2) 0))))
            (fprintf dpvc_rpt "  %s (%s) [%s] - crosses %d DP corridor%s:\n"
              (nth 0 dpvc_ag_d)                       ;; net_name
              (nth 3 dpvc_ag_d)                       ;; category
              (nth 4 dpvc_ag_d)                       ;; risk
              (nth 1 dpvc_ag_d)                       ;; corridor_count
              (if (equal (nth 1 dpvc_ag_d) 1)
                then "" else "s"))
            ;; Print each DP name on its own line with corridor center XY
            ;; Scan annotated violations for this aggressor to find XY per DP
            (foreach dpvc_ag_dp_name dpvc_ag_dps_sorted
              (let (dpvc_ag_dp_cx dpvc_ag_dp_cy dpvc_ag_dp_found)
                (setq dpvc_ag_dp_found nil)
                ;; Find the first violation record matching this aggressor
                ;; net and DP name to get the corridor P/N vias
                (foreach dpvc_ag_scan dpvc_annotated
                  (when (null dpvc_ag_dp_found)
                    (let (dpvc_ag_scan_vr dpvc_ag_scan_net
                          dpvc_ag_scan_dp dpvc_ag_scan_pxy dpvc_ag_scan_nxy)
                      (setq dpvc_ag_scan_vr (nth 0 dpvc_ag_scan))
                      (setq dpvc_ag_scan_net (nth 6 dpvc_ag_scan_vr))
                      (setq dpvc_ag_scan_dp (nth 0 dpvc_ag_scan_vr))
                      (when (and (equal dpvc_ag_scan_net (nth 0 dpvc_ag_d))
                                 (equal dpvc_ag_scan_dp dpvc_ag_dp_name))
                        (setq dpvc_ag_scan_pxy (nth 1 dpvc_ag_scan_vr))
                        (setq dpvc_ag_scan_nxy (nth 2 dpvc_ag_scan_vr))
                        (setq dpvc_ag_dp_cx
                          (quotient
                            (plus (nth 0 dpvc_ag_scan_pxy)
                                  (nth 0 dpvc_ag_scan_nxy)) 2.0))
                        (setq dpvc_ag_dp_cy
                          (quotient
                            (plus (nth 1 dpvc_ag_scan_pxy)
                                  (nth 1 dpvc_ag_scan_nxy)) 2.0))
                        (setq dpvc_ag_dp_found t)
                      )
                    )
                  )
                )
                (if dpvc_ag_dp_found
                then
                  (fprintf dpvc_rpt "    %-20s  at (%.2f, %.2f)\n"
                    dpvc_ag_dp_name dpvc_ag_dp_cx dpvc_ag_dp_cy)
                else
                  (fprintf dpvc_rpt "    %s\n" dpvc_ag_dp_name)
                )
              )
            )
            (fprintf dpvc_rpt "\n")
          )
        )
      )

      ;; ---------------------------------------------------------------
      ;; CUMULATIVE CROSSING ANALYSIS — estimates cumulative NEXT
      ;; voltage and S11 degradation for each aggressor net based on
      ;; the number and proximity of its corridor crossings.
      ;;
      ;; K-factor interpolation (spacing-dependent coupling):
      ;;   40 mil: 1.0  30 mil: 1.6  20 mil: 3.5  15 mil: 4.0
      ;; V_xtalk = K * V_swing / t_rise_ns per crossing
      ;; V_total = N * V_xtalk (coherent worst case)
      ;; S11(N) = -24.0 + 10*log10(N) + 2.0 dB
      ;; ---------------------------------------------------------------
      (let (dpvc_cum_fp dpvc_cum_rework dpvc_cum_review dpvc_cum_ok
            dpvc_cum_v_swing dpvc_cum_t_rise dpvc_cum_s11_single
            dpvc_cum_ag dpvc_cum_net dpvc_cum_viols dpvc_cum_total_x
            dpvc_cum_sum_xt dpvc_cum_s11 dpvc_cum_action
            dpvc_cum_dp_viols dpvc_cum_dp_groups dpvc_cum_dp_entry
            dpvc_cum_dp_name dpvc_cum_dp_cnt dpvc_cum_dp_min
            dpvc_cum_dp_lyrs dpvc_cum_k dpvc_cum_xt_single
            dpvc_cum_crit_dp dpvc_cum_ndps dpvc_cum_vr
            dpvc_cum_dp_lyr dpvc_cum_dp_key
            dpvc_cum_hdr_fmt dpvc_cum_row_fmt dpvc_cum_sep)

        ;; SI estimation constants
        (setq dpvc_cum_v_swing 1.0)
        (setq dpvc_cum_t_rise 0.1)
        (setq dpvc_cum_s11_single -24.0)

        ;; sprintf formats for consistent column alignment (gotcha #21)
        (setq dpvc_cum_hdr_fmt
          "  %-28s   %3s   %5s   %12s   %11s   %-7s\n")
        (setq dpvc_cum_row_fmt
          "  %-28s   %3d   %5d   %12.1f   %11.1f   %-7s\n")
        (setq dpvc_cum_sep
          "  ----------------------------   ---   -----   ------------   -----------   -------\n")

        ;; Accumulate into three bins: REWORK, REVIEW, acceptable
        (setq dpvc_cum_rework nil)
        (setq dpvc_cum_review nil)
        (setq dpvc_cum_ok nil)

        (foreach dpvc_cum_ag dpvc_aggr_summary
          (setq dpvc_cum_net (nth 0 dpvc_cum_ag))

          ;; Get all violations for this aggressor
          (setq dpvc_cum_viols nil)
          (foreach dpvc_cum_vr dpvc_all_viols
            (when (equal (nth 6 dpvc_cum_vr) dpvc_cum_net)
              (setq dpvc_cum_viols
                (append1 dpvc_cum_viols dpvc_cum_vr))))

          ;; Group by DP name
          (setq dpvc_cum_dp_groups nil)
          (foreach dpvc_cum_vr dpvc_cum_viols
            (setq dpvc_cum_dp_name (nth 0 dpvc_cum_vr))
            (setq dpvc_cum_dp_entry
              (assoc dpvc_cum_dp_name dpvc_cum_dp_groups))
            (if dpvc_cum_dp_entry
            then
              (rplacd dpvc_cum_dp_entry
                (append1 (cdr dpvc_cum_dp_entry) dpvc_cum_vr))
            else
              (setq dpvc_cum_dp_groups
                (cons (cons dpvc_cum_dp_name (list dpvc_cum_vr))
                  dpvc_cum_dp_groups))
            )
          )

          ;; Compute cumulative metrics
          (setq dpvc_cum_total_x 0)
          (setq dpvc_cum_sum_xt 0.0)
          (setq dpvc_cum_crit_dp 0)
          (setq dpvc_cum_ndps (length dpvc_cum_dp_groups))

          (foreach dpvc_cum_dp_entry dpvc_cum_dp_groups
            (setq dpvc_cum_dp_cnt (length (cdr dpvc_cum_dp_entry)))
            (setq dpvc_cum_total_x
              (plus dpvc_cum_total_x dpvc_cum_dp_cnt))

            ;; Find minimum distance for this DP
            (setq dpvc_cum_dp_min 1e12)
            (foreach dpvc_cum_vr (cdr dpvc_cum_dp_entry)
              (when (lessp (times (nth 9 dpvc_cum_vr) dpvc_to_mils)
                           dpvc_cum_dp_min)
                (setq dpvc_cum_dp_min
                  (times (nth 9 dpvc_cum_vr) dpvc_to_mils))))

            ;; K-factor interpolation
            (let (dpvc_cum_d)
              (setq dpvc_cum_d dpvc_cum_dp_min)
              (when (lessp dpvc_cum_d 15.0) (setq dpvc_cum_d 15.0))
              (when (greaterp dpvc_cum_d 40.0) (setq dpvc_cum_d 40.0))
              (cond
                ((leqp dpvc_cum_d 20.0)
                  (setq dpvc_cum_k
                    (plus 4.0
                      (times (difference dpvc_cum_d 15.0)
                             (quotient (difference 3.5 4.0) 5.0)))))
                ((leqp dpvc_cum_d 30.0)
                  (setq dpvc_cum_k
                    (plus 3.5
                      (times (difference dpvc_cum_d 20.0)
                             (quotient (difference 1.6 3.5) 10.0)))))
                (t
                  (setq dpvc_cum_k
                    (plus 1.6
                      (times (difference dpvc_cum_d 30.0)
                             (quotient (difference 1.0 1.6) 10.0)))))
              )
            )

            (setq dpvc_cum_xt_single
              (quotient (times dpvc_cum_k dpvc_cum_v_swing)
                        dpvc_cum_t_rise))
            (setq dpvc_cum_sum_xt
              (plus dpvc_cum_sum_xt
                    (times dpvc_cum_dp_cnt dpvc_cum_xt_single)))

            ;; Count CRITICALs with 3+ crossings on same DP
            (when (and (equal (nth 4 dpvc_cum_ag) "CRITICAL")
                       (geqp dpvc_cum_dp_cnt 3))
              (setq dpvc_cum_crit_dp
                (plus dpvc_cum_crit_dp 1)))
          )

          ;; S11 estimate
          (if (greaterp dpvc_cum_total_x 0)
          then
            (setq dpvc_cum_s11
              (plus dpvc_cum_s11_single
                    (plus (times 10.0
                            (quotient (log (float dpvc_cum_total_x))
                                      (log 10.0)))
                          2.0)))
          else
            (setq dpvc_cum_s11 -99.0))

          ;; Risk escalation
          (cond
            ((greaterp dpvc_cum_crit_dp 0)
              (setq dpvc_cum_action "REWORK"))
            ((geqp dpvc_cum_total_x 6)
              (setq dpvc_cum_action "REWORK"))
            ((greaterp dpvc_cum_s11 -15.0)
              (setq dpvc_cum_action "REWORK"))
            ((greaterp dpvc_cum_s11 -20.0)
              (setq dpvc_cum_action "REVIEW"))
            (t (setq dpvc_cum_action (nth 8 dpvc_cum_ag)))
          )

          ;; Bin into appropriate list
          (let (dpvc_cum_record)
            (setq dpvc_cum_record
              (list dpvc_cum_net dpvc_cum_ndps
                    dpvc_cum_total_x dpvc_cum_sum_xt
                    dpvc_cum_s11 dpvc_cum_action
                    dpvc_cum_dp_groups))
            (cond
              ((equal dpvc_cum_action "REWORK")
                (setq dpvc_cum_rework
                  (append1 dpvc_cum_rework dpvc_cum_record)))
              ((equal dpvc_cum_action "REVIEW")
                (setq dpvc_cum_review
                  (append1 dpvc_cum_review dpvc_cum_record)))
              (t
                (setq dpvc_cum_ok
                  (append1 dpvc_cum_ok dpvc_cum_record)))
            )
          )
        ) ;; end foreach aggressor

        ;; ----- Print the cumulative analysis section -----
        (fprintf dpvc_rpt "\n")
        (fprintf dpvc_rpt
          "================================================================================\n")
        (fprintf dpvc_rpt
          " CUMULATIVE CROSSING ANALYSIS\n")
        (fprintf dpvc_rpt
          "================================================================================\n\n")
        (fprintf dpvc_rpt "This section estimates the cumulative SI impact of corridor\n")
        (fprintf dpvc_rpt "crossings. Multiple crossings from the same aggressor compound:\n")
        (fprintf dpvc_rpt "  V_total = N * K(spacing) * V_swing / t_rise   (coherent worst case)\n")
        (fprintf dpvc_rpt "  S11(N) = S11_single + 10*log10(N) + 2dB       (interaction margin)\n")
        (fprintf dpvc_rpt "Assumptions: V_swing=1.0V, t_rise=0.1ns, S11_single=-24dB at 14GHz\n\n")

        ;; REWORK section
        (when dpvc_cum_rework
          (fprintf dpvc_rpt "REWORK REQUIRED:\n")
          (fprintf dpvc_rpt dpvc_cum_hdr_fmt
            "NET" "DPs" "Xings" "Est.NEXT(mV)" "Est.S11(dB)" "Action")
          (fprintf dpvc_rpt dpvc_cum_sep)
          (foreach dpvc_cum_r dpvc_cum_rework
            (fprintf dpvc_rpt dpvc_cum_row_fmt
              (nth 0 dpvc_cum_r)         ;; net name
              (nth 1 dpvc_cum_r)         ;; DPs
              (nth 2 dpvc_cum_r)         ;; total crossings
              (nth 3 dpvc_cum_r)         ;; est NEXT (mV)
              (nth 4 dpvc_cum_r)         ;; est S11 (dB)
              (nth 5 dpvc_cum_r)))       ;; action
          (fprintf dpvc_rpt "\n")
        )

        ;; REVIEW section
        (when dpvc_cum_review
          (fprintf dpvc_rpt "REVIEW:\n")
          (fprintf dpvc_rpt dpvc_cum_hdr_fmt
            "NET" "DPs" "Xings" "Est.NEXT(mV)" "Est.S11(dB)" "Action")
          (fprintf dpvc_rpt dpvc_cum_sep)
          (foreach dpvc_cum_r dpvc_cum_review
            (fprintf dpvc_rpt dpvc_cum_row_fmt
              (nth 0 dpvc_cum_r)
              (nth 1 dpvc_cum_r)
              (nth 2 dpvc_cum_r)
              (nth 3 dpvc_cum_r)
              (nth 4 dpvc_cum_r)
              (nth 5 dpvc_cum_r)))
          (fprintf dpvc_rpt "\n")
        )

        ;; Per-Pair Breakdown (REWORK nets only)
        (when dpvc_cum_rework
          (fprintf dpvc_rpt "Per-Pair Breakdown (REWORK nets only):\n")
          (foreach dpvc_cum_r dpvc_cum_rework
            (let (dpvc_cum_brk_net dpvc_cum_brk_dps dpvc_cum_brk_dp
                  dpvc_cum_brk_viols dpvc_cum_brk_min
                  dpvc_cum_brk_lyrs dpvc_cum_brk_lyr dpvc_cum_brk_vr)
              (setq dpvc_cum_brk_net (nth 0 dpvc_cum_r))
              (setq dpvc_cum_brk_dps (nth 6 dpvc_cum_r))

              (foreach dpvc_cum_brk_dp dpvc_cum_brk_dps
                ;; dpvc_cum_brk_dp is (dp_name . viol_list)
                (setq dpvc_cum_brk_viols (cdr dpvc_cum_brk_dp))

                ;; Find min distance and unique layers
                (setq dpvc_cum_brk_min 1e12)
                (setq dpvc_cum_brk_lyrs nil)
                (foreach dpvc_cum_brk_vr dpvc_cum_brk_viols
                  (when (lessp (times (nth 9 dpvc_cum_brk_vr) dpvc_to_mils)
                               dpvc_cum_brk_min)
                    (setq dpvc_cum_brk_min
                      (times (nth 9 dpvc_cum_brk_vr) dpvc_to_mils)))
                  (setq dpvc_cum_brk_lyr (nth 5 dpvc_cum_brk_vr))
                  (when (null (member dpvc_cum_brk_lyr dpvc_cum_brk_lyrs))
                    (setq dpvc_cum_brk_lyrs
                      (append1 dpvc_cum_brk_lyrs dpvc_cum_brk_lyr)))
                )

                (fprintf dpvc_rpt
                  "  %s -> %s:  %d crossing%s, closest %.1f mil, layer%s %s\n"
                  dpvc_cum_brk_net
                  (car dpvc_cum_brk_dp)  ;; dp name
                  (length dpvc_cum_brk_viols)
                  (if (equal (length dpvc_cum_brk_viols) 1)
                  then "" else "s")
                  dpvc_cum_brk_min
                  (if (equal (length dpvc_cum_brk_lyrs) 1)
                  then "" else "s")
                  (buildString dpvc_cum_brk_lyrs "-"))
              )
            )
          )
          (fprintf dpvc_rpt "\n")
        )

        ;; If no REWORK or REVIEW, note that
        (when (and (null dpvc_cum_rework) (null dpvc_cum_review))
          (fprintf dpvc_rpt "No aggressors exceed cumulative thresholds.\n\n"))

      ) ;; end let for cumulative analysis

      ;; ---------------------------------------------------------------
      ;; Summary: Violations by Risk Level
      ;; ---------------------------------------------------------------
      (fprintf dpvc_rpt
        "------------------------------------------------------------\n")
      (fprintf dpvc_rpt " SUMMARY: Violations by Risk Level\n")
      (fprintf dpvc_rpt
        "------------------------------------------------------------\n\n")
      (fprintf dpvc_rpt "Risk is based on the AGGRESSOR's switching speed, not the victim\n")
      (fprintf dpvc_rpt "pair. A fast-switching aggressor injects more crosstalk noise\n")
      (fprintf dpvc_rpt "into the victim pair's via transition.\n\n")
      (fprintf dpvc_rpt "CRITICAL: %d\n" dpvc_risk_crit)
      (fprintf dpvc_rpt "MEDIUM:  %d\n" dpvc_risk_med)
      (fprintf dpvc_rpt "LOW:     %d\n" dpvc_risk_low)

      ;; Summary: per diff pair
      (fprintf dpvc_rpt "\n")
      (fprintf dpvc_rpt
        "------------------------------------------------------------\n")
      (fprintf dpvc_rpt " SUMMARY: Violations per Diff Pair\n")
      (fprintf dpvc_rpt
        "------------------------------------------------------------\n\n")
      (fprintf dpvc_rpt "Which differential pairs have foreign conductors in their via\n")
      (fprintf dpvc_rpt "corridors, and how many. Higher counts mean more foreign objects\n")
      (fprintf dpvc_rpt "intruding into that pair's protected zone. Pairs with multiple\n")
      (fprintf dpvc_rpt "violations should be prioritized -- effects compound.\n\n")
      (fprintf dpvc_rpt "%-40s %s\n" "Diff Pair" "Count")
      (fprintf dpvc_rpt "%-40s %s\n"
        "----------------------------------------" "-----")
      (foreach dpvc_dc dpvc_dp_counts
        (fprintf dpvc_rpt "%-40s %d\n"
          (nth 0 dpvc_dc) (nth 1 dpvc_dc)))

      ;; Summary: per layer
      (fprintf dpvc_rpt "\n")
      (fprintf dpvc_rpt
        "------------------------------------------------------------\n")
      (fprintf dpvc_rpt " SUMMARY: Violations per Layer\n")
      (fprintf dpvc_rpt
        "------------------------------------------------------------\n\n")
      (fprintf dpvc_rpt "Which PCB layers have the most violations. A high count on one\n")
      (fprintf dpvc_rpt "layer suggests routing congestion on that layer. Consider moving\n")
      (fprintf dpvc_rpt "signals to less crowded layers or adjusting the layer assignment\n")
      (fprintf dpvc_rpt "in your routing plan.\n\n")
      (fprintf dpvc_rpt "%-30s %s\n" "Layer" "Count")
      (fprintf dpvc_rpt "%-30s %s\n"
        "------------------------------" "-----")
      (foreach dpvc_lc dpvc_lyr_counts
        (fprintf dpvc_rpt "%-30s %d\n"
          (nth 0 dpvc_lc) (nth 1 dpvc_lc)))

      ;; ---------------------------------------------------------------
      ;; CRITICAL violations quick-reference
      ;; ---------------------------------------------------------------
      (when (greaterp dpvc_risk_crit 0)
        (fprintf dpvc_rpt "\n")
        (fprintf dpvc_rpt
          "------------------------------------------------------------\n")
        (fprintf dpvc_rpt " CRITICAL VIOLATIONS (action required)\n")
        (fprintf dpvc_rpt
          "------------------------------------------------------------\n\n")
        (fprintf dpvc_rpt "These violations involve fast-switching aggressors (DDR5 data,\n")
        (fprintf dpvc_rpt "PCIe, USB3/4, SerDes) crossing a differential pair corridor.\n")
        (fprintf dpvc_rpt "Each should be resolved by rerouting the aggressor net.\n\n")
        (setq dpvc_hr_num 0)
        (foreach dpvc_hr_ann dpvc_annotated
          (when (equal (nth 3 dpvc_hr_ann) "CRITICAL")
            (setq dpvc_hr_num (plus dpvc_hr_num 1))
            (setq dpvc_hr_vr (nth 0 dpvc_hr_ann))
            (setq dpvc_hr_dist (nth 9 dpvc_hr_vr))
            (fprintf dpvc_rpt "#%-3d %-30s <--  %-20s on %-8s (%.2f mil from axis)\n"
              dpvc_hr_num
              (nth 0 dpvc_hr_vr)
              (nth 6 dpvc_hr_vr)
              (nth 5 dpvc_hr_vr)
              (times dpvc_hr_dist dpvc_to_mils))
          )
        )
      )

      ;; ---------------------------------------------------------------
      ;; MEDIUM RISK violations quick-reference
      ;; ---------------------------------------------------------------
      (when (greaterp dpvc_risk_med 0)
        (fprintf dpvc_rpt "\n")
        (fprintf dpvc_rpt
          "------------------------------------------------------------\n")
        (fprintf dpvc_rpt " MEDIUM RISK VIOLATIONS (review recommended)\n")
        (fprintf dpvc_rpt
          "------------------------------------------------------------\n\n")
        (fprintf dpvc_rpt "These involve moderate-speed aggressors. Review routing to\n")
        (fprintf dpvc_rpt "determine if rerouting is feasible.\n\n")
        (setq dpvc_mr_num 0)
        (foreach dpvc_mr_ann dpvc_annotated
          (when (equal (nth 3 dpvc_mr_ann) "MEDIUM")
            (setq dpvc_mr_num (plus dpvc_mr_num 1))
            (setq dpvc_mr_vr (nth 0 dpvc_mr_ann))
            (setq dpvc_mr_dist (nth 9 dpvc_mr_vr))
            (fprintf dpvc_rpt "#%-3d %-30s <--  %-20s on %-8s (%.2f mil from axis)\n"
              dpvc_mr_num
              (nth 0 dpvc_mr_vr)
              (nth 6 dpvc_mr_vr)
              (nth 5 dpvc_mr_vr)
              (times dpvc_mr_dist dpvc_to_mils))
          )
        )
      )

      ;; ---------------------------------------------------------------
      ;; Violation Details (full per-violation dump -- last for reference)
      ;; ---------------------------------------------------------------
      (fprintf dpvc_rpt "\n")
      (fprintf dpvc_rpt
        "------------------------------------------------------------\n")
      (fprintf dpvc_rpt " VIOLATION DETAILS (sorted by risk, then proximity)\n")
      (fprintf dpvc_rpt
        "------------------------------------------------------------\n\n")
      (fprintf dpvc_rpt "Each violation below is a foreign-net conductor detected inside\n")
      (fprintf dpvc_rpt "a differential pair's via corridor.\n")
      (fprintf dpvc_rpt "\n")
      (fprintf dpvc_rpt "  Corridor: The rectangular protected zone between the P and N\n")
      (fprintf dpvc_rpt "  vias of a differential pair. Its width is set by the via pad\n")
      (fprintf dpvc_rpt "  radius plus any user-specified clearance margin. Its length\n")
      (fprintf dpvc_rpt "  extends slightly past each via center.\n")
      (fprintf dpvc_rpt "\n")
      (fprintf dpvc_rpt "  Distance: Perpendicular distance from the foreign object to\n")
      (fprintf dpvc_rpt "  the P-to-N centerline. 0.0 = dead center between the vias\n")
      (fprintf dpvc_rpt "  (worst case). Larger values = closer to the corridor edge\n")
      (fprintf dpvc_rpt "  (less severe, but still inside the protected zone).\n")
      (fprintf dpvc_rpt "\n")
      (fprintf dpvc_rpt "  Risk Level: Based on the AGGRESSOR net's switching speed:\n")
      (fprintf dpvc_rpt "    CRITICAL = fast switching (DDR5 data/strobe, PCIe, USB3/4,\n")
      (fprintf dpvc_rpt "               high-speed SerDes) -- significant noise injection\n")
      (fprintf dpvc_rpt "    MEDIUM   = moderate speed (DDR address/cmd, SPI, RGMII)\n")
      (fprintf dpvc_rpt "    LOW      = slow signals (GPIO, I2C, JTAG, static config)\n")
      (fprintf dpvc_rpt "  Even LOW-risk violations still perturb differential impedance.\n")
      (fprintf dpvc_rpt "\n")
      (setq dpvc_vnum 0)
      (foreach dpvc_ann dpvc_annotated
        (setq dpvc_vnum (plus dpvc_vnum 1))
        (setq dpvc_vr      (nth 0 dpvc_ann))
        (setq dpvc_an_acat (nth 1 dpvc_ann))
        (setq dpvc_an_arsk (nth 2 dpvc_ann))
        (setq dpvc_an_orsk (nth 3 dpvc_ann))

        ;; Record: (dp_name p_xy n_xy p_layers n_layers
        ;;          layer net_name obj_type obj_dbid dist hw hl intrusion_xy)
        (setq dpvc_r_dp   (nth 0 dpvc_vr))
        (setq dpvc_r_pxy  (nth 1 dpvc_vr))
        (setq dpvc_r_nxy  (nth 2 dpvc_vr))
        (setq dpvc_r_plyrs (nth 3 dpvc_vr))
        (setq dpvc_r_nlyrs (nth 4 dpvc_vr))
        (setq dpvc_r_lyr  (nth 5 dpvc_vr))
        (setq dpvc_r_net  (nth 6 dpvc_vr))
        (setq dpvc_r_type (nth 7 dpvc_vr))
        (setq dpvc_r_dbid (nth 8 dpvc_vr))
        (setq dpvc_r_dist (nth 9 dpvc_vr))

        (setq dpvc_d_mils (times dpvc_r_dist dpvc_to_mils))
        (setq dpvc_d_mm   (times dpvc_r_dist dpvc_to_mm))

        (fprintf dpvc_rpt "Violation #%d  [%s]\n"
          dpvc_vnum dpvc_an_orsk)
        (fprintf dpvc_rpt
          "  Diff Pair:       %s\n" dpvc_r_dp)
        (fprintf dpvc_rpt
          "  P Net:           %s_P\n" dpvc_r_dp)
        (fprintf dpvc_rpt
          "  N Net:           %s_N\n" dpvc_r_dp)
        (fprintf dpvc_rpt
          "  P Via:           (%.2f, %.2f)  Layers: %s\n"
          (nth 0 dpvc_r_pxy) (nth 1 dpvc_r_pxy) dpvc_r_plyrs)
        (fprintf dpvc_rpt
          "  N Via:           (%.2f, %.2f)  Layers: %s\n"
          (nth 0 dpvc_r_nxy) (nth 1 dpvc_r_nxy) dpvc_r_nlyrs)
        (fprintf dpvc_rpt
          "  Violating Net:   %s\n" dpvc_r_net)
        (fprintf dpvc_rpt
          "  Object Type:     %s\n" dpvc_r_type)
        (fprintf dpvc_rpt
          "  Layer:           %s\n" dpvc_r_lyr)
        (fprintf dpvc_rpt
          "  Status:          VIOLATION - foreign net in exclusion zone\n")
        (fprintf dpvc_rpt
          "  Dist to P-N axis: %.2f mils / %.4f mm\n"
          dpvc_d_mils dpvc_d_mm)
        ;; Signal classification (risk based on aggressor switching speed)
        (fprintf dpvc_rpt
          "  Aggressor Signal: %s [%s / %s risk]\n"
          dpvc_r_net dpvc_an_acat dpvc_an_arsk)
        ;; Violation XY: use the corridor center (P-N midpoint) for
        ;; navigation. For vias, also show the foreign via location.
        ;; The corridor center is always the most useful zoom target.
        (let (corr_xy viol_obj_xy)
          (setq corr_xy
            (list
              (quotient (plus (nth 0 dpvc_r_pxy) (nth 0 dpvc_r_nxy)) 2.0)
              (quotient (plus (nth 1 dpvc_r_pxy) (nth 1 dpvc_r_nxy)) 2.0)))
          (fprintf dpvc_rpt
            "  Corridor Center: (%.2f, %.2f)\n"
            (nth 0 corr_xy) (nth 1 corr_xy))
          ;; For foreign vias, also print the via's own location
          (when (and dpvc_r_dbid (equal dpvc_r_type "via"))
            (setq viol_obj_xy nil)
            (errset (setq viol_obj_xy (getq dpvc_r_dbid xy)) t)
            (when viol_obj_xy
              (fprintf dpvc_rpt
                "  Foreign Via XY:  (%.2f, %.2f)\n"
                (nth 0 viol_obj_xy) (nth 1 viol_obj_xy))
            )
          )
          (fprintf dpvc_rpt "\n")
        )
      )
    else
      (fprintf dpvc_rpt "No violations found.\n")
    )

    (fprintf dpvc_rpt "\n--- End of Report ---\n")
    (unless (close dpvc_rpt)
      (error "The DP via corridor report could not be closed successfully."))
    (setq dpvc__report_port nil)

    (printf "  Report written: %s\n" dpvc__report_path)
    (printf "  Violations:     %d (CRITICAL: %d, MEDIUM: %d, LOW: %d)\n"
      (length dpvc_all_viols) dpvc_risk_crit dpvc_risk_med dpvc_risk_low)

    ;; Console: worst aggressor nets (REWORK and REVIEW only)
    (setq dpvc_ag_console
      (setof dpvc_ag_c dpvc_aggr_summary
        (or (equal (nth 8 dpvc_ag_c) "REWORK")
            (equal (nth 8 dpvc_ag_c) "REVIEW"))))
    (when dpvc_ag_console
      (printf "  --- Worst Aggressor Nets ---\n")
      (foreach dpvc_ag_c dpvc_ag_console
        (printf "  %s (%s/%s): %d corridor%s, %d violation%s [%s]\n"
          (nth 0 dpvc_ag_c)                           ;; net_name
          (nth 4 dpvc_ag_c)                           ;; risk
          (nth 3 dpvc_ag_c)                           ;; category
          (nth 1 dpvc_ag_c)                           ;; corridor_count
          (if (equal (nth 1 dpvc_ag_c) 1) then "" else "s")
          (nth 2 dpvc_ag_c)                           ;; violation_count
          (if (equal (nth 2 dpvc_ag_c) 1) then "" else "s")
          (nth 8 dpvc_ag_c))                          ;; action
      )
    )

    ;; Write .dat file for violation navigator
    (setq dpvc__run_error
      "The report was written, but navigator output failed before completion.")
    (dpvc_write_dat_file
      dpvc_aggr_summary dpvc_annotated dpvc_check_lyrs
      dpvc_dp_counts dpvc_design_name dpvc_units
      dpvc_total_dp dpvc_total_vp)

    ;; The managed workspace presents results and owns SDK canvas overlays.
    ;; Native analysis writes reports only; it does not open forms or viewers.

    (printf "========================================\n\n")
    ;; Publish only after the analysis and report close succeeded. Navigator
    ;; failure is represented by a nil path, not by a stale previous artifact.
    ;; Records contain scalar data only; the live object at index 8 is omitted.
    (setq dpvc__completed_result
      (list dpvc_design_name dpvc_units dpvc_total_dp dpvc_total_vp
        (length dpvc_aggr_summary) (length dpvc_annotated)
        dpvc_risk_crit dpvc_risk_med dpvc_risk_low
        dpvc__report_path dpvc__dat_path
        (foreach mapcar dpvc_cr_ann dpvc_annotated
          (let ((dpvc_cr_vr (nth 0 dpvc_cr_ann)))
            (list (nth 0 dpvc_cr_vr) (nth 1 dpvc_cr_vr)
              (nth 2 dpvc_cr_vr) (nth 5 dpvc_cr_vr)
              (nth 6 dpvc_cr_vr) (nth 7 dpvc_cr_vr)
              (nth 9 dpvc_cr_vr) (nth 10 dpvc_cr_vr)
              (nth 11 dpvc_cr_vr) (nth 12 dpvc_cr_vr)
              (nth 1 dpvc_cr_ann) (nth 3 dpvc_cr_ann))))))
    ;; Unsupported navigation geometry does not invalidate a full report.
    (setq dpvc__navigation_candidate
      (car (errset
        (dpvc_navigation_prepare dpvc_design_name
          dpvc__report_path dpvc_annotated) t)))
    (setq dpvc__run_error nil)
    (return dpvc__completed_result)
  )
)

;; -----------------------------------------------------------------------
;; Collect all vias from a list of nets by walking branches
;; -----------------------------------------------------------------------
(procedure (dpvc_get_net_vias net_list)
  (prog nil
    (setq dpvc_rv nil)
    (foreach dpvc_n net_list
      (when dpvc_n
        (foreach dpvc_br (getq dpvc_n branches)
          (when dpvc_br
            (foreach dpvc_ch (getq dpvc_br children)
              (when (and dpvc_ch
                         (equal (getq dpvc_ch objType) "via"))
                (when (null (member dpvc_ch dpvc_rv))
                  (setq dpvc_rv (cons dpvc_ch dpvc_rv)))
              )
            )
          )
        )
      )
    )
    (return dpvc_rv)
  )
)

;; -----------------------------------------------------------------------
;; Point-in-bounding-box test
;; -----------------------------------------------------------------------
(procedure (dpvc_point_in_bbox pt bbox)
  (prog nil
    (if (or (null pt) (null bbox))
    then (return nil))
    (setq dpvc_px (nth 0 pt))
    (setq dpvc_py (nth 1 pt))
    (setq dpvc_bx0 (nth 0 (nth 0 bbox)))
    (setq dpvc_by0 (nth 1 (nth 0 bbox)))
    (setq dpvc_bx1 (nth 0 (nth 1 bbox)))
    (setq dpvc_by1 (nth 1 (nth 1 bbox)))
    (return (and (geqp dpvc_px dpvc_bx0) (leqp dpvc_px dpvc_bx1)
                 (geqp dpvc_py dpvc_by0) (leqp dpvc_py dpvc_by1)))
  )
)

;; -----------------------------------------------------------------------
;; Pair P/N vias by greedy nearest-neighbor matching
;; Returns list of (via1 via2) pairs
;; -----------------------------------------------------------------------
(procedure (dpvc_pair_vias vias_a vias_b)
  (prog nil
    (setq dpvc_pairs nil)
    (if (or (null vias_a) (null vias_b))
    then (return nil))

    ;; Work with a mutable copy of the second list
    (setq dpvc_pool (copy vias_b))

    ;; Maximum pairing distance (avoids pairing distant vias on long buses)
    (setq dpvc_max_d
      (if (equal (nth 0 (axlDBGetDesignUnits)) "millimeters")
      then 12.7   ;; ~500 mils
      else 500.0))

    (foreach dpvc_va vias_a
      (when dpvc_pool
        (setq dpvc_best nil)
        (setq dpvc_best_d 1e12)
        (foreach dpvc_vb dpvc_pool
          (setq dpvc_d
            (axlDistance (getq dpvc_va xy) (getq dpvc_vb xy)))
          (when (lessp dpvc_d dpvc_best_d)
            (setq dpvc_best_d dpvc_d)
            (setq dpvc_best dpvc_vb))
        )
        (when (and dpvc_best (lessp dpvc_best_d dpvc_max_d))
          (setq dpvc_pairs
            (cons (list dpvc_va dpvc_best) dpvc_pairs))
          (setq dpvc_pool (remove dpvc_best dpvc_pool)))
      )
    )
    (return dpvc_pairs)
  )
)

;; -----------------------------------------------------------------------
;; Look up a layer's stackup index from the layer map
;; -----------------------------------------------------------------------
(procedure (dpvc_layer_index lname lmap)
  (prog nil
    (foreach dpvc_e lmap
      (when (equal (upperCase (nth 0 dpvc_e))
                   (upperCase lname))
        (return (nth 1 dpvc_e)))
    )
    (return -1)
  )
)

;; -----------------------------------------------------------------------
;; Get the list of etch layer names that a via spans
;;
;; Handles all via types:
;;   - Through-hole: isThrough=t, startEnd spans all layers -> return all_etch
;;   - Blind/buried (bbvia): isThrough=nil, startEnd gives actual span
;;     e.g., ("TOP" "S03") -> returns (TOP S02 S03)
;;   - Microvia (uvia): isThrough=nil, startEnd gives single-layer span
;;     e.g., ("TOP" "S02") -> returns (TOP S02)
;;
;; The padstack definition's startEnd attribute returns the start and end
;; layer names for the padstack. For through-hole padstacks this returns
;; ("TOP" "BOTTOM") which maps to all etch layers. For blind/buried, it
;; returns the actual restricted span. Layer name comparison is
;; case-insensitive because padstack definitions may store layer names in
;; different case than the design's paramLayerGroup:ETCH members.
;; -----------------------------------------------------------------------
(procedure (dpvc_get_via_layers via_dbid all_etch lmap)
  (prog nil
    ;; Quick check: if the via is explicitly through-hole, return all layers
    (when (getq via_dbid isThrough)
      (return all_etch))

    (setq dpvc_vdef (getq via_dbid definition))
    (if (null dpvc_vdef)
    then (return all_etch))

    ;; Also check the padstack definition's isThrough flag
    (when (getq dpvc_vdef isThrough)
      (return all_etch))

    (setq dpvc_vse (getq dpvc_vdef startEnd))
    (if (or (null dpvc_vse) (lessp (length dpvc_vse) 2))
    then (return all_etch))  ;; no layer span info -> assume through-hole

    ;; Parse start/end layers -- strip "ETCH/" prefix if present
    (setq dpvc_sl (nth 0 dpvc_vse))
    (setq dpvc_el (nth 1 dpvc_vse))
    ;; Guard against nil entries in startEnd list
    (when (or (null dpvc_sl) (null dpvc_el))
      (return all_etch))
    (setq dpvc_sl
      (if (and (greaterp (strlen dpvc_sl) 5)
               (equal (upperCase (substring dpvc_sl 1 5)) "ETCH/"))
      then (substring dpvc_sl 6)
      else dpvc_sl))
    (setq dpvc_el
      (if (and (greaterp (strlen dpvc_el) 5)
               (equal (upperCase (substring dpvc_el 1 5)) "ETCH/"))
      then (substring dpvc_el 6)
      else dpvc_el))

    ;; Look up stackup indices (case-insensitive via dpvc_layer_index)
    (setq dpvc_si (dpvc_layer_index dpvc_sl lmap))
    (setq dpvc_ei (dpvc_layer_index dpvc_el lmap))

    ;; If either layer is not found in the stackup, fall back to all layers
    (when (or (equal dpvc_si -1) (equal dpvc_ei -1))
      (return all_etch))

    ;; Ensure start <= end (handle reversed padstack layer order)
    (when (greaterp dpvc_si dpvc_ei)
      (setq dpvc_tmp dpvc_si)
      (setq dpvc_si dpvc_ei)
      (setq dpvc_ei dpvc_tmp))

    ;; Collect all layers in the span
    (setq dpvc_vl_result nil)
    (foreach dpvc_le lmap
      (setq dpvc_li (nth 1 dpvc_le))
      (when (and (geqp dpvc_li dpvc_si) (leqp dpvc_li dpvc_ei))
        (setq dpvc_vl_result (cons (nth 0 dpvc_le) dpvc_vl_result)))
    )

    ;; Safety: if span produced no layers, return all_etch
    (if dpvc_vl_result
    then (return dpvc_vl_result)
    else (return all_etch))
  )
)

;; -----------------------------------------------------------------------
;; Get the post-backdrill active layers for a via
;;
;; When a via has been backdrilled, the copper barrel is removed from
;; some layers. Those layers should NOT be checked for corridor
;; violations because there is no copper to cause EM coupling.
;;
;; Uses axlBackdrillGet(via_dbid) which returns a disembodied property
;; list with:
;;   topMustNotCutLayer — top boundary of remaining copper (if drilled from top)
;;   botMustNotCutLayer — bottom boundary of remaining copper (if drilled from bottom)
;; The effective post-backdrill span is between these two boundaries.
;;
;; Parameters:
;;   via_dbid   — the via to check
;;   all_etch   — full ordered etch layer list (TOP first, BOTTOM last)
;;   lmap       — layer name -> stackup index map
;;
;; Returns:
;;   nil  — via is NOT backdrilled (caller should use original span)
;;   list — ordered list of layer names where via still has copper
;; -----------------------------------------------------------------------
(procedure (dpvc_get_via_active_layers via_dbid all_etch lmap)
  (prog (dpvc_gval_bd dpvc_gval_top_mncl dpvc_gval_bot_mncl
         dpvc_gval_top_idx dpvc_gval_bot_idx
         dpvc_gval_orig_lyrs dpvc_gval_start dpvc_gval_end
         dpvc_gval_result dpvc_gval_le dpvc_gval_li
         dpvc_gval_bd_excl)

    ;; ---------------------------------------------------------------
    ;; Check BACKDRILL_EXCLUDE property on the via.
    ;; Possible values:
    ;;   EXCLUDE_BOTH   — via excluded from ALL backdrill, retains
    ;;                    full barrel -> return nil (use original span)
    ;;   EXCLUDE_TOP    — excluded from top backdrill only -> still
    ;;                    check bottom backdrill from axlBackdrillGet
    ;;   EXCLUDE_BOTTOM — excluded from bottom backdrill only -> still
    ;;                    check top backdrill from axlBackdrillGet
    ;;
    ;; Property lives on the via's prop list (same pattern as
    ;; 32_void_via_gap.il line 587).
    ;; ---------------------------------------------------------------
    (setq dpvc_gval_bd_excl nil)
    (errset (setq dpvc_gval_bd_excl
      (getq (getq via_dbid prop) BACKDRILL_EXCLUDE)) t)

    ;; EXCLUDE_BOTH: via retains full barrel, no narrowing needed
    (when (and dpvc_gval_bd_excl
               (equal (upperCase dpvc_gval_bd_excl) "EXCLUDE_BOTH"))
      (return nil))

    ;; Query backdrill data for this specific via
    (setq dpvc_gval_bd nil)
    (errset (setq dpvc_gval_bd (axlBackdrillGet via_dbid)) t)

    ;; If nil, this via is not backdrilled -> return nil (use original span)
    (when (null dpvc_gval_bd) (return nil))

    ;; Extract the "must not cut" layers — these define the boundaries of
    ;; remaining copper. A via can be backdrilled from top, bottom, or both.
    ;;
    ;; topMustNotCutLayer: the first layer from the top that must NOT be
    ;;   drilled through. Copper remains from this layer downward.
    ;; botMustNotCutLayer: the first layer from the bottom that must NOT
    ;;   be drilled through. Copper remains from this layer upward.
    ;;
    ;; If only drilled from one side, the other boundary comes from the
    ;; via's original padstack span.
    ;;
    ;; BACKDRILL_EXCLUDE handling:
    ;;   EXCLUDE_TOP    — ignore top backdrill (set topMustNotCutLayer nil)
    ;;   EXCLUDE_BOTTOM — ignore bottom backdrill (set botMustNotCutLayer nil)
    (setq dpvc_gval_top_mncl nil)
    (setq dpvc_gval_bot_mncl nil)

    ;; Only read top backdrill boundary if NOT excluded from top
    (when (or (null dpvc_gval_bd_excl)
              (nequal (upperCase dpvc_gval_bd_excl) "EXCLUDE_TOP"))
      (errset (setq dpvc_gval_top_mncl
        (get dpvc_gval_bd (quote topMustNotCutLayer))) t)
    )

    ;; Only read bottom backdrill boundary if NOT excluded from bottom
    (when (or (null dpvc_gval_bd_excl)
              (nequal (upperCase dpvc_gval_bd_excl) "EXCLUDE_BOTTOM"))
      (errset (setq dpvc_gval_bot_mncl
        (get dpvc_gval_bd (quote botMustNotCutLayer))) t)
    )

    ;; If neither boundary is defined, the backdrill data is incomplete
    ;; or the via wasn't actually drilled -> fall back to original span
    (when (and (null dpvc_gval_top_mncl) (null dpvc_gval_bot_mncl))
      (return nil))

    ;; Get the via's original (pre-backdrill) layer span as reference
    ;; for whichever side was NOT backdrilled
    (setq dpvc_gval_orig_lyrs
      (dpvc_get_via_layers via_dbid all_etch lmap))

    ;; Determine the active range start index (top boundary).
    ;; If backdrilled from top, topMustNotCutLayer is the new top boundary.
    ;; Otherwise, use the original topmost layer.
    (if dpvc_gval_top_mncl
    then
      ;; Strip ETCH/ prefix if present
      (when (and (greaterp (strlen dpvc_gval_top_mncl) 5)
                 (equal (upperCase (substring dpvc_gval_top_mncl 1 5))
                        "ETCH/"))
        (setq dpvc_gval_top_mncl
          (substring dpvc_gval_top_mncl 6)))
      (setq dpvc_gval_top_idx
        (dpvc_layer_index dpvc_gval_top_mncl lmap))
      ;; If layer not found, fall back to original topmost
      (when (equal dpvc_gval_top_idx -1)
        (setq dpvc_gval_top_idx nil))
    else
      (setq dpvc_gval_top_idx nil)
    )

    ;; Determine the active range end index (bottom boundary).
    ;; If backdrilled from bottom, botMustNotCutLayer is the new bottom.
    ;; Otherwise, use the original bottommost layer.
    (if dpvc_gval_bot_mncl
    then
      ;; Strip ETCH/ prefix if present
      (when (and (greaterp (strlen dpvc_gval_bot_mncl) 5)
                 (equal (upperCase (substring dpvc_gval_bot_mncl 1 5))
                        "ETCH/"))
        (setq dpvc_gval_bot_mncl
          (substring dpvc_gval_bot_mncl 6)))
      (setq dpvc_gval_bot_idx
        (dpvc_layer_index dpvc_gval_bot_mncl lmap))
      ;; If layer not found, fall back to original bottommost
      (when (equal dpvc_gval_bot_idx -1)
        (setq dpvc_gval_bot_idx nil))
    else
      (setq dpvc_gval_bot_idx nil)
    )

    ;; Resolve nil boundaries from original span.
    ;; Find the min and max stackup indices of the original layer span.
    (when (or (null dpvc_gval_top_idx) (null dpvc_gval_bot_idx))
      (setq dpvc_gval_start 99999)
      (setq dpvc_gval_end -1)
      (foreach dpvc_gval_le dpvc_gval_orig_lyrs
        (setq dpvc_gval_li (dpvc_layer_index dpvc_gval_le lmap))
        (when (and (nequal dpvc_gval_li -1)
                   (lessp dpvc_gval_li dpvc_gval_start))
          (setq dpvc_gval_start dpvc_gval_li))
        (when (and (nequal dpvc_gval_li -1)
                   (greaterp dpvc_gval_li dpvc_gval_end))
          (setq dpvc_gval_end dpvc_gval_li))
      )
      (when (null dpvc_gval_top_idx)
        (setq dpvc_gval_top_idx dpvc_gval_start))
      (when (null dpvc_gval_bot_idx)
        (setq dpvc_gval_bot_idx dpvc_gval_end))
    )

    ;; Sanity: ensure top <= bottom (lower index = higher in stackup)
    (when (greaterp dpvc_gval_top_idx dpvc_gval_bot_idx)
      (setq dpvc_gval_li dpvc_gval_top_idx)
      (setq dpvc_gval_top_idx dpvc_gval_bot_idx)
      (setq dpvc_gval_bot_idx dpvc_gval_li))

    ;; Collect all layers within the active range that are also in
    ;; the original via span (don't add layers the via never touched)
    (setq dpvc_gval_result nil)
    (foreach dpvc_gval_le lmap
      (setq dpvc_gval_li (nth 1 dpvc_gval_le))
      (when (and (geqp dpvc_gval_li dpvc_gval_top_idx)
                 (leqp dpvc_gval_li dpvc_gval_bot_idx)
                 (exists dpvc_gval_ol dpvc_gval_orig_lyrs
                   (equal (upperCase dpvc_gval_ol)
                          (upperCase (nth 0 dpvc_gval_le)))))
        (setq dpvc_gval_result
          (cons (nth 0 dpvc_gval_le) dpvc_gval_result)))
    )

    ;; If backdrill reduced to zero layers (extreme case), return nil
    ;; so caller falls back to original span
    (if dpvc_gval_result
    then (return dpvc_gval_result)
    else (return nil))
  )
)

;; -----------------------------------------------------------------------
;; Check a single P/N via corridor for foreign-net violations
;;
;; The corridor is an oriented bounding box (OBB) defined as:
;;   Center:      midpoint of P and N via centers
;;   Long axis:   direction from P to N (unit vector u)
;;   Half-length: span/2 (center-to-center between vias)
;;   Half-width:  pad_radius (+ optional margin, default 0)
;;
;; The corridor is the rectangle from one via center to the other,
;; with width equal to the via pad diameter. Any foreign conductor
;; that passes through this zone is flagged as a violation.
;; The pad radius is queried from the actual padstack using axlDBGetPad.
;;
;; Returns a list of violation records, each:
;;   (p_xy n_xy p_layer_str n_layer_str etch_layer
;;    net_name obj_type_str obj_dbid closest_dist hw hl intrusion_xy)
;; -----------------------------------------------------------------------
(procedure (dpvc_check_corridor via_p via_n own_net_names
            check_lyrs all_etch lmap pad_margin seen_objs)
  (prog nil
    (setq dpvc_cv nil)  ;; corridor violations
    (setq dpvc_pxy (getq via_p xy))
    (setq dpvc_nxy (getq via_n xy))

    ;; Corridor geometry
    (setq dpvc_cx
      (quotient (plus (nth 0 dpvc_pxy) (nth 0 dpvc_nxy)) 2.0))
    (setq dpvc_cy
      (quotient (plus (nth 1 dpvc_pxy) (nth 1 dpvc_nxy)) 2.0))
    (setq dpvc_ctr (list dpvc_cx dpvc_cy))

    (setq dpvc_dx (difference (nth 0 dpvc_nxy) (nth 0 dpvc_pxy)))
    (setq dpvc_dy (difference (nth 1 dpvc_nxy) (nth 1 dpvc_pxy)))
    (setq dpvc_span
      (sqrt (plus (times dpvc_dx dpvc_dx)
                  (times dpvc_dy dpvc_dy))))

    ;; Skip degenerate pairs (overlapping vias)
    (if (lessp dpvc_span 0.01)
    then (return nil))

    ;; Unit vectors: u along P->N, v perpendicular
    (setq dpvc_ux (quotient dpvc_dx dpvc_span))
    (setq dpvc_uy (quotient dpvc_dy dpvc_span))
    (setq dpvc_vx (times -1.0 dpvc_uy))
    (setq dpvc_vy dpvc_ux)

    ;; Determine layer span for checking (padstack definition)
    (setq dpvc_p_lyrs
      (dpvc_get_via_layers via_p all_etch lmap))
    (setq dpvc_n_lyrs
      (dpvc_get_via_layers via_n all_etch lmap))

    ;; Backdrill-aware layer narrowing:
    ;; When backdrill has been performed, the copper barrel is removed
    ;; from some layers. Only check layers where the via still has
    ;; copper post-backdrill. dpvc_get_via_active_layers returns nil
    ;; if the via is not backdrilled, in which case we keep the
    ;; original padstack span.
    (when dpvc__has_backdrill
      (let (dpvc_cc_p_active dpvc_cc_n_active)
        (setq dpvc_cc_p_active
          (dpvc_get_via_active_layers via_p all_etch lmap))
        (setq dpvc_cc_n_active
          (dpvc_get_via_active_layers via_n all_etch lmap))
        ;; Only narrow if backdrill data was found for that via
        (when dpvc_cc_p_active
          (setq dpvc_p_lyrs dpvc_cc_p_active))
        (when dpvc_cc_n_active
          (setq dpvc_n_lyrs dpvc_cc_n_active))
      )
    )

    ;; Common layers spanned by both vias (corridor only exists where
    ;; BOTH vias have copper — case-insensitive comparison)
    (setq dpvc_com_lyrs
      (setof dpvc_cl dpvc_p_lyrs
        (exists dpvc_nl dpvc_n_lyrs
          (equal (upperCase dpvc_cl) (upperCase dpvc_nl)))))

    ;; Intersect with user-selected check layers
    (setq dpvc_tgt_lyrs
      (setof dpvc_tl dpvc_com_lyrs
        (exists dpvc_ckl check_lyrs
          (equal (upperCase dpvc_tl) (upperCase dpvc_ckl)))))

    (if (null dpvc_tgt_lyrs) then (return nil))

    ;; Query actual via pad radius from padstack definition
    ;; Use the first target layer to query (inner layers have same pad)
    ;; Take the larger pad between P and N vias for conservative corridor
    (setq dpvc_pad_r nil)
    (setq dpvc_antipad_r nil)
    (let (dpvc_cc_try_lyr dpvc_cc_pr_p dpvc_cc_pr_n
          dpvc_cc_apr_p dpvc_cc_apr_n)
      (setq dpvc_cc_try_lyr (car dpvc_tgt_lyrs))
      ;; Regular (copper) pad radius
      (setq dpvc_cc_pr_p (dpvc_get_pad_radius via_p dpvc_cc_try_lyr))
      (setq dpvc_cc_pr_n (dpvc_get_pad_radius via_n dpvc_cc_try_lyr))
      (cond
        ((and dpvc_cc_pr_p dpvc_cc_pr_n)
          (setq dpvc_pad_r (max dpvc_cc_pr_p dpvc_cc_pr_n)))
        (dpvc_cc_pr_p (setq dpvc_pad_r dpvc_cc_pr_p))
        (dpvc_cc_pr_n (setq dpvc_pad_r dpvc_cc_pr_n))
      )
      ;; Anti-pad (clearance) radius — defines the EM coupling zone boundary
      ;; The void in the reference plane extends to the anti-pad edge, so this
      ;; is the physically correct exclusion zone for differential coupling.
      (setq dpvc_cc_apr_p (dpvc_get_antipad_radius via_p dpvc_cc_try_lyr))
      (setq dpvc_cc_apr_n (dpvc_get_antipad_radius via_n dpvc_cc_try_lyr))
      (cond
        ((and dpvc_cc_apr_p dpvc_cc_apr_n)
          (setq dpvc_antipad_r (max dpvc_cc_apr_p dpvc_cc_apr_n)))
        (dpvc_cc_apr_p (setq dpvc_antipad_r dpvc_cc_apr_p))
        (dpvc_cc_apr_n (setq dpvc_antipad_r dpvc_cc_apr_n))
      )
    )

    ;; Half-width: prefer anti-pad radius (EM coupling zone boundary),
    ;; fall back to regular pad radius, then to span-based estimate.
    ;; The anti-pad is the void in the reference plane — foreign conductors
    ;; within this zone cause impedance discontinuity.
    (setq dpvc_hw
      (cond
        (dpvc_antipad_r (plus dpvc_antipad_r pad_margin))
        (dpvc_pad_r     (plus dpvc_pad_r pad_margin))
        (t              (plus (times dpvc_span 0.3) pad_margin))))

    ;; Minimum floor (prevents degenerate corridors)
    (setq dpvc_min_hw
      (if (equal (nth 0 (axlDBGetDesignUnits)) "millimeters")
      then 0.025    ;; ~1 mil floor
      else 1.0))    ;; 1 mil floor
    (setq dpvc_hw (max dpvc_hw dpvc_min_hw))

    ;; Half-length: center-to-center (from one via center to the other)
    (setq dpvc_hl (quotient dpvc_span 2.0))

    ;; Layer strings for the report
    (setq dpvc_p_str (buildString dpvc_p_lyrs "/"))
    (setq dpvc_n_str (buildString dpvc_n_lyrs "/"))

    ;; Axis-aligned bounding box (AABB) for pre-filter selection
    ;; Expand generously to capture all OBB-intersecting objects
    (setq dpvc_fe (plus dpvc_hw 1.0))  ;; extra 1 unit padding
    (setq dpvc_bbx0 (difference dpvc_cx (plus dpvc_hl dpvc_fe)))
    (setq dpvc_bby0 (difference dpvc_cy (plus dpvc_hl dpvc_fe)))
    (setq dpvc_bbx1 (plus dpvc_cx (plus dpvc_hl dpvc_fe)))
    (setq dpvc_bby1 (plus dpvc_cy (plus dpvc_hl dpvc_fe)))
    (setq dpvc_bbox
      (list (list dpvc_bbx0 dpvc_bby0)
            (list dpvc_bbx1 dpvc_bby1)))

    ;; ---------------------------------------------------------------
    ;; Check each target layer for violations
    ;; ---------------------------------------------------------------
    ;; Candidates come from database traversal, including hidden layers.

    ;; The via broadphase is independent of target layer. Cache it once per
    ;; corridor, then retain the existing per-layer active-barrel tests.
    (setq dpvc_avias
      (dpvc_managed_candidates_in_box (cadr dpvc__managed_candidates) dpvc_bbox))
    (foreach dpvc_tl dpvc_tgt_lyrs
      (setq dpvc_elyr (strcat "ETCH/" dpvc_tl))

      ;; -- Find cline segments in the AABB --
      (setq dpvc_segs (dpvc_managed_candidates_in_box
        (arrayref (car dpvc__managed_candidates) (dpvc_managed_layer_key dpvc_tl))
        dpvc_bbox))

      ;; Filter to this specific etch layer.
      ;; Robust comparison: (getq seg layer) may return "ETCH/S03",
      ;; "S03", or other variants depending on object type. Compare
      ;; using upperCase and check both the full "ETCH/<lyr>" form
      ;; and the bare "<lyr>" form against the segment's layer string.
      (setq dpvc_lsegs
        (setof dpvc_s dpvc_segs
          (and dpvc_s
               (let (dpvc_seg_lyr_uc)
                 (setq dpvc_seg_lyr_uc
                   (upperCase (getq dpvc_s layer)))
                 (or (equal dpvc_seg_lyr_uc (upperCase dpvc_elyr))
                     (equal dpvc_seg_lyr_uc (upperCase dpvc_tl)))))))

      ;; Check each cline segment
      (foreach dpvc_s dpvc_lsegs
        (setq dpvc_snet (getq dpvc_s net))
        (setq dpvc_snn
          (if (and dpvc_snet (getq dpvc_snet name))
          then (upperCase (getq dpvc_snet name))
          else ""))
        ;; Skip own nets, GND nets, and optionally power nets
        (when (and (null (member dpvc_snn own_net_names))
                   (null (dpvc_is_ignored_net dpvc_snn)))
          ;; Skip if we already flagged this object
          (when (null (member dpvc_s seen_objs))
            (setq dpvc_se (getq dpvc_s startEnd))
            (when (and dpvc_se (geqp (length dpvc_se) 2))
              (setq dpvc_sp1 (nth 0 dpvc_se))
              (setq dpvc_sp2 (nth 1 dpvc_se))
              ;; Test intersection with the OBB (returns (tmin tmax) or nil)
              (setq dpvc_clip
                (dpvc_seg_intersects_obb
                  dpvc_sp1 dpvc_sp2
                  dpvc_ctr dpvc_ux dpvc_uy
                  dpvc_vx dpvc_vy dpvc_hl dpvc_hw))
              (when dpvc_clip
                ;; Compute the clipped segment (portion inside OBB)
                (setq dpvc_tmin_c (nth 0 dpvc_clip))
                (setq dpvc_tmax_c (nth 1 dpvc_clip))
                (setq dpvc_seg_dx (difference (nth 0 dpvc_sp2)
                                              (nth 0 dpvc_sp1)))
                (setq dpvc_seg_dy (difference (nth 1 dpvc_sp2)
                                              (nth 1 dpvc_sp1)))
                ;; Clipped entry point (where segment enters corridor)
                (setq dpvc_entry_pt
                  (list
                    (plus (nth 0 dpvc_sp1)
                      (times dpvc_tmin_c dpvc_seg_dx))
                    (plus (nth 1 dpvc_sp1)
                      (times dpvc_tmin_c dpvc_seg_dy))))
                ;; Clipped exit point
                (setq dpvc_exit_pt
                  (list
                    (plus (nth 0 dpvc_sp1)
                      (times dpvc_tmax_c dpvc_seg_dx))
                    (plus (nth 1 dpvc_sp1)
                      (times dpvc_tmax_c dpvc_seg_dy))))
                ;; Distance to P-N axis (informational severity indicator).
                ;; Sample entry, exit, and midpoint of clipped region,
                ;; take minimum. Closer = worse coupling.
                (setq dpvc_clip_mid
                  (list
                    (quotient (plus (nth 0 dpvc_entry_pt)
                                   (nth 0 dpvc_exit_pt)) 2.0)
                    (quotient (plus (nth 1 dpvc_entry_pt)
                                   (nth 1 dpvc_exit_pt)) 2.0)))
                (setq dpvc_d_entry
                  (dpvc_dist_to_line_segment dpvc_entry_pt
                    dpvc_pxy dpvc_nxy))
                (setq dpvc_d_exit
                  (dpvc_dist_to_line_segment dpvc_exit_pt
                    dpvc_pxy dpvc_nxy))
                (setq dpvc_d_mid
                  (dpvc_dist_to_line_segment dpvc_clip_mid
                    dpvc_pxy dpvc_nxy))
                (setq dpvc_cdist
                  (min dpvc_d_entry dpvc_d_exit dpvc_d_mid))
                ;; Intrusion XY: midpoint of clipped segment inside corridor
                (setq dpvc_intrusion_xy dpvc_clip_mid)
                ;; Record violation (includes hw/hl for visualization)
                (setq dpvc_cv
                  (cons
                    (list dpvc_pxy dpvc_nxy
                      dpvc_p_str dpvc_n_str
                      dpvc_elyr dpvc_snn
                      "cline_segment" dpvc_s dpvc_cdist
                      dpvc_hw dpvc_hl dpvc_intrusion_xy)
                    dpvc_cv))
              )
            )
          )
        )
      )

      ;; Check the via candidates cached for this corridor.
      (foreach dpvc_av dpvc_avias
        (when dpvc_av
          (setq dpvc_vnet (getq dpvc_av net))
          (setq dpvc_vnn
            (if (and dpvc_vnet (getq dpvc_vnet name))
            then (upperCase (getq dpvc_vnet name))
            else ""))
          ;; Skip own nets, GND, power, and already-seen objects
          (when (and (null (member dpvc_vnn own_net_names))
                     (null (dpvc_is_ignored_net dpvc_vnn))
                     (null (member dpvc_av seen_objs)))
            (setq dpvc_vxy (getq dpvc_av xy))
            ;; Point-in-OBB test
            (when (dpvc_point_in_obb dpvc_vxy
                    dpvc_ctr dpvc_ux dpvc_uy
                    dpvc_vx dpvc_vy dpvc_hl dpvc_hw)
              ;; Verify foreign via actually has copper on the layer being
              ;; checked. Start with the padstack definition span, then
              ;; narrow by backdrill data if available. A blind via
              ;; (e.g., TOP->S03) should NOT be flagged on S05. A
              ;; backdrilled via whose copper has been removed from this
              ;; layer is also NOT a violation (no barrel = no coupling).
              (setq dpvc_vlyrs
                (dpvc_get_via_layers dpvc_av all_etch lmap))
              ;; Backdrill check on the foreign via itself
              (when dpvc__has_backdrill
                (let (dpvc_fv_active)
                  (setq dpvc_fv_active
                    (dpvc_get_via_active_layers dpvc_av all_etch lmap))
                  (when dpvc_fv_active
                    (setq dpvc_vlyrs dpvc_fv_active))
                )
              )
              (when (exists dpvc_vlc dpvc_vlyrs
                      (equal (upperCase dpvc_vlc) (upperCase dpvc_tl)))
                (setq dpvc_cdist
                  (dpvc_dist_to_line_segment dpvc_vxy
                    dpvc_pxy dpvc_nxy))
                ;; Intrusion XY: the foreign via's center point
                (setq dpvc_intrusion_xy (list (nth 0 dpvc_vxy)
                                              (nth 1 dpvc_vxy)))
                (setq dpvc_cv
                  (cons
                    (list dpvc_pxy dpvc_nxy
                      dpvc_p_str dpvc_n_str
                      dpvc_elyr dpvc_vnn
                      "via" dpvc_av dpvc_cdist
                      dpvc_hw dpvc_hl dpvc_intrusion_xy)
                    dpvc_cv))
              )
            )
          )
        )
      )

      ;; -- Find shapes (copper pours/fills) in the AABB --
      ;; axlDBGetShapes returns shapes on a given layer. Check if any
      ;; foreign-net shape's bounding box overlaps the corridor AABB.
      ;; This catches copper fills/pours that cross the exclusion zone.
      ;;
      ;; Key filtering:
      ;; 1. dpvc_is_ignored_net — skip GND, PD_, PU_, NC, and
      ;;    optionally power nets (VDD/VCC/VSS/PWR).
      ;; 2. Plane pour detection — if a shape's bBox fully encloses
      ;;    the corridor AABB, it is a plane pour (reference plane or
      ;;    power plane) covering the entire area. These are expected
      ;;    and should not be flagged. Only shapes that PARTIALLY
      ;;    intrude into the corridor are real violations.
      ;; 3. Distance is computed from the closest point on the shape's
      ;;    bBox to the corridor center, not from the shape's center.
      (let (dpvc_cs_shapes dpvc_cs_shp dpvc_cs_net dpvc_cs_nn
            dpvc_cs_sbb dpvc_cs_sx0 dpvc_cs_sy0 dpvc_cs_sx1 dpvc_cs_sy1
            dpvc_cs_dist dpvc_cs_clx dpvc_cs_cly dpvc_cs_encloses)
        (setq dpvc_cs_shapes nil)
        (errset
          (setq dpvc_cs_shapes (axlDBGetShapes dpvc_elyr)) t)
        (when dpvc_cs_shapes
          (foreach dpvc_cs_shp dpvc_cs_shapes
            (when dpvc_cs_shp
              (setq dpvc_cs_net (getq dpvc_cs_shp net))
              (setq dpvc_cs_nn
                (if (and dpvc_cs_net (getq dpvc_cs_net name))
                then (upperCase (getq dpvc_cs_net name))
                else ""))
              ;; Skip shapes with no net, own nets, ignored nets,
              ;; and already-seen objects
              (when (and (greaterp (strlen dpvc_cs_nn) 0)
                         (null (member dpvc_cs_nn own_net_names))
                         (null (dpvc_is_ignored_net dpvc_cs_nn))
                         (null (member dpvc_cs_shp seen_objs)))
                ;; Quick pre-filter: check if shape bBox overlaps
                ;; corridor AABB
                (setq dpvc_cs_sbb nil)
                (errset
                  (setq dpvc_cs_sbb (getq dpvc_cs_shp bBox)) t)
                (when (and dpvc_cs_sbb
                           (listp dpvc_cs_sbb)
                           (geqp (length dpvc_cs_sbb) 2))
                  (setq dpvc_cs_sx0 (nth 0 (nth 0 dpvc_cs_sbb)))
                  (setq dpvc_cs_sy0 (nth 1 (nth 0 dpvc_cs_sbb)))
                  (setq dpvc_cs_sx1 (nth 0 (nth 1 dpvc_cs_sbb)))
                  (setq dpvc_cs_sy1 (nth 1 (nth 1 dpvc_cs_sbb)))
                  ;; AABB overlap test: shape bBox vs corridor AABB
                  (when (and (leqp dpvc_cs_sx0 dpvc_bbx1)
                             (geqp dpvc_cs_sx1 dpvc_bbx0)
                             (leqp dpvc_cs_sy0 dpvc_bby1)
                             (geqp dpvc_cs_sy1 dpvc_bby0))
                    ;; Check if shape bBox FULLY encloses the
                    ;; corridor AABB. A shape whose bBox covers the
                    ;; entire corridor is a plane pour (power or
                    ;; reference plane) — these are expected copper
                    ;; and must NOT be flagged. Only flag shapes
                    ;; that PARTIALLY intrude into the corridor.
                    (setq dpvc_cs_encloses
                      (and (leqp dpvc_cs_sx0 dpvc_bbx0)
                           (geqp dpvc_cs_sx1 dpvc_bbx1)
                           (leqp dpvc_cs_sy0 dpvc_bby0)
                           (geqp dpvc_cs_sy1 dpvc_bby1)))
                    (unless dpvc_cs_encloses
                      ;; Distance: closest point on shape bBox to
                      ;; the corridor center (P-N midpoint). Clamp
                      ;; the corridor center to the shape's bBox to
                      ;; find the nearest point on the bBox edge.
                      (setq dpvc_cs_clx
                        (max dpvc_cs_sx0 (min dpvc_cx dpvc_cs_sx1)))
                      (setq dpvc_cs_cly
                        (max dpvc_cs_sy0 (min dpvc_cy dpvc_cs_sy1)))
                      (setq dpvc_cs_dist
                        (dpvc_dist_to_line_segment
                          (list dpvc_cs_clx dpvc_cs_cly)
                          dpvc_pxy dpvc_nxy))
                      ;; Intrusion XY: corridor center (shapes are areas)
                      (setq dpvc_intrusion_xy
                        (list dpvc_cx dpvc_cy))
                      ;; Record violation
                      (setq dpvc_cv
                        (cons
                          (list dpvc_pxy dpvc_nxy
                            dpvc_p_str dpvc_n_str
                            dpvc_elyr dpvc_cs_nn
                            "shape" dpvc_cs_shp dpvc_cs_dist
                            dpvc_hw dpvc_hl dpvc_intrusion_xy)
                          dpvc_cv))
                    )
                  )
                )
              )
            )
          )
        )
      )
    )

    (return dpvc_cv)
  )
)

;; -----------------------------------------------------------------------
;; Point-in-OBB (Oriented Bounding Box) test
;;
;; Projects the point onto the OBB's local axes and checks if it falls
;; within the half-extents.
;;
;;   center : OBB center
;;   ux,uy  : unit vector along long axis (P->N direction)
;;   vx,vy  : unit vector along short axis (perpendicular)
;;   half_l : half-length along long axis
;;   half_w : half-width along short axis
;; -----------------------------------------------------------------------
(procedure (dpvc_point_in_obb pt ctr ux uy vx vy hl hw)
  (prog nil
    (setq dpvc_odx (difference (nth 0 pt) (nth 0 ctr)))
    (setq dpvc_ody (difference (nth 1 pt) (nth 1 ctr)))
    ;; Project onto local axes
    (setq dpvc_pu (plus (times dpvc_odx ux) (times dpvc_ody uy)))
    (setq dpvc_pv (plus (times dpvc_odx vx) (times dpvc_ody vy)))
    (return (and (leqp (dpvc_abs dpvc_pu) hl)
                 (leqp (dpvc_abs dpvc_pv) hw)))
  )
)

;; -----------------------------------------------------------------------
;; Segment vs OBB intersection test (returns clip parameters)
;;
;; Uses Liang-Barsky parametric clipping in OBB-local coordinates.
;; First transforms segment endpoints to the OBB local frame, then
;; performs AABB intersection test on the unit square.
;;
;; Returns: (tmin tmax) if intersection exists (0 <= tmin <= tmax <= 1)
;;          nil if no intersection
;; The caller can compute the clipped segment as:
;;   entry_pt = p1 + tmin*(p2-p1), exit_pt = p1 + tmax*(p2-p1)
;; -----------------------------------------------------------------------
(procedure (dpvc_seg_intersects_obb p1 p2 ctr ux uy vx vy hl hw)
  (prog nil
    ;; Transform p1 to OBB local coords
    (setq dpvc_d1x (difference (nth 0 p1) (nth 0 ctr)))
    (setq dpvc_d1y (difference (nth 1 p1) (nth 1 ctr)))
    (setq dpvc_lu1 (plus (times dpvc_d1x ux) (times dpvc_d1y uy)))
    (setq dpvc_lv1 (plus (times dpvc_d1x vx) (times dpvc_d1y vy)))

    ;; Transform p2 to OBB local coords
    (setq dpvc_d2x (difference (nth 0 p2) (nth 0 ctr)))
    (setq dpvc_d2y (difference (nth 1 p2) (nth 1 ctr)))
    (setq dpvc_lu2 (plus (times dpvc_d2x ux) (times dpvc_d2y uy)))
    (setq dpvc_lv2 (plus (times dpvc_d2x vx) (times dpvc_d2y vy)))

    ;; In local coords, OBB is an AABB: [-hl,hl] x [-hw,hw]
    ;; Segment goes from (lu1,lv1) to (lu2,lv2)

    ;; Liang-Barsky parametric line clipping
    (setq dpvc_du (difference dpvc_lu2 dpvc_lu1))
    (setq dpvc_dv (difference dpvc_lv2 dpvc_lv1))
    (setq dpvc_tmin 0.0)
    (setq dpvc_tmax 1.0)
    (setq dpvc_hit t)

    ;; Clip against U axis (left/right bounds)
    (if (lessp (dpvc_abs dpvc_du) 1e-10)
    then
      ;; Segment is parallel to V axis
      (when (or (greaterp dpvc_lu1 hl)
                (lessp dpvc_lu1 (times -1.0 hl)))
        (setq dpvc_hit nil))
    else
      (setq dpvc_t1
        (quotient (difference (times -1.0 hl) dpvc_lu1) dpvc_du))
      (setq dpvc_t2
        (quotient (difference hl dpvc_lu1) dpvc_du))
      (when (greaterp dpvc_t1 dpvc_t2)
        (setq dpvc_tt dpvc_t1)
        (setq dpvc_t1 dpvc_t2)
        (setq dpvc_t2 dpvc_tt))
      (setq dpvc_tmin (max dpvc_tmin dpvc_t1))
      (setq dpvc_tmax (min dpvc_tmax dpvc_t2))
      (when (greaterp dpvc_tmin dpvc_tmax)
        (setq dpvc_hit nil))
    )

    ;; Clip against V axis (top/bottom bounds)
    (when dpvc_hit
      (if (lessp (dpvc_abs dpvc_dv) 1e-10)
      then
        (when (or (greaterp dpvc_lv1 hw)
                  (lessp dpvc_lv1 (times -1.0 hw)))
          (setq dpvc_hit nil))
      else
        (setq dpvc_t1
          (quotient (difference (times -1.0 hw) dpvc_lv1) dpvc_dv))
        (setq dpvc_t2
          (quotient (difference hw dpvc_lv1) dpvc_dv))
        (when (greaterp dpvc_t1 dpvc_t2)
          (setq dpvc_tt dpvc_t1)
          (setq dpvc_t1 dpvc_t2)
          (setq dpvc_t2 dpvc_tt))
        (setq dpvc_tmin (max dpvc_tmin dpvc_t1))
        (setq dpvc_tmax (min dpvc_tmax dpvc_t2))
        (when (greaterp dpvc_tmin dpvc_tmax)
          (setq dpvc_hit nil))
      )
    )

    ;; Return clip parameters or nil
    (if dpvc_hit
    then (return (list dpvc_tmin dpvc_tmax))
    else (return nil))
  )
)

;; -----------------------------------------------------------------------
;; Distance from a point to a line segment (for closest-approach calc)
;; Uses parametric projection clamped to [0,1] range.
;; -----------------------------------------------------------------------
(procedure (dpvc_dist_to_line_segment pt seg_a seg_b)
  (prog nil
    (setq dpvc_ax (nth 0 seg_a))
    (setq dpvc_ay (nth 1 seg_a))
    (setq dpvc_bx (nth 0 seg_b))
    (setq dpvc_by (nth 1 seg_b))
    (setq dpvc_ptx (nth 0 pt))
    (setq dpvc_pty (nth 1 pt))

    (setq dpvc_abx (difference dpvc_bx dpvc_ax))
    (setq dpvc_aby (difference dpvc_by dpvc_ay))
    (setq dpvc_abl2
      (plus (times dpvc_abx dpvc_abx) (times dpvc_aby dpvc_aby)))

    ;; Degenerate segment (point)
    (if (lessp dpvc_abl2 1e-10)
    then (return (axlDistance pt seg_a)))

    ;; Parametric projection of pt onto line A-B
    (setq dpvc_t
      (quotient
        (plus (times (difference dpvc_ptx dpvc_ax) dpvc_abx)
              (times (difference dpvc_pty dpvc_ay) dpvc_aby))
        dpvc_abl2))

    ;; Clamp to segment range
    (setq dpvc_t (max 0.0 (min 1.0 dpvc_t)))

    ;; Closest point on segment
    (setq dpvc_cpx (plus dpvc_ax (times dpvc_t dpvc_abx)))
    (setq dpvc_cpy (plus dpvc_ay (times dpvc_t dpvc_aby)))

    ;; Distance
    (setq dpvc_ddx (difference dpvc_ptx dpvc_cpx))
    (setq dpvc_ddy (difference dpvc_pty dpvc_cpy))
    (return (sqrt (plus (times dpvc_ddx dpvc_ddx)
                        (times dpvc_ddy dpvc_ddy))))
  )
)

;; -----------------------------------------------------------------------
;; Write machine-readable .dat file for the violation navigator
;;
;; Produces the retained pipe-delimited export without re-running analysis.
;; Live navigation uses the private scalar snapshot prepared separately.
;;
;; Parameters:
;;   aggr_data  — the aggressor summary list (9-element records)
;;   annot_data — the annotated violation list (4-element records)
;;   check_lyrs — list of layer names that were checked
;;   dp_counts  — list of (dp_name count) pairs
;;   design_nm  — design name string
;;   units_str  — "mils" or "millimeters"
;;   total_dp   — number of diff pairs checked
;;   total_vp   — number of via pairs analyzed
;; -----------------------------------------------------------------------
(procedure (dpvc_write_dat_file aggr_data annot_data check_lyrs
                                dp_counts design_nm units_str
                                total_dp total_vp)
  (prog (dpvc_wd_fp dpvc_wd_dat_path dpvc_wd_vr dpvc_wd_ann
         dpvc_wd_ag dpvc_wd_lyrs_csv dpvc_wd_dps_csv
         dpvc_wd_dp_names dpvc_wd_ixy dpvc_wd_ix dpvc_wd_iy)

    ;; Derive .dat path from report path: replace .txt with .dat
    ;; If report path does not end in .txt, just append .dat
    (if (and (stringp dpvc__report_path)
             (greaterp (strlen dpvc__report_path) 4)
             (equal (lowerCase
               (substring dpvc__report_path
                 (difference (strlen dpvc__report_path) 3) 4))
               ".txt"))
    then
      (setq dpvc_wd_dat_path
        (strcat (substring dpvc__report_path 1
                  (difference (strlen dpvc__report_path) 4))
                ".dat"))
    else
      (setq dpvc_wd_dat_path
        (strcat dpvc__report_path ".dat"))
    )

    ;; Open output file
    (setq dpvc_wd_fp (outfile dpvc_wd_dat_path))
    (setq dpvc__dat_port dpvc_wd_fp)
    (when (null dpvc_wd_fp)
      (printf "  WARNING: Cannot write .dat file: %s\n"
        dpvc_wd_dat_path)
      (return nil))

    ;; -----------------------------------------------------------------
    ;; Header: version
    ;; -----------------------------------------------------------------
    (fprintf dpvc_wd_fp "DPVC_DAT_VERSION|1\n")

    ;; -----------------------------------------------------------------
    ;; DESIGN line: name, units, total_dp, total_vp, date
    ;; -----------------------------------------------------------------
    (fprintf dpvc_wd_fp "DESIGN|%s|%s|%d|%d|%s\n"
      design_nm units_str total_dp total_vp (getCurrentTime))

    ;; -----------------------------------------------------------------
    ;; LAYERS line: comma-separated list of checked layers
    ;; -----------------------------------------------------------------
    (fprintf dpvc_wd_fp "LAYERS|%s\n"
      (buildString check_lyrs ","))

    ;; -----------------------------------------------------------------
    ;; DPNAMES line: comma-separated list of all checked diff pair names
    ;; dp_counts is a list of (dp_name count) pairs
    ;; -----------------------------------------------------------------
    (setq dpvc_wd_dp_names
      (foreach mapcar dpvc_wd_dc dp_counts (nth 0 dpvc_wd_dc)))
    (fprintf dpvc_wd_fp "DPNAMES|%s\n"
      (if dpvc_wd_dp_names
      then (buildString dpvc_wd_dp_names ",")
      else ""))

    ;; -----------------------------------------------------------------
    ;; AGGR lines: one per aggressor net
    ;; Record: (net_name corr viols cat risk min_dist layers dps action)
    ;;          0        1    2     3   4    5        6      7   8
    ;; -----------------------------------------------------------------
    (foreach dpvc_wd_ag aggr_data
      ;; Convert layer list and dp list to CSV strings
      (setq dpvc_wd_lyrs_csv
        (if (listp (nth 6 dpvc_wd_ag))
        then (buildString (nth 6 dpvc_wd_ag) ",")
        else ""))
      (setq dpvc_wd_dps_csv
        (if (listp (nth 7 dpvc_wd_ag))
        then (buildString (nth 7 dpvc_wd_ag) ",")
        else ""))
      (fprintf dpvc_wd_fp "AGGR|%s|%d|%d|%s|%s|%.6f|%s|%s|%s\n"
        (nth 0 dpvc_wd_ag)         ;; net_name
        (nth 1 dpvc_wd_ag)         ;; corridor_count
        (nth 2 dpvc_wd_ag)         ;; violation_count
        (nth 3 dpvc_wd_ag)         ;; category
        (nth 4 dpvc_wd_ag)         ;; risk
        (nth 5 dpvc_wd_ag)         ;; min_distance (design units)
        dpvc_wd_lyrs_csv           ;; layers CSV
        dpvc_wd_dps_csv            ;; dp names CSV
        (nth 8 dpvc_wd_ag))        ;; action
    )

    ;; -----------------------------------------------------------------
    ;; VIOL lines: one per violation (annotated)
    ;; annotated record: (viol_rec aggr_cat aggr_risk overall_risk)
    ;; viol_rec: (dp_name p_xy n_xy p_layers n_layers
    ;;            layer net_name obj_type obj_dbid dist hw hl intrusion_xy)
    ;;            0      1    2    3        4
    ;;            5     6        7        8       9    10 11 12
    ;; -----------------------------------------------------------------
    (foreach dpvc_wd_ann annot_data
      (setq dpvc_wd_vr (nth 0 dpvc_wd_ann))

      ;; Extract intrusion XY safely (may be nil for some records)
      (setq dpvc_wd_ixy (nth 12 dpvc_wd_vr))
      (setq dpvc_wd_ix
        (if (and (listp dpvc_wd_ixy) (nth 0 dpvc_wd_ixy))
        then (nth 0 dpvc_wd_ixy)
        else 0.0))
      (setq dpvc_wd_iy
        (if (and (listp dpvc_wd_ixy) (nth 1 dpvc_wd_ixy))
        then (nth 1 dpvc_wd_ixy)
        else 0.0))

      (fprintf dpvc_wd_fp
        "VIOL|%s|%.4f|%.4f|%.4f|%.4f|%s|%s|%s|%.6f|%.4f|%.4f|%.4f|%.4f|%s|%s\n"
        (nth 0 dpvc_wd_vr)          ;; dp_name
        (nth 0 (nth 1 dpvc_wd_vr))  ;; p_x
        (nth 1 (nth 1 dpvc_wd_vr))  ;; p_y
        (nth 0 (nth 2 dpvc_wd_vr))  ;; n_x
        (nth 1 (nth 2 dpvc_wd_vr))  ;; n_y
        (nth 5 dpvc_wd_vr)          ;; layer
        (nth 6 dpvc_wd_vr)          ;; net_name
        (nth 7 dpvc_wd_vr)          ;; obj_type
        (nth 9 dpvc_wd_vr)          ;; distance (design units)
        (nth 10 dpvc_wd_vr)         ;; hw (corridor half-width)
        (nth 11 dpvc_wd_vr)         ;; hl (corridor half-length)
        dpvc_wd_ix                  ;; intrusion_x
        dpvc_wd_iy                  ;; intrusion_y
        (nth 1 dpvc_wd_ann)         ;; category (aggr_cat)
        (nth 3 dpvc_wd_ann))        ;; risk (overall_risk)
    )

    (unless (close dpvc_wd_fp)
      (error "DP via corridor navigator output could not be closed successfully."))
    (setq dpvc__dat_port nil)
    ;; Publish navigator state only after successful output completion.
    (setq dpvc__dat_path dpvc_wd_dat_path)
    (printf "  Navigator data:   %s\n" dpvc_wd_dat_path)
    (return t)
  )
)

;; -----------------------------------------------------------------------
;; Managed navigation. DBIDs are command-scoped and MUST NOT cross the idle
;; boundary between report and zoom. Cache scalar locators and independent
;; polygon values only, then resolve unique fresh objects on every zoom.
;; Cache: (drawingIdentity units layers report name rows sdkIdentity).
;; Row: (ready reason layer bounds scalarLocators signatures edgeCount).
;; Polygons are compared with equal (native geometric equality), not eq and
;; not reconstructed from the implementation-specific signed arc radii.
;; -----------------------------------------------------------------------
(procedure (dpvc_navigation_clear)
  (prog ()
    (setq dpvc__navigation_cache nil)
    (return t)))

(procedure (dpvc_nav_design_identity)
  (prog (dpvc_ndi_name dpvc_ndi_path)
    (setq dpvc_ndi_name (axlCurrentDesign))
    (setq dpvc_ndi_path (axlGetDrawingName))
    (unless (and (stringp dpvc_ndi_name) (greaterp (strlen dpvc_ndi_name) 0)
                 (stringp dpvc_ndi_path) (greaterp (strlen dpvc_ndi_path) 0))
      (error "The native drawing identity is unavailable."))
    (return (list dpvc_ndi_name dpvc_ndi_path))))

(procedure (dpvc_nav_layer_key dpvc_nk_layer)
  (prog ()
    (unless (and (stringp dpvc_nk_layer)
                 (greaterp (strlen dpvc_nk_layer) 0))
      (error "Missing native layer name."))
    (return
      (upperCase
        (if (and (greaterp (strlen dpvc_nk_layer) 5)
                 (equal (upperCase (substring dpvc_nk_layer 1 5)) "ETCH/"))
          then (substring dpvc_nk_layer 6) else dpvc_nk_layer)))))

(procedure (dpvc_nav_layer_index dpvc_ni_layer dpvc_ni_layers)
  (prog (dpvc_ni_tail)
    (setq dpvc_ni_tail
      (member (dpvc_nav_layer_key dpvc_ni_layer) dpvc_ni_layers))
    (unless dpvc_ni_tail (error "Layer is no longer in the stackup."))
    (return (difference (length dpvc_ni_layers) (length dpvc_ni_tail)))))

(procedure (dpvc_nav_point dpvc_np_xy)
  (prog ()
    (unless (and (listp dpvc_np_xy) (equal (length dpvc_np_xy) 2)
                 (numberp (car dpvc_np_xy)) (numberp (cadr dpvc_np_xy))
                 (lessp (abs (car dpvc_np_xy)) 1e9)
                 (lessp (abs (cadr dpvc_np_xy)) 1e9))
      (error "Native geometry contains an invalid point."))
    ;; Own the point list; refreshing a DBID must not mutate its snapshot.
    (return (list (car dpvc_np_xy) (cadr dpvc_np_xy)))))

(procedure (dpvc_nav_box dpvc_nb_box)
  (prog (dpvc_nb_min dpvc_nb_max)
    (unless (and (listp dpvc_nb_box) (equal (length dpvc_nb_box) 2))
      (error "Native geometry contains an invalid bounding box."))
    (setq dpvc_nb_min (dpvc_nav_point (car dpvc_nb_box)))
    (setq dpvc_nb_max (dpvc_nav_point (cadr dpvc_nb_box)))
    (unless (and (lessp (car dpvc_nb_min) (car dpvc_nb_max))
                 (lessp (cadr dpvc_nb_min) (cadr dpvc_nb_max)))
      (error "Native bounding box is empty or reversed."))
    (return (list dpvc_nb_min dpvc_nb_max))))

(procedure (dpvc_nav_poly_edges dpvc_ne_polys)
  (prog (dpvc_ne_queue dpvc_ne_poly dpvc_ne_holes dpvc_ne_edges
         dpvc_ne_objects dpvc_ne_vertices)
    (unless (listp dpvc_ne_polys) (error "Invalid native polygon list."))
    (setq dpvc_ne_queue (append dpvc_ne_polys nil))
    (setq dpvc_ne_edges 0)
    (setq dpvc_ne_objects 0)
    (while dpvc_ne_queue
      (setq dpvc_ne_objects (add1 dpvc_ne_objects))
      (when (greaterp dpvc_ne_objects 128)
        (error "Native polygon topology exceeds navigation bounds."))
      (setq dpvc_ne_poly (car dpvc_ne_queue))
      (setq dpvc_ne_queue (cdr dpvc_ne_queue))
      (setq dpvc_ne_vertices (getq dpvc_ne_poly vertices))
      (unless (and (listp dpvc_ne_vertices) dpvc_ne_vertices)
        (error "Native polygon contour is unavailable."))
      (setq dpvc_ne_edges (plus dpvc_ne_edges (length dpvc_ne_vertices)))
      (when (greaterp dpvc_ne_edges 4096)
        (error "Native contour exceeds navigation bounds."))
      (setq dpvc_ne_holes (getq dpvc_ne_poly holes))
      (unless (listp dpvc_ne_holes) (error "Invalid native holes."))
      (when (greaterp (length dpvc_ne_holes) 128)
        (error "Native hole count exceeds navigation bounds."))
      (setq dpvc_ne_queue (append dpvc_ne_holes dpvc_ne_queue)))
    (return dpvc_ne_edges)))

(procedure (dpvc_nav_via_metadata dpvc_nm_obj dpvc_nm_layers dpvc_nm_layer)
  (prog (dpvc_nm_def dpvc_nm_span dpvc_nm_through dpvc_nm_lo dpvc_nm_hi
         dpvc_nm_idx dpvc_nm_exclude dpvc_nm_status dpvc_nm_bd
         dpvc_nm_top dpvc_nm_bot dpvc_nm_data dpvc_nm_key dpvc_nm_value)
    (setq dpvc_nm_def (getq dpvc_nm_obj definition))
    (unless (and dpvc_nm_def (axlDBRefreshId dpvc_nm_def))
      (error "Via padstack is unavailable."))
    (setq dpvc_nm_through
      (or (getq dpvc_nm_obj isThrough) (getq dpvc_nm_def isThrough)))
    (setq dpvc_nm_span (getq dpvc_nm_def startEnd))
    (if dpvc_nm_through
    then
      (setq dpvc_nm_lo 0)
      (setq dpvc_nm_hi (sub1 (length dpvc_nm_layers)))
    else
      (unless (and (listp dpvc_nm_span) (equal (length dpvc_nm_span) 2))
        (error "Via span cannot be established."))
      (setq dpvc_nm_lo
        (dpvc_nav_layer_index (car dpvc_nm_span) dpvc_nm_layers))
      (setq dpvc_nm_hi
        (dpvc_nav_layer_index (cadr dpvc_nm_span) dpvc_nm_layers))
      (when (greaterp dpvc_nm_lo dpvc_nm_hi)
        (setq dpvc_nm_idx dpvc_nm_lo)
        (setq dpvc_nm_lo dpvc_nm_hi)
        (setq dpvc_nm_hi dpvc_nm_idx)))
    (setq dpvc_nm_exclude (getq (getq dpvc_nm_obj prop) BACKDRILL_EXCLUDE))
    (when dpvc_nm_exclude
      (unless (stringp dpvc_nm_exclude) (error "Invalid backdrill exclusion."))
      (setq dpvc_nm_exclude (upperCase dpvc_nm_exclude)))
    (unless (member dpvc_nm_exclude
              (list nil "" "EXCLUDE_TOP" "EXCLUDE_BOTTOM" "EXCLUDE_BOTH"))
      (error "Unsupported backdrill exclusion."))
    (setq dpvc_nm_status (axlBackdrillGet 'status))
    (unless (member dpvc_nm_status (list 'notStarted t))
      (error "Backdrill status is unavailable or out of date."))
    (when (eq dpvc_nm_status t)
      (setq dpvc_nm_bd (axlBackdrillGet dpvc_nm_obj)))
    (when dpvc_nm_bd
      (foreach dpvc_nm_key
        '(holeSize backdrillSize electricalStub mfgStub
          topStartLayer topMustCutLayer topMustNotCutLayer topDrillDepth
          topRemainingStub botStartLayer botMustCutLayer botMustNotCutLayer
          botDrillDepth botRemainingStub)
        (setq dpvc_nm_value (get dpvc_nm_bd dpvc_nm_key))
        (unless (or (null dpvc_nm_value) (stringp dpvc_nm_value)
                    (numberp dpvc_nm_value))
          (error "Backdrill data is not a supported scalar."))
        (setq dpvc_nm_data (cons dpvc_nm_value dpvc_nm_data)))
      (when (and (get dpvc_nm_bd 'topStartLayer)
                 (not (member dpvc_nm_exclude '("EXCLUDE_TOP" "EXCLUDE_BOTH"))))
        (setq dpvc_nm_top
          (dpvc_nav_layer_index (get dpvc_nm_bd 'topMustNotCutLayer)
            dpvc_nm_layers))
        (setq dpvc_nm_lo (max dpvc_nm_lo dpvc_nm_top)))
      (when (and (get dpvc_nm_bd 'botStartLayer)
                 (not (member dpvc_nm_exclude '("EXCLUDE_BOTTOM" "EXCLUDE_BOTH"))))
        (setq dpvc_nm_bot
          (dpvc_nav_layer_index (get dpvc_nm_bd 'botMustNotCutLayer)
            dpvc_nm_layers))
        (setq dpvc_nm_hi (min dpvc_nm_hi dpvc_nm_bot))))
    (setq dpvc_nm_idx (dpvc_nav_layer_index dpvc_nm_layer dpvc_nm_layers))
    (unless (and (geqp dpvc_nm_idx dpvc_nm_lo) (leqp dpvc_nm_idx dpvc_nm_hi))
      (error "Via is not active on the finding layer."))
    (return
      (list (getq dpvc_nm_def name) (if dpvc_nm_through then t else nil)
        (if (and (listp dpvc_nm_span) (equal (length dpvc_nm_span) 2))
          then (list (car dpvc_nm_span) (cadr dpvc_nm_span)) else nil)
        dpvc_nm_lo dpvc_nm_hi dpvc_nm_exclude dpvc_nm_status
        (reverse dpvc_nm_data)))))

(procedure (dpvc_nav_signature dpvc_ns_obj dpvc_ns_layer dpvc_ns_layers)
  (prog (dpvc_ns_type dpvc_ns_net dpvc_ns_name dpvc_ns_xy dpvc_ns_meta
         dpvc_ns_regular dpvc_ns_anti dpvc_ns_drill dpvc_ns_edges
         dpvc_ns_ends dpvc_ns_box)
    ;; A design/branch refresh does not refresh its children. Refresh the
    ;; exact object and its net/definition before reading any cached field.
    (unless (and dpvc_ns_obj (axlDBRefreshId dpvc_ns_obj))
      (error "The finding object was removed."))
    (setq dpvc_ns_type (getq dpvc_ns_obj objType))
    (unless (member dpvc_ns_type '("via" "line" "arc" "shape"))
      (error "The finding object type is unsupported."))
    (setq dpvc_ns_net (getq dpvc_ns_obj net))
    (unless (and dpvc_ns_net (axlDBRefreshId dpvc_ns_net))
      (error "The finding net was removed."))
    (setq dpvc_ns_name (getq dpvc_ns_net name))
    (unless (stringp dpvc_ns_name) (error "The finding net has no name."))
    (setq dpvc_ns_box (dpvc_nav_box (getq dpvc_ns_obj bBox)))
    (if (equal dpvc_ns_type "via")
    then
      (setq dpvc_ns_xy (dpvc_nav_point (getq dpvc_ns_obj xy)))
      (setq dpvc_ns_meta
        (dpvc_nav_via_metadata dpvc_ns_obj dpvc_ns_layers dpvc_ns_layer))
      (setq dpvc_ns_anti
        (axlPolyFromDB dpvc_ns_obj ?layer dpvc_ns_layer ?padType 'ANTI ?holes t))
      (setq dpvc_ns_drill (axlPolyFromDrillHole dpvc_ns_obj))
      (unless dpvc_ns_drill (error "The via's drill geometry is unavailable."))
    else
      (unless (equal (dpvc_nav_layer_key (getq dpvc_ns_obj layer))
                     (dpvc_nav_layer_key dpvc_ns_layer))
        (error "The finding object changed layers."))
      (if (equal dpvc_ns_type "shape")
      then
        (when (or (getq dpvc_ns_obj fillOOD)
                  (greaterp (length (getq dpvc_ns_obj segments)) 4096)
                  (greaterp (length (getq dpvc_ns_obj voids)) 128))
          (error "Shape fill is out of date or too complex for navigation."))
        (setq dpvc_ns_meta (list (getq dpvc_ns_obj shapeIsBoundary)
          (if (listp (getq dpvc_ns_obj fill))
            then (append (getq dpvc_ns_obj fill) nil)
            else (getq dpvc_ns_obj fill))))
      else
        (setq dpvc_ns_ends (getq dpvc_ns_obj startEnd))
        (unless (and (listp dpvc_ns_ends) (equal (length dpvc_ns_ends) 2))
          (error "The route endpoints are unavailable."))
        (setq dpvc_ns_meta
          (list (dpvc_nav_point (car dpvc_ns_ends))
            (dpvc_nav_point (cadr dpvc_ns_ends)) (getq dpvc_ns_obj width)))
        (when (equal dpvc_ns_type "arc")
          (setq dpvc_ns_meta
            (append dpvc_ns_meta
              (list (dpvc_nav_point (getq dpvc_ns_obj xy))
                (getq dpvc_ns_obj isClockwise) (getq dpvc_ns_obj isCircle)))))))
    (setq dpvc_ns_regular
      (axlPolyFromDB dpvc_ns_obj ?layer dpvc_ns_layer ?padType 'REGULAR
        ?holes t ?endCapType 'ROUND ?xhatch t))
    (unless dpvc_ns_regular (error "Selected-layer copper is unavailable."))
    (setq dpvc_ns_edges
      (plus (dpvc_nav_poly_edges dpvc_ns_regular)
        (dpvc_nav_poly_edges dpvc_ns_anti) (dpvc_nav_poly_edges dpvc_ns_drill)))
    (when (greaterp dpvc_ns_edges 4096)
      (error "The complete object signature exceeds navigation bounds."))
    ;; Stamp, regular, anti, drill, edgeCount. No lossy contour truncation.
    (return (list (list dpvc_ns_type (upperCase dpvc_ns_name) dpvc_ns_xy
                    dpvc_ns_layer dpvc_ns_box dpvc_ns_meta)
              dpvc_ns_regular dpvc_ns_anti dpvc_ns_drill dpvc_ns_edges))))

(procedure (dpvc_nav_resolve_signature dpvc_nrs_locator dpvc_nrs_candidates
                                      dpvc_nrs_layers)
  (prog (dpvc_nrs_type dpvc_nrs_layer dpvc_nrs_objects dpvc_nrs_object
         dpvc_nrs_net dpvc_nrs_name dpvc_nrs_signature dpvc_nrs_result
         dpvc_nrs_matches)
    (setq dpvc_nrs_type (nth 0 dpvc_nrs_locator))
    (setq dpvc_nrs_layer (nth 3 dpvc_nrs_locator))
    ;; Reuse the complete selection-free branch collector and its broadphase.
    ;; Shapes retain the original visibility-independent native layer query.
    (setq dpvc_nrs_objects
      (cond
        ((equal dpvc_nrs_type "via")
          (dpvc_managed_candidates_in_box (cadr dpvc_nrs_candidates)
            (nth 4 dpvc_nrs_locator)))
        ((member dpvc_nrs_type '("line" "arc"))
          (dpvc_managed_candidates_in_box
            (arrayref (car dpvc_nrs_candidates) (dpvc_nav_layer_key dpvc_nrs_layer))
            (nth 4 dpvc_nrs_locator)))
        ((equal dpvc_nrs_type "shape") (axlDBGetShapes dpvc_nrs_layer))
        (t (error "The stored native locator type is unsupported."))))
    (setq dpvc_nrs_matches 0)
    (foreach dpvc_nrs_object dpvc_nrs_objects
      (unless (axlDBRefreshId dpvc_nrs_object)
        (error "A native locator candidate could not be refreshed."))
      (when (and (equal (getq dpvc_nrs_object objType) dpvc_nrs_type)
                 (equal (getq dpvc_nrs_object bBox) (nth 4 dpvc_nrs_locator))
                 (or (not (equal dpvc_nrs_type "via"))
                     (equal (getq dpvc_nrs_object xy) (nth 2 dpvc_nrs_locator))))
        (setq dpvc_nrs_net (getq dpvc_nrs_object net))
        (setq dpvc_nrs_name "")
        (when dpvc_nrs_net
          (unless (axlDBRefreshId dpvc_nrs_net)
            (error "A native locator candidate's net could not be refreshed."))
          (setq dpvc_nrs_name (getq dpvc_nrs_net name)))
        (when (and (stringp dpvc_nrs_name)
                   (equal (upperCase dpvc_nrs_name) (nth 1 dpvc_nrs_locator)))
          (setq dpvc_nrs_signature
            (dpvc_nav_signature dpvc_nrs_object dpvc_nrs_layer dpvc_nrs_layers))
          ;; Require unique equality of ALL scalar fields, including exact
          ;; endpoints/arc metadata or padstack/span/backdrill state. Only
          ;; then may the caller compare complete copper/anti/drill polygons.
          (when (equal (car dpvc_nrs_signature) dpvc_nrs_locator)
            (setq dpvc_nrs_matches (add1 dpvc_nrs_matches))
            (setq dpvc_nrs_result dpvc_nrs_signature)))))
    (unless (equal dpvc_nrs_matches 1)
      (printf "DPVC native locator: type=%s exact scalar matches=%d\n"
        dpvc_nrs_type dpvc_nrs_matches)
      (error "The native object is missing, changed, or ambiguous."))
    (return dpvc_nrs_result)))

(procedure (dpvc_nav_row dpvc_nr_ann dpvc_nr_units dpvc_nr_layers)
  (prog (dpvc_nr_vr dpvc_nr_p dpvc_nr_n dpvc_nr_dx dpvc_nr_dy
         dpvc_nr_span dpvc_nr_cx dpvc_nr_cy dpvc_nr_hw dpvc_nr_hl
         dpvc_nr_ex dpvc_nr_ey dpvc_nr_margin dpvc_nr_scale dpvc_nr_box
         dpvc_nr_layer dpvc_nr_ids dpvc_nr_signatures dpvc_nr_obj dpvc_nr_edges)
    (setq dpvc_nr_vr (car dpvc_nr_ann))
    (unless (and (listp dpvc_nr_vr) (equal (length dpvc_nr_vr) 15))
      (error "This crossing has no private native references."))
    (setq dpvc_nr_p (dpvc_nav_point (nth 1 dpvc_nr_vr)))
    (setq dpvc_nr_n (dpvc_nav_point (nth 2 dpvc_nr_vr)))
    (setq dpvc_nr_hw (nth 10 dpvc_nr_vr))
    (setq dpvc_nr_hl (nth 11 dpvc_nr_vr))
    (unless (and (numberp dpvc_nr_hw) (greaterp dpvc_nr_hw 0)
                 (numberp dpvc_nr_hl) (greaterp dpvc_nr_hl 0))
      (error "This crossing has invalid native bounds."))
    (setq dpvc_nr_dx (difference (car dpvc_nr_n) (car dpvc_nr_p)))
    (setq dpvc_nr_dy (difference (cadr dpvc_nr_n) (cadr dpvc_nr_p)))
    (setq dpvc_nr_span (sqrt (plus (times dpvc_nr_dx dpvc_nr_dx)
                                  (times dpvc_nr_dy dpvc_nr_dy))))
    (unless (greaterp dpvc_nr_span 0) (error "The via pair is degenerate."))
    (setq dpvc_nr_scale (if (equal (car dpvc_nr_units) "millimeters")
      then 0.0254 else 1.0))
    ;; Add 25..100 mil of context, independently of design units.
    (setq dpvc_nr_margin (max (times 25.0 dpvc_nr_scale)
      (min (times 100.0 dpvc_nr_scale) (quotient dpvc_nr_span 2.0))))
    (setq dpvc_nr_ex (plus dpvc_nr_margin
      (quotient (plus (times (abs dpvc_nr_dx) dpvc_nr_hl)
                     (times (abs dpvc_nr_dy) dpvc_nr_hw)) dpvc_nr_span)))
    (setq dpvc_nr_ey (plus dpvc_nr_margin
      (quotient (plus (times (abs dpvc_nr_dy) dpvc_nr_hl)
                     (times (abs dpvc_nr_dx) dpvc_nr_hw)) dpvc_nr_span)))
    (setq dpvc_nr_cx (quotient (plus (car dpvc_nr_p) (car dpvc_nr_n)) 2.0))
    (setq dpvc_nr_cy (quotient (plus (cadr dpvc_nr_p) (cadr dpvc_nr_n)) 2.0))
    (setq dpvc_nr_box (dpvc_nav_box
      (list (list (difference dpvc_nr_cx dpvc_nr_ex)
                  (difference dpvc_nr_cy dpvc_nr_ey))
            (list (plus dpvc_nr_cx dpvc_nr_ex) (plus dpvc_nr_cy dpvc_nr_ey)))))
    ;; Preserve the exact serialized finding layer; normalize only for lookup.
    (setq dpvc_nr_layer (nth 5 dpvc_nr_vr))
    (dpvc_nav_layer_index dpvc_nr_layer dpvc_nr_layers)
    (setq dpvc_nr_ids (list (nth 13 dpvc_nr_vr) (nth 14 dpvc_nr_vr)
                            (nth 8 dpvc_nr_vr)))
    (setq dpvc_nr_edges 0)
    (foreach dpvc_nr_obj dpvc_nr_ids
      (setq dpvc_nr_signatures
        (cons (dpvc_nav_signature dpvc_nr_obj dpvc_nr_layer dpvc_nr_layers)
          dpvc_nr_signatures)))
    (setq dpvc_nr_signatures (reverse dpvc_nr_signatures))
    (unless (and
              (equal (car (car (nth 0 dpvc_nr_signatures))) "via")
              (equal (car (car (nth 1 dpvc_nr_signatures))) "via")
              (equal (nth 2 (car (nth 0 dpvc_nr_signatures))) dpvc_nr_p)
              (equal (nth 2 (car (nth 1 dpvc_nr_signatures))) dpvc_nr_n)
              (equal (nth 1 (car (nth 2 dpvc_nr_signatures)))
                     (upperCase (nth 6 dpvc_nr_vr)))
              (if (equal (nth 7 dpvc_nr_vr) "cline_segment")
                then (member (car (car (nth 2 dpvc_nr_signatures))) '("line" "arc"))
                else (equal (car (car (nth 2 dpvc_nr_signatures)))
                            (nth 7 dpvc_nr_vr))))
      (error "This crossing no longer matches its native objects."))
    (foreach dpvc_nr_obj dpvc_nr_signatures
      (setq dpvc_nr_edges (plus dpvc_nr_edges (nth 4 dpvc_nr_obj))))
    ;; Only the scalar stamps survive command completion. Never retain the
    ;; original object handles: even an unchanged board invalidates those.
    (return (list t "" dpvc_nr_layer dpvc_nr_box
      (foreach mapcar dpvc_nr_obj dpvc_nr_signatures (car dpvc_nr_obj))
      dpvc_nr_signatures dpvc_nr_edges))))

(procedure (dpvc_navigation_prepare dpvc_npr_name
                                    dpvc_npr_report dpvc_npr_annotated)
  (prog (dpvc_npr_units dpvc_npr_layers dpvc_npr_layer dpvc_npr_tail
         dpvc_npr_count dpvc_npr_edges dpvc_npr_rows dpvc_npr_call dpvc_npr_row
         dpvc_npr_drawing)
    (setq dpvc_npr_drawing (dpvc_nav_design_identity))
    (setq dpvc_npr_units (axlDBGetDesignUnits))
    (unless (and (equal (length dpvc_npr_units) 3)
                 (member (car dpvc_npr_units) '("mils" "millimeters")))
      (return nil))
    (setq dpvc_npr_units (list (car dpvc_npr_units)
      (cadr dpvc_npr_units) (caddr dpvc_npr_units)))
    (setq dpvc_npr_layers
      (foreach mapcar dpvc_npr_layer
        (getq (axlGetParam "paramLayerGroup:ETCH") groupMembers)
        (dpvc_nav_layer_key dpvc_npr_layer)))
    (unless (and dpvc_npr_layers (leqp (length dpvc_npr_layers) 256))
      (return nil))
    (setq dpvc_npr_tail dpvc_npr_annotated)
    (setq dpvc_npr_count 0)
    (setq dpvc_npr_edges 0)
    (while (and dpvc_npr_tail (lessp dpvc_npr_count 200))
      (setq dpvc_npr_call
        (errset (dpvc_nav_row (car dpvc_npr_tail)
          dpvc_npr_units dpvc_npr_layers) nil))
      (setq dpvc_npr_row (car dpvc_npr_call))
      (if (and dpvc_npr_row
               (leqp (plus dpvc_npr_edges (nth 6 dpvc_npr_row)) 65536))
      then (setq dpvc_npr_edges (plus dpvc_npr_edges (nth 6 dpvc_npr_row)))
      else (setq dpvc_npr_row (list nil
        "Exact native geometry is unavailable or exceeds navigation limits."
        nil nil nil nil 0)))
      (setq dpvc_npr_rows (cons dpvc_npr_row dpvc_npr_rows))
      (setq dpvc_npr_count (add1 dpvc_npr_count))
      (setq dpvc_npr_tail (cdr dpvc_npr_tail)))
    (unless dpvc_npr_rows (return nil))
    (return (list dpvc_npr_drawing dpvc_npr_units dpvc_npr_layers
      dpvc_npr_report dpvc_npr_name (reverse dpvc_npr_rows) nil))))

(procedure (dpvc_navigation_publish dpvc_npu_report dpvc_npu_count dpvc_npu_identity)
  (prog (dpvc_npu_cache dpvc_npu_rows dpvc_npu_tail dpvc_npu_i)
    (setq dpvc_npu_cache dpvc__navigation_cache)
    (unless (and (fixp dpvc_npu_count) (geqp dpvc_npu_count 0)
                 (leqp dpvc_npu_count 200))
      (dpvc_navigation_clear)
      (return nil))
    (when (zerop dpvc_npu_count) (dpvc_navigation_clear) (return t))
    ;; A full report can succeed even when navigation snapshots are unsupported.
    (unless dpvc_npu_cache (return t))
    (unless (and (equal (nth 3 dpvc_npu_cache) dpvc_npu_report)
                 (geqp (length (nth 5 dpvc_npu_cache)) dpvc_npu_count))
      (dpvc_navigation_clear)
      (return nil))
    (setq dpvc_npu_tail (nth 5 dpvc_npu_cache))
    (setq dpvc_npu_i 0)
    (while (lessp dpvc_npu_i dpvc_npu_count)
      (setq dpvc_npu_rows (cons (car dpvc_npu_tail) dpvc_npu_rows))
      (setq dpvc_npu_tail (cdr dpvc_npu_tail))
      (setq dpvc_npu_i (add1 dpvc_npu_i)))
    (setq dpvc__navigation_cache
      (list (nth 0 dpvc_npu_cache) (nth 1 dpvc_npu_cache)
        (nth 2 dpvc_npu_cache) (nth 3 dpvc_npu_cache)
        (nth 4 dpvc_npu_cache) (reverse dpvc_npu_rows) dpvc_npu_identity))
    (return t)))

(procedure (dpvc_nav_selection_equal dpvc_nse_left dpvc_nse_right)
  (prog (dpvc_nse_item)
    (return (and (equal (length dpvc_nse_left) (length dpvc_nse_right))
      (forall dpvc_nse_item dpvc_nse_left (member dpvc_nse_item dpvc_nse_right))))))

(procedure (dpvc_nav_box_near dpvc_nbn_left dpvc_nbn_right dpvc_nbn_epsilon)
  (prog (dpvc_nbn_i dpvc_nbn_j)
    (for dpvc_nbn_i 0 1
      (for dpvc_nbn_j 0 1
        (when (greaterp (abs (difference
                  (nth dpvc_nbn_j (nth dpvc_nbn_i dpvc_nbn_left))
                  (nth dpvc_nbn_j (nth dpvc_nbn_i dpvc_nbn_right))))
                dpvc_nbn_epsilon)
          (return nil))))
    (return t)))

(procedure (dpvc_nav_viewport_box dpvc_nvb_requested dpvc_nvb_current dpvc_nvb_grid)
  (prog (dpvc_nvb_ratio dpvc_nvb_cx dpvc_nvb_cy dpvc_nvb_hw dpvc_nvb_hh
         dpvc_nvb_fit_hw dpvc_nvb_fit_hh)
    ;; Allegro rounds to its native grid and aspect-expands from the lower
    ;; left, not the requested center. Fit explicitly about the crossing's
    ;; center using the actual current viewport, with an outward grid guard.
    ;; The immutable requested crossing bounds remain the acceptance target.
    (setq dpvc_nvb_ratio
      (quotient (difference (caadr dpvc_nvb_current) (caar dpvc_nvb_current))
                (difference (cadadr dpvc_nvb_current) (cadar dpvc_nvb_current))))
    (setq dpvc_nvb_cx
      (quotient (plus (caar dpvc_nvb_requested) (caadr dpvc_nvb_requested)) 2.0))
    (setq dpvc_nvb_cy
      (quotient (plus (cadar dpvc_nvb_requested) (cadadr dpvc_nvb_requested)) 2.0))
    (setq dpvc_nvb_hw
      (plus dpvc_nvb_grid
        (quotient (difference (caadr dpvc_nvb_requested)
                              (caar dpvc_nvb_requested)) 2.0)))
    (setq dpvc_nvb_hh
      (plus dpvc_nvb_grid
        (quotient (difference (cadadr dpvc_nvb_requested)
                              (cadar dpvc_nvb_requested)) 2.0)))
    (setq dpvc_nvb_fit_hw (max dpvc_nvb_hw (times dpvc_nvb_hh dpvc_nvb_ratio)))
    (setq dpvc_nvb_fit_hh (max dpvc_nvb_hh (quotient dpvc_nvb_hw dpvc_nvb_ratio)))
    (return (dpvc_nav_box
      (list (list (difference dpvc_nvb_cx dpvc_nvb_fit_hw)
                  (difference dpvc_nvb_cy dpvc_nvb_fit_hh))
            (list (plus dpvc_nvb_cx dpvc_nvb_fit_hw)
                  (plus dpvc_nvb_cy dpvc_nvb_fit_hh)))))))

(procedure (dpvc_restore_find_filter dpvc_rff_enabled dpvc_rff_buttons)
  (prog (dpvc_rff_clear_enabled dpvc_rff_clear_buttons dpvc_rff_flag)
    ;; NOALL does not clear every option in all Allegro versions. Explicitly
    ;; disable each currently active keyword before replaying the saved lists.
    ;; Capture both lists first: changing enabled flags can affect buttons.
    (setq dpvc_rff_clear_enabled
      (foreach mapcar dpvc_rff_flag (axlGetFindFilter)
        (strcat "NO" dpvc_rff_flag)))
    (setq dpvc_rff_clear_buttons
      (foreach mapcar dpvc_rff_flag (axlGetFindFilter t)
        (strcat "NO" dpvc_rff_flag)))
    (axlSetFindFilter
      ?enabled (cons "NOALL" (append dpvc_rff_clear_enabled dpvc_rff_enabled))
      ?onButtons (cons "NOALL" (append dpvc_rff_clear_buttons dpvc_rff_buttons)))
    (return t)))

(procedure (dpvc_nav_restore dpvc_nre_vis dpvc_nre_box dpvc_nre_sel
                            dpvc_nre_enabled dpvc_nre_buttons dpvc_nre_epsilon)
  (prog (dpvc_nre_ok dpvc_nre_call)
    (setq dpvc_nre_ok t)
    (unless (errset (axlVisibleSet dpvc_nre_vis) t) (setq dpvc_nre_ok nil))
    (unless (errset (axlWindowBoxSet (dpvc_nav_box dpvc_nre_box)) t)
      (setq dpvc_nre_ok nil))
    (unless (errset (progn
      (unless (dpvc_nav_selection_equal (axlGetSelSet) dpvc_nre_sel)
        (axlClearSelSet)
        (when dpvc_nre_sel (axlAddSelectObject dpvc_nre_sel)))
      (unless (and (equal (axlGetFindFilter) dpvc_nre_enabled)
                   (equal (axlGetFindFilter t) dpvc_nre_buttons))
        (dpvc_restore_find_filter dpvc_nre_enabled dpvc_nre_buttons))
      (axlVisibleUpdate t)
      (axlFlushDisplay)) t) (setq dpvc_nre_ok nil))
    (setq dpvc_nre_call (errset
      (and (equal (axlVisibleGet) dpvc_nre_vis)
           (dpvc_nav_box_near (axlWindowBoxGet) dpvc_nre_box dpvc_nre_epsilon)
           (dpvc_nav_selection_equal (axlGetSelSet) dpvc_nre_sel)
           (equal (axlGetFindFilter) dpvc_nre_enabled)
           (equal (axlGetFindFilter t) dpvc_nre_buttons)) t))
    (return (and dpvc_nre_ok dpvc_nre_call (car dpvc_nre_call)))))

(procedure (dpvc_navigation_zoom dpvc_nz_report dpvc_nz_index dpvc_nz_identity
                               dpvc_nz_publish dpvc_nz_context)
  (prog (dpvc_nz_cache dpvc_nz_row dpvc_nz_layers dpvc_nz_layer
         dpvc_nz_signatures dpvc_nz_obj dpvc_nz_validate dpvc_nz_capture
         dpvc_nz_vis dpvc_nz_window dpvc_nz_sel dpvc_nz_enabled dpvc_nz_buttons
         dpvc_nz_requested dpvc_nz_adjusted dpvc_nz_actual dpvc_nz_apply
         dpvc_nz_units dpvc_nz_epsilon dpvc_nz_i dpvc_nz_value
         dpvc_nz_phase dpvc_nz_design dpvc_nz_current dpvc_nz_saved dpvc_nz_j
         dpvc_nz_candidates dpvc_nz_viewport_input)
    (setq dpvc_nz_cache dpvc__navigation_cache)
    (unless (and dpvc_nz_cache (fixp dpvc_nz_index) (geqp dpvc_nz_index 1)
                 (leqp dpvc_nz_index (length (nth 5 dpvc_nz_cache)))
                 (equal dpvc_nz_report (nth 3 dpvc_nz_cache))
                 (nth 6 dpvc_nz_cache)
                 (equal dpvc_nz_identity (nth 6 dpvc_nz_cache)))
      (return (list 'rejected "This crossing is not in the current published analysis.")))
    (setq dpvc_nz_row (nth (sub1 dpvc_nz_index) (nth 5 dpvc_nz_cache)))
    (unless (car dpvc_nz_row) (return (list 'rejected (cadr dpvc_nz_row))))
    ;; Revalidate the complete critical-object signatures before ANY display write.
    (setq dpvc_nz_phase "design identity")
    (setq dpvc_nz_validate (errset (progn
      (setq dpvc_nz_design (axlDBGetDesign))
      (unless (and dpvc_nz_design
                   (equal (dpvc_nav_design_identity) (car dpvc_nz_cache)))
        (error "The design changed."))
      (setq dpvc_nz_phase "design units")
      (setq dpvc_nz_units (axlDBGetDesignUnits))
      (unless (equal dpvc_nz_units (nth 1 dpvc_nz_cache))
        (error "The design units changed."))
      (setq dpvc_nz_phase "stackup")
      (setq dpvc_nz_layers
        (foreach mapcar dpvc_nz_layer
          (getq (axlGetParam "paramLayerGroup:ETCH") groupMembers)
          (dpvc_nav_layer_key dpvc_nz_layer)))
      (unless (equal dpvc_nz_layers (nth 2 dpvc_nz_cache))
        (error "The stackup changed."))
      (setq dpvc_nz_phase "fresh native object discovery")
      (setq dpvc_nz_candidates (dpvc_collect_managed_candidates dpvc_nz_design))
      (setq dpvc_nz_i 0)
      (foreach dpvc_nz_obj (nth 4 dpvc_nz_row)
        (setq dpvc_nz_phase (sprintf nil "capture critical object %d" dpvc_nz_i))
        (setq dpvc_nz_signatures
          (cons (dpvc_nav_resolve_signature dpvc_nz_obj
                  dpvc_nz_candidates dpvc_nz_layers)
            dpvc_nz_signatures))
        (setq dpvc_nz_i (add1 dpvc_nz_i)))
      (setq dpvc_nz_phase "exact signature comparison")
      (setq dpvc_nz_signatures (reverse dpvc_nz_signatures))
      (unless (equal dpvc_nz_signatures (nth 5 dpvc_nz_row))
        ;; Failure-only bounded diagnostics: no DBIDs or polygon contours.
        ;; Object order is P via, N via, aggressor. Stamp field order is
        ;; type, net, xy, layer, bounds, type-specific metadata.
        (for dpvc_nz_i 0 2
          (setq dpvc_nz_current (nth dpvc_nz_i dpvc_nz_signatures))
          (setq dpvc_nz_saved (nth dpvc_nz_i (nth 5 dpvc_nz_row)))
          (printf "DPVC native object %d equality: stamp=%L regular=%L anti=%L drill=%L edges=%L\n"
            dpvc_nz_i
            (equal (nth 0 dpvc_nz_current) (nth 0 dpvc_nz_saved))
            (equal (nth 1 dpvc_nz_current) (nth 1 dpvc_nz_saved))
            (equal (nth 2 dpvc_nz_current) (nth 2 dpvc_nz_saved))
            (equal (nth 3 dpvc_nz_current) (nth 3 dpvc_nz_saved))
            (equal (nth 4 dpvc_nz_current) (nth 4 dpvc_nz_saved)))
          (unless (equal (car dpvc_nz_current) (car dpvc_nz_saved))
            (for dpvc_nz_j 0 5
              (printf "DPVC native object %d stamp field %d equal=%L\n"
                dpvc_nz_i dpvc_nz_j
                (equal (nth dpvc_nz_j (car dpvc_nz_current))
                       (nth dpvc_nz_j (car dpvc_nz_saved)))))))
        (error "The crossing geometry changed."))
      t) t))
    (unless (and dpvc_nz_validate (car dpvc_nz_validate))
      (printf "DPVC native revalidation failed during %s\n" dpvc_nz_phase)
      (dpvc_navigation_clear)
      (return (list 'rejected "The native crossing changed. Run the analysis again.")))
    (setq dpvc_nz_capture (errset (progn
      (setq dpvc_nz_vis (axlVisibleGet))
      (setq dpvc_nz_window (dpvc_nav_box (axlWindowBoxGet)))
      (setq dpvc_nz_sel (append (axlGetSelSet) nil))
      (setq dpvc_nz_enabled (append (axlGetFindFilter) nil))
      (setq dpvc_nz_buttons (append (axlGetFindFilter t) nil))
      (unless (and dpvc_nz_vis (numberp (caddr dpvc_nz_units))
                   (greaterp (caddr dpvc_nz_units) 0))
        (error "Display state cannot be preserved."))
      ;; One native database grid unit permits only readback rounding.
      (setq dpvc_nz_epsilon (quotient 1.0 (caddr dpvc_nz_units)))
      t) nil))
    (unless (and dpvc_nz_capture (car dpvc_nz_capture))
      (return (list 'failed "Cannot preserve the current native display state.")))
    (setq dpvc_nz_requested (dpvc_nav_box (nth 3 dpvc_nz_row)))
    (setq dpvc_nz_viewport_input
      (dpvc_nav_viewport_box dpvc_nz_requested dpvc_nz_window dpvc_nz_epsilon))
    (setq dpvc_nz_apply (errset (progn
      (unless (axlVisibleLayer (nth 2 dpvc_nz_row) t)
        (error "Cannot enable the finding layer."))
      (axlVisibleUpdate t)
      (setq dpvc_nz_adjusted
        (dpvc_nav_box (axlWindowBoxSet (dpvc_nav_box dpvc_nz_viewport_input))))
      (axlFlushDisplay)
      (setq dpvc_nz_actual (dpvc_nav_box (axlWindowBoxGet)))
      (unless (and (dpvc_nav_box_near dpvc_nz_actual dpvc_nz_adjusted dpvc_nz_epsilon)
                   (axlIsVisibleLayer (nth 2 dpvc_nz_row))
                   (dpvc_nav_selection_equal (axlGetSelSet) dpvc_nz_sel)
                   (equal (axlGetFindFilter) dpvc_nz_enabled)
                   (equal (axlGetFindFilter t) dpvc_nz_buttons))
        (error "Native viewport readback did not match the requested change."))
      (for dpvc_nz_i 0 1
        (unless (and
          (leqp (nth dpvc_nz_i (car dpvc_nz_actual))
            (nth dpvc_nz_i (car dpvc_nz_requested)))
          (geqp (nth dpvc_nz_i (cadr dpvc_nz_actual))
            (nth dpvc_nz_i (cadr dpvc_nz_requested))))
          (printf "DPVC viewport containment: axis=%d units=%s grid=%.12g\n"
            dpvc_nz_i (car dpvc_nz_units) dpvc_nz_epsilon)
          (printf "DPVC requested: (%.12g, %.12g) (%.12g, %.12g)\n"
            (caar dpvc_nz_requested) (cadar dpvc_nz_requested)
            (caadr dpvc_nz_requested) (cadadr dpvc_nz_requested))
          (printf "DPVC input:     (%.12g, %.12g) (%.12g, %.12g)\n"
            (caar dpvc_nz_viewport_input) (cadar dpvc_nz_viewport_input)
            (caadr dpvc_nz_viewport_input) (cadadr dpvc_nz_viewport_input))
          (printf "DPVC adjusted:  (%.12g, %.12g) (%.12g, %.12g)\n"
            (caar dpvc_nz_adjusted) (cadar dpvc_nz_adjusted)
            (caadr dpvc_nz_adjusted) (cadadr dpvc_nz_adjusted))
          (printf "DPVC actual:    (%.12g, %.12g) (%.12g, %.12g)\n"
            (caar dpvc_nz_actual) (cadar dpvc_nz_actual)
            (caadr dpvc_nz_actual) (cadadr dpvc_nz_actual))
          (error "Native viewport does not contain the crossing bounds.")))
      (setq dpvc_nz_value
        (list (nth 4 dpvc_nz_cache) dpvc_nz_report
          (sprintf nil "crossing-%d" dpvc_nz_index) (nth 2 dpvc_nz_row)
          (car dpvc_nz_units) dpvc_nz_requested dpvc_nz_actual))
      ;; Publication is inside the display guard: a retired SDK context or
      ;; JSON failure restores the prior viewport, not an unreported success.
      (unless (apply dpvc_nz_publish (list dpvc_nz_context dpvc_nz_value))
        (error "The native zoom result could not be published."))
      t) t))
    (unless (and dpvc_nz_apply (car dpvc_nz_apply))
      (dpvc_navigation_clear)
      (unless (dpvc_nav_restore dpvc_nz_vis dpvc_nz_window dpvc_nz_sel
                dpvc_nz_enabled dpvc_nz_buttons dpvc_nz_epsilon)
        (return (list 'failed "Native zoom failed and display restoration needs attention.")))
      (return (list 'failed "Native zoom failed; the previous display was restored.")))
    (return (list 'succeeded "Native Allegro view centered on the validated crossing."))))

;; -----------------------------------------------------------------------
;; Report-only entry. Dynamic bindings isolate each request's settings and
;; temporary report state; the geometry implementation remains dpvc_run.
;; Return (complete scalar-result) or (failed message), never DBIDs.
;; No shared marker cleanup: this entry creates no analysis geometry and must
;; not delete an operator's existing analysis artifacts.
;; -----------------------------------------------------------------------
(procedure (dpvc_run_report_only dpvc_ro_margin dpvc_ro_module
                                 dpvc_ro_unused dpvc_ro_path)
  (prog (dpvc_ro_saved_vis dpvc_ro_saved_window dpvc_ro_saved_selection
         dpvc_ro_saved_enabled dpvc_ro_saved_buttons dpvc_ro_capture
         dpvc_ro_call dpvc_ro_restore dpvc_ro_result dpvc_ro_verify_selection
         dpvc_ro_verify_enabled dpvc_ro_verify_buttons dpvc_ro_item
         dpvc_ro_verify_vis dpvc_ro_verify_window
         dpvc__pad_margin
         dpvc__module_name dpvc__include_unused dpvc__report_path
         dpvc__has_backdrill dpvc__bd_stub_warn_count
         dpvc__analysis_error
         dpvc__dat_path dpvc__completed_result dpvc__run_error
         dpvc__report_port dpvc__dat_port dpvc__navigation_candidate)
    (dpvc_navigation_clear)
    (setq dpvc__pad_margin dpvc_ro_margin)
    (setq dpvc__module_name dpvc_ro_module)
    (setq dpvc__include_unused dpvc_ro_unused)
    (setq dpvc__report_path dpvc_ro_path)
    (unless (axlDBGetDesign)
      (return (list 'failed "An open Allegro design is required.")))
    (unless (and (numberp dpvc_ro_margin) (geqp dpvc_ro_margin 0.0)
                 (leqp dpvc_ro_margin 50.0) (stringp dpvc_ro_module)
                 (leqp (strlen dpvc_ro_module) 64)
                 (stringp dpvc_ro_path) (leqp (strlen dpvc_ro_path) 1024)
                 (greaterp (strlen dpvc_ro_path) 4)
                 (equal (lowerCase (substring dpvc_ro_path
                          (difference (strlen dpvc_ro_path) 3))) ".rpt")
                 (not (isFile dpvc_ro_path))
                 (not (isFile (strcat dpvc_ro_path ".dat"))))
      (return (list 'failed "Invalid settings or report output already exists.")))
    ;; Capture all transient display and selection state before analysis.
    (setq dpvc_ro_capture
      (errset (progn
        (setq dpvc_ro_saved_vis (axlVisibleGet))
        (setq dpvc_ro_saved_window (dpvc_nav_box (axlWindowBoxGet)))
        (setq dpvc_ro_saved_selection (append (axlGetSelSet) nil))
        (setq dpvc_ro_saved_enabled (append (axlGetFindFilter) nil))
        (setq dpvc_ro_saved_buttons (append (axlGetFindFilter t) nil))
        t) t))
    (unless (and dpvc_ro_capture dpvc_ro_saved_vis dpvc_ro_saved_window)
      (return (list 'failed "Cannot preserve the current Allegro display state.")))
    (setq dpvc_ro_call (errset (dpvc_run) t))
    ;; Closing partial outputs never promotes them to successful artifacts.
    (when dpvc__report_port (errset (close dpvc__report_port) t))
    (when dpvc__dat_port (errset (close dpvc__dat_port) t))
    ;; Managed analysis never changes selection, filters, visibility or view.
    ;; Compare before any cleanup so a changed state cannot masquerade as a
    ;; successful report. Do not write no-op settings: Allegro can initialize
    ;; persistent defaults even when replaying an empty Find Filter.
    (setq dpvc_ro_restore t)
    (unless (errset (progn
              (setq dpvc_ro_verify_selection (axlGetSelSet))
              (setq dpvc_ro_verify_enabled (axlGetFindFilter))
              (setq dpvc_ro_verify_buttons (axlGetFindFilter t))
              (setq dpvc_ro_verify_vis (axlVisibleGet))
              (setq dpvc_ro_verify_window (axlWindowBoxGet))
              (unless (and
                        (equal (length dpvc_ro_verify_selection)
                               (length dpvc_ro_saved_selection))
                        (forall dpvc_ro_item dpvc_ro_saved_selection
                          (member dpvc_ro_item dpvc_ro_verify_selection))
                        (equal dpvc_ro_verify_enabled dpvc_ro_saved_enabled)
                        (equal dpvc_ro_verify_buttons dpvc_ro_saved_buttons)
                        (equal dpvc_ro_verify_vis dpvc_ro_saved_vis)
                        (equal dpvc_ro_verify_window dpvc_ro_saved_window))
                (setq dpvc_ro_restore nil))
              t) t)
      (setq dpvc_ro_restore nil))
    (unless dpvc_ro_restore
      ;; Best-effort display cleanup only when an unexpected difference was
      ;; observed. Selection/filter writes are deliberately never attempted.
      (unless (equal dpvc_ro_verify_vis dpvc_ro_saved_vis)
        (errset (progn (axlVisibleSet dpvc_ro_saved_vis) (axlVisibleUpdate t)) t))
      (unless (equal dpvc_ro_verify_window dpvc_ro_saved_window)
        (errset (axlWindowBoxSet dpvc_ro_saved_window) t))
      ;; A bounded native diagnostic identifies the actual failing boundary.
      (printf "DPVC restore: selection %d -> %d; enabled %L -> %L; buttons %L -> %L\n"
        (length dpvc_ro_saved_selection) (length dpvc_ro_verify_selection)
        dpvc_ro_saved_enabled dpvc_ro_verify_enabled
        dpvc_ro_saved_buttons dpvc_ro_verify_buttons)
      (return (list 'failed
        "Analysis ended, but Allegro display/selection restoration failed.")))
    (unless (and dpvc_ro_call (car dpvc_ro_call) dpvc__completed_result)
      (return (list 'failed
        (if dpvc__run_error then dpvc__run_error
          else "Analysis or report output failed; no completed result is available."))))
    (setq dpvc_ro_result dpvc__completed_result)
    ;; Commit only after native analysis/report and state restoration succeed.
    ;; The adapter later binds this to exactly the rows it publishes.
    (setq dpvc__navigation_cache dpvc__navigation_candidate)
    (return (list 'complete dpvc_ro_result))
  )
)
