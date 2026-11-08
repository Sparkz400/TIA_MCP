# Time-synchronised PLCSIM captures — 8 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.plcsim.timespan-capture.20261108`](../../evidence/local.plcsim.timespan-capture.20261108.json) | `qualify_plcsim_diagnostics.py` | passed |

`runtime_capture` with `source: plcsim` and `cycleSynchronous` already stepped the single-step modes, which
[ran natively on 7 November](../reference/runtime-captures.md). It now also takes `TimespanSynchronized_C`, `_P` and
`_CP` with `spanMilliseconds` (1–60000, required for these modes and refused for the others). Each sample calls
`StartProcessing` for that much CPU time and reads every signal while the CPU holds at the next synchronization point.
Entering the mode takes one minimal span when PLCSIM reports no synchronization point, as virtual time does.

On an owned Softbus CPU 1511 whose only cyclic OB is OB1 (2 s windows, 100 ms interval, 10 ms span):

| Mode | Rows | End | Operating mode afterwards |
| --- | --- | --- | --- |
| TimespanSynchronized_C | 40 | duration limit | Default |
| TimespanSynchronized_CP | 40 | duration limit | Default |
| TimespanSynchronized_P | 2 | sessionUnavailable (no process-image partition) | Default |
| SingleStep_C / CP / CT / CPT (rerun) | 40 / 40 / 30 / 31 | duration limit | Default |
| SingleStep_P (rerun) | 2 | sessionUnavailable | Default |

A time-synchronised mode without `spanMilliseconds` was refused before any capture intent existed.

Not covered: other spans and scale factors, a fixture with a process-image partition, other CPUs and TCP/IP instances.
