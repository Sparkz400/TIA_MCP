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
  remote with active hooks, `hooksPath` or includes is refused before dispatch.
- Store records `vci:push:` and `vci:fetch:` with hash-linked journals; a
  fetched revision is accepted wherever a committed revision is
  ([`VciWorkspaceStore.Remote.cs`](../../src/TiaMcp.Core/VersionControl/VciWorkspaceStore.Remote.cs)).
- `vci_conflict_resolve` ([workflow](../reference/vci-package-workflow.md#two-sided-conflict-resolution)):
  the Host binds the request to one `divergentChange` row of a retained
  comparison and its exact package bytes; the worker requires the reviewed
  remote side in the workspace and runs `Synchronize` in the direction of the
  chosen side, with checkpoint, compile, `Equal` readback and package readback.

## Offline checks

| Suite | Result | Covers |
| --- | --- | --- |
| `tests/TiaMcp.VersionControl.Workflow.Tests` (real Git) | 123 checks | Remote parsing; push, CAS refusal, restart reopen; fetch fast-forward, exact blobs, unchanged parent, history; non-fast-forward push and fetch refusals; hook, `hooksPath` and `.gitattributes` refusals; resolution payload for all three takes and eight refusals |
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
  plan is supplied evidence (the object's native export hash on the base
  package files); `take=native` exports the project only when applied.
- Whether `MappedObject.Synchronize` honours a directed synchronization while
  both sides changed is the open native question of the pending run.
- Network remotes have no credential route and are not qualified.
