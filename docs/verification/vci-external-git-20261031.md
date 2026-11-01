# VCI external Git delivery and two-sided conflicts — 31 October 2026

Scope: D01 remote delivery of Host VCI revisions (`vci_push`, `vci_fetch`) to
one Host-configured remote, and the prepared change kind `vci_conflict_resolve`
that resolves a native two-sided conflict from a reviewed three-way plan.
Offline checks, then a passed native round trip (`local.vci_git.20261031`) and
Host-only remote evidence (`local.hostonly.vci.git.20261031`), both on build `133a513`.

## What was built

- Host options `--git-remote` (a local bare repository by path or `file:///`
  URL) and `--git-allow-network-remote` (credential-free `https://` only). User
  info, queries, SSH and other transports are refused at startup
  ([`GitRemote`](../../src/TiaMcp.Core/VersionControl/GitRemote.cs)).
- Runner operations `RemoteHeadAsync`, `PushAsync` and `FetchAsync`
  ([`GitWorkspaceRunner`](../../src/TiaMcp.Core/VersionControl/GitWorkspaceRunner.cs)):
  the existing cleared environment, pinned configuration and literal arguments,
  plus only the remote's protocol. Push is a compare-and-swap fast-forward;
  fetch validates the remote tree before a fast-forward checkout. A local
  remote must pass the runner's own `[core]`-only config allowlist (bare) and
  indirection checks with no active hooks, no packed replace refs, no `.git`
  entry (Git's local lookup enters `<remote>/.git` first) and no junction or link
  anywhere below it (a `refs/heads` junction let plain Git write a pushed ref
  outside the remote), when read and again
  immediately before dispatch. Only drive-letter paths are accepted, so UNC and
  device spellings (`//server/share`, `\\?\`, `\??\`) are refused.
- Store records `vci:push:` and `vci:fetch:` with hash-linked journals; a
  fetched revision is accepted wherever a committed revision is
  ([`VciWorkspaceStore.Remote.cs`](../../src/TiaMcp.Core/VersionControl/VciWorkspaceStore.Remote.cs)).
- `vci_conflict_resolve` ([workflow](../reference/vci-package-workflow.md#two-sided-conflict-resolution)):
  the Host binds the request to one `divergentChange` row of a retained
  comparison, its workspace and its exact package bytes; the worker requires the
  reviewed remote side in the workspace and a live object export hashing to the
  reviewed native side (`contentSha256`, now reported by `vci_workspace_inspect`),
  then runs `Synchronize` in the direction of the chosen side, with checkpoint,
  compile, `Equal` readback and package readback.

## Offline checks

| Suite | Result | Covers |
| --- | --- | --- |
| `tests/TiaMcp.VersionControl.Workflow.Tests` (real Git) | 159 checks | Remote parsing incl. `//server/share`, `\\server\share`, `\\?\` and `\??\`; an inner `<remote>/.git` repository with a hook and a `gitdir:` file, each first shown live with plain Git (the hook runs, the ref lands in the inner or gitdir repository), then refused for push and fetch (the hook never runs, no Host Git call, no object read); nested `refs/heads` and `objects/pack` junctions (a plain-Git control writes through the `refs/heads` one), refused for push and fetch; packed `refs/replace`; push, CAS refusal, restart reopen; fetch fast-forward, exact blobs, unchanged parent, history; non-fast-forward push and fetch refusals; remote hook, five config redirections (separate-line and same-line `hooksPath`, `[core][include]`, `alternateRefsCommand`, another section) for push and fetch, `objects/info/alternates`, `.gitattributes`; resolution payload for all three takes with the native hash and workspace, and nine refusals |
| `tests/TiaMcp.HardwareEngineering.Tests` | 264 passed | Pure `VciResolutionRules`: package-file parsing (traversal, absolute, empty, short hash, tampered or missing content, length, repeats, non-objects, bound) and the live-mapping refusals (take/direction, missing native hash, no workspace change, foreign package, stale package, extra file, edited project object, write sets) |
| `tests/TiaMcp.VersionControl.Tests` (real Git) | 124/124 | Existing runner, durability and repository-selection checks with the extended process signature |
| `tests/TiaMcp.Library.Tests` | 98 passed | Host option parsing for `--git-remote` and `--git-allow-network-remote` |
| `scripts/test_vci_host_mcp.py` (real Host, no workers) | 80 checks | `vci_push` and `vci_fetch` through MCP against a bare remote, schema-valid outputs, Host restart |
| `scripts/test_vci_contracts.py`, `scripts/test_qualify_vci_mappings.py` | passed | Input and output contracts; the runner's other-engineer Git steps against a real bare repository |

## Native round trip

Passed on 31 October 2026: `scripts/qualify_vci_mappings.py` on a frozen Debug
build of `133a513` (`artifacts/vgd`), 202 of 202 steps, report
`artifacts/vg31c/vc_0f598d0c/qualification.json`, curated as
`local.vci_git.20261031`. Every kind (`vci_mapping_add`, `vci_synchronize`,
`vci_mapping_remove`, `vci_conflict_resolve`) has applied, persisted, refused and
restored evidence. V21 performed the directed `Synchronize` in both directions
(take=remote and take=merged `WorkspaceToProject`, take=native
`ProjectToWorkspace`) while the mapping reported both sides changed; the
export hash was stable between inspect and prepare in all four resolutions. The
merged round kept the remote value with the native header family in one
document. The bare remote's first-parent history ends with the baseline, five
other-engineer commits and the take=native and merged resolution commits, in
order. `vci_push`/`vci_fetch` are verified by the Host-only run on the same
Host build (`artifacts/w3c-hostonly-133a513a`, 80 checks).

The runner's phase C: a second workspace and SCL FC,
a baseline pushed to a run-owned bare remote, and three rounds of a remote
commit by a plain-Git clone plus a native edit of the same FC. Each round
fetches, checks the fetched package out into the workspace, detects the
conflict (`VciConflict` both ways in round 1, a `divergentChange` comparison in
every round) and resolves it `remote`, `native` and `merged`, then commits and
pushes the result over the remote commit. TIA is checked through the block
export, the workspace through package hashes and Git through the bare
repository's first-parent history. Phase B adds a fourth round restored from its
checkpoint.

## Earlier native attempts (31 October 2026, not curated)

Two attempts in owned scratch projects (CPU 1511-1 PN V4.1), each on a frozen
Debug build: `artifacts/vg-31` (build `74932b9`, 96 of 97 steps passed) and
`artifacts/vg31b` (build `45921ef`, 96 of 97 steps passed). Both stopped at the
runner's own no-replay assertion after the first resolution, so the runs are
not evidence; take=native, take=merged, the reopen and the restored round did
not run. What the first 96 steps established natively in both attempts:

- Phase A as on 28 October; the second workspace, export mapping, baseline
  commit and `vci_push` to the local bare remote; a second push without
  `expectedRemoteCommit` refused (`GitReferenceConflict`).
- Round 1: a plain-Git commit on the remote and a native header-family edit;
  `vci_fetch` with the expected remote commit (stale pin refused,
  `PreviewStale`); three captures and a `divergentChange` comparison;
  `vci_conflict_resolve` before the checkout refused (`VciResolutionNotNeeded`);
  after the checkout `vci_synchronize` refused `VciConflict` in both directions.
- `vci_conflict_resolve` take=remote prepared with the live export hash equal
  to the inspected `contentSha256` (the hash is stable between inspect and
  prepare) and applied: V21 performed `Synchronize(WorkspaceToProject)` while
  both `ProjectObjectChanged` and `WorkspaceFileChanged` were set, with
  checkpoint, compile and save; the mapping read back `Equal`, the package kept
  the remote bytes, the block export lost the native edit and the state
  differed in exactly the block.
- A second `change_apply` under a new idempotency key was refused, but by the
  shared mutation admission with `RecoveryRequired` ("The earlier engineering
  attempt cannot be replayed with this key/state."), not with the code the
  runner expected (`InvalidChangeState` in the first attempt, `IdempotencyConflict`
  in the second, the session-level codes behind that admission). The runner now
  expects `RecoveryRequired`; the third run above passed with it.

## Limits

- V21 has no project-neutral VCI export, so the native side of the three-way
  plan is the base package files with the object's inspected `contentSha256`;
  `take=native` exports the project package only when applied.
- The remote is rechecked immediately before dispatch; a writer of the remote
  after that check is outside the Host's control.
- The directed `Synchronize` with both sides changed was observed in both
  directions on one SCL FC in one scratch project; other object families,
  companion files and connect-mode mappings are not exercised.
- Network remotes have no credential route and are not qualified.
