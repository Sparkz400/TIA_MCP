# Safety evidence runtime guards — 17 October 2026

Running the seven existing safety qualification tests under `python -O` made all
seven fail: rejected signatures, preservation differences and independent-audit
failures were accepted because the checks used `assert`. The
pre-fix run (`artifacts/safety-guards-before.log`) records the failures.

The direct safety fixture, scratch discovery fixture, checkpoint-retrieval runner
and independent retained-export auditor now use the existing
`engineering_preservation.require` helper. All 67 conditions remain identical:
40 direct-fixture, four scratch, 12 retrieval and 11 auditor checks. Existing
failure messages are preserved; previously unnamed assertions now identify their
failed condition. The AST comparison (`artifacts/safety-guard-conversion.json`)
records condition/message equivalence. This also preserves expression evaluation
and failure checks when Python runs with `-O` or `-OO`.

No native API, mutation route, retry, recovery policy or retained evidence was
changed. This repair prevents optimization from removing existing guards; it does
not prove that the existing guards cover every qualification requirement.

## Verification

Four new test methods cover truncated/warned signature refusal, bad recovery
archive/hash/signature refusal before Host creation, incomplete scratch refusal
before catalog/mutation dispatch, and ambiguous direct-fixture refusal before
inspection. Mocked entry-point tests verify failure receipts and cleanup, without
starting a native process. The existing auditor test now also rejects incomplete
source reports, original/export hash mismatches, unowned/readback-mismatched and
reused process identities. Existing successful pure-helper and synthetic retained
audit cases continue to pass.

The focused run (`artifacts/safety-guards-targeted-02.log`) passes 12 parent
tests, including the optimization subprocess test. Both optimized child suites
run 89 cases each, including all 11 safety methods; child counts are not added to
the parent count. An initial new-test fixture lacked an explicit fake Host file;
the corrected test uses its own source file for the digest and a mocked client.
No native Host was launched by that test.

Frozen Git tree: `e91e9ecb99e646568ffba0c43b3b142469cfc246` (1992 files).
Source manifest (`artifacts/safety-guards-01/source-manifest.json`) SHA-256:
`cde80ab5e2c59bf9f585e3a67f7a120e41d60445ea1250ef065d0858474fbfae`.
Source ZIP SHA-256: `57298f78e006cdcb848144befd69a091aaa87a6854d5f6df1de4f23a5fe67379`.

The full Python aggregate (`artifacts/safety-guards-01/source/artifacts/checks/result.json`)
passes all 1,361 tests with one unavailable-historical-evidence skip, plus contracts,
source bindings and retained-evidence inventory. The optimized child suites pass
within that aggregate.
Run: 2026-10-17T09:16:50.9185317Z to 2026-10-17T09:18:04.6409774Z.
Receipt SHA-256: `090b8570142c928229418f03e2c4332de204909bf044dd7739996e7a319c82c3`.
Console evidence (`artifacts/safety-guards-01/python-console.log`).

The source comparison (`artifacts/safety-guards-01/final-source-comparison.json`)
confirms only completion-documentation changes after the tested snapshot and
preservation of all 106 unavailable historical evidence binding rows. The
integration receipt (`artifacts/safety-guards-01/integration.json`) records the
pushed commit, hosted CI outcome and cleanup.

This increment changes Python qualification/audit code only. The previous
package run (`artifacts/xml-document-01/source/artifacts/package-checks/result.json`)
and full managed aggregate (`artifacts/encoder-checkpoint-01/source/artifacts/checks/result.json`)
remain evidence for their own source snapshots; no package, managed-suite or
browser rerun is claimed here.

## Remaining gates

The safety qualification path still needs complete compiler-target/diagnostic
proof, exact signature identity/value/coverage/freshness contracts and independent
source/admission/lifetime verification. Preserving an existing predicate does not
make it sufficient for those requirements. In particular, aggregate zero compile
errors alone are not complete compiler evidence.

No Siemens SDK build, native safety import, archive retrieval, F-I/O, activation
test or physical acceptance ran. Historical native evidence and its unavailable
binding rows remain unchanged. D14 reporting/lifecycle/qualification and the full
44-workstream offline queue remain open.
