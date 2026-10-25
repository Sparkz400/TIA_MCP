# Typed LAD/FBD native qualification ledger

Completed exact fixture attempts only. Historical binary lineages remain explicit; this ledger does not certify the current release, remote hosts, unexecuted assertions or broader instruction/type/route combinations.

Generated 2026-10-25T13:02:14.101838+00:00. 2 of 2 executed cells have all four native gates; 0 have all five gates including runtime. Failed and superseded attempts remain in the JSON ledger.

E = import/compile; X = complete XML; P = save/reopen; R = checkpoint recovery/reopen; B = independent simulator assertions. A dash means that gate was not passed in the selected attempt.

| Exact fixture | E | X | P | R | B | Executed passing assertions | Selected report |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `typed-insert-fbd` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/ins1/typed-insert-fbd/typed-insert-fbd/result.json>) |
| `typed-insert-lad` | pass | pass | pass | pass | — | 0 | [report](<../../artifacts/ins1/typed-insert-lad/typed-insert-lad/result.json>) |

The report path and its SHA-256, native target, profile IDs, operation, binary hashes, diagnostics, failed-change recovery and assertion counts are recorded in `fixtures/typed-graphical/qualification.v1.json`. A newly built binary needs its own acceptance evidence; earlier results are retained as development evidence.
