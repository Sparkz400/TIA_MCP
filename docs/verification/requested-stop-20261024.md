# Requested stop during a native runtime batch — 24 October 2026

`run_block_tests.py --stop-file` stops a live batch cooperatively. Qualified on
this PC with the boolean family (8 cases), credential provisioning on.

- The stop file appeared at 18:37:34 UTC while case 2 (`truth-1`) was running.
- The runner finished that case, verified its cleanup, and observed the stop
  request at 18:38:28 UTC.
- `truth-0` and `truth-1` passed. Each instance was unregistered, and the Host
  and both workers exited 0 with verified exits and no forced termination.
- No further case was admitted. `truth-2` to `truth-7` are recorded as
  unattempted.
- The suite ends `blocked` with `statusReason: stopped_by_request`, without
  uncertain runtime state. The index records the request and when it was observed.

Evidence: `evidence/local.runtime.requested-stop.20261024.json`
(raw run `artifacts/block-tests/stop-20261024-01/` in the lane 0 worktree).

The stop is cooperative between cases: an operation in flight is never cancelled
natively. Project-lock release after normal TIA shutdown is separate and still open.
