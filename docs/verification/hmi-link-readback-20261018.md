# HMI library-link readback and preserving-import guards

Status: shared source and Unified caller repairs pass focused and complete frozen
aggregate/package checks. No Siemens SDK or full native caller was executed.

The exact old readback reproduced mixed type/version identifiers, a verified flag
for a different reported version, and acceptance of a foreign service owner.
Both Classic prepare/apply guards also admitted a present service with no version.
Original portable and actual Framework reproductions remain under
`artifacts/hmi-readback-preflight-01/`; their failures have not been overwritten.

`HmiLibraryLink` now captures the selected service, native owner, observed version,
type and scalar identifiers once per role. Native equality permits different
wrappers for the same object. Required readback compares the requested type and
version to the same captured pair it reports. Results retain detached values;
this is not an atomic native snapshot or proof of content/dependency preservation.

Only confirmed absent service permits Classic preserving import. Missing provider,
owner, version, type, identity or display version remains unavailable. Native read
failures remain explicit; unexpected exceptions propagate. Known links require
library handoff; unknown links raise `HmiPreservationUnavailable`. Required
readback raises `PreservationMismatch` when it cannot verify the exact pair.

All five existing readback callers use the shared helper. Both Classic guards use
it before export/import. Optional HMI types remain inside the existing no-inline
caller boundaries; the helper uses only base engineering/library types. Valid
readback fields remain, with an explicit `linkStatus`; unverified reads retain
`nativeLinkVerified: false` and all results retain `runtimeVerified: false`.

Focused validation passes 32 existing recipe checks and 45 new linked production
helper cases on .NET 10. The same 45 cases pass on actual x64 .NET Framework with
synthetic Siemens providers. Three deliberate controls removing owner checks,
rereading version identity, and admitting unknown links fail with 3, 6 and 16
failed cases respectively. Source-only wiring checks cover the five callers,
both import phases and optional-module separation; they do not compile those
native callers. Evidence is retained in `artifacts/hmi-link-readback-01/`.

The caller audit found a further defect even with the repaired helper: Unified
apply discarded its verified read, built another view and forced its verified
flag to true. Exact extracted view/apply-tail code reproduced a different returned
version marked verified. The repaired view accepts the expected version and
returns that single observation; apply returns it directly. Prepare also retains
the verified view instead of discarding it and reading again for the preview.
Five exact extracted view/apply-tail checks pass on portable and actual Framework
runtimes with synthetic types. Four source-only wiring checks pass. Full native
prepare/apply methods remain unexecuted. The original caller reproduction and
earlier frozen runs remain retained; final validation must use the repaired caller.

Affected HMI/native library catalog entries remain unverified. Historical evidence
is retained without promoting it to qualification of this source. Native SDK
compilation, real service ownership/equality, optional-module loading, native
prepare/apply/compile/reopen/recovery and HMI behavior remain unexecuted.

Remaining offline work includes full C01 dependency/consumer mapping and authored
generation, C04 executable provenance, D16 pressure/profile work and the rest of
the original 44-workstream queue. This bug repair does not complete that goal.

## Final frozen verification

The final two source snapshots contain 2,409 identical files, including the Unified
caller repair. All 44 managed suites, 1,518 Python tests (one existing skip),
25 Explorer model/client checks and all 38 package stages pass. Both executions
retain the 578 HMI MCP checks and 726 compatibility checks across 13 prior v1-v4
records. The linked helper's 45 cases and 32 existing recipe checks pass in the
aggregate. Separate exact extracted caller checks cover the repaired view/apply
tail on portable and actual Framework runtimes; this is not a full native caller
or SDK build.

The final package has 1730 files, manifest
`f4e3481908070f47c34f7b2ab690a47b91bab2f1a940b881b087bb72d72385ab`. Five Explorer assets match the previous
browser-tested assets; no new browser run is claimed. All 112 historical evidence
entries remain unchanged, including 106 unavailable inputs. Original fixtures and
the original scope reconciliation remain unchanged. Only final documentation
differs from the passed source snapshots.

The earlier `final-*` snapshots in the evidence directory predate the Unified
caller repair and do not qualify that repair. Final evidence uses `complete-*`. The later `fixed-*` package failed at the
terminal-read oracle before that oracle was repaired; its failed result remains
retained and is not promoted to a passing combined run.
Their original results and the caller failure reproduction remain retained.
Full C01 and the original 44-workstream goal remain open.
