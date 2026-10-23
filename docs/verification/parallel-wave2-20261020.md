# Second parallel C01, C04, runtime and coordinator increment — 20 October 2026

This increment extends the earlier Bool-generation and single-pair experiment
work with integer state text lists, repeated experiment reports, an actual
60-second runtime pressure qualification, and durable native dispatch quarantine.
It does not complete C01, C03, C04, D16 or the full 44-workstream scope.

## Delivered scope

- [C01 integer state generation](hmi-numeric-generation-20261020.md): the
  existing author accepts the explicit v3 profile bound to an original saved v7
  component/output/integer binding. It generates and regenerates paired
  configuration and translations for SInt, USInt, Int, UInt, DInt, UDInt and
  LInt. Exact disjoint intervals, missing value ranges, state changes and
  selected original dependencies remain visible. Manual conflicts suppress
  both candidates; retained peer bytes and prior author records are preserved.
  Complete dependency closure and native installation are unqualified.
- [C04 repeated experiments](diagnostic-repeated-experiments-20261020.md): two
  new tools compare two to eight explicitly paired before/after experiments
  and export revalidated single or repeated reports as JSON, Markdown or HTML.
  Original measurements, exact windows, mappings, units, direct replans and
  quality remain binding. Missing or poor-quality evidence cannot become a
  verified hypothesis. Repetition does not establish statistical independence,
  causality, native acquisition or a verified fix.
- [C03/D16 real-clock pressure](runtime-soak-wave2-20261020.md): an opt-in
  harness exercises the production poller/store with four concurrent captures,
  touched memory, real OS handles and hashing load. It checks cancellation,
  worker refusal, retained crash prefixes, no replay and fresh recovery.
  Acquisition uses an explicit session test double. The later full-package run
  also exposed an early-timer polling defect, described below.
- [Native coordinator containment](native-dispatch-guard-20261020.md): future
  fixture helpers enter a Windows job atomically at process creation. A shared
  per-user dispatch lock and durable attempt registry refuse another guarded
  window while an earlier attempt is unresolved. Original handles and job
  accounting are required for release; legacy missing lifetime evidence cannot
  be replaced with a new job receipt. No native process was launched in this
  increment.

The static API inventory records 238 tool entrypoints and 56 mutation kinds;
the normal Host lists 218 tools, with qualification tools excluded. The catalog
contains 312 features. New feature entries remain partial and unverified for native use.
Both new MCP workflows run in the aggregate and relocated package. The
60-second soak and actual process-job checks are explicit qualification runs,
not extra delays in every unit-test execution.

## Frozen validation

The repaired implementation snapshot is `artifacts/pw20w2s2`, containing 2,528
files. Its adjacent manifest SHA-256 is
`18bcd05cfc33684d3dad931ebacd678474e0a9dfa14b133c217879b3eca153cb`.
The full aggregate, all 42 package stages, browser, soak and process-job
qualification pass from this snapshot. The earlier
`artifacts/pw20w2s1` snapshot remains unchanged
with 2,525 files and manifest SHA-256
`3d77e82bb34c395e45aeee4319dc5bcee30923599b351697eb9ba5f592b89518`.
Final reporting changes are limited to this note, TODO, STATUS and the offline
execution plan.

The first snapshot's complete aggregate passed all 44 managed suites, 1,585
Python tests with one existing skip, all 25 Explorer model/client checks and
every Host workflow.
The first relocated package run failed in the existing runtime-session
workflow: after a slow capture ended at its duration boundary, a new capture
was refused with `RuntimeSessionUnavailable`. The failed `package-01/result.json`
and complete console are retained unchanged; the diagnosis and repair are
described below.
The repaired snapshot passes final offline validation.

The failed package completed 40 of 42 stages, including both new HMI/diagnostic
workflows. Its original and relocated bundle inventories still match all 1,821
files, and independent ZIP verification matches 1,822 entries. All 2,525 first
snapshot files remained unchanged. The failure and inventory findings are
separately retained in `package-01/independent-summary.json`; matching hashes do
not turn the failed workflow into a pass.

An artifact-only probe against the unchanged compiled poller reproduced two
errors using its existing manual clock: a poll scheduled at 100 ms began at
99 ms, and a poll scheduled at the 1,000 ms duration boundary began at 999 ms.
The on-time control passed. Evidence is retained in
`artifacts/runtime-deadline-investigation-20261020/probe-01/`.
Session verification cancellation correctly makes that session unusable; the
harness incorrectly assumed it could continue later acquisitions on the same
session. The [production repair](runtime-early-timer-20261020.md) now passes 343
runtime checks, including 18 new early-wake regressions. The identical compiled
comparison probe fails the original poller and passes the repair; the existing
32,768-read pressure oracle and seven mutation controls also pass. The repaired
full loopback workflow passes 1,605 checks with four explicitly isolated
cancellation scenarios, seven sequencing mutation controls, the existing
lifecycle oracle and 62 actual prior-capture compatibility checks. Its retained
receipt is `artifacts/runtime-capture-sequencing-20261020/verification.json`.
The repaired integration freeze above passes full offline validation. Identity
invalidation remains unchanged.

Repaired-source validation:

| Validation | Result | Evidence under `artifacts/pw20w2s2/artifacts/` |
| --- | --- | --- |
| Full aggregate | All 44 managed suites, 1,585 Python tests with one existing skip, 25 Explorer model/client checks and every Host workflow passed | `all-01/result.json` |
| Offline package and relocation | All 42 stages passed; 1,592 runtime MCP checks and 62 prior-capture compatibility checks | `package-01/result.json` |
| Explorer browser | 38 checks, 15 screenshots; no page errors or network requests | `browser-01/browser-results.json` |
| Packaged diagnostic report browser | 26 checks, four desktop/narrow screenshots; no errors, remote requests or dialogs | `experiment-browser-01/result.json` |
| Real-clock runtime soak | 22 checks; eight independent altered-evidence controls rejected | `soak-01/verification.json`, `soak-01/independent-oracle.json` |
| Actual Windows process jobs | 22 checks; owned children, live-job refusal, breakaway refusal and failed atomic launch | `jobs-01/verification.json` |

The final aggregate exited successfully after 2,997.691286 seconds. Its runtime
workflow passes 1,593 checks and 62 actual prior-capture checks; numeric HMI
passes 248 MCP checks and repeated experiments pass 78. The runtime managed
suite reports 345 checks, including 43 completion and 71 pressure checks; its
concurrent observation count can vary. Two existing CS8625 warnings remain in
the ProjectModel suite's linked `LibraryDependencyCapture.cs:19` and
`BridgeException.cs:10` sources. The complete console and 70 per-suite receipts
are bound by `all-01/independent-summary.json`, which rechecks all frozen sources.
Summary SHA-256:
`0ebb740f209e0cf850007e2ad9a6dee5dc7af1623b98253f28ce91f1d653b74a`.

The repaired offline bundle is
`artifacts/pw20w2s2/artifacts/releases/tia-mcp-offline_20261020_095659_a85ff202`.
Both original and relocated strict inventories match all 1,824 files; independent
ZIP verification matches 1,825 files and 21 directory entries. Manifest SHA-256:
`63acf16fded4f189c34e90030f03df780b0ff36be9ee5e65a05eeec30548f72f`;
ZIP SHA-256:
`b96bb7624d898026556b8a71b6603ff26037ccb16de982727f71baaf372276c6`.
The package's independent receipt also verifies four isolated cancellation
scenarios, seven sequencing mutation controls, 248 numeric HMI MCP checks,
78 repeated-experiment MCP checks and unchanged frozen sources.
`package-01/independent-summary.json` SHA-256:
`d773f301b90a39899039af17dddc4b400586c012eef5a084b6c175512b085c1c`.
These hashes establish the tested artifact identity, not a publisher signature
or native release qualification.

The repaired frozen soak acquired for 60.0092508 seconds, retaining 10,566 loaded
read outcomes, 663 gap rows and 1,746 skipped slots. Cancellation drained in
1,503.991 ms. Peak sampled private bytes were 185,118,720 and peak handles
were 543. The crashed process left 142 committed reads and eight gaps; a
separate process preserved that exact prefix, refused replay and acquired 16
outcomes under a fresh intent. Original child handles exited with codes
0 / 73 / 0. These are bounded load measurements, not system exhaustion,
zero-growth or long-term leak qualification.

`soak-browser-jobs-verification.json` binds 361 evidence/runtime files and
confirms all 2,528 frozen source files remained unchanged after those runs.
Its SHA-256 is
`e1cdd86f8411884b8349ecac061dd31c31b3e9ee619aa4c995bc64add2282e82`.

The repaired snapshot's packaged report browser passes at 1440 and 390 pixels, with
bounded keyboard-scrollable evidence tables at narrow width. Exact JSON and
rendered Markdown checks pass alongside HTML. Its separate
`experiment-browser-verification.json` binds eight browser/input/log artifacts
and rechecks all frozen source files; SHA-256
`0342384473ce89129095bc5a1a81eaf23a602f14e900a76ef322b822d63aea96`.

Focused qualification before the freeze includes 315 numeric generation
checks, 248 numeric MCP checks, 170 actual prior-v1/v2 author checks and the
71 existing Bool generation checks. Diagnostic qualification includes 42
groups with 117 new repeated-experiment checks and 102 existing experiment
checks, 78 repeated MCP checks, 26 report browser checks and nine prior-producer
compatibility checks preserving 46 preexisting files (42 historical originals
and four earlier qualification exports). Coordinator checks include
33 fixture tests, 27 existing store/recovery tests and 12 quarantine controls.
The lane notes retain exact scope, earlier failed harness attempts and
source/runtime bindings; focused receipts are not substituted for the final
frozen aggregate.

The root retained-evidence audit matches all 112 available references.
The source-only snapshot matches six and reports 106 unavailable historical
artifacts, which were deliberately not copied. Missing artifacts remain
unavailable; no native or release evidence is promoted.
The final root receipt is
`artifacts/native-guard-wave2-20261020/final-verification.json`. It binds the
validation receipts, tested binaries, original failed package, final reporting
files, unchanged Classic archive and continuing shared quarantine, and confirms
that root implementation files match the tested freeze.

## Native and external gates

The earlier user authorization to resume HMI prerequisites remains in effect.
The Classic start screens are saved, and its two complete compiles passed with
zero errors and warnings in the earlier attempt. The configured archive remains
`artifacts/hmic20c/native/configured.zap21`, SHA-256
`e6f1ae79c105d5fd473399d121f80d75e86ce494a8370dab486c6e5b6b3f3498`.
The ten paired Classic exports passed their narrowly documented comparison;
that is not whole-project preservation.

The earlier reporter PID 38560 disappeared before its original lifetime handle
was captured. All eleven captured original targets exited, but complete owned
tree exit remains unverified. The immutable original
`artifacts/hmic20c/coordinator-reconciliation-final.json` remains failed and
prohibits dependent native dispatch. Its historical statement that enforcement
was absent is retained unchanged; the new shared registry now enforces that
quarantine separately.

`artifacts/native-guard-wave2-20261020/machine-guard/quarantine-verification.json`
records refusal of a real new guarded window. A read-only search for retained
event evidence found the Security log inaccessible and no installed Sysmon
Operational log. No original reporter lifetime was recovered; PID absence,
available locks or a newly empty job cannot resolve it.

Unified transfer-password settings, fresh HMI reopen, exact operation effects,
preservation and operation-specific bridge recovery remain pending. Future
Siemens compatibility with job containment is untested. The guard governs
repository `StoreExclusiveWindow` callers, not arbitrary TIA clients; brokered
or alternate-parent process creation is outside the ordinary descendant
containment guarantee.

Actual disk-full remains blocked by the previously recorded owned VHD mount
privilege failure (`0x80070522`). No retry, privilege change or real-volume fill
was attempted.

## Next independent work

Continue established-format C01 templates and complete member/dependency/
consumer coverage; C04 acquisition contracts and broader quality/time-base
profiles; and C03/D16 longer loads, broader profiles and independent online
association prerequisites. Native continuation first requires resolution of
the retained old lifetime boundary. Typed builders, library installation,
writable remote I/O and the remaining 44-workstream requirements stay open.
