# Safety harness file lifetime — 18 October 2026

Baseline: `5deddf7ab12aa67e857ed3c95d0ea50293065e8c`. The previous Teamcenter aggregate exposed an unclosed-file warning in `scripts/smoke_safety_direct.py`. Both reads used `open(...).read()` without closing the stream explicitly.

## Change and regression

Both reads now use `pathlib.Path(...).read_bytes()`, matching the neighboring block-read validation. The SHA-256 checks and refusal of overwritten original exports are unchanged. No Host, worker, native operation or safety policy changed.

The existing synthetic round-trip/recovery test now records `ResourceWarning` messages through garbage collection and asserts that there are none. On the original source this test fails with two unclosed `original.xml` streams. On the corrected source all 26 safety qualification tests pass, including the full synthetic round-trip and independent preservation audit. The regression uses real temporary files and a fake safety session; it executes no Siemens code.

Retained evidence is under `artifacts/safety-file-reads-01/`: `before.log`, `after.log`, the Python-suite receipt and `verification.json`. The full Python run passed **1,464 tests with one skip** in 186.337 seconds and emitted no `ResourceWarning`. Contract checks, source bindings and retained-evidence inventory also passed. Source-binding regeneration produces no binding change; all 106 missing historical evidence records retain their original hashes.

## Remaining scope

Native F-block import, compilation, signatures, recovery and UI behavior still require Siemens. The prior Teamcenter aggregate/package receipts remain evidence for their frozen source; this Python-only fix does not claim a newly executed managed/package run.

The [Add-In plan](../plans/expansion-plans/tia-addin-companion.md#ai-03-owner-preflight--18-october-2026) now records the actual Host/worker ownership constraints. Its next concrete implementation step is verifying the connected bridge peer against the launched worker, with an actual same-user impostor subprocess test, before building the complete AI-03 receiver. This preflight adds no authenticated transport. The full 44-workstream goal remains active; hosted CI refusal due to account billing is separate from local validation.
