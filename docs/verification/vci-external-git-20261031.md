# VCI external Git delivery and two-sided conflicts — 31 October 2026

Scope: D01 remote delivery of Host VCI revisions (`vci_push`, `vci_fetch`) to
one Host-configured remote, and the prepared change kind `vci_conflict_resolve`
that resolves a native two-sided conflict from a reviewed three-way plan.
Offline checks only; the native round trip is pending.

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
  indirection checks with no active hooks, no packed replace refs and no `.git`
  entry (Git's local lookup enters `<remote>/.git` first), when read and again
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
| `tests/TiaMcp.VersionControl.Workflow.Tests` (real Git) | 146 checks | Remote parsing incl. `//server/share`, `\\server\share`, `\\?\` and `\??\`; an inner `<remote>/.git` repository with a hook and a `gitdir:` file, each for push and fetch (the hook never runs, the gitdir target is never reached); packed `refs/replace`; push, CAS refusal, restart reopen; fetch fast-forward, exact blobs, unchanged parent, history; non-fast-forward push and fetch refusals; remote hook, five config redirections (separate-line and same-line `hooksPath`, `[core][include]`, `alternateRefsCommand`, another section) for push and fetch, `objects/info/alternates`, `.gitattributes`; resolution payload for all three takes with the native hash and workspace, and nine refusals |
| `tests/TiaMcp.HardwareEngineering.Tests` | 264 passed | Pure `VciResolutionRules`: package-file parsing (traversal, absolute, empty, short hash, tampered or missing content, length, repeats, non-objects, bound) and the live-mapping refusals (take/direction, missing native hash, no workspace change, foreign package, stale package, extra file, edited project object, write sets) |
| `tests/TiaMcp.VersionControl.Tests` (real Git) | 124/124 | Existing runner, durability and repository-selection checks with the extended process signature |
| `tests/TiaMcp.Library.Tests` | 98 passed | Host option parsing for `--git-remote` and `--git-allow-network-remote` |
| `scripts/test_vci_host_mcp.py` (real Host, no workers) | 80 checks | `vci_push` and `vci_fetch` through MCP against a bare remote, schema-valid outputs, Host restart |
| `scripts/test_vci_contracts.py`, `scripts/test_qualify_vci_mappings.py` | passed | Input and output contracts; the runner's other-engineer Git steps against a real bare repository |

## Pending native run

`scripts/qualify_vci_mappings.py` gains phase C: a second workspace and SCL FC,
a baseline pushed to a run-owned bare remote, and three rounds of a remote
commit by a plain-Git clone plus a native edit of the same FC. Each round
fetches, checks the fetched package out into the workspace, detects the
conflict (`VciConflict` both ways in round 1, a `divergentChange` comparison in
every round) and resolves it `remote`, `native` and `merged`, then commits and
pushes the result over the remote commit. TIA is checked through the block
export, the workspace through package hashes and Git through the bare
repository's first-parent history. Phase B adds a fourth round restored from its
checkpoint. Until that run passes, `vci_push`, `vci_fetch` and
`change.vci_conflict_resolve` stay unverified.

## Limits

- V21 has no project-neutral VCI export, so the native side of the three-way
  plan is the base package files with the object's inspected `contentSha256`;
  `take=native` exports the project package only when applied.
- The remote is rechecked immediately before dispatch; a writer of the remote
  after that check is outside the Host's control.
- Whether `MappedObject.Synchronize` honours a directed synchronization while
  both sides changed is the open native question of the pending run.
- Network remotes have no credential route and are not qualified.
