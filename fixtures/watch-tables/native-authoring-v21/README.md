# Owned V21 Bool creation lifecycle

The additional `creation.reviewed.xml` and `profile-candidate-review.json` retain the original input and review from the same `wa1604` run for portable exact-scope admission tests. The separate [candidate manifest](profile-candidate-manifest.json) pins those bytes and the source review hash. They do not amend the historical native manifest or qualify a current binary. The review's original absolute artifact references describe provenance; portable scope tests read only these two retained files.

These five files retain the exact native output and absence receipts from WT-05 run `wa1604` on 16 October 2026. The run used TIA Portal V21 Update 2, CPU 1511-1 PN V4.1, a direct root FB instance and active culture `de-DE`. The [manifest](manifest.json) binds every file, the frozen binaries/source manifest, installed API fingerprints, independent oracle and complete local audit.

`creation.applied.xml`, `creation.reopened.xml` and `creation.original.xml` are unmodified Siemens `WithDefaults` exports. `WT05_BoolCreation` contains exactly one `"TiaMcp_EnableGate_DB".Active` Bool row, an empty modification value and the default diagnostic reason. `absence.before.txt` and `absence.restored.txt` are the exact adapter absence receipts, **not native XML exports**. The latter was captured after the restored checkpoint reopened in another native process.

The independent audit verified generation/source declarations, exact reviewed rows/XML, six unchanged peer exports (four blocks, the default tag table and empty force table), saved fresh reopen, checkpoint recovery, fresh recovered reopen and original/restored reconciliation. Five native processes, one Host and one worker exited normally. The original saved project retains the created row; the separate restored checkpoint has no such table.

This is evidence for one creation lifecycle. Production authoring remains disabled. The generic creation, Bool-addition and ID-remapping profile keys also cover cases not exercised here; additions to existing tables, removals, reordering, explicit multilingual comments, other scalar types, populated watch peers, collision and stale-source refusal require separate native trials and exact admission binding. Earlier failed attempts remain failed in the [trial report](../../../docs/verification/wt05-authoring-20261016.md).

Historical: these files were captured under TIA Portal V21 Update 2. Since 28 October 2026 the pinned profile follows V21 Update 3; its inputs are in [`native-authoring-v21-update3`](../native-authoring-v21-update3/README.md).
