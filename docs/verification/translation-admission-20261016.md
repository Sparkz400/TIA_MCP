# Translation admission and failure-case follow-on — 16 October 2026

This increment preserves the accepted `artifacts/translation-native-20261016-02` package-03/package-06 results. It adds an exact admission proposal/checker, portable negative checks and a bounded native failure controller. Production import remains disabled; [exact admission and the remaining same-fixture proposal](../reference/translation-production-admission.md) explain why fresh-fixture receipts cannot enable a broad profile.

## Source and portable checks

The recorded starting source is baseline.json (`artifacts/translation-admission-20261016-01/baseline.json`). The first managed run passed **146/146** checks, including 51 new exact admission cases, on a private Release build. After replacing raw peer hashes with hashes of the actual permitted semantic comparison and binding the complete Host deployment inventory, the focused admission suite passed **53/53** checks. It proves that permitted creation-time metadata changes match while real peer content, exact project/key/view, culture, target and binary changes do not. These checks use synthetic adapters and do not grant native qualification.

The Python failure-controller and packet refusal checks passed **11/11** alongside the prior native-packet verifier regressions. The actual frozen qualification Host exposed the two optional failure fields and refused unsupported/conflicting cases with a nonexistent worker; both refusals occurred without native startup and the Host exited normally.

Receipts:

- 146-check managed result (`artifacts/translation-admission-20261016-01/managed-01/verification.json`).
- 53-check normalized admission result (`artifacts/translation-admission-20261016-01/admission-02/verification.json`).
- Actual qualification Host boundary (`artifacts/translation-admission-20261016-01/qualification-host-boundary/verification.json`).

## Frozen native package and queue

Package-01 and package-02 were rejected before compilation because concurrent source changes failed the new freeze stability check. They remain immutable failed snapshots. Package-03 copied and rehashed all 1,551 source inputs during a coordinated source-write pause and built isolated qualification Host/worker binaries. It has no production-enabling flag.

- Package manifest SHA-256: `63571e41b830c819327563bf49c6e20d5aa1445c8ece2e51caef6072a15b1c09`.
- Host implementation DLL SHA-256: `1b8d9ea5cabf90d0784fb827785638065487f44eaf650ea9cd690952abc93cfd`.
- Worker SHA-256: `8f768f7cee7b24494abd4c55fd5914796bb2160b2d80a159e72261b2730e0c28`.

The controller pins every package file and its own sources before each call. `ExactClient`/`RetainedNativeTree` keep original process handles; graceful detach/EOF and terminal proof precede release of foreign-store locks. No PID adoption, automatic termination, import retry or restore retry is introduced.

The fresh before-dispatch packet at `D:/TIAW/ta1601` passed its independent file-only verifier: **zero imports**, one retained checkpoint and one distinct restoration, complete ten-row and peer preservation, fresh recovered reopen, **three normal exact owned exits**, and **zero owned pending resources**. Its 45-store preflight preserved the unrelated historical `wt02` pending operation and found no overlapping reservation.

- Exact reviewed before-dispatch packet (`artifacts/translation-admission-20261016-01/proposed-before-dispatch.json`).
- Independent before-dispatch verification (`artifacts/translation-admission-20261016-01/before-dispatch-verification.json`).

The fresh import-response packet at `D:/TIAW/ta1602` also passed: one import, one distinct restoration, full ten-row/peer comparison of the independent original and recovered observations, three normal exact owned exits and zero owned pending resources. The 46-store preflight included the closed ta1601 store. Reviewed packet (`artifacts/translation-admission-20261016-01/proposed-import-reply.json`) and independent receipt (`artifacts/translation-admission-20261016-01/import-reply-verification.json`) remain tied to immutable package-03. No accepted native trial was replayed.

The subsequent runtime design adds the six ordinary native tool declarations and fixed deployment registry, without installing admission. The focused exact-key/registry suite passes **65/65**, including full Host DLL/dependency changes with unchanged apphost, missing/unknown/duplicate fields, missing gates, foreign instance, traversal and evidence alteration. Receipt (`artifacts/translation-admission-20261016-01/admission-03/verification.json`). The actual stdio Host suite passes **41/41** with two normal Host exits and no workers: five missing-registry dispatch refusals, journal-only status, hidden default qualification surfaces, explicit runtime qualification, invalid injections and all pairwise qualification-mode conflicts. Receipt (`artifacts/translation-admission-20261016-01/host-boundary-03/verification.json`). Host and worker private Release builds complete without warnings/errors.

The Python controller/oracle suite now passes **14/14** tests, including all seven failure cases, exact-key drift before mutation, reverse transitions only after independently verified applied outcomes, and actual normalized peer equality. The new independent oracle also rechecks ta1601/ta1602 from files without native execution. The bounded `--same-instance` controller requires one new fixture, unchanged forward key across all cases, at most five separately reviewed reverse transitions, at most ten imports/twelve distinct restorations and one final unattempted forward plan. Source drift requires actual changed export evidence and an exact stale refusal before dispatch. This source is not yet native-qualified: the next immutable package/packet, reviewed registry installation and default-mode lifecycle on unchanged binaries remain required.

Restricted category/object imports and source/clear/empty-target/rich-text production writes remain refused. Qualification-only source drift is a bounded negative test, not production support for source editing.

## Closeout state

The implementation and portable boundary work is retained for integration. No package-04 source freeze, same-instance native trial, reverse operation, source-drift mutation, admission-registry installation or default-mode native proof was executed. Package-03 and the two accepted failure roots remain unchanged and terminal. No translation-owned process or pending native resource remains.

Production admission is **not complete**. The remaining gates are the seven-case same-instance qualification on the current executable package, independent review of every required native/portable receipt for its one exact key, installation of reviewed registry data only into the candidate deployment, and a default-mode lifecycle on those unchanged binaries. That last proof must capture/prepare a **new** operation from the retained approved candidate after restart; the final qualification plan retains its old session epoch and must not be reused or weakened. No broad project/profile equivalence has been asserted.

The current ordinary tools remain fail-closed without the fixed registry. The historical `verify_translation_failure.py` verifies the original individually admitted packets; `verify_translation_admission.py` is the new bounded same-instance oracle. The next native packet must hash-pin its frozen controller/verifier, all executable dependencies and declared foreign stores, obtain a new queue admission, and retain exact cleanup and zero-pending proof. These outstanding native gates are deliberately reported as pending, not relabelled as passed by portable tests.
