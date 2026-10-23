# Runtime real-clock soak — 20 October 2026

The second runtime pressure increment adds an opt-in Windows qualification using the linked production Host poller/Core store and the system clock. The authenticated session remains an explicit test double. Production runtime code is unchanged. No TIA, PLCSIM, network/device access, VHD mount retry or real-volume fill occurred.

The final retained run is `artifacts/runtime-soak-wave2-20261020/run-02/`. Acquisition ran for **60.0096761 actual seconds** with four simultaneous 16-signal captures, 64 MiB of touched managed memory, 256 owned OS wait handles and bounded hashing work. It retained 10,794 loaded read outcomes and 675 explicit gap rows accounting for 1,728 skipped slots. Concurrent cancellation drained in 1,002.4327 ms and committed each blocked read's `RuntimeCancelled` outcome. A refused fifth worker created no intent; repeated stopped intents neither replayed acquisition nor changed original committed files. A fresh capture then acquired 16 additional outcomes.

All 22 managed soak checks passed. The 138 resource observations recorded these values:

| Measurement | Baseline | Load first held | After resource disposal |
| --- | ---: | ---: | ---: |
| Managed bytes | 1,011,704 | 68,153,488 | 3,931,648 |
| Process handles | 224 | 501 | 284 |
| Working set bytes | 37,281,792 | 105,107,456 | 203,014,144 |
| Private bytes | 14,360,576 | 81,870,848 | 170,258,432 |

Peak sampled private bytes were 198,787,072 and peak sampled handles were 549. The final handle count remained within the stated baseline-plus-64 allowance; the allocation became reclaimable. The process working set/private bytes did not return to baseline. These measurements support the explicit bounded-resource checks, not a claim of zero growth, a long-term leak qualification or OS exhaustion. The hashing loop completed 2,293 iterations over 1 MiB slices.

The owned failure process exited with code 73 via `Environment.Exit` before returning read 177, bypassing disposal. Its committed prefix contained 143 reads and eight gaps; 33 completed read outcomes remained uncommitted. A separate recovery process reopened the exact prefix as `interrupted` with unknown uncommitted loss. Every original plan/head/segment hash remained unchanged. Repeating the old intent acquired zero reads; a new capture acquired exactly 16 outcomes. All three original child process handles were waited to exit, with codes **0 / 73 / 0** and no forced termination.

The independent Python oracle verified original SHA-256 chains, cumulative row accounting, exact per-cycle selection order, gaps, real OS read timestamps, mixed large integer/scientific/structured/string values, timeout/transport/provider/cancellation distinctions and exact exports. It also checked resource measurements and crash/recovery receipts. Eight mutation controls were rejected: wrong signal, timeout-to-success, large-integer change, omitted gap with renumbered rows, replayed row with renumbered rows, wrong scheduled time, fabricated elapsed duration and missing observed handle pressure.

`inputs-before.json` and `verification.json` bind the relevant source files and adjacent runtime DLL/JSON/EXE files before execution and confirm that they stayed unchanged through the run. The tested assembly SHA-256 is `bb38df6660cadf05dfcd2bfb4214a81301732afad2ed34fcb032d7bdb6d0e83e`. The final build, `build-02.log`, passed with zero warnings/errors.

Earlier evidence remains intact:

- `build-01.log` and `trial-01.log`: first build and successful 30-second trial, 22 checks and 5,391 outcomes.
- `trial-01-independent.log`: a failed initial oracle assumption that every requested 2 ms Windows timer wait must measure at least 1 ms. Actual short waits included 0.2601 ms; the requested 145 ms waits measured at least 143.9097 ms. This was an oracle assumption failure, not a production capture defect.
- `trial-01-independent-02.log`: corrected independent checks and all eight mutation controls pass. The verifier requires positive observed durations, actual slow reads exceeding the 100 ms poll interval, and original timestamps spanning the requested acquisition duration.
- `run-01/`: successful first 60-second execution, with 10,857 total outcomes including the 16-read healthy capture. `run-01-recheck.json` additionally verifies its healthy export using the final oracle. `run-02/` exercises the final before/after source/runtime input binding.

Integration commands and limits are documented in [runtime soak qualification](../reference/runtime-soak-qualification.md). The runner is deliberately opt-in; it does not add 60 seconds to every unit run. Combined frozen-suite qualification is recorded by the coordinator separately.

Actual disk-full remains blocked by the previously reproduced VHD mount privilege failure (`0x80070522`). Longer soaks, system-wide memory/handle exhaustion, real transport-worker failures, device timing/identity, independent online program association and broader C03/D16 completion remain open.
