# Trusted release of failed provisioning admissions — 2 November 2026

`simulation_provision_reconcile` now bounds the whole project and the opaque simulator storage before it
releases an `outcomeUnknown` `simulation_provision_scratch_credential` admission. The earlier rule and its first
release are in the [24 October record](provision-reconciliation-20261024.md).

## What changed

In the fresh empty worker-owned TIA instance, besides the earlier checks (own report and scope, archive hash, card
manifest, no native mutation):

- **Whole project.** The retrieved pre-provision archive and the current scratch project must have the same
  location-independent `EngineeringFingerprint` plus project library revision (the fingerprint admission
  reconciliation uses), and TIA's `Project.LastModified` must precede the archive's write time. TIA persists project
  edits only by Save, so a project last saved before its full-project archive still is that archive, whatever
  provisioning did in memory; this is the `change_apply` rule of [admission reconciliation](admission-reconciliation-20261024.md).
  An ambiguous save time can only hold. Project file write times are not used: TIA writes `System\PEData.plf` while
  a project is open without saving it (seen in all five retained storages below).
- **Storage outside the card, metadata only.** Every entry of the `recoveryBaseline.storageMetadata` inventory
  (taken before the attempt wrote anything) outside `SIMATIC_MC` must still exist with the same size and write time.
  New entries may only be this attempt's report and checkpoint stream, `.provision.claim`, `before-provision.zap21`,
  the attempt's empty-card backup and the two runtime files PLCSIM creates at power-on (`retain.pms`,
  `sim_hwdb.ini`). Their contents are never opened or hashed. The card itself is still compared with its manifest.
- **Runtime protection store.** The attempt's write-ahead checkpoint stream is written through to disk and records
  `credentialSubmissionMayHaveOccurred=true` before `SetPlcMasterSecret` is entered. A readable stream that starts
  with `attemptPrepared` and records neither an opened credential nor a possible submission in any record, together
  with the report's `credentialSubmissionArmed=false`, proves that no credential reached the runtime protection store.
  A torn record holds, because it could be that intent.
- **Observer.** A discovery observer destination that was never created releases as `unchanged`; an existing one must
  still open with no device.

The resources are then the project `unchanged`, the storage `verifiedApplied` (runtime state from the empty startup,
no credential) and the observer `unchanged` or `verifiedApplied`. `scripts/reconcile_provision.py` now tracks each
Host lifetime exactly (`ExactClient`: Host, worker and owned Portal with their exit codes, never terminated) and,
after the reconciling Portal has exited, opens the original project in a second fresh Portal, which works only if
the first released the project lock. It calls no simulator tool.

## Evidence

Offline: `ProvisionReconciliationChecks` (35 checks: each new refusal, a missing save time, the absent observer) and
the pure rules in `TiaMcp.HardwareEngineering.Tests` (storage differences, torn, armed, opened and empty streams, the
ambiguous save time).

Native: see [native release](#native-release).

## Native release

At b3787562 (frozen `artifacts/pc1`, Debug Host and V21 worker), one `reconcile_provision.py` run per admission, no
PLCSIM instance and no simulator tool, every unresolved provisioning admission on this PC (`local.sim.reconcile.release.20261102`):

| Admission | Left by | Failed at | Observer | Result |
| --- | --- | --- | --- | --- |
| `f52861ec…` | checkpoint attempt `p24cp2`, Softbus, 24 October | `discoverExactRuntimeAddress` | none declared | released |
| `ac16fa74…` | card-move run `p1`, Softbus, 1 November | `captureRecoveryBaseline` | never created | released |
| `d7b97fe0…`, `600a8af7…`, `6963dcf6…` | real-server runs `rt`, TCP/IP, 29 October | discovery, `goOnline`, observer start | empty | released |

Each fresh session (its own Portal, a different process from the provisioning one) found the archive hash equal, the
fingerprint of the retrieved archive equal to the current project (4, 5 or 7 native block fingerprints), the project
last saved 1.7–2.9 s before its archive was written, all 86 card files equal to the original manifest, no storage
difference outside the card and a stream that never opened or armed the credential. `f52861ec…` is the reservation the
[checkpoint-copy retrieval](runtime-admission-resume-20261024.md#verified-checkpoint-copy-retrieval) had kept. Every Host,
worker and Portal of both lifetimes per admission exited with code 0 within its deadline (exact handles, no
termination), and the second fresh Portal opened each original project, so the reconciling session released its lock.

With the [frozen corpus](runtime-frozen-20261024.md) (65 cases through the runner's explicit checkpoint/compile/save
before provisioning, `7531ed1`), this closes the provisioning-reservation item on this PC.

## Limits

- No held admission was reconciled natively; the refusal paths (each new check) are managed tests.

- Opaque storage is bounded, not byte-compared: runtime files PLCSIM creates at power-on are runtime state, recorded
  by metadata and never read, under the semantic preservation rule of the
  [post-RUN card inventory](card-inventory-and-lock-release-20261024.md). An attempt whose stream may have submitted
  the credential stays held; nothing can release it without reading the protection store.
- The fingerprint keeps the boundaries of `EngineeringFingerprint` policy 2 (no generated payload bytes, no unexposed
  vendor data); the save-time rule covers the rest of the saved project.
