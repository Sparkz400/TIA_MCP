# Typed LAD/FBD native qualification ledger

Completed exact fixture attempts only. Historical binary lineages remain explicit; this ledger does not certify the current release, remote hosts, unexecuted assertions or broader instruction/type/route combinations.

Generated 2026-10-26T14:45:32.086356+00:00. 4 of 4 executed cells have all four native gates; 0 have all five gates including runtime. Failed and superseded attempts remain in the JSON ledger.

E = import/compile; X = complete XML; P = save/reopen; R = checkpoint recovery/reopen; B = independent simulator assertions. A dash means that gate was not passed in the selected attempt.

| Exact fixture | E | X | P | R | B | Executed passing assertions | Selected report |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `typed-namespace-dboperand-fbd` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/rt5/typed-namespace-dboperand-fbd/result.json>) |
| `typed-namespace-dboperand-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/rt5/typed-namespace-dboperand-lad/result.json>) |
| `typed-unit-dboperand-fbd` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/rt5/typed-unit-dboperand-fbd/result.json>) |
| `typed-unit-dboperand-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/rt5/typed-unit-dboperand-lad/result.json>) |

The report path and its SHA-256, native target, profile IDs, operation, binary hashes, diagnostics, failed-change recovery and assertion counts are recorded in `fixtures/typed-graphical/qualification.v1.json`. A newly built binary needs its own acceptance evidence; earlier results are retained as development evidence.
