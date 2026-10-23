# Production Host qualification — 16 October 2026

The updated adapter passed the requested qualification through actual stdio `TiaMcp.Host.exe` processes against one frozen Host/worker package. This is the offline, owned-scratch Bool `ModifyValue` profile; qualification-only switches seed the fixture and inject process loss. All watch preparation, dispatch, journaling, readback and recovery use the production Host/worker route. Expanded edit profiles remain gated. The final independent audit (`artifacts/production-host-20261016/qualification-final.json`) binds all three native cases to the same complete package inventories.

The build freeze (`artifacts/production-host-20261016/build-freeze.json`) binds 261 retained source/package files. Host and worker builds each passed with zero warnings and errors. The immutable package is `artifacts/production-host-20261016/binary-01`; its complete dependency inventories are checked before each native tool call. No installed package was replaced.

| Binary | SHA-256 |
| --- | --- |
| Host managed assembly | `8b471be8d6f11dc7c6ab2a5ec5711f0b1a07d5b6c217ad6457199326c5f5433b` |
| Core assembly | `6a4d286dab64bfc5e2e86ea0d624867fd7e37aceb3b82d2bfbb456ee1e19e84e` |
| V21 worker executable | `d7460d05ca82b97467d01c6c8549ddafc062505026136af7608fddcbdb3a6dfc` |

Concurrent watch-authoring edits appeared after the freeze. They were preserved and are recorded separately (`artifacts/production-host-20261016/later-working-tree-changes.json`); this qualification does not apply to a rebuild containing those later changes.

## Catalog and controller gates

The three missing entries are `qualification_watch_fixture_seed`, `qualification_watch_compete` and `qualification_component_window`. Each describes its explicit qualification-only availability and remains unverified at broad catalog level. The frozen-source contract audit (`artifacts/production-host-20261016/contract-frozen-final.log`) passes **98 tool declarations, 55 mutation kinds and 171 feature entries**. Actual Host pagination returns all 171 features; normal registration exposes 95 tools. Registration tests check all three qualification tools against normal, component-only and watch-qualification options, plus invalid option combinations. The separate current-tree audit (`artifacts/production-host-20261016/contract-current-final.log`), including concurrent authoring additions, passes 100 tools / 173 entries; that is contract coverage, not native qualification of those additions.

The pre-reboot `startup/reindex-worker-02` remains failed. Its original native exit receipt is retained. The observed OS reboot establishes that its earlier process objects cannot remain live; it cannot recover missing historical Host/worker/controller terminal codes or handle-disposal receipts. The external reconciliation (`artifacts/production-host-20261016/retained-startup-reconciliation.json`) records that evidence loss, an empty engineering-operation store, no project mutation, no reservation clearing, and no intervention in current processes.

The first fresh attempt, `startup-01`, exposed a remaining recursive descendant-collection race after successful start/status/detach. Its native exit proof was already complete. Explicit reconciliation duplicated the original Host/worker process objects from the still-held controller, proved both terminal with exit 0, and only then released controller 39316 with exit 125. Reconciliation receipt (`artifacts/production-host-20261016/startup-01-reconciled/result.json`). This attempt remains failed, and its exact original controller source bytes were retained against the pre-start hashes.

The collector now traverses direct Host→worker→native children and preserves the verified ancestor chain in immutable receipts. It does not recursively enumerate short-lived native helpers. Unknown direct startup candidates still fail closed. The recovery utility can honor a previously completed original-handle exit receipt while duplicating the remaining retained process objects; it never reopens native PIDs or treats process-table absence as exit evidence.

`startup-02` passes start → status → detach → final discovery and exact cleanup on the same frozen binaries. All three owned processes exited 0, all original handles were closed, and preexisting discovery was unchanged. Independent review (`artifacts/production-host-20261016/startup-02-independent-review.json`).

## Owned native workflow

`D:\TIAW\ph1601` passes **9/9 independent gates**: reviewed FALSE→TRUE Bool edit, native readback and unchanged peers, fresh original reopen, checkpoint recovery to a separate destination, fresh restored reopen and original/restored reconciliation. All six native instances, the worker and Host exited 0. The independent byte comparison permits only the native export timestamp and the one approved literal; IDs, comments, whitespace and all seven peer exports are preserved. Detailed independent result (`artifacts/production-host-20261016/bool-01-independent.json`).

The original saved project retains TRUE; the separately recovered checkpoint retains FALSE. Reconciliation records both dispositions rather than replacing the original project.

## Interruption qualification

| Case | Actual loss and cleanup | Independent result |
| --- | --- | --- |
| `D:\TIAW\ph1602` | Worker exits 73 after Save, before response; Host exits 1. The exact orphaned native process requires the admitted cleanup helper and exits 75. Fresh recovery Host/worker/native lifetimes exit 0. | 6/6 gates; 11 original-operation journal revisions; one dispatch; final state `reconciled`. |
| `D:\TIAW\ph1603` | Host exits 74 after receiving the saved native result, before publication. Its worker and native processes exit 0; fresh recovery lifetimes also exit 0. | 6/6 gates; 11 original-operation journal revisions; one dispatch; final state `reconciled`. |

Both restarted Hosts reject duplicate apply with `RecoveryRequired` and competing acquisition with `ResourceBusy` before starting recovery. Worker loss persists `outcomeUnknown`; Host loss retains the uncertain dispatch intent. After checkpoint retrieval and a fresh restored reopen, both refusals are tested again while original readback remains outstanding. Only fresh original-resource observation plus restored evidence completes reconciliation. The independent audit verifies the immutable journal chain, exactly one dispatch, strict native bytes, distinct process/session epochs, and original retained-handle terminal evidence. Worker-loss detail (`artifacts/production-host-20261016/worker-loss-01-independent.json`); both complete cases are included in the final audit (`artifacts/production-host-20261016/qualification-final.json`).

The worker-loss orphan termination is explicit coordinator intervention, not normal lifecycle success. Intentional lost-response operations remain interrupted/reconciled; their successful recovery tests do not rewrite the original outcomes.

The final resource audit (`artifacts/production-host-20261016/final-resource-audit.json`) verifies **29 owned process exits** across fresh startup and the three cases, no remaining owned process lifetime or pending reservation, unchanged frozen package bytes, and all ten preexisting process lifetimes still present. Prepared competitor records remain undispatched and hold no resources.

## Supporting validation

The rebuilt engineering suite passes **468 checks**, and the preserving watch suite passes **29 groups**. The combined focused Python run (`artifacts/production-host-20261016/focused-python-final.log`) passes **186 tests**, including 48 + 26 independent XML/workflow oracle tests. Controller regressions cover cancellation, cleanup failure, immutable dependency inventories, direct-child discovery and mixed completed/retained terminal evidence. Five additional file-only adversarial checks reject altered process births, missing handle closure, changed dependency inventories, duplicate dispatch and premature competing ownership. These offline checks are distinct from the native results above.

All native runs are sequential and use separate owned scratch roots. Preexisting TIA sessions and unrelated Hosts are outside this qualification. Physical PLCs, online monitoring/forcing, simulator/runtime behavior, other mutation profiles and other CPU/product configurations are not qualified by these results.
