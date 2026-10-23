# Safety signature evidence — 17 October 2026

The safety qualification helper accepted Boolean, negative, string, array and
overflow signature values. The before/after reproduction (`artifacts/safety-signature-comparison.json`)
compares the committed baseline with the repaired helper using synthetic receipts.

The shared helper now requires the existing CPU 1511F fixture's exact four program
signature types and one selected-block signature type, with unique identities and
strict UInt64 values. Missing, duplicate, unknown and extra row fields refuse.
The capture must explicitly report scopeComplete true, truncated false, an empty
warnings array and an available selected-block signature service. Overall
inspection completeness remains false because the worker documents broader gaps;
scopeComplete is the narrower capture flag this check uses.

[Siemens V21 SafetySignatureProvider documentation](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/f-related-openness/safetysignatureprovider?contentId=H4xeHjhFT3D1iSsRVRfS5w)
defines Value as UInt64 and zero as a nonexistent or invalid signature. Required
collective, software, hardware and block signatures must therefore be nonzero.
The documented fixture has a zero communication-address signature: it is retained
as unavailable/invalid and is not evidence of a valid communication configuration.
This is a narrow fixture qualification policy, not an inventory of every supported
F-CPU or a general safety-report format. The historical fixture receipt is still
unavailable on this workstation; the older documentation is not a new native test.

Post-apply checks validate the readback before comparing reported signatures.
Preservation equality validates both baseline and readback, so two identically
malformed snapshots cannot pass. Recovery preflight uses the same helper before
creating a Host. Existing direct import/reopen/recovery callers reuse this guard;
no production worker or native dispatch source changed.

## Verification

Four added regression methods cover value boundaries and zero semantics, exact
signature sets, explicit capture flags and matching malformed comparisons.
The existing recovery test now covers bad values and types before Host creation,
while retaining the original archive bytes. Fixtures in both test modules now
represent the explicit supported shape.

The focused run (`artifacts/safety-signature-targeted.log`) passes all 54
parent tests, including conformance and the optimization subprocess runner. The
runner executes the evidence suites under both -O and -OO; these are child tests,
not additional parent tests. No Siemens process starts.

Frozen Git tree: `b8ac8ad27d5952215c0e0765455b9f2a87013bba` (1994 files).
Source manifest (`artifacts/safety-signature-01/source-manifest.json`) SHA-256:
`8bbd52da720f97cb956d4f7cc0f1c3106c11f2e9c25e5d94e4122368520dce43`.
Source ZIP SHA-256: `7cd24219143c2feac930e429a1760365c68695b7e217d3b31773ae3882e0c639`.

The full Python aggregate (`artifacts/safety-signature-01/source/artifacts/checks/result.json`)
passes all 1,370 tests with one unavailable-historical-evidence skip, plus contracts,
source bindings and retained-evidence inventory. The optimized child suites pass
within that aggregate.
Run: 2026-10-17T09:36:02.8202334Z to 2026-10-17T09:37:23.4031008Z.
Receipt SHA-256: `20a952bc2655abec3b3467c4df35de4a743046149464fe921c8f052baf3261b7`.
Console evidence (`artifacts/safety-signature-01/python-console.log`).

The source comparison (`artifacts/safety-signature-01/final-source-comparison.json`)
confirms only completion-documentation changes after the tested snapshot and
preservation of all 106 unavailable historical evidence binding rows. The
integration receipt (`artifacts/safety-signature-01/integration.json`) records the
pushed commit, hosted CI outcome and cleanup.

This Python-only increment does not repeat managed, package or browser checks.
The prior package result (`artifacts/xml-document-01/source/artifacts/package-checks/result.json`)
and full managed aggregate (`artifacts/encoder-checkpoint-01/source/artifacts/checks/result.json`)
remain evidence for their own source snapshots.

## Remaining gates

Exact native owner/program/block identity and freshness, fingerprint coverage by
owning PLC, required compiler-target coverage, original admission and exact process
lifetime remain open. The independent XML/compiler auditor does not establish
signature preservation; complete signature audit across every lifecycle phase
and broader D14 report/F-I/O workflows remain open. Equal supplied integers do not
establish native authenticity, functional safety or activation readiness.

No Siemens SDK build, native safety import, archive retrieval, F-I/O, activation
test or physical acceptance ran. The full 44-workstream queue remains open.
