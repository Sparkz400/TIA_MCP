# Safety signature phase audit — 17 October 2026

The retained safety auditor checked XML and compiler diagnostics but ignored all
signature observations. Seven synthetic cases with absent, malformed, changed or
wrong-owner signatures passed. Separately, the post-apply guard accepted safety
fingerprint coverage from another PLC. The before/after reproduction (`artifacts/safety-audit-reproduction/comparison.json`)
records all eight refusals against the previous committed implementation.

Two additional reported-value reproductions (`artifacts/safety-audit-reproduction/reported-signatures.json`)
show that Python equality previously allowed True or 1.0 in a reported post-compile
signature to match integer 1 in a valid readback. Reported values now require strict
integers before comparison; matching values alone cannot substitute for their type.

The auditor now requires complete selected-block signature observations before
application, after application and after reopening the changed project. Recovery
adds restored, recompiled and reopened-copy observations. Each observation binds
the requested block, reported block/PLC paths, recorded project copy and current
owned process ID. Booleans cannot substitute for process IDs. Recovery must name
a separate destination; an arbitrary different project is not accepted as that
destination. Failed selected-block inspections cannot disappear from the audit.

Existing signature and compiler validators are reused. Applied observations must
also agree with the post-compile signature receipt and exactly one owning-PLC
fingerprint coverage row, with explicit availability and the full signature count.
Coverage from other PLCs cannot fill that requirement. This shared guard reaches
direct import and recovery qualification as well as retained auditing.

Unchanged native XML requires baseline signature/runtime-group equality. An edited
payload may establish different post-apply signatures; its reopened copy must
retain those observations. Every recovery phase must match the original baseline.
Repeated observations within a phase must agree. Signature checks retain their
source step, phase, paths, process and original inspection payload in the output.

`signaturePhaseCoverageVerified` describes the recorded observations and their
comparisons. `signatureNativeAuthenticityVerified` remains false: supplied paths,
IDs and signature integers are not independent proof of a native capture. The CLI
implementation digest now includes the shared safety validator source.

## Verification

The focused run (`artifacts/safety-audit-targeted.log`) passes 55 parent tests,
including conformance and the -O/-OO subprocess runner. One new test method covers
exact owning-PLC coverage and malformed target metadata. The retained audit test
now supplies explicit inspections and exercises 12 rejection cases in each of six
phases, a wrong recovery destination, unchanged-import signature drift, and a valid
edited-signature/reopen/recovery control. Existing compiler and XML negatives remain.
These are synthetic fixtures and processes; no native process starts.

The actual CLI run (`artifacts/safety-audit-reproduction/cli-audit.json`)
checks a retained three-phase synthetic report, retains the shared validator hash
and leaves its original export bytes unchanged.

Frozen Git tree: `715b6b43c873b59f96d331727379cf237caf1e26` (1995 files).
Source manifest (`artifacts/safety-audit-01/source-manifest.json`) SHA-256:
`bc3c41bdc70b58799942c13d07a3c83da6e7f69b0e94377115d1dfbf7aebaeb9`.
Source ZIP SHA-256: `9861de2864f24a5514c28a4e7b5467e09dc6e80ca930b2f3e30f9845b996a361`.

The full Python aggregate (`artifacts/safety-audit-01/source/artifacts/checks/result.json`)
passes all 1,371 tests with one unavailable-historical-evidence skip, plus contracts,
source bindings and retained-evidence inventory. The optimized child suites pass
within that aggregate.
Run: 2026-10-17T09:45:42.8189359Z to 2026-10-17T09:46:56.8387028Z.
Receipt SHA-256: `1efefeda9edf48bdc092a5df55cd63b470ddd902a5569f0b8691b1feca255182`.
Console evidence (`artifacts/safety-audit-01/python-console.log`).

The source comparison (`artifacts/safety-audit-01/final-source-comparison.json`)
confirms only completion-documentation changes after the tested snapshot and
preservation of all 106 unavailable historical evidence binding rows. The
integration receipt (`artifacts/safety-audit-01/integration.json`) records the
pushed commit, hosted CI outcome and cleanup.

This Python-only increment does not repeat managed, package or browser checks.
The prior package result (`artifacts/xml-document-01/source/artifacts/package-checks/result.json`)
and full managed aggregate (`artifacts/encoder-checkpoint-01/source/artifacts/checks/result.json`)
remain evidence for their own source snapshots.

## Remaining gates

Independent source authenticity/freshness, exact admission and Host/worker/native
process terminal evidence remain open. The auditor checks recorded identities and
phase coverage; it does not prove original handle ownership, OS process lifetime,
archive provenance or native recovery-copy endpoint correspondence. Complete
required compiler-target coverage and baseline compilation also remain separate.

A separate synthetic detach-failure reproduction (`artifacts/safety-lifetime-before/detach-failure.json`)
still leaves the direct harness's completed flag true despite detachFailure. This
is an open lifecycle defect, not a passing native receipt. Reuse the existing exact
process-lifetime machinery for the next repair; do not infer terminal cleanup from
the new signature phase checks.

Historical reports missing these fields now refuse; stored receipts and hashes
are not rewritten. Full D14 report/F-I/O workflows, native SDK compilation, safety
engineering, activation testing and functional-safety acceptance remain open.
