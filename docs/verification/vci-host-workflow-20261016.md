# VCI local Git Host workflow — 16 October 2026

This increment adds the local document revision workflow described in the
[caller guide](../reference/vci-host-workflow.md). No Siemens product is installed,
no native worker is started and no native profile is enabled. D01 remains open for
package/mapping comparisons through Host and its native/remote obligations.

The shared `GitWorkspaceRunner` supplies controlled processes, raw Git objects,
private-index commits, exact branch compare-and-swap and raw revision materialization.
`VciWorkspaceStore` supplies committed-upload intake, independent parent/candidate
repositories, bounded storage, durable file-only callbacks, immutable review and
completion handles, downloads and explicit receipt reconciliation. It does not
fabricate `NativeTargetObservation` to use the native engineering journal.

## Executed checks

- `artifacts/vci-host-dev/run04.log`: 36 managed checks with actual installed Git.
  They cover exact BOM/CRLF/binary/Unicode bytes, empty files, explicit deletion,
  preserved parent history, old-revision ZIP export, wrong/stale review refusal,
  damaged content, unsafe Git configuration before dispatch, durable unknown intent,
  actual completion-file failure after Git verification, receipt-only reconciliation,
  callback tampering, count bounds and an incompressible 16 MiB raw file whose Git
  object exceeds the raw-file size limit. No native callback is used.
- `artifacts/vci-host-dev/mcp02/verification.json`: 31 actual stdio checks across
  four normal Host lifetimes, with nonexistent native worker paths. Every VCI
  response is checked against the JSON contract. Upload release and Host restart
  retain independent candidates; missing completion receipts reconcile without a
  second commit; exact old revision bytes download and verify.
- `artifacts/vci-host-dev/mcp04/verification.json`: expanded to 42 stdio checks,
  including an actual owned Host interruption after its durable dispatch callback,
  fresh Host refusal of replay and early-phase reconciliation, unchanged parent
  bytes and independently hashed complete receipts. The report pins Host DLLs,
  executable, Git, schema and harness. The process-tree utility returned 128 while
  reporting failed child stops; the original retained Host process handle confirmed
  exit. This proves Host interruption and journal behavior, not independent retained
  handle evidence for every transient Git descendant or any native process cleanup.
- `run05` and `run06` retain fixture-only failures while adding Git object corruption: Windows read-only object attributes first prevented mutation; after correcting that, Git itself refused the corrupted object before publication. The final fixture accepts this earlier refusal and requires no new export receipt.
- `artifacts/vci-host-dev/run07.log`: 41 real-Git workflow checks pass, adding bounded/canonical reference refusal, independently altered Git tree rejection with no completed export receipt, and export-to-reviewed-manifest equality.
- Four independent Python contract groups cover the exact tool/catalog/D01 mapping,
  empty/upload/deletion distinction, preview-kind/review-hash admission and response
  limits that cannot promote native qualification.
- The first MCP attempt remains retained as `mcp01`: its final test incorrectly
  required the native operation directory to be absent. Normal Host initialization
  creates an empty directory. Inspection confirmed no native records or workers;
  the corrected test checks the actual invariant and the rerun passes.
- `mcp03` retained another harness failure: it required process-tree utility exit
  zero as the termination oracle. The owned Host did exit. The corrected check
  retains the utility result and verifies the original Host process handle instead.

The intermediate combined run `artifacts/vci-host-01/source/artifacts/checks/result.json` passed all 28 managed suites and the then-current 31-check VCI stdio workflow. It precedes the interruption and final export-manifest checks.

The final combined run **passed** from `artifacts/vci-host-02/source`, starting
`2026-10-16T11:50:13Z` and completing `2026-10-16T11:56:55Z`. Its source manifest is
`artifacts/vci-host-02/source-manifest.json`, SHA-256
`b550ee96148840fa99d2a0c0a851978ac7bc7b16c9d93f8853ceb9b782dbf75d`.
The aggregate receipt is `source/artifacts/checks/result.json` beneath that run.
All 28 managed suites pass, including the 41 real-Git workflow checks; all 1,254
Python tests pass with one unavailable-native-observation skip; 21 Explorer
model/client checks and all actual stdio workflows pass. The final VCI stdio
receipt records 42 checks across five Host lifetimes, including intentional loss.
Its process-tree command returned 255 and reported one unsuccessful child stop;
the original Host process handle confirmed exit. This retains the same explicit
limit on descendant cleanup evidence described above. The reported child PID was
absent on the subsequent process inventory; that snapshot is not retained-handle
proof. No native worker ran. Catalog checks report 237 features, 164 declared MCP
tools and 144 normally registered Host tools; qualification-only tools stay gated.

Only documentation changed after this tested source snapshot. Hosted commit
`b51f3da` passed managed, isolated OPC UA and Explorer checks, but its Python job
failed on CRLF-versus-LF source binding hashes. The [following package increment](vci-package-workflow-20261016.md)
records that failure and repairs checkout-stable source collection. The previous
repair commit `caa4c30` passed
[Python/.NET/OPC UA](https://github.com/Sparkz400/TIA_MCP/actions/runs/38892144946) and
[Explorer](https://github.com/Sparkz400/TIA_MCP/actions/runs/38892144734); those runs
precede this VCI increment.

## Unexecuted gates

Native VCI workspace creation/mapping/export/import, genuine complete companion
packages, Siemens compilation/reopen/preservation, project restoration, runtime,
remote Git endpoints and native admission remain unexecuted here. Small real Git
fixtures and one maximum-size raw file do not qualify 1,000-object native exchange
or large-scale process performance. Corrupt or earlier-phase unknown commits are
retained and refused; the reconciliation route only repairs a missing receipt for
an already verified exact local Git result.
