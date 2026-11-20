# PLCSIM process image partition fixture

An owned CPU 1511-1 PN scratch program for the two cycle-synchronous capture modes that stalled on the
OB1-only fixture, `SingleStep_P` and `TimespanSynchronized_P`
([record](../../docs/verification/plcsim-timespan-capture-20261108.md)). OB30 (`PipCyclic`, cyclic interrupt,
100 ms) is assigned to process image partition 1 and counts its starts in `PipDb.Ob30Count`; OB1 counts its scans
in `PipDb.Ob1Count`.

`fixture.json` is the definition: the build steps, the two signals, what each mode should show and what is
unverified. The sources are authored text, not native exports. Run
[`scripts/qualify_plcsim_process_image.py`](../../scripts/qualify_plcsim_process_image.py) with the other native
runners' arguments to build and test it; that run has not happened yet.

Not covered: I/O modules in the partition, isochronous OBs and `SingleStep_Bus`.
