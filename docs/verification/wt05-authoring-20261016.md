# WT-05 harness and first native creation lifecycle — 16 October 2026

The first planner-backed Bool creation lifecycle **passes** on the frozen `revision-03` Host/worker package. The [independent audit](../../artifacts/wt05-authoring-20261016/create-bool-04-audit.json) verifies exact generation and review, native rows/XML, unchanged peers, saved fresh reopen, checkpoint recovery and fresh recovered reopen. This completes the immediate harness-extension and first-creation trial; broader WT-05 profiles and production activation remain open.

The [runner](../../scripts/qualify_watch_authoring.py) extends the existing exact-process qualification controller and invokes actual stdio MCP tools: `watch_table_generate` → `watch_table_prepare_authoring` → `watch_table_apply`. It creates a fresh isolated scratch project and uses the packaged genuine native row template without a seed watch import. The worker's current saved scratch session remains open through apply; persistence checks use later fresh processes. Review failure prevents apply, and native failure is retained while explicit checkpoint recovery proceeds without replay.

The [file-only verifier](../../scripts/verify_watch_authoring.py) independently compares source FB/instance declarations against native peer exports, complete candidate XML and explicit ID mappings, exact absence receipts, typed/XML readback, peer bytes, immutable journal records, fresh process/session identities and original-handle terminal proofs. It reports successful recovery separately from a failed creation trial.

The passing run is `D:\TIAW\wa1604`, operation `93db799b413f415c9ca0f730f52f0470`. Its exact case is TIA V21 Update 2 / CPU 1511-1 PN `6ES7 511-1AL03-0AB0` V4.1, a direct root instance, active culture `de-DE`, and one `"TiaMcp_EnableGate_DB".Active : Bool` row in `WT05_BoolCreation`. The modification value is empty and modification intention is false. The default diagnostic reason is retained. All four blocks, the default tag table and the empty force table remain unchanged; peer byte comparison excludes only the native export timestamp.

The original saved project retains the created row. The separately retrieved checkpoint and its fresh reopen prove that the destination table is absent. Original and restored resources are reconciled. Repeat apply and competing ownership are refused while original reconciliation is outstanding. Five native instances, the worker and Host exit 0 with their original process handles closed. No online PLC operation occurs.

The [portable fixtures](../../fixtures/watch-tables/native-authoring-v21/README.md) retain three genuine native exports and two explicitly identified absence receipts. The [profile evidence](../../artifacts/wt05-authoring-20261016/revision-03/passing-profile-evidence.json) binds the audit and fixtures to all 277 frozen source/test files, the complete binary package inventories, and the independently reconstructed installed Siemens API fingerprint.

| Final binary | SHA-256 |
| --- | --- |
| Host managed assembly | `0e8ccf64f920cdf73f9391c2eec179b6cd4084568b1faed4b00dba22769e0de5` |
| Core assembly | `c42df17638e321bb5e09fb5d4ab220b10e79b3738c02a03944a5793d8135c9aa` |
| V21 worker | `ab3956e0bb551a79e3bbdc00be776dac103413befb495c4993872f8d6216cf4f` |

Earlier attempts remain retained:

| Run | Result and disposition |
| --- | --- |
| `wa1601` | Generation refused an internal `maxItems=10000` query because the shared reader admits at most 2000. No watch operation was prepared. Fixed the query bound and added reflection checks against the actual shared reader. [Audit](../../artifacts/wt05-authoring-20261016/create-bool-01-audit.json). |
| `wa1602` | Generation/review passed; apply refused before checkpoint because the harness reopened the unseeded scratch and lost its current scratch ownership. The operation stopped without native dispatch. Corrected the harness order without widening ownership admission. [Audit](../../artifacts/wt05-authoring-20261016/create-bool-02-audit.json). |
| `wa1603` | Import produced the intended row and unchanged peers, but verification refused before save: the candidate used `<ModifyValue></ModifyValue>` while TIA exported `<ModifyValue />`. The original saved project remained unchanged; checkpoint recovery and fresh recovered reopen passed. The selected candidate scalar now uses `RemoveNodes()` when cleared. Readback hashing remains strict, with a regression proving the two serializations remain distinct. [Delta and recovery evidence](../../artifacts/wt05-authoring-20261016/create-bool-03-delta.json). |
| `wa1604` | Full creation lifecycle passed on the corrected candidate. No earlier mutation was replayed. |

The [final resource audit](../../artifacts/wt05-authoring-20261016/final-resource-audit.json) proves 21 normal owned process exits across all four attempts and no pending watch reservation. Prepared competitor records never dispatched.

Validation includes zero-warning/error final builds, 30 watch planner/document groups, 57 managed authoring checks, 27 worker target/query boundary checks, 23 qualification/retained-fixture Python tests, and the existing 53 controller/expansion checks. The frozen source binding audit passes 100 tool declarations, 55 mutation kinds and 173 features. A separate current-tree contract audit encountered concurrent Test Suite/translation declarations without catalog entries; those unrelated changes were preserved and are outside this frozen qualification. No installed package was replaced.

**Production authoring gates remain closed.** The required keys are `watch.create-instance.v1`, `watch.add-instance-bool.v1` and `watch.reference-free-authoring-ids.v1`; this one-row creation cannot establish every case admitted by those generic keys. Next qualify collision and stale-source refusal, additions to existing tables, removals, reordering, explicit multilingual comments and other scalar types independently, then bind accepted evidence to exact native admission profiles. This result does not qualify populated watch peers, other CPU/product configurations, monitoring, forcing or runtime execution.
