# PLCSIM cycle-synchronous capture modes — 7 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.plcsim.capture-modes.20261107`](../../evidence/local.plcsim.capture-modes.20261107.json) | `qualify_plcsim_diagnostics.py` | completed |

`runtime_capture_start` with `source: plcsim` and `cycleSynchronous` puts an owned running PLCSIM Advanced V8 instance into
a SingleStep operating mode (`syncMode`, SingleStep_C by default), steps one sync point per sample and restores the
previous mode at the end. The diagnostics runner now captures 2 s in each mode on its owned CPU1511 (the DiagGate
fixture, OB1 only) and reads the operating mode back after each capture:

| Mode | Rows | End | Mode after |
| --- | --- | --- | --- |
| SingleStep_C | 40 | durationLimit | Default |
| SingleStep_P | 2 | sessionUnavailable | Default |
| SingleStep_CP | 40 | durationLimit | Default |
| SingleStep_CT | 32 | durationLimit | Default |
| SingleStep_CPT | 31 | durationLimit | Default |

SingleStep_P steps by process image partition updates. The fixture configures none besides OB1's automatic process
image, and after the first sync point no further one came within the worker's wait, so the capture ended
`sessionUnavailable`. The first run (`cm1-r01`) treated that as a failure and stopped; the runner now records it, and
still requires the restored mode. `syncMode` without `cycleSynchronous` is refused.

Not covered: a CPU with configured process image partitions (where SingleStep_P should step), rows compared with scan
counts, time-synchronised and bus modes (not offered).
