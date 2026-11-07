# PLC component OB caller combinations — 7 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.library_integrate.candidate_shapes.ob_combinations.20261107`](../../evidence/local.library_integrate.candidate_shapes.ob_combinations.20261107.json) | `qualify_library_integrate_apply.py --candidates` | 7/7 installed |

Since 2 November a fresh component could go after the last network of an existing FBD OB (`obCaller`); every other OB
combination stayed plan-only. `COMBINATIONS` gains seven OB cases, each in its own owned de-DE-only CPU 1511-1 PN V4.1
scratch. The OB fixture is Main's export renamed to `TiaMcpComponentOb` (OB123) with the peer FC's coil network rebound to
two global tags; a LAD OB takes its network from the LAD FC caller fixture, and two networks repeat it through the OB's
own export.

| Case | Features |
| --- | --- |
| obLad | ladCaller, obCaller |
| obEarlier | earlierPlacement, obCaller |
| obLadEarlier | earlierPlacement, ladCaller, obCaller |
| obReuse | obCaller, reusedComponent |
| obReuseLad | ladCaller, obCaller, reusedComponent |
| obReuseEarlier | earlierPlacement, obCaller, reusedComponent |
| obReuseLadEarlier | earlierPlacement, ladCaller, obCaller, reusedComponent |

Each preview named exactly its candidate features, and each install passed the gates of the earlier shapes: the exact
installed-object diff, the native caller insertion proof after the selected network, the call oracle, exact library
versions, a fresh reopen, a 0-error compile and `change_restore` of the initial checkpoint into a new location equal to
the baseline. `PlcComponentQualificationGate.VerifiedShapes` now lists the seven shapes, so production runs them.

Not covered: OB event classes other than program cycle, other languages than de-DE, runtime.
