# PLC component OB event classes — 8 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.library_integrate.candidate_shapes.ob_event_classes.20261108`](../../evidence/local.library_integrate.candidate_shapes.ob_event_classes.20261108.json) | `qualify_library_integrate_apply.py --candidates --case obStartup --case obCyclic` | 2/2 installed |

The OB caller evidence of [2](component-ob-combinations-20261107.md) and 7 November ran on program-cycle OBs only, but the
combined-apply gate admitted any existing OB as `obCaller`. The gate now reads the caller export's `SecondaryType`: any
event class other than `ProgramCycle`, or a missing one, adds the feature `obEventClass.<class>`, so its shape stays
plan-only until it has its own native run.

The runner builds each OB from Main's export with the event class, number and informative inputs replaced, in its own owned
de-DE-only CPU 1511-1 PN V4.1 scratch:

| Case | OB | Features |
| --- | --- | --- |
| obStartup | OB100, `Startup` (inputs `LostRetentive`, `LostRTC`) | obCaller, obEventClass.Startup |
| obCyclic | OB30, `CyclicInterrupt` (inputs `Initial_Call`, `Event_Count`) | obCaller, obEventClass.CyclicInterrupt |

Each preview named exactly its features, so TIA kept the imported event class. Each fresh component went after the OB's
last network and passed the gates of the earlier shapes: the exact installed-object diff, the native caller insertion proof,
the call oracle, a fresh reopen, a 0-error compile and `change_restore` of the initial checkpoint into a new location.
`PlcComponentQualificationGate.VerifiedShapes` now lists `obCaller+obEventClass.Startup` and
`obCaller+obEventClass.CyclicInterrupt`, so production runs them.

Not covered: these classes combined with LAD, an earlier placement or reuse; other event classes (time-of-day, time delay,
hardware interrupt, diagnostic error and the rest); OB attributes beyond V21's defaults; other languages than de-DE; runtime.
