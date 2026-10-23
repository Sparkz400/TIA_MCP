# Launched bridge worker identity — 18 October 2026

Baseline: `0ebaef756afe273adbd652e07214d93ff29e6591`. This implements the first ownership correction identified by the [AI-03 preflight](../plans/expansion-plans/tia-addin-companion.md#ai-03-owner-preflight--18-october-2026). It does not deliver the Add-In handoff receiver or authenticate a native companion.

## Defect and behavior

The bridge's randomized, current-user-only pipe previously accepted any connected process with that user's access. It did not compare the peer with the worker it launched. A controlled second process running the same test executable could receive and answer the request while the launched process remained alive.

`BridgeClient` now retains the launched process handle and queries the connected pipe's client PID before sending each request. It rejects a different peer, a failed identity query or an exited original worker. Exit is checked on both sides of the peer query; process identity is never recovered by reopening a PID. Failures close the pipe and make the bridge terminal, preserving the existing no-retry/no-restart policy. Production shutdown does not kill either the worker or TIA.

Native bridge startup now explicitly requires Windows. Offline supplied-document workflows with native workers disabled remain available. A launcher that starts a separate process to connect is refused; configure the actual worker executable. Executable-name equality, matching user and valid response framing do not substitute for process identity. This is not executable/package-signature verification, protection against code injection or a compromised configured worker, or native Add-In/selection authentication.

## Documentation and focused checks

Context7 resolved Microsoft's Win32 reference but returned mostly unrelated APIs. The implementation was checked against the primary [GetNamedPipeClientProcessId reference](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid), [process handle lifetime](https://learn.microsoft.com/en-us/windows/win32/procthread/process-handles-and-identifiers), [.NET SafeHandle](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.safehandle?view=net-10.0) and [HasExited semantics](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.hasexited?view=net-10.0). No new dependency was added.

Evidence under `artifacts/bridge-peer-01/`:

- `before.log`: 17/18 bridge checks passed on the original implementation; the new impostor refusal failed because the request succeeded.
- `after.log`: the initial corrected implementation passed all 18 checks.
- `final-focused.log`: all **19 bridge checks** passed after adding the original-worker exit/recheck case. Existing serialization, cancellation, protocol errors, early exit, disposal and shutdown coverage remain.
- The impostor test uses two real same-user Windows processes, records distinct process IDs, asserts zero requests received and verifies both exit. A later call is refused as `BridgeFaulted`. The exit case waits on the original live worker handle, then verifies that neither the next request nor its retry is sent.

The full frozen aggregate passed **43 managed suites**, including all **228 general managed checks**, **1,464 Python tests** (one skip), Explorer checks and all configured MCP workflows. All **29 package stages** passed. Both aggregate and package runtime workflows passed 643 checks; the previous package's 644 included one extra repeated status poll, with no scenario-label difference. That comparison is retained in `runtime-check-count-comparison.json`. No Siemens API was executed.

The relocated bundle contains 1,510 files and retains manifest SHA-256 `ee0bbedcdb7ee8c70bf9ba3e62c919f72684b24617064afb4210fea7ca382d82`. Strict hash verification is package integrity evidence, not a publisher signature. `verification.json` binds the source snapshots, final results and preserved historical inputs. `integration.json` records the pushed commit, exact-head hosted CI state and checkout cleanup.

Both validation snapshots contain identical maps of 2,147 source files. The aggregate manifest SHA-256 is `8ae63195b4e8acd54d3bc8702090b1ddd8be1e32f51bb52b437a0e39e2d13064`; the package manifest SHA-256 is `cdbe8281a3ee24769ad7803f5bbfe3127c86ff1f8e89e22556a71c0cea524cb3`. Final verification checks that later changes are documentation only and all 106 missing historical evidence bindings remain unchanged.

## Remaining acceptance

AI-03 still needs the existing-Host receiver, enrolled-producer verification, native TIA process/session binding, one-use challenge and durable receipt/recovery protocol. Native worker/SDK interoperability and real companion UI behavior remain unqualified. The full 44-workstream scope remains active. Hosted CI account refusal is not a local or hosted pass.
