# PLC component OB event classes, second set: runs of 10 November 2026

Five further OB event classes joined the six of [8 November](component-ob-event-classes-20261108.md): status (OB55),
update (OB56), profile (OB57), pull or plug of modules (OB83) and rack or station failure (OB86). The runs were
`scripts/qualify_library_integrate_apply.py` on PCNico, each case in its own owned de-DE-only CPU 1511-1 PN V4.1
scratch, from frozen Debug builds (`git archive`; Host and Openness worker).

| Evidence | Mode | Frozen build | Result |
| --- | --- | --- | --- |
| [`local.library_integrate.candidate_shapes.ob_event_classes_3.20261110`](../../evidence/local.library_integrate.candidate_shapes.ob_event_classes_3.20261110.json) | `--candidates`, the five classes as FBD OBs | `7e50a9e1` (`artifacts/nc7`) | 5/5 installed (`ob5/i4a168c`) |
| [`local.library_integrate.candidate_shapes.ob_combinations_4.20261110`](../../evidence/local.library_integrate.candidate_shapes.ob_combinations_4.20261110.json) | `--candidates`, each class with every combination of LAD, an earlier placement and reuse | `7e50a9e1` (`artifacts/nc7`) | 35/35 installed (`ob8/i6ab092`) |
| [`local.library_integrate.combined_apply.regression.20261110`](../../evidence/local.library_integrate.combined_apply.regression.20261110.json) | the verified flow on the normal Host; then `--candidates --case obStatus --case obRackFailureReuseLadEarlier` | `90f63137` (`artifacts/nc8`) | both runs passed (`ob9/id4c59d`, `ob10/i32f66a`) |

Each case's preview named exactly `obCaller`, its `obEventClass.<class>` and the combination's features. Each install
passed the exact installed-object diff, the native caller insertion proof, the call oracle, a fresh reopen, a 0-error
compile and `change_restore` of the initial checkpoint into a new location. All 40 shapes joined `VerifiedShapes`
(`90f63137`). On that build the verified flow passed on the normal Host, and the two rerun shapes installed with
previews that named no candidate feature. That rerun used the qualification Host, because the runner has no mode
that installs an OB combination without the flag; the other 38 shapes have their candidate runs only.

The fixtures are Main's envelope with the event class, the OB number and the informative inputs replaced, as V21
imported and ran them in [`qualify_plcsim_gaps.py`](plcsim-openness-gaps-20261029.md): OB55 to OB57 have no start
information, OB83 and OB86 have `LADDR`, `Event_Class` and `Fault_ID`.

## Attempts that did not count

- `ob5/i87a77b` and `ob5/i2c2140` stopped in the author step before any case ran: runners that send `tools/call`
  themselves read a journal that the change tools' summary result (default since 9 November) no longer carries. The
  shared test client now asks for full change views.
- `ob6/i84c84a` ran the 35 combinations and installed 34. `obProfileReuseLadEarlier` passed both applies and failed
  in the closing `change_restore`: TIA's `Retrieve` reported that access to its new destination folder
  (`changes\lr_c5f39d5de6\before`) "was denied by the system". The case alone then passed on the same build
  (`ob7/i71b1ad`), and all 35 were repeated for the curated report. The denial was not reproduced or explained; the
  other 75 restores of the day went through.

## Not covered

- Event classes without a run: programming error, I/O access error, time error, synchronous cycle, the motion OBs
  and the rest stay plan-only.
- An event-class OB with a multi-instance or an embedding parent.
- OB attributes and event attachments beyond V21's defaults (no status, update, profile, pull/plug or rack-failure
  event source is configured); other languages than de-DE; runtime (no OB ran).
