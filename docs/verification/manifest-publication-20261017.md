# Shared manifest publication — 17 October 2026

## Reproduced failure and repair

The previous safety-evidence-02 aggregate stopped at 207/208 general checks when
`ArtifactUploadStore.Commit` received Windows access denied from
`PackageStorage.Save` during `File.Move(temporary, path, true)`. The unchanged
binary later passed 208/208. A controlled Windows handle denying delete sharing
reproduced the same `80070005` overwrite denial; the existing replacement helper
instead returned `80070020` and did not retry. The original external lock owner
is unconfirmed. Failed logs and the exact-binary recheck remain retained in
`artifacts/safety-evidence-02`; the controlled probe is in
`artifacts/package-save-investigation/probe.log`.

`Save` now delegates only the already serialized and flushed file rename to a
bounded publisher. It pins source and prior destination hashes, rechecks both
and their link-free paths before every attempt, and verifies returned publication.
On Windows it retries only access-denied/sharing/lock errors while the original
prepared file remains present and both states remain unchanged: at most ten
rename attempts with nine 25 ms waits. Those waits bound retry delays, not total
filesystem latency. Other errors, changed bytes, unexpected destinations, links,
interruption and a missing source stop immediately. A move that completed before
throwing is never replayed or silently treated as a verified result. Terminal
failures retain any unpublished prepared file; no native call or serialization is retried.

## Audited call sites

All 15 production save sites use existing ownership:

| Caller | Save sites | Existing guard and meaning |
| --- | ---: | --- |
| ArtifactUploadStore | 4 | Shared intake lock; begin, chunk acknowledgement, commit and abort. A failed chunk may retain an unacknowledged data tail; the existing identical-chunk retry reconciles it from the persisted offset. |
| PackageStore | 7 | Shared intake lock; staging/ready/failed state, immutable manifest, references and retain/unretain. Original package bytes and retention semantics remain unchanged. |
| ComponentQualificationWindowStore | 1 | Shared window lock; monotonic immutable revisions, including initial staged publication. |
| EngineeringGitOperationJournal | 1 | Exclusive callback directory admission plus single-owner callback lock and exact operation/revision checks. Repeated callback/native publication is not authorized. |
| TestSuiteStyleJobs | 2 | Prepare lock and per-job control lock; initial queue and stop marker. No run execution is retried by this repair. |

Directory publication and native journal replacement retain their existing
separate rules. The six mutable upload/package-open catalog routes remain
conservatively unverified against historical profiles; their evidence IDs and
all 106 unavailable historical binding records are preserved.

## Verification

The first new targeted run reached 214/215: the missing-source test accidentally
selected another parent directory and correctly hit the cross-parent guard.
After correcting that fixture, all 215 general checks pass. Seven new groups
cover real Windows source/destination locks, initial creation and replacement,
permanent errors/read-only files, exact retry counts, drift, interruption,
completed-but-thrown moves, junction substitution, upload recovery and one-time
serialization. An early development copy had an encoding mismatch outside the
intended helper; its guard blocked wholesale copying, and only the intended
helper/call change was integrated. Existing path-validation bytes are preserved.

The actual MCP harness passes 26 checks under -OO, with two clean Host lifetimes
and absent native workers. It holds the real upload manifest against deletion,
verifies structured failures and unchanged prior metadata, then checks exact
chunk/commit retry, restart and abort preservation. Non-Windows runs explicitly
skip Windows lock injection and still exercise normal upload/restart/abort flow.
Focused evidence: `artifacts/manifest-publication-01-general-final.log` and
`artifacts/manifest-publication-01/mcp/verification.json`.

Final frozen Git tree: `609a231d982cb206cbfc331a233a4a5111057b60` (2021 files).
Source manifest (`artifacts/manifest-publication-02/source-manifest.json`) SHA-256:
`ca55fe266f3909936801ff801cd0019b8a8d849c458c4e14c67d0f46d65d0c75`.
Source ZIP SHA-256: `bd9d2f438f21edeb5fe71642c15a4880b718704fdca48f17b6f804044a6d14d4`.

The aggregate (`artifacts/manifest-publication-02/source/artifacts/checks/result.json`)
passes 41 managed suites, 1,400 Python tests (one unavailable-evidence skip),
24 Explorer model/client checks, contracts/source bindings and all offline MCP
workflows. The general executable passes 215 checks; the new manifest publication
MCP workflow passes all 26 checks, including Windows lock injection and two clean
Host lifetimes. The focused MCP run also passes under -OO.
Run: 2026-10-17T12:24:57.9384595Z to 2026-10-17T12:38:29.7944765Z.
Aggregate receipt SHA-256: `35b1b9829c090bad8200eccef91dbc4e1165aa3abe2bc8f232253f94aa65a129`.
Console evidence (`artifacts/manifest-publication-02/aggregate-console.log`).

All 24 package stages (`artifacts/manifest-publication-02/source/artifacts/package-checks/result.json`)
pass, including the same 26-check failure/recovery workflow through the relocated
portable Host. Bundle `tia-mcp-offline_20261017_122529_d967316f` contains 1429 manifest files.
Package receipt SHA-256: `188566a4056a030ed2a5f678a75cc6696538f863d41ba8fb30debbec3b50704a`.
Manifest SHA-256: `9b0c5fcd7c85f2cb0319f3d3eb4d23833466bc03de3ca41f50fc45421de8b279`.
ZIP SHA-256: `711d3bb73f21444ae4101294d618783699b115f94c0fa6e5231d52825655dc8d`.
The portable package disables native workers. Browser tests were not repeated
for this storage-only increment; SDK/native compilation remains unexecuted.

The source comparison (`artifacts/manifest-publication-02/final-source-comparison.json`)
verifies the frozen source remained unchanged and permits only completion docs
to differ in the final commit. All 106 unavailable historical evidence binding
rows remain unchanged. The integration receipt (`artifacts/manifest-publication-02/integration.json`)
records the pushed commit, exact hosted CI outcome and cleanup.

This fixes local file publication behavior; it does not qualify native products,
prove the original transient lock owner, or make the complete 44-workstream goal
finished. The reproduced encoder compiler-target mismatch, fresh baseline
compilation and the full D14/TO queues remain open.
