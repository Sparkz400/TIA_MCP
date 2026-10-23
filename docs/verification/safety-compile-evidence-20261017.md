# Safety compiler diagnostic evidence — 17 October 2026

The safety qualification helper accepted a compile receipt containing only an
aggregate zero error count. It also accepted a Boolean count, error-state target
and truncated target diagnostics. The [before/after reproduction](../../artifacts/safety-compile-comparison.json)
compares the helper at `5950901` with the repaired helper using synthetic receipts.

Safety fixture and recovery paths now reuse `compilation_evidence.require_clean_compile`.
It requires explicit successful named targets, strict integer zero error counts,
complete diagnostic arrays and non-error states throughout nested messages and
diagnostic metadata. Warning and information states remain supported. This checks
the supplied diagnostics; it does not prove that the supplied target list includes
every target that should have been compiled.

Both scratch creation paths validate their initial compiler receipt. The hardware
discovery fixture also requires applied state and compile/save stages before it
continues to inventory or reports `changeAppliedAndCompiled`. Safety imports and
recovery recompilation validate diagnostics before further native readback.
The shared post-apply qualification helper repeats the check alongside its existing
fingerprint, signature and save-stage requirements. Failed MCP responses no longer
continue through success-looking scratch/hardware payloads.

The independent safety auditor checks every recorded scratch creation and change
application, including failed envelopes and recovery compilation. It retains each
checked compiler receipt and includes the validator source digest in its CLI
implementation record. `compileDiagnosticsVerified` describes these observed
receipts. `baselineCompilationObserved` remains false when the source contains no
pre-mutation scratch compile; reopened-source workflows do not invent that proof.
`compileTargetCoverageVerified` is explicitly false. Historical sources without
sufficient diagnostic fields cannot pass the strengthened audit, and their stored
files/hashes are not rewritten.

## Verification

Five added regression methods cover malformed receipts and valid warning/info
controls, both scratch entry points, contradictory MCP error envelopes, hardware
compile/save admission, and the recovery runner's complete fake-client flow.
The independent retained-audit test also covers initial/apply/recovery compiler
receipts and all six preservation stages across three synthetic owned process IDs.
Bad receipts stop before subsequent inspection/reopen; the mutation is never
replayed and original source/archive/export files remain unchanged.

The [focused run](../../artifacts/safety-compile-targeted-04.log) passes all 17
parent tests, including the optimization subprocess test. Each child suite passes
94 cases under `-O` and `-OO`; child counts are not added to the parent count.
The new fake client supplies both MCP structured content and text content so that
empty-object replies follow the real protocol shape. No native process starts.

Frozen Git tree: `67dd20fe669d0ae1252f7d1740e963da9e82e768` (1993 files).
[Source manifest](../../artifacts/safety-compile-02/source-manifest.json) SHA-256:
`5a6453bbbed0cf6fe7fbda0f67abfda46cb22d35ebbb98c57450850447d5d6f3`.
Source ZIP SHA-256: `45e3360858e102a7aa14cbfc6eb9bb47b8fc91e2db2bdd857e2cf85fcfa3172f`.

The [full Python aggregate](../../artifacts/safety-compile-02/source/artifacts/checks/result.json)
passes all 1,366 tests with one unavailable-historical-evidence skip, plus contracts,
source bindings and retained-evidence inventory. The optimized child suites pass
within that aggregate.
Run: 2026-10-17T09:26:39.4484921Z to 2026-10-17T09:27:58.8780801Z.
Receipt SHA-256: `268f9e6ffe76f495ed2f63bfc69b3e3bb6b7ece310e9363ba5c29f3d50521277`.
[Console evidence](../../artifacts/safety-compile-02/python-console.log).

The [source comparison](../../artifacts/safety-compile-02/final-source-comparison.json)
confirms only completion-documentation changes after the tested snapshot and
preservation of all 106 unavailable historical evidence binding rows. The
[integration receipt](../../artifacts/safety-compile-02/integration.json) records the
pushed commit, hosted CI outcome and cleanup.

This increment changes qualification and audit Python only. Package, managed and
browser runs were not repeated. The prior [package result](../../artifacts/xml-document-01/source/artifacts/package-checks/result.json)
and [full managed aggregate](../../artifacts/encoder-checkpoint-01/source/artifacts/checks/result.json)
remain evidence for their own source snapshots.

## Remaining gates

Exact required target-set coverage, independent signature identity/value/coverage
and freshness, source admission and exact process/worker lifecycle remain open.
A reopened scratch still needs independently established baseline compilation.
The retained auditor is a comparison and diagnostic audit, not whole-project
qualification or functional-safety acceptance.

No Siemens SDK build, native safety import, archive retrieval, F-I/O, activation
test or physical acceptance ran. D14 reporting/lifecycle/qualification, TO-03–12
and the full 44-workstream queue remain open.
