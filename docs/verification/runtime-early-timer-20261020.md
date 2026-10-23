# Runtime capture early-timer repair — 20 October 2026

The first frozen second-wave package failed in its existing runtime-session
workflow. That failure remains at
`artifacts/pw20w2s1/artifacts/package-01/runtime-session-mcp/verification.json`
with `completed:false`, 175 completed checks and two clean Host shutdowns.
The full traceback is retained in
`artifacts/pw20w2s1/artifacts/package-console.log`.

## Observed failure and distinct causes

The one-second slow capture `070ce8a3f09e44eaa1e7297b8270f296` retained
successful reads at cycles 0 and 5, followed by a cancelled attempt at cycle 10,
whose scheduled time was exactly 1,000 ms. Its final prefix retained three
attempts, eight skipped slots and `durationLimit`. The next sample-limited
capture was refused with `RuntimeSessionUnavailable`.

Cancellation during session identity verification deliberately invalidates the
owned session. The existing harness incorrectly assumed that a duration-ended
capture always left that session reusable. Explicit disconnect, a new attempt
and a fresh symbol selection are required; neither the production repair nor
the harness may revive the old session or automatically replay acquisition.

Separately, the poller treated a completed delay as proof that its scheduled
time had arrived. An early callback could therefore acquire a slot early, even
one at the exact duration boundary. The failed live receipt does not record the
precise start time of the cancelled verification. An independent deterministic
probe reproduces the scheduling defect against the unchanged frozen binary.

## Production repair and deterministic evidence

The poller now waits until the earlier of its next scheduled slot and the
duration boundary. After every wake it checks monotonic elapsed time again and
uses a positive whole-millisecond wait for any remaining fraction. A slot at or
beyond the duration is never dispatched. Waiting through the boundary preserves
duration, explicit-stop and gap accounting.

Evidence is under `artifacts/runtime-deadline-investigation-20261020/`:

- `probe-01/` retains the original failing control; `early-timer-probe/` and
  `probe-build/` retain its source and compiled harness.
- The same compiled `comparison-probe` oracle fails against the frozen binary
  in `comparison-old-01/` and passes against the repaired binary in
  `comparison-fixed-01/`. For a 1,000 ms duration, an early callback at 999 ms
  formerly produced read starts `[0,999]` with scheduled slots `[0,1000]`;
  the repair retains only the first read. For an interior 100 ms slot and
  callback at 99 ms, the old read started at 99 ms and the repaired read starts
  at 100 ms. The on-time control passes with both binaries.
- Eighteen added regression checks cover repeated early wakes, exact and
  outside-duration slots, unchanged gap bounds, stop and deadline while waiting
  again, and release of every timer. Existing on-time, late-wake, delayed
  cancellation, UTC-jump and in-flight-result tests remain enabled.
- `managed-01.log` records 343 passing physical-runtime checks, including 43
  completion-failure checks and 71 pressure checks. The suite's total can vary
  with its concurrent status-observation loop.
- `pressure-oracle-01.log` and the retained independent oracle confirm 32,768
  mixed reads, 32,800 rows, 128 skipped slots, worker/reservation recovery and
  rejection of all seven altered-evidence controls.

The focused managed build has zero warnings and errors. The tested physical
runtime assembly SHA-256 is
`6d6e1133cf58ab1755ee9cec56fbc4e07f0cd43b5b95709baeaa8829309a1f30`.
The shared old/fixed comparison harness SHA-256 is
`3c3f5e40915f531246b93c4dfa98a42a79b9863d9a07126642669bf2cae82aa3`.

The probes compile and execute the production poller/store while replacing the
clock and authenticated-session boundary with explicit fixtures. They use no
network or native device. Retention formats, original-evidence checks, session
invalidation, application capacity limits and the prohibition on automatic
replay are unchanged. These checks do not establish hard real-time behavior,
physical controller timing, system exhaustion or actual disk-full behavior.
The separately repaired loopback harness and the new combined source freeze
require their own final validation; this note does not relabel the failed
first frozen package as passing.
