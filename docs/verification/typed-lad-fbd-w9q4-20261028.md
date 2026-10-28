# Typed LAD/FBD native qualification ledger

Completed exact fixture attempts only. Historical binary lineages remain explicit; this ledger does not certify the current release, remote hosts, unexecuted assertions or broader instruction/type/route combinations.

Generated 2026-10-28T03:22:39.234201+00:00. 8 of 9 executed cells have all four native gates; 0 have all five gates including runtime. Failed and superseded attempts remain in the JSON ledger.

E = import/compile; X = complete XML; P = save/reopen; R = checkpoint recovery/reopen; B = independent simulator assertions. A dash means that gate was not passed in the selected attempt.

| Exact fixture | E | X | P | R | B | Executed passing assertions | Selected report |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `typed-add-dint-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q4/typed-add-dint-lad/result.json>) |
| `typed-add-real-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q4/typed-add-real-lad/result.json>) |
| `typed-move-dint-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q4/typed-move-dint-lad/result.json>) |
| `typed-move-real-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q4/typed-move-real-lad/result.json>) |
| `typed-mul-dint-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q4/typed-mul-dint-lad/result.json>) |
| `typed-mul-int-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q4/typed-mul-int-lad/result.json>) |
| `typed-mul-real-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q4/typed-mul-real-lad/result.json>) |
| `typed-rtrig-lad` | — | — | — | — | — | 0 | [report](<../../artifacts/w9q4/typed-rtrig-lad/result.json>) |
| `typed-tonr-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q4/typed-tonr-lad/result.json>) |

The report path and its SHA-256, native target, profile IDs, operation, binary hashes, diagnostics, failed-change recovery and assertion counts are recorded in `fixtures/typed-graphical/qualification.v1.json`. A newly built binary needs its own acceptance evidence; earlier results are retained as development evidence.
