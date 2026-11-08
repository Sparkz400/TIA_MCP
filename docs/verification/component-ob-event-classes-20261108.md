# PLC component OB event classes — 8 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.library_integrate.candidate_shapes.ob_event_classes.20261108`](../../evidence/local.library_integrate.candidate_shapes.ob_event_classes.20261108.json) | `qualify_library_integrate_apply.py --candidates --case obStartup --case obCyclic` | 2/2 installed |
| [`local.library_integrate.candidate_shapes.ob_event_classes_2.20261108`](../../evidence/local.library_integrate.candidate_shapes.ob_event_classes_2.20261108.json) | the same, seven further cases | 7/7 installed |
| [`local.library_integrate.candidate_shapes.ob_combinations_2.20261108`](../../evidence/local.library_integrate.candidate_shapes.ob_combinations_2.20261108.json) | the same, fifteen combinations | 15/15 installed |

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

## Second run

The same runner then covered four further event classes and the cyclic-interrupt OB in combination:

| Case | OB | Features |
| --- | --- | --- |
| obTimeOfDay | OB10, `TimeOfDay` | obCaller, obEventClass.TimeOfDay |
| obTimeDelay | OB20, `TimeDelayInterrupt` | obCaller, obEventClass.TimeDelayInterrupt |
| obHardware | OB40, `HardwareInterrupt` (attached to no channel) | obCaller, obEventClass.HardwareInterrupt |
| obDiagnostic | OB82, `DiagnosticErrorInterrupt` | obCaller, obEventClass.DiagnosticErrorInterrupt |
| obCyclicLad | OB30, LAD | ladCaller, obCaller, obEventClass.CyclicInterrupt |
| obCyclicEarlier | OB30, after the first of two networks | earlierPlacement, obCaller, obEventClass.CyclicInterrupt |
| obCyclicReuse | OB30, reusing the installed FB | obCaller, obEventClass.CyclicInterrupt, reusedComponent |

All seven passed the same gates and joined `VerifiedShapes`.

## Third run

The startup, time-of-day, time-delay, hardware and diagnostic error interrupt OBs then ran in the three combinations the
cyclic-interrupt OB had passed: as a LAD OB, after the first of two networks, and reusing the installed FB (15 cases, all
installed and joined `VerifiedShapes`).

Not covered: pairs of these combinations with an event class (LAD with an earlier placement, and so on); event classes
not named here (programming and I/O access errors, time error, rack failure, synchronous cycle and the rest); OB
attributes and event attachments beyond V21's defaults; other languages than de-DE; runtime.
