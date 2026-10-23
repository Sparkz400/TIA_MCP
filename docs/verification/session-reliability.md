# Session and recovery qualification

`scripts/qualify_reliability.py` exercises the real packaged stdio MCP host against a deliberately faulty named-pipe worker. The default run makes no Siemens API calls. Its dependency-free worker lives in `scripts/native/ReliabilityWorker`; it is a test helper, not a production worker or a replacement for native qualification.

The harness distinguishes an acknowledged operation failure from an interrupted call whose outcome is unknown. It deliberately commits a small durable test journal **before** breaking the connection or losing the response. Every interrupted mutation must appear exactly once in the worker event log. Further calls on the same bridge must return `BridgeFaulted`, without restarting the worker or dispatching the mutation again. A separate fresh MCP host then reads the persisted journal without reapplying the change.

## Host boundary matrix

| Case | Expected result |
| --- | --- |
| Worker exits before connecting | `WorkerExited`, then terminal `BridgeFaulted`; no mutation dispatched |
| Worker closes after committing | `WorkerDisconnected`; the durable outcome remains committed |
| Worker sends an incomplete response then closes | `WorkerDisconnected`; no replay |
| Wrong response identity | `ProtocolMismatch`; no replay |
| Malformed response | `InvalidResponse`; no replay |
| Mutation committed, response stalls | `TimeoutOutcomeUnknown`; same session is terminal |
| Helper-owned worker forcibly exits after commit | `WorkerDisconnected`; fresh-host journal inspection succeeds |
| Helper-owned MCP host forcibly exits after commit | Worker observes EOF; fresh-host journal inspection succeeds |
| MCP request cancellation after commit | Subsequent request sees terminal `BridgeFaulted`; cancelled mutation appears once |
| Worker rejects a block operation normally | `ProtectedBlock`; the session remains usable |
| MCP stdin closes normally | Host and worker release their connection cleanly |

MCP cancellation can suppress delivery of the original cancelled response. The cancellation case therefore proves the observable subsequent terminal state and durable single-dispatch evidence, rather than assuming a cancelled response must be delivered. The existing C# `BridgeClient` tests additionally verify the exact `CancelledOutcomeUnknown` exception, active/queued cancellation, serialized requests, bounded framing and protocol validation.

## Running without TIA

From the repository root, in an ordinary Windows user session:

```powershell
python scripts/test_reliability.py
python scripts/qualify_reliability.py --build-worker --host "C:\bundle\host\TiaMcp.Host.exe" --report artifacts/reliability.json
```

`--build-worker` builds only the isolated test helper. It does not build or modify shared host/worker binaries. Without `--host`, the runner uses the recorded v0.3 release bundle in repository artifacts. Use `--modes timeout kill-worker kill-host` to rerun only affected cases. The host must be allowed to create current-user named pipes; a restricted application-container token can prevent the helper from connecting even on the same account. Such a connection failure is a failed qualification run, not a successful no-replay test.

## Native owned-copy qualification

The optional native phase requires an existing scratch-project change journal under repository `artifacts/changes`. It verifies the archive SHA-256 recorded in that journal before any TIA operation. The source journal and archive remain unchanged.

```powershell
python scripts/qualify_reliability.py --live `
  --checkpoint-journal artifacts/changes/change-<id>/journal.json `
  --host "C:\bundle\host\TiaMcp.Host.exe" `
  --native-worker "C:\bundle\workers\openness\TiaMcp.Openness.V21.exe" `
  --report artifacts/reliability-native.json
```

The native phase:

1. Reads the recorded change journal through a fresh MCP host without an attached project.
2. Starts a new worker-owned TIA instance and verifies rejection of an incorrect archive hash.
3. Retrieves the checkpoint into a new unique directory, preserving the source archive.
4. Attaches a second MCP host to that newly owned instance, reads a fixture block, and detaches. The owning session and the block content must remain intact.
5. Attaches the observer again and terminates only that observer MCP host. The owning TIA session and block content must still remain intact.
6. Detaches the owner, starts a fresh MCP host and owned TIA instance, and retrieves the original checkpoint into another new directory. The fixture block must have the same semantic XML digest.

`--block-name` selects the known fixture block (default `TiaMcp_IsEnabled`). Native XML comparison excludes only the export document metadata; it retains the program payload. This is a fixture-level check, not proof that every proprietary project field is identical. No live PLC or simulator is contacted. No TIA process is terminated, and no `TiaPortalProcess` descriptor is disposed.

The harness uses the `Popen` handle to terminate only MCP hosts it created. Worker interruption first opens a process handle and verifies that its executable is the exact isolated `ReliabilityWorker.exe` before termination. It never performs a process-name or process-tree kill. Native fault injection during a real Siemens mutation is deliberately outside this harness: evidence for ambiguous outcomes comes from the host boundary tests, while actual archive recovery and attachment preservation are qualified separately.

## Evidence and limits

Each run retains a top-level JSON report, individual case results, worker event logs and durable fake journals under a new `artifacts/reliability-*` directory. Native copies use separate short `artifacts/rl_*` roots to respect Siemens project-directory length limits. Nothing is automatically deleted after a run.

`completed: true` means all requested cases passed. The report keeps native qualification explicitly `not_run` unless `--live` actually succeeds. Fake-worker results do not certify TIA crash behavior, archive completeness, project recovery or remote-network behavior. Native results are limited to the recorded fixture and installed TIA version. The suite is reusable on a VM once TIA and the package prerequisites are installed there.

### Recorded local result — 8 October 2026

`artifacts/reliability-native-first.json` records **11/11 actual MCP host fault cases and all 10 recorded native lifecycle/recovery steps passing** against the stable `tia-mcp-v21_20261008_064849_e96b6805` bundle. Both owned checkpoint retrievals and the observer attachment test completed; the source archive SHA-256 remained unchanged. The separate pure guard suite has **15 passing tests**.

The preceding `artifacts/reliability-first.json` is retained as failed environment evidence: the restricted application-container token prevented current-user pipe connections. The successful run used the normal Windows token. These local results do not qualify a VM or any network interruption scenario.

## Actual SSH checkpoint recovery

`qualify_ssh_recovery.py` uses the selected strict SSH MCP configuration and creates only its own new scratch project. It applies one rename, transfers and hashes the pre-change archive, reconnects through a fresh host, verifies the saved changed block, and retrieves the checkpoint into a separate owned directory. Export comparison excludes only document provenance metadata.

```powershell
python -B scripts/qualify_ssh_recovery.py `
    --config artifacts/mcp.vm.interactive.json --server tia-v21 `
    --remote-artifact-root 'C:\TiaMcpData' `
    --report artifacts/ssh-recovery-vm.json --interrupt
```

`--interrupt` additionally prepares a bounded SCL fixture in the recovered copy. An independent MCP connection reads its durable journal. Only after a hashed recovery archive is recorded, and while the application response is still pending, the runner terminates its exact local SSH process. It never terminates a Siemens process or resends the mutation. A fresh host reads the journal until a recorded terminal outcome appears, then retrieves the checkpoint into another owned copy. The recovered baseline XML, archive hash and complete exposed program-block inventory must agree; the generated fault blocks must be absent from that recovered scope.

The journal observation precedes connection termination and cannot establish the precise internal Siemens execution point. If the response wins the race, interruption is recorded as `not_exercised` and qualification fails without retrying the write. If the journal remains unsettled, the outcome stays unknown. Native process exit and whole-project semantic completeness are not inferred from transport closure or the bounded block inventory.

Use `--cpu` to select an installed CPU type identifier; the default is CPU 1511-1 PN V4.0. No simulator or physical PLC operations are performed. Evidence and owned project copies remain available for diagnosis. Run `python -B scripts/test_ssh_recovery.py` for the separate pure ownership/checkpoint guards.

The actual VM run on 8 October 2026 passed using CPU 1511-1 PN V4.1 and package `tia-mcp-v21_20261008_102055_7f56be0e`; see `artifacts/ssh-recovery-vm-qualified.json` and `artifacts/vm-access/qualification-summary.json`. The observed `applying` journal later settled to `applied`, no mutation was replayed, and the recovered baseline excluded all 100 fault-fixture FCs. Earlier failed evidence is retained separately; consult [deployment status](deployment-reliability-status.md) for exact package hashes and scope.
