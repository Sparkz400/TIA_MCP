# Draft: PLCSIM Runtime Manager placeholder in provisioning isolation — review request

**Status: merged into `main` on 23 October 2026** after three independent review passes
(approve with changes, approve, approve with no new findings). Requested by the user on
23 October 2026.

## Problem

`simulation_provision_scratch_credential` requires two consecutive empty PN/IE / PLCSIM
discovery snapshots before it starts the owned CPU. On PCNICO every attempt fails
(`SimulationProvisionIsolationRequired`) because discovery always returns one
`Accessible device` at `00-00-00-00-00-00`. The [discovery probe](native-stop-20261023.md#discovery-probe--the-entry-comes-with-the-runtime-manager)
shows that entry appears as soon as the PLCSIM Advanced V8 Runtime Manager provides the
Softbus route, with **zero registered instances**, and stays unchanged with and after one
owned OFF instance. The VM saw the same entry intermittently.

## Rule

1. New read-only tool `simulation_discovery_baseline`, called by the runners **before**
   `sim_create`. In the worker that will provision, it requires a worker-owned scratch PLC
   that is offline, Runtime Manager V8, network mode already Softbus (never changed), and
   **zero registered instances**, checked before and after every snapshot.
2. It needs two consecutive identical snapshots that show either nothing or exactly one
   entry with address `00-00-00-00-00-00`, MAC `00-00-00-00-00-00` and empty device series
   (name is recorded and must match exactly later; it is localized). Anything else refuses.
3. It records that entry and the Runtime Manager's process ID and start time. The
   process must be the same before and after the baseline.
4. The next `simulation_provision_scratch_credential` in the worker **consumes** the
   baseline as its first action, so any attempt (even one refused early) uses it up. It
   applies only within five minutes and only to the same `TiaPortal` and `Project` objects
   (so `tia_start`, detach or a reopen invalidates it) and the same project and PLC path.
5. At each discovery point (pre-start absence, post-startup endpoint, address discovery,
   both rechecks before credential submission) at most **one** device equal to the recorded
   entry in all four fields is excluded. Two or more matches exclude nothing, and a read
   that hit its limit while an exclusion would apply is refused. Exclusion also requires
   exactly one Runtime Manager process with the recorded ID and start time; any failure to
   read that process means nothing is excluded. Otherwise the previous behaviour applies
   unchanged.
6. Every excluded entry is counted in the report (`baselinePlaceholderExcluded`), the
   raw device lists stay in the report, and the pre-start status reads
   `baselinePlaceholderOnly`. The baseline evidence is copied into the provisioning report
   (`preRegistrationDiscoveryBaseline`).

## What this could wrongly accept (please challenge)

- A real foreign Softbus participant that presents itself with an all-zero address and
  MAC and no device series, already present before registration under the same Runtime
  Manager process. The probe could not attribute the entry to any process other than the
  Runtime Manager (state A had no route; only that process was added in state B), but it
  does not prove what the entry is internally.
- A foreign instance registered and removed between baseline and `sim_create`: the
  baseline requires zero registrations at the time, and provisioning still verifies that
  only the owned instance is registered.
- Address/MAC/series comparisons are exact string matches; a localized name is matched,
  not interpreted.

## Changes

- `src/TiaMcp.Core/SimulationDiscoveryWait.cs`: `SimulationRuntimePlaceholder` (pure).
- `src/TiaMcp.Openness.V21/OpennessSession.SimulationDiscoveryBaseline.cs`: baseline
  operation and helpers.
- `src/TiaMcp.Openness.V21/OpennessSession.SimulationProvision.cs`: consume the
  baseline; exact exclusion at five discovery points.
- `src/TiaMcp.Host/EngineeringTools.cs`, `OpennessSession.cs`: tool, dispatch, capability.
- `scripts/block_test_runtime.py`, `scripts/run_block_tests.py`,
  `scripts/validate_simulation.py`: call the baseline before `sim_create` when
  provisioning, and require the tool.
- Contracts: feature catalog, API registry obligation (L03), source bindings. The
  bindings pin `Directory.Build.props` at its committed content because the working tree
  carries an unrelated uncommitted edit.

## Offline checks

- `tests/TiaMcp.Tests`: 244/244, including the new
  "Runtime Manager placeholder exclusion is exact and narrow" (near misses on address,
  MAC, series and name; two placeholders; only a placeholder can be a baseline).
- `scripts/test_block_test_runtime.py` 36/36, including baseline-before-`sim_create`
  and "a refused baseline creates nothing"; case-session, uncertainty, stage and stop
  tests pass.
- `scripts/test_contracts.py`: 262 tools, 56 mutation kinds, 336 features.

## Live check — the rule worked; the run stopped later for unrelated reasons

`artifacts/block-tests/placeholder-guard-live-20261023-01`, `--live
--provision-runtime-credential --trust-owned-empty-cpu-certificate --fixture runtime-boolean`,
Debug Host/worker from this branch, under the empty guard registry lock.

- `truth-0`: `simulation_discovery_baseline` ran before `sim_create` and recorded
  `classification: placeholder` from two identical snapshots. Provisioning's pre-start
  check then passed with `status: baselinePlaceholderOnly`, `baselinePlaceholderExcluded: 1`,
  two consecutive snapshots, 595 ms.
- The next stage, `stageProtectedCardWhileOff`, failed: moving the downloaded
  `SIMATIC_MC` to owned staging threw `System.IO.IOException` HRESULT `0x80070005`
  (access denied) on the first attempt. No CPU start, credential access or submission
  happened. Cleanup powered off, restored the original card (revision `e521be71…`) and
  released the interface; the case is `blocked` with no runtime uncertainty.
- `truth-1`: `simulation_download` was refused with `ResourceBusy` because the failed
  provisioning attempt's mutation admission is unreconciled, as designed. The runner then
  stopped with `stopped_due_to_uncertain_runtime`. Six cases were not attempted.

The placeholder rule therefore cleared exactly the entry it was meant to and nothing else.
Post-startup discovery, address discovery and the pre-credential rechecks were not reached,
so their exclusion paths are untested natively. Two pre-existing blockers now stand in
front of live provisioning on this PC: the access-denied card move and reconciliation of
the failed attempt's admission.

## Independent review and v2 changes

The [independent review](plcsim-placeholder-guard-independent-review-20261023.md) of
`e0b33ce` returned **approve with changes**. Branch `review/plcsim-placeholder-guard-v2`
addresses every finding:

| Finding | Change |
|---|---|
| F1 duplicates/truncation could pass | `SimulationRuntimePlaceholder.SingleMatchIndex` excludes at most one exact match and none for two or more; the worker refuses a truncated read once an exclusion would apply. The worker now calls this Core function, so the tested code is the code that runs. |
| F2 baseline outlived one attempt | Consumed first in provisioning; `BaselineApplies` adds a 300 s lifetime and same-session/project/PLC binding by object identity. |
| F3 runner misreported a refused baseline | The baseline runs before `creation_attempted`; a test now calls `close()` and asserts no creation attempt and no runtime uncertainty. |
| F4 process-read errors aborted provisioning | `Win32Exception` and `InvalidOperationException` now mean "nothing excluded", as the rule states. |
| F5 tool labelled read-only | Kept `ReadOnly` (no engineering data changes) and documented the Runtime Manager start and the stored baseline in the tool and capability descriptions. |

New tests: `[P, P, cpu]`, `[cpu, P]`, null vs empty series, lifetime boundaries and
binding flags, `close()` after a refused baseline, and the baseline-before-`sim_create`
order in `validate_simulation.py`. Not changed: name matching stays exact (review answer 4).

### Second pass and follow-up

The second pass on `e126072` returned **approve**, with R1 (medium, functional) and R2/R3 (low):

- **R1 confirmed natively, then fixed.** Live run `card-move-capture-20261023-01` showed the
  baseline was never applied: Openness returns a new `Project` object per access, so the
  `ReferenceEquals` check failed and provisioning behaved exactly like `main` (safe, not
  working). The project is now bound by path; the session binding stays on the worker's own
  `_portal` field, which changes on `tia_start`, attach and detach. Run
  `card-move-capture-20261023-02` then applied it (`baselinePlaceholderOnly`, baseline copied
  into the report).
- **R2:** the five-minute age is now checked at every exclusion, not only at the start.
- **R3:** a new baseline call first discards any earlier one, so a refused call leaves none.
- The truncation decision moved into Core (`SimulationRuntimePlaceholder.Decide`) and is tested.

Offline after these changes: 245/245 core tests, runner suites, 262-tool contracts, bindings valid.

## Live check 2 — past the card move, stopped at address discovery

`artifacts/block-tests/card-move-capture-20261023-02` (same flags as the first live check):

- Pre-start isolation passed with the baseline. All three card moves (stage, and the two
  restore moves) succeeded on the first attempt this time, so the earlier access-denied
  failure is intermittent. A handle capture armed for `moveThrew` never triggered; its cause
  is not identified. Outside reproductions (`artifacts/card-lock-probe-20261023-03`…`07`) never
  saw a holder or a lasting denial; only an immediate reverse rename needed one 0.25 s retry.
- The empty CPU started (`emptyStartupVerified: true`, controller address `192.168.0.1`), but in
  36 address-discovery samples over 10 s, PN/IE / PLCSIM discovery showed only the excluded
  placeholder and never the CPU. Provisioning refused with `SimulationProvisionTargetAmbiguous`
  at `discoverExactRuntimeAddress`; no credential was opened. Cleanup powered off, restored the
  original card and released the interface.
- `truth-1` was then blocked by the failed attempt's unreconciled admission (`ResourceBusy`).

Next blocker on this PC: the owned PLCSIM Advanced CPU is not visible to TIA's PLCSIM
discovery after startup. A failed provisioning attempt always leaves its admission
`outcomeUnknown` (the Host marks any native error that way) and no trusted reconciliation
exists for provisioning, so the rest of that suite's cases are blocked; new runs use fresh
scratch projects and are unaffected.

## Review checklist

- [ ] Is excluding an entry that exists with zero registrations acceptable isolation evidence?
- [ ] Is binding to Runtime Manager PID + start time sufficient, or should the route also be
      required absent→present across the Runtime Manager start?
- [ ] Are all five discovery points covered, and does every other path still fail closed?
- [ ] Should the name be ignored (localization) or kept exact as drafted?
- [ ] Is a one-attempt, same-worker, same-project/PLC lifetime narrow enough?
