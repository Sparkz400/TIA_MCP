# Safety exact process lifetime — 17 October 2026

The direct safety fixture reported completed true after a failed detach. The
original synthetic report (`artifacts/safety-lifetime-before/detach-failure.json`)
is unchanged. The same path now fails with completed false, cleanupVerified false
and a retained cleanupFailure after its qualification checks pass. Comparison (`artifacts/safety-lifetime-after/comparison.json`)
and new report (`artifacts/safety-lifetime-after/detach-failure.json`).

Direct F-CPU qualification, catalog-created safety scratch and explicit checkpoint
retrieval now reuse ComponentOwnedScratch and the retained original process tree.
The controller captures Host/worker/native identity and handles, requires a fresh
empty owned native start, and validates exact terminal receipts. Reopen closes the
original tree before starting a new Host/worker/TIA session. The safety harnesses
no longer call the automatic-kill smoke Client.close path.

QualificationChecksPassed and final completed are separate. Final completion
requires every session's clean native/worker/Host terminal evidence, matching
worker birth receipt, stderr EOF and no forced stop or interrupted closure. A
failure or cancellation remains a failure after cleanup. Uncaptured startup or
unresolved handles retain the existing controller's hold behavior; a timeout never
authorizes a replacement process, another native command or abandonment of handles.

Calls retain their original intent/result/status through the existing audit and
steps projection. An uncertain request prevents later safety dispatch, including
automatic status/recovery attempts. The explicit source_upsert SafetyBoundary
negative probe is the only accepted refusal; it remains recorded as failed with an
expected marker. Lost replies and other errors cannot take that path. When any
request is uncertain, the controller uses its existing guarded EOF/tree settlement
path rather than issuing another detach command.

All three entrypoints refuse to overwrite an existing report before startup.
The catalog scratch CLI accepts --report for successive runs. Direct qualification
now selects the exact worker binary explicitly, defaulting to its Debug V21 path.
The old --project option still admits the existing narrowly named workspace scratch
path; stronger original-source admission remains a separate requirement.

The retained safety auditor now requires the safety lifetime policy and terminal
receipts, binds their native session IDs to the audited starts and records
processLifetimeVerified. Historical reports without these proofs refuse unchanged.
This verifies supplied receipts, not their independent authenticity or a new native
run. The shared encoder verifier accepts an explicit policy; its default remains
the existing encoder policy, and its encoder regression coverage passes.

## Verification

Seven new regression methods and extended recovery/fixture tests cover exact
terminal success, detach/native/Host/worker/forced/interrupted failures, missing or
substituted receipts, lost startup/read responses, prohibited followups, the exact
negative probe, cleanup failure in all three entrypoints and report preservation.
The focused run (`artifacts/safety-lifetime-targeted.log`) passes 51 parent
tests, including encoder orchestration and the optimization subprocess runner.
These are fake process handles and fixtures; no Siemens process starts.

Frozen Git tree: `c0152698558bc3920eb66edc325de385a9b680f8` (1998 files).
Source manifest (`artifacts/safety-lifetime-01/source-manifest.json`) SHA-256:
`7fcffc26acb9796938fba6c3cd6ab3445212dec5a2fec51d32209ce0f7aa619d`.
Source ZIP SHA-256: `388d4577145f1ba11459e0be9d5969e20ff09e18cf27a6a6b0a83a259cbf0930`.

The full Python aggregate (`artifacts/safety-lifetime-01/source/artifacts/checks/result.json`)
passes all 1,378 tests with one unavailable-historical-evidence skip, plus contracts,
source bindings and retained-evidence inventory. The optimized child suites pass
within that aggregate.
Run: 2026-10-17T09:56:11.7068344Z to 2026-10-17T09:57:28.9140459Z.
Receipt SHA-256: `2fa600bc1f7c1a1f23c7a35a139aa0c1c694f8bd1f3181acc0107e6834be8920`.
Console evidence (`artifacts/safety-lifetime-01/python-console.log`).

The source comparison (`artifacts/safety-lifetime-01/final-source-comparison.json`)
confirms only completion-documentation changes after the tested snapshot and
preservation of all 106 unavailable historical evidence binding rows. The
integration receipt (`artifacts/safety-lifetime-01/integration.json`) records the
pushed commit, hosted CI outcome and cleanup.

This Python-only increment does not repeat managed, package or browser checks.
The prior package result (`artifacts/xml-document-01/source/artifacts/package-checks/result.json`)
and full managed aggregate (`artifacts/encoder-checkpoint-01/source/artifacts/checks/result.json`)
remain evidence for their own source snapshots.

## Remaining gates

Actual SDK/native startup, loss, detach, recovery and exact terminal behavior need
qualification on a Siemens-equipped workstation. Original report/archive source
admission, complete required compiler-target coverage, independently established
baseline compilation and recovery-copy endpoint correspondence remain open.
No production safety profile, F-I/O configuration or activation test is enabled by
this change. Full D14 reporting/lifecycle scope and the 44-workstream queue remain
open; tool evidence does not establish functional-safety acceptance.
