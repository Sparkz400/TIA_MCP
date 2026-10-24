# Frozen runtime corpus — 24 October 2026

The complete frozen corpus passed in one run: **9 families, 65 cases, 358 steps,
478 assertions**, with no retries and no stitched results
(`artifacts/block-tests/frozen-20261024-04/`, run `block_tests_20261024_135736_1258252a`,
13:57–15:25 UTC). An independent audit agrees.

## Run

`scripts/run_block_tests.py --live --provision-runtime-credential
--trust-owned-empty-cpu-certificate --timeout 900`, with the Debug Host, engineering
worker and simulator worker built from commit `7531ed1`. No `src/` difference
from that commit and one 27-file binary manifest in every case. Each case ran on:

- a fresh Host and owned TIA session;
- the reopened qualified scratch project, with exports matched to the suite
  revision before any CPU was created;
- a freshly created owned CPU1511.

Each case downloaded the program and provisioned the runtime credential through
the fresh discovery observer. The original protected card (85 files) was verified
restored before normal power-on. Then RUN, and the independently authored stimulus
and assertion sequences. All 3,740 MCP tool calls returned without error. Every
instance was unregistered, and every Host and worker exited with code 0 without
forced termination.

## Independent audit

A separate agent, given only the evidence paths and the requirements, wrote its
own read-only checker (`artifacts/audit-frozen-20261024-04/`). It first confirmed
the checker catches deliberately corrupted copies of a case. It then found:

- 9/65/358/478 derived from `fixtures/runtime_catalog.py`, each present once in
  authored order;
- all 478 verdicts recomputed from the observations, matching the recorded verdicts;
- unique instances, sessions and certificate pins per case;
- identical original and restored cards;
- clean cleanup and one binary lineage;
- no retries, blocked cases or window-edge timing decisions.

Verdict: qualifies as the complete frozen corpus pass.

## Fixes it needed (attempts 01–03, failed and kept as history)

1. **Attempt 01** stopped at branches case 5: a disposed discovery observer
   Portal exited about 300 s later, past a 60 s wait. The wait is now the measured
   360 s, and the receipt records it.
2. **Attempt 02** stopped at the last timer case: the engineering worker died
   from an `AccessViolationException` in PLCSIM's `CInstanceNet.Dispose`, right
   after a completed `simulation_download`. Its `outcomeUnknown` download admission
   is still held; the provisioning reconciliation does not cover downloads.
3. **Attempt 03** was blocked at boolean cases 5 and 6. Holding interfaces until
   worker exit (the first fix for 02) kept the old Softbus endpoint visible after
   unregistration, so the next discovery baseline refused to start. Interfaces are
   now released at provisioning's power-off, before unregistration, with a
   corrupted-state guard that records a Siemens access violation as a cleanup
   failure instead of crashing the worker.

A boolean-only diagnostic run (`diag-boolean-20261024-01`, 8/8) checked the last
fix before attempt 04. It does not count toward the corpus.

## Catalog

`sim_tags`, `sim_read`, `sim_write` and `simulation_discovery_baseline` are
verified by this record (`local.runtime.frozen.20261024`). `sim_state` stays
unverified: the run used only `powerOn` and `run` (65 each), not `powerOff` or
`stop`. `simulation_provision_scratch_credential` stays unverified: the per-case
export match covers program content, not the full project metadata AC-preserve asks for.

## Limits

- Discovery always showed one all-zero placeholder entry. It was excluded under
  the reviewed placeholder rule in every case.
- Certificate trust is the authorized first-use pin, not independent CA verification.
- Timing is host-observed only; there is no scan synchronization.
- The restored-card match is proven by what the run recorded; the cards changed
  afterwards, as expected after RUN.
- PLCSIM on this PC with the generated scratch fixtures only.
- Requested native stop during a runtime batch and project-lock release remain
  unqualified.
