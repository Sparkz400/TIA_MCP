# Independent review: PLCSIM Runtime Manager placeholder in provisioning isolation

Review of commit `e0b33ce` on branch `review/plcsim-placeholder-guard` against its parent
`8115e49`, requested in the [review request](plcsim-placeholder-guard-review-20261023.md).
Read-only: `git show` / `git diff` only. Nothing was built, run or started (no TIA, PLCSIM or
native code). 23 October 2026.

## Verdict

**Approve with changes.** The exclusion rule itself is sound. The excluded entry has an
all-zero address, so it can never be the credential target. Any change to one of its four
fields makes it count again. All five discovery points in provisioning use the exclusion,
and there are no other `GetAccessibleDevices` calls in provisioning. F1 and F2 must be
fixed before merge. F1 is a path that failed closed before this change and now passes. F2
is a baseline lifetime that is looser than the documented rule.

## Findings

### F1 — Medium, fix before merge: duplicate matches are all excluded; with truncation a hidden device can pass

- `src/TiaMcp.Openness.V21/OpennessSession.SimulationDiscoveryBaseline.cs:105-106` (`WithoutSimulationPlaceholder`)
- `src/TiaMcp.Core/SimulationDiscoveryWait.cs:72` (`WithoutBaseline`)
- Rechecks at `src/TiaMcp.Openness.V21/OpennessSession.SimulationProvision.cs:577` and `:604` (`.Take(3)`)
- Discovery points at `OpennessSession.SimulationProvision.cs:329` and `:404` (`.Take(65)`)

The baseline proves there is exactly one candidate. The exclusion, however, removes
*every* entry equal to it.

Failure scenario:

1. Before credential submission, discovery returns `[P, P, owned, foreign]`.
2. `Take(3)` reads `[P, P, owned]`.
3. Both P entries are removed, which leaves `[owned]`.
4. The recheck passes, and the foreign device is never inspected.

Before this change, `Take(2)` gave `[P, P]`, which failed.

The same applies at the `Take(65)` points: 64 copies of P plus the owned CPU leave exactly
one entry. `truncated` is reported there but never enforced.

Fix:

- Exclude at most one match. If more than one entry matches, exclude none.
- Fail when the observed list reaches its read limit and an exclusion was applied.

### F2 — Medium, fix before merge: the baseline can outlive one attempt

- `OpennessSession.SimulationProvision.cs:50` (first call to `TakeSimulationDiscoveryBaseline`)
- `OpennessSession.SimulationDiscoveryBaseline.cs:86-91`

Three problems:

- **Early refusals don't consume the baseline.** Every refusal before line 50 leaves it in
  place for the next caller in the worker. That covers a non-boolean trust argument, an
  external certificate pin, missing `_ownsPortal`, an invalid ticket, `SelectPlc` and
  `AmbiguousTarget`.
- **Nothing else invalidates it.** There is no time limit, and project close or reopen,
  `tia_start` and detach don't clear it. A same-path project reopened later still matches.
- **The process binding narrows less than it appears.** `RetainSimulationRuntimeUntilWorkerExit`
  keeps the Runtime Manager running for the whole worker lifetime. "Same PID and start
  time" is therefore close to "same worker". A baseline taken by one client can apply to
  a provisioning call made hours later.

Fix:

- Take and clear the baseline as the first statement of `SimulationProvision`.
- Add a short time limit between baseline and provisioning.
- Clear the baseline on project open/close, `tia_start` and detach.

### F3 — Low: a refused baseline is recorded as an uncertain creation

`scripts/block_test_runtime.py:281-286`

`creation_attempted = True` is set before `simulation_discovery_baseline` is called. If the
baseline refuses:

- `close()` records `instanceCreationOutcome: unknown`, `runtimeStateUncertain: true` and
  `stopped_due_to_uncertain_runtime`, although `sim_create` never ran.
- That contradicts the code comment "a failure leaves nothing owned to reconcile".

This fails safe, because the batch stops, but the evidence is wrong.

Fix: call the baseline before setting `creation_attempted`. The new test
(`scripts/test_block_test_runtime.py:126`) never calls `close()`, so it misses this.

### F4 — Low: non-Bridge process errors abort instead of excluding nothing

`OpennessSession.SimulationDiscoveryBaseline.cs:99-102`, `:115`

`AcceptedSimulationPlaceholder` catches only `BridgeException`. `Process.StartTime` can
also throw:

- `Win32Exception`, when access is denied (for example an elevated Runtime Manager)
- `InvalidOperationException`, when the process exited between lookup and read

Either exception propagates and aborts provisioning instead of meaning "nothing
excluded". That is still fail-closed, but rule 5 of the request does not describe it.
Either catch these exceptions or correct the rule text.

### F5 — Low: the "read-only" annotation hides side effects

`src/TiaMcp.Host/EngineeringTools.cs:24` (`ReadOnly = true`)

Calling the tool has three side effects:

- The first `SimulationRuntimeManager` access probably starts the Runtime Manager (not
  verified).
- It registers a shutdown for the worker's lifetime.
- It stores worker state that later loosens a provisioning safety check.

None of these touch a target, so the annotation can stay. The tool description should
state these effects.

## Test gaps

- `tests/TiaMcp.Tests/SimulationDiscoveryWaitTests.cs:39` has no case for duplicate
  placeholders at exclusion time, such as `[P, P, cpu]`, or for truncation (F1).
- The test exercises `SimulationRuntimePlaceholder.WithoutBaseline`, but the worker uses
  its own `Where` + `Matches` (`OpennessSession.SimulationDiscoveryBaseline.cs:106`). The
  code that actually runs is not covered.
- `IsCandidate` accepts both `null` and `""` as the series, but `Matches` compares exactly.
  A `null` baseline followed by `""` therefore fails closed. That is acceptable, but no
  test pins it down.
- The baseline lifetime has no tests: consumption, project/PLC mismatch, PID mismatch and
  early refusal. This logic could move into Core as pure functions so it can be tested
  offline.
- `scripts/test_block_test_runtime.py:126` does not run `close()` after a refused baseline
  (F3).
- No test covers the baseline-before-`sim_create` order in `scripts/validate_simulation.py:112-115`.

## Review checklist

1. **Is an entry that exists with zero registrations acceptable isolation evidence?** Yes.
   Zero registrations, checked before and after every snapshot under one Runtime Manager
   process, rule out a PLCSIM Advanced instance. The remaining risk is a
   non-PLCSIM-Advanced Softbus participant. Later checks still require exactly one
   S7-1500 at the documented startup address, plus address assignment and readback on
   the owned `IInstance`. This is acceptable once F1 is fixed.
2. **Is PID + start time sufficient, or should absent→present be required?** It is
   sufficient as a binding, but see F2 for how little it narrows. Record whether the
   Runtime Manager was already running before the baseline, but don't require
   absent→present. `scripts/validate_simulation.py:112` calls `sim_status` before the
   baseline, which probably starts the Runtime Manager already (not verified).
3. **Are all five discovery points covered, and does every other path fail closed?** All
   five are covered (`OpennessSession.SimulationProvision.cs:230`, `:329`, `:404`, `:577`,
   `:604`). Every other path fails closed except F1.
4. **Should the name be ignored or kept exact?** Keep it exact. The worker and its TIA
   language are the same, so exact matching only narrows the rule.
5. **Is a one-attempt, same-worker, same-project/PLC lifetime narrow enough?** Not as
   implemented; see F2.

## Not checked

- Whether `sim_create` runs in the same process as the provisioning worker.
- Whether `sim_status` initializes the Runtime Manager.
- Whether `GetAccessibleDevices` returns devices in a stable order.

None of these changes the verdict.
