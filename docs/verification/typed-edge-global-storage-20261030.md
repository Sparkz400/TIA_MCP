# Typed R_TRIG/F_TRIG global storage — 30 October 2026

Typed LAD/FBD (L01) had four open wave 9 cases: R_TRIG and F_TRIG on an existing global instance
DB, in LAD and FBD. They had no profile because TIA deleted every edge instance DB the capture
created, whether it came from SCL or `CreateInstanceDB` ([native slot](wave10-native-slot-20261027.md)).
This run tested whether a referencing call keeps the DB, so the storage can be captured.

## Run

`scripts/capture_typed_routes_owned.py` ran from a Debug build frozen with `git archive` at
`ffe22f2` (`artifacts/te2`). It used one owned TIA V21 lifetime on the SHA-bound standard baseline
(CPU 1511-1 PN V4.1). It captured 10 sources in this order: the R_TRIG multi-instance caller,
`global-rtrig-storage.scl`, the LAD global caller, then the same for F_TRIG, then the FBD callers.
Each storage source holds the instance DB and an SCL FC that calls it (`Tgp_GlobalRTrig_Ref`,
`Tgp_GlobalFTrig_Ref`), so the DB is referenced in its first compile. Report:
`artifacts/q/ec2/capture.json`. Record:
[`local.typed.edge-global-storage.20261030`](../../evidence/local.typed.edge-global-storage.20261030.json).

## Result

The capture passed. Every source imported, compiled with zero errors, saved and added exactly its
declared blocks; all other native objects stayed unchanged; the saved state matched a fresh-process
reopen.

- **A referencing call keeps the DB.** `Tgp_GlobalRTrig` and `Tgp_GlobalFTrig` survive compile, save
  and reopen, and the LAD/FBD global callers compile against them.
- **V21 marks the DB know-how protected.** The Openness inventory reports both DBs as
  `IsKnowHowProtected`, like the R_TRIG/F_TRIG system FBs TIA copies into System blocks. A first
  capture on `4f7603a` (`artifacts/q/ec`, not curated) stopped there, because the runner treated
  protected blocks as generated. Since `ffe22f2`, the runner records a declared protected block by
  its inventory identity instead.

## Consequence

No R_TRIG/F_TRIG global storage document can be exported, so it cannot be captured or compared. The
typed builder reads an existing global DB through its export (`DeclarationUnavailable` for protected
blocks), and creating a global DB needs a captured template. Both stay refused for R_TRIG/F_TRIG.
This is a V21 boundary for the four existing-global cases; the edge profiles keep no
`globalInstanceType`. Multi-instance edge storage (`existingMulti`, `createMulti`) passes all five gates
(`local.typed.wave9.runtime.20261029`, `local.typed.wave9-created-storage.runtime.20261029`).

Supporting a protected DB by its identity alone (name, number, `InstanceOfName`) would be a new,
weaker storage-evidence rule. It is not built and needs a decision.
