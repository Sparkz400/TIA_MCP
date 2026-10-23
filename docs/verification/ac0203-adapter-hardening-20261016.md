# AC-02/AC-03 production watch adapter — 16 October 2026

The working tree already contained the production Host/worker watch route, preserving document editor and the first owned-scratch native workflow. This change closes specific boundary gaps in that implementation and adds regular regression coverage against the genuine XML captures. It does not enable broader watch edits or promote a new native binary as qualified.

## Implemented boundaries

- The worker reacquires and compares the exact project, PLC, object, process/session, adapter, installed profile, hardware, probes, content and preservation manifest immediately before both archive and import. A new observation receipt ID is allowed; equal XML on another target is insufficient. Native exclusive access remains held across both effects.
- Lost lease-acquisition/release replies retain the resource as `outcomeUnknown`, including before any usable checkpoint exists. A known stale-target refusal releases ownership only after the native lease is accounted for. A fresh-reopen transport loss also becomes unknown. Restart after durable acquisition requires explicit readback reconciliation and cannot resume the lease or replay a mutation.
- Returned apply readback is appended to `EngineeringOperationStore` as immutable `dispatch.readback` evidence before validation. Failed readback remains inspectable, and worker errors retain their structured native diagnostics. Success and recovery retain the original result. An additional `dispatchIntent` revision may only append evidence; it cannot authorize another dispatch. The independent journal oracle enforces that distinction.
- The preserving editor's semantic comparison now recognizes native containers by their position in the captured grammar. Familiar element names nested inside opaque payload no longer cause meaningful whitespace to be ignored. No-op retains exact bytes. All seven genuine owned exports are checked by the normal watch test suite; an independent XML comparison checks the one approved literal, all remaining nodes/IDs/cultures, fresh-reopen output and recovered baseline.
- Production preparation/apply remain restricted to the existing offline Bool `ModifyValue` profile. Expanded comment, clone, reorder, delete and document-ID transformations still require the isolated qualification harness. The Host description now states that restriction explicitly.

The integrated worker build exposed an existing unfinished ancillary VCI compile error: `WorkspaceGroup` has no `Name` property. Its existing capture now reads `Name` from the installed API's `WorkspaceSystemGroup` or `WorkspaceUserGroup`, refusing other profiles. No ancillary workflow was executed.

## Validation

| Check | Result | Evidence class |
| --- | --- | --- |
| Engineering suite | 468 checks, including 88 adapter checks and 38 interruption-matrix checks | Fake bridge plus actual managed child exits, restart and competing processes; no Siemens execution |
| Watch planner/document suite | 29 groups | Authored adversarial XML and seven hash-verified genuine exports |
| Production worker target guard | 24 checks | Reflection into the built V21 worker; no native construction/attachment |
| Test Suite | 233 checks | Offline shared-store consumer regression |
| VCI | 93 checks | 84 pure checks plus 9 explicitly selected isolated Git/journal/process checks |
| Python watch/evidence/binding suites | 120 tests | Offline oracle and binding tests |
| Actual Host registration | Five boundaries | Normal/qualification tool registration and option refusal; no worker launched |
| Host and worker | Build passed | Host reports zero warnings/errors; worker built through its boundary test project |

The [managed transcript](../../artifacts/ac0203-adapter-20261016/validation/managed.log), [final engineering run](../../artifacts/ac0203-adapter-20261016/engineering-final.log), [Git journal run](../../artifacts/ac0203-adapter-20261016/git-journal.log) and [Host registration run](../../artifacts/ac0203-adapter-20261016/host-registration.log) retain the results. The new boundary tests are registered in the solution and engineering-workstation test script. The genuine capture checks run in the ordinary offline watch suite.

The repository-wide `scripts/test_contracts.py` audit still fails on three preexisting qualification-only declarations without catalog entries: `qualification_watch_fixture_seed`, `qualification_watch_compete` and `qualification_component_window`. This is not recorded as a passing full-repository audit. No catalog qualification was promoted.

## Owned-scratch acceptance evidence

The file-only independent oracle revalidated the immutable prior native runs:

- `wt04`: **9/9 stages**, reviewed Bool change → typed/native readback and peer preservation → fresh reopen → checkpoint retrieval, fresh recovered reopen and original/restored reconciliation. [Current verification receipt](../../artifacts/ac0203-adapter-20261016/retained-wt04-verification.json).
- `wt06`: **8/8 stages**, actual lost worker response → retained unknown ownership → explicit replay refusal → checkpoint recovery and original/restored reconciliation. [Current verification receipt](../../artifacts/ac0203-adapter-20261016/retained-wt06-verification.json).

Those are the previously frozen binaries and native operations documented in the [production watch wave](production-watch-wave-20261015.md). This turn did not launch TIA or mutate a native project. A fresh native run of the newly hardened binary remains open; the separate startup/controller reconciliation recorded in the active handoff was not adopted or cleared by these offline checks. Broader watch operations remain gated.
