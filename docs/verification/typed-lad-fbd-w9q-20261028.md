# Typed LAD/FBD native qualification ledger

Completed exact fixture attempts only. Historical binary lineages remain explicit; this ledger does not certify the current release, remote hosts, unexecuted assertions or broader instruction/type/route combinations.

Generated 2026-10-28T03:22:38.961042+00:00. 14 of 15 executed cells have all four native gates; 0 have all five gates including runtime. Failed and superseded attempts remain in the JSON ledger.

E = import/compile; X = complete XML; P = save/reopen; R = checkpoint recovery/reopen; B = independent simulator assertions. A dash means that gate was not passed in the selected attempt.

| Exact fixture | E | X | P | R | B | Executed passing assertions | Selected report |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `typed-add-int-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-add-int-lad/result.json>) |
| `typed-eq-dint-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-eq-dint-lad/result.json>) |
| `typed-eq-real-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-eq-real-lad/result.json>) |
| `typed-ge-dint-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-ge-dint-lad/result.json>) |
| `typed-ge-real-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-ge-real-lad/result.json>) |
| `typed-gt-dint-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-gt-dint-lad/result.json>) |
| `typed-gt-real-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-gt-real-lad/result.json>) |
| `typed-le-dint-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-le-dint-lad/result.json>) |
| `typed-le-real-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-le-real-lad/result.json>) |
| `typed-lt-dint-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-lt-dint-lad/result.json>) |
| `typed-lt-real-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-lt-real-lad/result.json>) |
| `typed-move-int-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-move-int-lad/result.json>) |
| `typed-ne-dint-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-ne-dint-lad/result.json>) |
| `typed-ne-real-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/w9q/typed-ne-real-lad/result.json>) |
| `typed-sub-int-lad` | — | — | — | — | — | 0 | [report](<../../artifacts/w9q/typed-sub-int-lad/result.json>) |

The report path and its SHA-256, native target, profile IDs, operation, binary hashes, diagnostics, failed-change recovery and assertion counts are recorded in `fixtures/typed-graphical/qualification.v1.json`. A newly built binary needs its own acceptance evidence; earlier results are retained as development evidence.
