;;; Command: pd.workflow-engine.controls.dp-via-corridor
;;; Description: Typed, report-only adapter for the maintained DPVC checker.
;;; Functions: pddpvc_run_handler, pddpvc__result_json, pddpvc__finding_json.
;;; Ownership: the checker owns geometry; the SDK owns invocation identity.
;;; No board edits, legacy forms, palette changes, or arbitrary source paths.

;; Content-bound dependencies are staged by the SDK, not resolved from an old
;; installed SI tree. Refuse replacement while legacy DPVC forms own review.
(when (or (and (boundp 'dpvc__form) dpvc__form)
          (and (boundp 'dpvc_nav__form) dpvc_nav__form))
  (error "Close the legacy DP via corridor checker/navigator before loading the modern workspace. If its old form handle remains, restart Allegro."))

;; Categories are data only. Keep the typed command's exact shipped rules,
;; while preserving any legacy session's currently selected classification.
(let (dpvc__managed_report_only dpvc_signal_categories
      dpvc_default_risk dpvc_default_category)
  (setq dpvc__managed_report_only t)
  (unless (pdceLoadSibling "categories.il")
    (error "The packaged DP via corridor classification rules could not be loaded."))
  (unless (and dpvc_signal_categories (listp dpvc_signal_categories)
               (stringp dpvc_default_category)
               (member dpvc_default_risk (list "CRITICAL" "MEDIUM" "LOW")))
    (error "The packaged DP via corridor classification rules are invalid."))
  (setq pddpvc__signal_categories dpvc_signal_categories)
  (setq pddpvc__default_risk dpvc_default_risk)
  (setq pddpvc__default_category dpvc_default_category)
  (unless (pdceLoadSibling "dp_via_corridor_check.il")
    (error "The packaged report-only DP via corridor checker could not be loaded.")))
;; A first load also leaves legacy commands with valid initial category data.
(unless (boundp 'dpvc_signal_categories)
  (setq dpvc_signal_categories pddpvc__signal_categories))
(unless (boundp 'dpvc_default_risk)
  (setq dpvc_default_risk pddpvc__default_risk))
(unless (boundp 'dpvc_default_category)
  (setq dpvc_default_category pddpvc__default_category))

(procedure (pddpvc__quote pddpvc_q_text)
  (prog (pddpvc_q_parts pddpvc_q_i pddpvc_q_char pddpvc_q_code)
    (unless (stringp pddpvc_q_text)
      (error "DPVC result contains a non-string text field."))
    (setq pddpvc_q_parts nil)
    (for pddpvc_q_i 1 (strlen pddpvc_q_text)
      (setq pddpvc_q_char (substring pddpvc_q_text pddpvc_q_i 1))
      (setq pddpvc_q_code (charToInt (getchar pddpvc_q_text pddpvc_q_i)))
      (setq pddpvc_q_parts
        (cons
          (cond
            ((equal pddpvc_q_char "\"") "\\\"")
            ((equal pddpvc_q_char "\\") "\\\\")
            ((equal pddpvc_q_code 10) "\\n")
            ((equal pddpvc_q_code 13) "\\r")
            ((equal pddpvc_q_code 9) "\\t")
            ((equal pddpvc_q_code 8) "\\b")
            ((equal pddpvc_q_code 12) "\\f")
            ((lessp pddpvc_q_code 32)
              (error "DPVC result contains an unsupported control character."))
            (t pddpvc_q_char))
          pddpvc_q_parts)))
    (return (strcat "\"" (buildString (reverse pddpvc_q_parts) "") "\""))
  )
)

(procedure (pddpvc__point_json pddpvc_pj_xy pddpvc_pj_scale)
  (prog nil
    (when (null pddpvc_pj_xy) (return "null"))
    (unless (and (listp pddpvc_pj_xy) (equal (length pddpvc_pj_xy) 2)
                 (numberp (nth 0 pddpvc_pj_xy))
                 (numberp (nth 1 pddpvc_pj_xy)))
      (error "DPVC result contains an invalid coordinate."))
    (return (sprintf nil "{\"xMil\":%.12g,\"yMil\":%.12g}"
      (times (nth 0 pddpvc_pj_xy) pddpvc_pj_scale)
      (times (nth 1 pddpvc_pj_xy) pddpvc_pj_scale)))
  )
)

(procedure (pddpvc__finding_json pddpvc_fj_row pddpvc_fj_index pddpvc_fj_scale)
  (prog nil
    ;; Scalar owner row: pair, P, N, layer, net, type, distance, hw, hl,
    ;; intrusion (optional), category, risk. No live database references.
    (unless (and (listp pddpvc_fj_row) (equal (length pddpvc_fj_row) 12)
                 (nth 1 pddpvc_fj_row) (nth 2 pddpvc_fj_row)
                 (member (nth 11 pddpvc_fj_row)
                   (list "CRITICAL" "MEDIUM" "LOW")))
      (error "DPVC returned an invalid scalar finding."))
    (return
      (sprintf nil
        "{\"id\":\"crossing-%d\",\"pairName\":%s,\"aggressorNet\":%s,\"objectType\":%s,\"layer\":%s,\"category\":%s,\"risk\":%s,\"p\":%s,\"n\":%s,\"intrusion\":%s,\"distanceMil\":%.12g,\"halfWidthMil\":%.12g,\"halfLengthMil\":%.12g}"
        pddpvc_fj_index
        (pddpvc__quote (nth 0 pddpvc_fj_row))
        (pddpvc__quote (nth 4 pddpvc_fj_row))
        (pddpvc__quote (nth 5 pddpvc_fj_row))
        (pddpvc__quote (nth 3 pddpvc_fj_row))
        (pddpvc__quote (nth 10 pddpvc_fj_row))
        (pddpvc__quote (nth 11 pddpvc_fj_row))
        (pddpvc__point_json (nth 1 pddpvc_fj_row) pddpvc_fj_scale)
        (pddpvc__point_json (nth 2 pddpvc_fj_row) pddpvc_fj_scale)
        (pddpvc__point_json (nth 9 pddpvc_fj_row) pddpvc_fj_scale)
        (times (nth 6 pddpvc_fj_row) pddpvc_fj_scale)
        (times (nth 7 pddpvc_fj_row) pddpvc_fj_scale)
        (times (nth 8 pddpvc_fj_row) pddpvc_fj_scale)))
  )
)

(procedure (pddpvc__identity pddpvc_id_context)
  (prog (pddpvc_id_session pddpvc_id_generation pddpvc_id_catalog)
    (setq pddpvc_id_generation (pdceBoardGeneration pddpvc_id_context))
    (setq pddpvc_id_catalog (pdceCatalogGeneration pddpvc_id_context))
    (unless (and (numberp pddpvc_id_generation) (geqp pddpvc_id_generation 0)
                 (numberp pddpvc_id_catalog) (geqp pddpvc_id_catalog 0))
      (error "The SDK analysis identity is unavailable."))
    ;; 1.8 documents this as optional/development API. Verify availability;
    ;; never invent a session identifier or retain an inactive context.
    (when (isCallable 'pdceSessionId)
      (setq pddpvc_id_session (pdceSessionId pddpvc_id_context))
      (unless (and (stringp pddpvc_id_session)
                   (greaterp (strlen pddpvc_id_session) 0))
        (error "The SDK session context is no longer active.")))
    (return (list pddpvc_id_generation pddpvc_id_catalog pddpvc_id_session))))

(procedure (pddpvc__result_json pddpvc_rj_context pddpvc_rj_result)
  (prog (pddpvc_rj_scale pddpvc_rj_rows pddpvc_rj_row pddpvc_rj_json
         pddpvc_rj_count pddpvc_rj_chars pddpvc_rj_row_json pddpvc_rj_stop)
    (unless (and (listp pddpvc_rj_result)
                 (equal (length pddpvc_rj_result) 12)
                 (member (nth 1 pddpvc_rj_result) (list "mils" "millimeters")))
      (error "DPVC returned an unsupported completion record."))
    (setq pddpvc_rj_scale
      (if (equal (nth 1 pddpvc_rj_result) "millimeters")
        then (quotient 1.0 0.0254) else 1.0))
    (setq pddpvc_rj_rows nil)
    (setq pddpvc_rj_count 0)
    (setq pddpvc_rj_chars 0)
    (setq pddpvc_rj_stop nil)
    ;; Keep the first (severity/distance sorted) 200 rows. A conservative
    ;; 200k-character ceiling also bounds UTF-8 below the SDK's 1 MiB limit.
    ;; Full totals and the full on-disk report remain available.
    (foreach pddpvc_rj_row (nth 11 pddpvc_rj_result)
      (when (and (not pddpvc_rj_stop) (lessp pddpvc_rj_count 200))
        (setq pddpvc_rj_row_json
          (pddpvc__finding_json pddpvc_rj_row
            (add1 pddpvc_rj_count) pddpvc_rj_scale))
        (if (greaterp (plus pddpvc_rj_chars (strlen pddpvc_rj_row_json)) 200000)
        then (setq pddpvc_rj_stop t)
        else
          (setq pddpvc_rj_chars
            (plus pddpvc_rj_chars (strlen pddpvc_rj_row_json) 1))
          (setq pddpvc_rj_rows (cons pddpvc_rj_row_json pddpvc_rj_rows))
          (setq pddpvc_rj_count (add1 pddpvc_rj_count)))))
    (setq pddpvc_rj_json
      (sprintf nil
        "{\"schema\":\"pd-dp-via-corridor-result-v1\",\"status\":\"complete\",\"boardGeneration\":%d,\"design\":%s,\"units\":\"mils\",\"sourceUnits\":%s,\"reportPath\":%s,\"navigatorPath\":%s,\"navigatorWritten\":%s,\"pairCount\":%d,\"corridorCount\":%d,\"aggressorCount\":%d,\"findingCount\":%d,\"criticalCount\":%d,\"mediumCount\":%d,\"lowCount\":%d,\"truncated\":%s,\"findings\":[%s]}"
        (pdceBoardGeneration pddpvc_rj_context)
        (pddpvc__quote (nth 0 pddpvc_rj_result))
        (pddpvc__quote (nth 1 pddpvc_rj_result))
        (pddpvc__quote (nth 9 pddpvc_rj_result))
        (if (nth 10 pddpvc_rj_result)
          then (pddpvc__quote (nth 10 pddpvc_rj_result)) else "null")
        (if (nth 10 pddpvc_rj_result) then "true" else "false")
        (nth 2 pddpvc_rj_result) (nth 3 pddpvc_rj_result)
        (nth 4 pddpvc_rj_result) (nth 5 pddpvc_rj_result)
        (nth 6 pddpvc_rj_result) (nth 7 pddpvc_rj_result)
        (nth 8 pddpvc_rj_result)
        (if (lessp pddpvc_rj_count (nth 5 pddpvc_rj_result))
          then "true" else "false")
        (buildString (reverse pddpvc_rj_rows) ",")))
    (when (greaterp (strlen pddpvc_rj_json) 220000)
      (error "DPVC result metadata exceeds the bounded payload limit."))
    ;; Bind only the exact prefix admitted by BOTH row and payload limits.
    (unless (dpvc_navigation_publish (nth 9 pddpvc_rj_result)
              pddpvc_rj_count (pddpvc__identity pddpvc_rj_context))
      (error "The bounded native navigation result could not be bound."))
    (return pddpvc_rj_json)
  )
)

(procedure (pddpvc_run_handler pddpvc_rh_context)
  (prog (pddpvc_rh_margin pddpvc_rh_module pddpvc_rh_unused
         pddpvc_rh_path pddpvc_rh_call pddpvc_rh_result pddpvc_rh_json
         pddpvc_rh_publish
         dpvc_signal_categories dpvc_default_risk dpvc_default_category)
    (dpvc_navigation_clear)
    (when (pdceCancellationRequested pddpvc_rh_context)
      (return (list 'cancelled "Cancelled before corridor analysis.")))
    (setq pddpvc_rh_margin (pdceArgument pddpvc_rh_context "margin_mils"))
    (setq pddpvc_rh_module (pdceArgument pddpvc_rh_context "module_filter"))
    (setq pddpvc_rh_unused (pdceArgument pddpvc_rh_context "include_unused"))
    (setq pddpvc_rh_path (pdceArgument pddpvc_rh_context "report_path"))
    (when (null pddpvc_rh_module) (setq pddpvc_rh_module ""))
    (unless (and (stringp pddpvc_rh_margin)
                 ;; SDK decimal text is canonical, without exponent or sign.
                 ;; atof alone would silently accept garbage as zero or a
                 ;; numeric prefix. Check the entire text before conversion.
                 (leqp (strlen pddpvc_rh_margin) 32)
                 (or (rexMatchp "^[0-9][0-9]*$" pddpvc_rh_margin)
                     (rexMatchp "^[0-9][0-9]*[.][0-9][0-9]*$"
                       pddpvc_rh_margin))
                 (stringp pddpvc_rh_module) (leqp (strlen pddpvc_rh_module) 64)
                 (member pddpvc_rh_unused (list "true" "false"))
                 (stringp pddpvc_rh_path) (leqp (strlen pddpvc_rh_path) 1024))
      (return (list 'rejected "The corridor analysis settings are invalid.")))
    (setq pddpvc_rh_margin (atof pddpvc_rh_margin))
    (unless (and (numberp pddpvc_rh_margin) (geqp pddpvc_rh_margin 0.0)
                 (leqp pddpvc_rh_margin 50.0))
      (return (list 'rejected "The corridor margin must be 0 to 50 mils.")))
    (unless (and (isCallable 'dpvc_run_report_only)
                 pddpvc__signal_categories)
      (return (list 'failed
        "The content-bound DP via corridor checker or classification rules are unavailable.")))
    (setq dpvc_signal_categories pddpvc__signal_categories)
    (setq dpvc_default_risk pddpvc__default_risk)
    (setq dpvc_default_category pddpvc__default_category)
    (when (pdceCancellationRequested pddpvc_rh_context)
      (return (list 'cancelled "Cancelled before corridor analysis.")))
    (setq pddpvc_rh_call
      (errset (dpvc_run_report_only pddpvc_rh_margin pddpvc_rh_module
                (equal pddpvc_rh_unused "true") pddpvc_rh_path) t))
    (unless (and pddpvc_rh_call (listp (car pddpvc_rh_call))
                 (equal (length (car pddpvc_rh_call)) 2))
      (dpvc_navigation_clear)
      (return (list 'failed "The native corridor checker failed.")))
    (setq pddpvc_rh_result (car pddpvc_rh_call))
    (unless (equal (nth 0 pddpvc_rh_result) 'complete)
      (dpvc_navigation_clear)
      (return (list 'failed (nth 1 pddpvc_rh_result))))
    (setq pddpvc_rh_result (nth 1 pddpvc_rh_result))
    (setq pddpvc_rh_json
      (errset (pddpvc__result_json pddpvc_rh_context pddpvc_rh_result) t))
    (when pddpvc_rh_json
      (setq pddpvc_rh_publish
        (errset (pdceSetResultJson pddpvc_rh_context (car pddpvc_rh_json)) t)))
    (unless (and pddpvc_rh_publish (car pddpvc_rh_publish))
      (dpvc_navigation_clear)
      (return (list 'failed "The completed corridor result could not be published.")))
    (return (list 'succeeded
      (if (nth 10 pddpvc_rh_result)
        then "Corridor analysis and report completed."
        else "Corridor analysis and report completed; navigator data could not be written.")))
  )
)

(procedure (pddpvc__bounds_json pddpvc_bj_bounds pddpvc_bj_scale)
  (prog (pddpvc_bj_box)
    (setq pddpvc_bj_box (dpvc_nav_box pddpvc_bj_bounds))
    (return (sprintf nil
      "{\"minimumXMil\":%.12g,\"minimumYMil\":%.12g,\"maximumXMil\":%.12g,\"maximumYMil\":%.12g}"
      (times (caar pddpvc_bj_box) pddpvc_bj_scale)
      (times (cadar pddpvc_bj_box) pddpvc_bj_scale)
      (times (caadr pddpvc_bj_box) pddpvc_bj_scale)
      (times (cadadr pddpvc_bj_box) pddpvc_bj_scale)))))

(procedure (pddpvc__publish_zoom pddpvc_pz_context pddpvc_pz_value)
  (prog (pddpvc_pz_identity pddpvc_pz_scale pddpvc_pz_json)
    (when (pdceCancellationRequested pddpvc_pz_context) (return nil))
    (unless (and (listp pddpvc_pz_value) (equal (length pddpvc_pz_value) 7)
                 (member (nth 4 pddpvc_pz_value) '("mils" "millimeters")))
      (error "Native zoom returned an invalid result."))
    (setq pddpvc_pz_identity (pddpvc__identity pddpvc_pz_context))
    (setq pddpvc_pz_scale
      (if (equal (nth 4 pddpvc_pz_value) "millimeters")
        then (quotient 1.0 0.0254) else 1.0))
    (setq pddpvc_pz_json (sprintf nil
      "{\"schema\":\"pd-dp-via-corridor-zoom-v1\",\"status\":\"complete\",\"boardGeneration\":%d,\"design\":%s,\"reportPath\":%s,\"findingId\":%s,\"layer\":%s,\"units\":\"mils\",\"requestedBounds\":%s,\"actualBounds\":%s}"
      (car pddpvc_pz_identity)
      (pddpvc__quote (nth 0 pddpvc_pz_value))
      (pddpvc__quote (nth 1 pddpvc_pz_value))
      (pddpvc__quote (nth 2 pddpvc_pz_value))
      (pddpvc__quote (nth 3 pddpvc_pz_value))
      (pddpvc__bounds_json (nth 5 pddpvc_pz_value) pddpvc_pz_scale)
      (pddpvc__bounds_json (nth 6 pddpvc_pz_value) pddpvc_pz_scale)))
    (return (pdceSetResultJson pddpvc_pz_context pddpvc_pz_json))))

(procedure (pddpvc_zoom_handler pddpvc_zh_context)
  (prog (pddpvc_zh_report pddpvc_zh_finding pddpvc_zh_index pddpvc_zh_call)
    (when (pdceCancellationRequested pddpvc_zh_context)
      (return (list 'cancelled "Cancelled before native crossing navigation.")))
    (setq pddpvc_zh_report (pdceArgument pddpvc_zh_context "report_path"))
    (setq pddpvc_zh_finding (pdceArgument pddpvc_zh_context "finding_id"))
    (unless (and (stringp pddpvc_zh_report)
                 (leqp (strlen pddpvc_zh_report) 1024)
                 (stringp pddpvc_zh_finding)
                 (leqp (strlen pddpvc_zh_finding) 12)
                 (rexMatchp "^crossing-[1-9][0-9]*$" pddpvc_zh_finding))
      (return (list 'rejected "The native crossing navigation identity is invalid.")))
    (setq pddpvc_zh_index (fix (atof (substring pddpvc_zh_finding 10))))
    (unless (and (geqp pddpvc_zh_index 1) (leqp pddpvc_zh_index 200))
      (return (list 'rejected "This crossing is outside the published navigation bounds.")))
    (setq pddpvc_zh_call (errset
      (dpvc_navigation_zoom pddpvc_zh_report pddpvc_zh_index
        (pddpvc__identity pddpvc_zh_context)
        'pddpvc__publish_zoom pddpvc_zh_context) t))
    (unless (and pddpvc_zh_call (listp (car pddpvc_zh_call))
                 (equal (length (car pddpvc_zh_call)) 2))
      (dpvc_navigation_clear)
      (return (list 'failed "The native crossing navigation owner failed.")))
    (return (car pddpvc_zh_call))))
