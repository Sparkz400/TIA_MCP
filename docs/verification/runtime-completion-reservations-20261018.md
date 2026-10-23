# Runtime capture completion reservations — 18 October 2026

Base: `02c49138b3c200b010399fd462dd3ce365549117`. Focused runtime and frozen package
validation pass. No Siemens product ran.

## Reproduced defect and repair

While recording final timing gaps after deadline cancellation, an actual segment
publication failure could skip `Writer.Finish`. That skipped its reservation
release even though the polling worker and timers ended. Seven small failed
captures each retained a 64 MiB reservation; an eighth capture was refused with
`RuntimeCaptureCapacity` when the files occupied only 21,828 bytes. A Host restart
cleared the in-memory reservations, but that was not an acceptable recovery path.

The poller now records that final-gap storage failure and still reaches the
existing writer completion path. Its `finally` releases capacity even if final
publication also fails. It does not adopt an uncommitted tail or replay acquisition.
No request/schema, configured quota, persistence format or native gate changed.

## Evidence

- `artifacts/runtime-final-gap-probe/before-01.log` reproduces the eighth-capture
  refusal and independently counts actual bytes. The complete regression also
  fails against the original source in `before-03.log`.
- `after-03.log` passes 43 checks against the isolated repair. Eight actual
  next-segment directory collisions preserve exact committed heads, keep missing
  data unknown, dispose timers and refuse implicit replay. A ninth healthy capture
  succeeds. The test uses the production poller and an explicit session/clock
  fixture; it needs no network, full disk or elevated permissions.
- The integrated physical-runtime suite passes all 304 checks, including those
  43 regressions and the existing real Windows access-denial checks. Receipts are
  under `artifacts/runtime-completion-reservations-01/` and its adjacent managed log.
- The earlier `before-02.log`/`after-02.log` probe setup failed compilation because
  generated files from two build directories were included together. Explicit
  compile items corrected the probe. Those failures are not defect evidence.

## Scope and remaining work

The immediately preceding base passed 44 managed suites, 1,530 Python tests (one
skip), 25 Explorer checks and all 38 package stages. Current verification targets
the changed runtime path and a fresh portable package; the base's full aggregate
is not relabeled as a current-source aggregate run.

Real disk-full, broader sustained resource pressure, device/profile coverage,
independent online association, UA/control work and the full 44-workstream scope
remain open. This regression is an actual filesystem publication collision with
a deterministic clock and test session; it does not prove physical PLC timing,
native identity or behavior under an exhausted operating-system volume.

## Final current-source verification

The 2,461-file frozen source passes contracts/bindings and the
physical-runtime suite (293 checks), including 40 scheduling/lifetime
checks, 43 new completion-failure checks and seven actual Windows ACL checks.
The earlier primary-checkout run reported 304 checks; total counts vary because
the existing concurrent-publication test checks every observed status poll.
All 43 regression checks and existing required outcomes pass in both runs.

All 38 fresh package stages pass. The relocated Host passes
1,447 actual loopback TLS/DPAPI/runtime checks, including the existing
two intentional process-kill scenarios, independent lifecycle auditing and 62
actual prior-capture compatibility checks across two clean Host lifetimes.
The full Python/Explorer/44-suite aggregate belongs to the preceding base;
it was not rerun or relabeled for this one-line cleanup change.

Frozen source manifest SHA-256:
`ba2e538fde52bfba7849780b89546d72ff0e5eb3fddf13896d1ff8a20a8c3885`.
Package strict manifest SHA-256:
`5be3f65ea29a8cf575cf9a48bfa8737432c363a3a4b9514945c4f1be9d350e9a`.
All 112 historical evidence rows remain unchanged, with 106 unavailable inputs.
Catalog/native qualification and the original 44-workstream audit are unchanged.
Only final status/verification prose differs after the freeze. Local
`verification.json` and `integration.json` retain exact file and Git/CI evidence.
