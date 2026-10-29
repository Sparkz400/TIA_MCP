# Fresh credential recovery admission — 17 October 2026

This portable WA-09 increment consumes explicitly planned recovery slots once under
an admitted same-owner recovery context. It reuses the existing operation journal,
protected vault and independent external-state reconciliation. It enables no native
provider or new public mutation tool. The full offline/release queue remains open.

## Behavior and invariant coverage

A trusted adapter reviews a recovery target and reacquires it under its native
exclusive-access lease. Compared with the original plan, the exact object, canonical
project path, hardware/native owner, adapter/profile, offline state and TIA version
must still agree. A new epoch and changed source/content are allowed in the recovery
review; the fresh observation must then match that review and observe required
probes. Its complete `recoveryTarget` is durable before the consumer receives bytes.

All reference lifecycle pins remain mandatory. Only recovery-slot ciphertext is
read/decrypted during recovery, so a damaged dispatch-only generation cannot disable
a separately readable prior generation. Only named recovery slots reach the trusted
synchronous consumer. Borrowed bytes are cleared and consumer exceptions sanitized.
A successful return remains `recoveryIntent`; a failure records `outcomeUnknown`
when possible. Neither is reconciliation or permission to replay.

External-only recovery needs no local restore destination. Its immutable context
still makes admission one-use across lost replies/restarts. Combined local/external
recovery reserves new local destinations in the same journal. Later revisions cannot
add destinations, replace/remove context, or replay after unknown completion.
Independent evidence must reconcile every original resource and admitted destination;
local restoration alone cannot release an external resource or its credentials.

Historical credential-bearing local recovery intents with an already admitted
destination remain readable without invented context. Their existing destination
marker prevents another consumer invocation. Fresh admission always requires a
reviewed/reacquired target. Optional omission preserves old journal hashes and
records without general credential requirements retain prior behavior.

Read the [workflow and trust boundary](../reference/protected-credentials.md#fresh-recovery-consumption).

## Source and targeted checks

Final frozen source: `artifacts/recover-02/source`, tree
`c43c2987778a5fc7165ac508d5c0fa953798f4a9`, 1,932 files.
Manifest (`artifacts/recover-02/source-manifest.json`) SHA-256:
`4d72c9f099153598c48dc6dd155cf8f40e71c0c574ddfa8c1e161a25351e00fa`.
Archive SHA-256:
`568f376eb75ce821aa424503224d816f42e5683299044d7adb7c1a85bafcc964`.

The earlier `recover-01` source tree was
`0739393a465d5f7366381ed615cd1ab664d9bd3d`. It passes the full baseline and 21 package
stages, but its recovery admission unnecessarily required an unselected local archive.
The added counterexample in `artifacts/recover-dev-04-console.log` reproduces that
refusal before the consumer. Final source verifies selected local archives and retains
independent external baseline validation through journal replay. External-only
recovery can proceed while an unrelated local archive is unavailable, while all
original resources remain reserved. A selected local restore still refuses without
its exact checkpoint. The final snapshot changes only that guard, its tests, the
workflow guide and refreshed source bindings from the earlier snapshot.

Final targeted checks pass in `artifacts/recover-dev-05/credentials/`: 55 protected
storage checks, 81 operation/dispatch checks and 106 recovery checks. These include
retained file comparisons; they are not 106 distinct native scenarios. Actual Host
status passes 32 recovery checks across two normal lifetimes in
`artifacts/recover-host-05/`, while the existing operation-status path passes 29 in
`artifacts/recover-host-operations-05/`.

The recovery suite verifies changed content/epoch acceptance, wrong owner/profile
and changing freshness refusals, missing probe refusal, unreadable recovery bytes,
usable prior bytes despite damaged dispatch-only ciphertext and an unavailable
unselected project archive, selected-archive refusal, phase isolation,
clearing/error sanitation, immutable context/destinations, combined local/external
reconciliation and historical recovery compatibility. An actual child exits 18
after durable context/intent and a synthetic recovery effect. Restart observes one
invocation, refuses replay with zero local destinations, retains both generations,
and requires separate external-state reconciliation before retirement.

The actual Host also rejects a modified retained recovery context, preserves all
journal/vault bytes after restoration of the test input and exits normally without
starting a native worker. Strict schema negatives reject null/surplus context,
impossible context states and context without planned recovery credentials.

Early development runs added coverage incrementally (87, then 90 recovery checks);
the later historical-compatibility suite passes 103 checks; final targeted acceptance
uses the 106-check source including archive selection. One schema-authoring command
failed with a Python syntax error before writing files; it was corrected before
schema/MCP validation and the frozen source. No failed command is counted as a pass.

## Binary compatibility

The compatibility receipt (`artifacts/recover-02/legacy-compatibility.json`),
SHA-256 `df315f159628cf6d71a02a040817924065cac225498c6d1d39d4d7eea279f208`,
compares the previous and current Host binaries against the explicit historical
local-recovery shape. Both return identical independently reconciled records without
`recoveryTarget`. All 68 retained files remain unchanged; both Hosts exit normally
with no worker startup. The previous Host refuses the new context-bearing recovery
history rather than silently dropping that history and releasing ownership.
This uses synthetic recovery evidence and does not certify a native restore.

## Full frozen verification

The final frozen aggregate receipt (`artifacts/recover-02/source/artifacts/checks/result.json`)
passes all 38 managed suites, 1,280 Python tests (one existing unavailable-evidence
skip), 24 Explorer model/client checks and all MCP workflows, contracts and source
bindings. It ran from `2026-10-17T02:23:12.7483019Z` to
`2026-10-17T02:33:00.6783646Z`; receipt SHA-256:
`30febf3b33ea1a5f8a8ab64fe5fe621c30755d237a7461328ee1286d37aa1e95`.

Credential checks in that aggregate and the packaged run include 55 storage,
81 operation/dispatch, 106 recovery, 82 CLI checks across 21 processes,
29 operation-status MCP and 32 recovery-status MCP checks. These remain portable
synthetic contracts plus actual local Windows DPAPI/ACL and Host process checks.

All 21 package stages (`artifacts/recover-02/source/artifacts/package-checks/result.json`)
pass, including original and Unicode-relocated verification and the recovery workflow.
Receipt SHA-256: `b5c5c10b9e4ad0ac38b15b9f73d9fc2ba0d04c23416b1cbfa52e737fa0682f4c`.
The bundle `tia-mcp-offline_20261017_022336_ab0bf374` contains 1,374 manifest files;
manifest SHA-256: `74824d0b90a0d8c77f9af395db28607e01c6c25e7a54eaab60164a076c04968d`.
ZIP SHA-256: `e41a511f1bf28b527b63524c12b3c1495a3325f5a3752c4123bb25d2509552f3`.
Manifest integrity is not a publisher signature or native compatibility claim.

The final source comparison (`artifacts/recover-02/final-source-comparison.json`)
records only completion-documentation differences from the frozen source: status,
TODO, this verification record and the execution queue. Production source, schemas,
tests, build scripts and source bindings are unchanged after successful verification.
The integration receipt (`artifacts/recover-02/integration.json`) records the
pushed commit and its separate hosted CI status.

## Open work

The callback is a trusted adapter contract, not remote authentication established by
public fields or a mock. Native providers, authenticated external target association,
secure transport, exact product/version eligibility, profile/context migration and
inaccessible/different original owners remain unqualified. Do not infer a new owner
mapping or weaken profile checks to recover another object. Actual endpoint/trust,
PLC behavior, TIA/SDK compilation, native recovery and release acceptance remain
separate gates. Existing scratch simulator credentials retain their own format.

The catalog remains 197 static tools, 55 mutation kinds and 270 features. All 106
unavailable historical evidence rows/hashes remain preserved. The previous base is
`4e6663436f51d9dedeca59e3f99624e54a8f000b`; it passes Explorer `38993112983` and
all four Python/.NET jobs `38993112982`, with the last managed job completed at
`2026-10-17T02:14:50Z`. New-commit hosted acceptance remains separate.
The [44-workstream queue](../history/offline-execution-20261016.md) remains active.
