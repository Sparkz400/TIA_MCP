# Parallel completion plan — 24 October 2026

Goal: finish every open release-checklist and TODO item, as fast as possible, in
parallel lanes that do not interfere. 1.0 ships only when all of them are done.
Commits, PRs and merges are automatic checkpoints.

## Ground rules (every lane)

1. **One lane, one worktree, one branch.** Each lane runs in its own session and
   git worktree, branched from the current `main`. Never edit another lane's files
   or the shared checkout `D:\Entwicklung\TIA_MCP-public`.
2. **Implementation first; test only when necessary.**
   - While implementing, run only the unit tests for the code you touched.
   - Run nothing else locally before opening a PR; hosted CI runs the full
     Python, managed, package and Explorer suites on every PR into `main`.
   - Native runs are qualification evidence. Run each scenario once; rerun only
     after a fix for its failure. Never rerun a passing native scenario.
3. **One native lane at a time on this PC.** TIA Portal and PLCSIM runs take the
   machine-wide lock `D:\Entwicklung\native-lane.lock` through
   `scripts/native_lane.py acquire|release`, which waits and creates the lock
   exclusively. Hold it for one batch, then release it. Implementation never needs it.
4. **Shared files belong to the integrator.** Lanes do not edit
   `contracts/feature-catalog.v1.json`, `contracts/api-coverage/*`,
   `docs/STATUS.md`, `docs/TODO.md` or
   `docs/plans/1.0-release-checklist-20261013.md`. Each lane:
   - writes its new or changed feature rows and evidence IDs into its PR description;
   - curates evidence with `scripts/curate_evidence.py` into `evidence/` (new files only).

   The integrator applies catalog rows, bindings and status docs after each merge.
5. **Avoid hot-file conflicts.** Put new code in new partial or class files. In
   shared dispatch files (`OpennessSession.cs` case list, Host tool files,
   `WatchTableDispatcher.cs` switch), add single lines only.
6. **Checkpoint = merged PR.** When a coherent slice is implemented (and its
   native evidence exists, if any), the lane:
   - commits;
   - syncs with `main`;
   - pushes, and opens a PR into `main` with what changed, the evidence and the
     catalog rows;
   - reports the PR to the integrator.

   Commit locally at every working step; do not wait for perfection. The
   integrator merges with `gh pr merge --merge` when CI is green and the diff stays
   inside the lane's scope. A red CI goes back to the lane once; a second failure
   is reported to the user.
7. **Blocked means reported, not worked around.** If an item needs something only
   the user can provide (license, VM, download approval, target hardware), the lane
   records it in its PR and moves on.

## Lanes

| Lane | Scope (owns) | First checkpoint | Native need |
| --- | --- | --- | --- |
| **0 Integrator** (this session) | Merge #9 then #10; `scripts/native_lane.py`; catalog/bindings/status docs; native queue; runtime leftovers in provisioning/simulation code | #9, #10 and the lock script merged | Yes, runtime leftovers |
| **A Typed LAD/FBD** | `OpennessSession.Typed*`, typed builders, `fixtures/typed-graphical`, typed qualifiers | Candidate lineage and builder engineering for the active profiles | Yes, batched |
| **B PLC components** | Component install/recovery code (`PlcComponent*`, `ComponentWindow*`), component qualifiers | Fresh FC/DB/call/bindings install with first-checkpoint recovery | Yes |
| **C Libraries** | `OpennessSession.Library*`, library Core/tests, library qualifiers | Behavior-changing and breaking release qualification | Yes |
| **D HMI** | `OpennessSession.Hmi*`, HMI Core/tests, HMI qualifiers | Production-bridge archive retrieval and read-only inspection | Yes; some families blocked on targets/licenses |
| **E Failure handling** | Admission/reconciliation (`WatchTableDispatcher.Admission`, `EngineeringMutationAdmissionStore`, recovery adapters), interruption qualifiers | Reconciliation adapters for `simulation_download` and `change_apply`/`change_restore` | Yes, interruption runs |
| **F Remote I/O + Explorer** | Hardware/remote-I/O code and qualifiers; `ui/project-explorer`, project-model/review code | Configured-module lifecycle; relationship provider scope | Remote I/O yes; Explorer mostly no |

Lane 0's runtime leftovers:
- requested stop during a runtime batch, with no next-case admission;
- ~~the all-zero placeholder entry~~ (identified: [PLCSIM Softbus endpoint](../verification/plcsim-placeholder-identity-20261024.md));
- the post-RUN card `.info` inventory with full project/card preservation;
- `sim_state` `stop`/`powerOff` coverage.

Lanes A–F run in parallel. Lane E changes shared admission code, so other lanes
consume it only after it is merged, and never edit it themselves.

## External blockers (user)

- Vendor packages (PracticalSeries at its pinned commit, 124 MB DMC): download
  approval. This unblocks the vendor intake review and the two vendor distributions.
- VMware guest: VM deployment qualification, the open dispatch ticket, transfer checks.
- HMI targets and licenses (e.g. RT Professional), plus a clean Classic fixture
  with a valid start screen.

## Last phase (after lanes A–F and the blockers are done)

The integrator, alone:
1. Freeze the support matrix and catalog.
2. Compatibility/scale measurements still open.
3. Release candidate source and input hashes.
4. One full regression and one final frozen runtime run on the candidate.
5. Bundle verification and deployment.
6. Release notes, tag.

These are the only intended repetitions: one regression and one runtime run on
the final candidate.
