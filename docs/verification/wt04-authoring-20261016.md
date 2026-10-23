# WT-04 authoring implementation — 16 October 2026

The exact-instance authoring path is implemented through production Host registration, `WatchTablePlanner`, the preserving XML editor, the durable dispatcher and the V21 worker. **New native mutation profiles remain unverified and disabled in normal Hosts.** This report does not close WT-05 or promote the earlier Bool literal evidence to broader authoring support.

`watch_table_generate` captures an exact saved offline FB/direct instance DB using the existing instance resolver and declaration reader. `watch_table_prepare_authoring` retains complete creation/update reviews with immutable source artifacts, exact destination identity, collision/absence evidence, sequential bounded edits and explicit profile gates. The packaged genuine native row capture permits first-table creation and empty-table updates without requiring a manually authored seed table in that project.

The worker rechecks the source before checkpoint and import, uses `ImportOptions.None` for creation, and verifies the saved readback. The shared dispatcher retains readback before validating it, keeps ownership until fresh reopen, and restores a creation baseline as an independently observed absence at a distinct reserved recovery location. Lost import and retrieval responses can be reconciled without replay. Native mutation retains the existing worker-owned-scratch admission boundary.

Current native capture is limited to direct root instances with ordinary symbolic identifiers. Arrays, embedded multi-instances, aliases and unsupported selectors remain explicit gaps; namespaced/software-unit instance profiles are refused. Scalar materialization, row removal/reordering, existing/new comment cultures, comment-row preservation and reference-free ID remapping each contribute explicit required native profiles. Monitoring and forcing are outside this slice.

## Validation

- Host and V21 worker builds passed with zero warnings and errors, using isolated artifact output directories.
- **57 WT-04 managed checks** passed: native-source planning, complete review, scalar selectors/formats, multiline multilingual comments, packaged templates, active cultures, empty targets, mixed row preservation, collision/stale-source refusal, request bounds, production profile refusal before native admission, retained readback, fresh reopen, creation/update recovery and lost-response reconciliation.
- The existing engineering suite passed its **468** identity, journal, admission, interruption, adapter and recovery checks. Its initial combined WT-04 run passed 55 checks; the final focused authoring run adds the mixed-row checks for 57.
- **29/29** watch planner/document test groups passed on the final document code.
- **24** production-worker boundary checks passed through offline reflection; no `TiaPortal` was constructed.
- **Five actual Host registration/qualification-option checks** passed, including the two new public tools. No native worker was started by these checks.
- **94** existing independent watch-verifier regression tests and **26** source-binding collector tests passed.
- Contract coverage passed for **100 tools, 55 mutation kinds and 173 features**; generated source bindings were refreshed and checked against current sources.

The optional Python Draft 2020-12 meta-validator was unavailable (`jsonschema` is not installed). The repository's standard contract checks parsed the new schema and passed; no standalone meta-schema validation is claimed.

The first default-output build encountered an already-running engineering-test executable. That process was left alone; builds and tests continued in isolated output directories. Receipts are under `artifacts/wt04-authoring-20261016/`, including `engineering-checks.txt`, `authoring-checks-final.txt`, `watch-checks-final.txt` and the collected binding manifests.

No native authoring trial was run or newly qualified. Production activation requires independent evidence for each exact mutation/type/target profile: intended delta, complete native XML/typed readback, unchanged peers, saved fresh reopen, checkpoint restoration and fresh recovered reopen. Existing native failures and release obligations remain unchanged.
