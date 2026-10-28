# Runtime UA, web and capture on a real server — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close `AC-recovery` (and the other open criteria) for the 33 `runtime_ua_*`, `runtime_web_*` and `runtime_capture_*` features with evidence from a real server: the OPC UA and web servers of a simulated S7-1500 CPU in an owned PLCSIM Advanced V8 instance in TCP/IP mode.

**Architecture:** The simulator worker gains a TCP/IP admission path next to its Softbus path. It never changes PLCSIM's machine-wide network mode, adapters or IPs; the operator sets those once per window. A new reviewed engineering kind turns on a CPU's OPC UA server. A native runner provisions an owned scratch program into a TCP/IP instance, drives the existing runtime tools against it, and checks every value through an independent channel (the PLCSIM API). A Host qualification hook proves that uncertain writes are never replayed.

**Tech Stack:** C# (.NET 10 Host and Core, .NET Framework 4.8 workers on the V21 PublicAPI and PLCSIM Advanced V8 API), Python runners with `scripts/curate_evidence.py`.

**Spec:** The decision of 28 October 2026: loopback fixture servers do not close `AC-recovery` for these features; a PLCSIM Advanced or physical server is required. Background: [offline sweep rank 3](../verification/host-only-qualification-20261027.md#remaining-offline-reachable-work-ranked-by-cells-per-effort), [OPC UA runtime](../reference/opcua-runtime.md), [physical runtime sessions](../reference/physical-runtime-sessions.md), [runtime captures](../reference/runtime-captures.md).

## Global Constraints

- Code never changes `SimulationRuntimeManager.NetworkMode`, network adapters, IP addresses, firewall rules or Openness/Windows security settings. Those are operator steps (Task 0).
- The Softbus path behaves exactly as today. Existing Softbus refusals keep the code `SoftbusRequired`.
- Writes and RUN/STOP only on an owned PLCSIM instance bound with `runtime_plcsim_bind`; never on a physical PLC.
- Loopback evidence keeps its value for the other criteria but never closes `AC-recovery` for these 33 features.
- Native evidence uses a frozen build: `git archive <commit>` into `D:\wp\bN\source` with `D:\wp\bN\source-manifest.json`, and a curated `verification.json`, as in `local.watch.native.20261028`.
- Do not edit `scripts/` in a checkout while a native runner from it is in flight; the runners hash their own sources and abort.
- Target profile: TIA Portal V21 Update 3, PLCSIM Advanced V8, CPU 1511-1 PN `6ES7 511-1AL03-0AB0` V4.1; any other profile is a separate qualification.
- After each TCP/IP window the operator switches PLCSIM back to Softbus; the Softbus runtime corpus must still pass.

## Review Focus

1. The operator switches PLCSIM back to Softbus while a TCP/IP instance is bound: every simulator and runtime call refuses with a typed code and nothing switches mode. Test in Task 1.
2. The CPU's IP is not on the host adapter's subnet, or the session endpoint is not a binding address: connect refuses before any login. Test in Task 2.
3. Compile re-issues the CPU's server certificate (seen for WebServer certificates on 28 October): the pin comes from `certificate_inspect` after compile, and a stale pin refuses connect. Test in Task 4.
4. The CPU stops or powers off during a subscription or capture: the session is invalidated, the capture records downtime, and nothing reconnects implicitly. Test in Task 5.
5. The Host dies after a write is sent and before its readback: the outcome stays unknown, no Host retries it, and only an explicit read resolves it. Test in Task 5.

---

### Task 0: Operator prerequisites and feasibility gate (no code)

This proves the premise before any code: a TCP/IP-mode PLCSIM Advanced instance serves a CPU's OPC UA and web endpoints that the existing Host runtime client can reach.

- [x] **Step 1 (operator):** In the PLCSIM Advanced control panel, set *Online Access* to *PLCSIM Virtual Eth. Adapter* (TCP/IP single adapter). The adapter is `Ethernet 2` ("Siemens PLCSIM Virtual Ethernet Adapter").
- [x] **Step 2 (operator, elevated PowerShell):** Give the adapter a static address in the CPU's subnet. Today it has only `169.254.140.84`.

```powershell
New-NetIPAddress -InterfaceAlias 'Ethernet 2' -IPAddress 192.168.0.10 -PrefixLength 24
```

- [x] **Step 3 (operator, TIA UI):** Build a throwaway CPU 1511-1 PN V4.1 project at `192.168.0.1`. Activate the OPC UA server (guest access, runtime license *SIMATIC OPC UA S7-1500 small*, *automatically accept client certificates*) and the web server. Download it to a manually created TCP/IP instance.
- [x] **Step 4 (agent):** From the Host, `runtime_ua_client_identity`, then `runtime_ua_connect` to `opc.tcp://192.168.0.1:4840/` (the trailing slash is required; see the gate record), pinning the server certificate shown in TIA's certificate manager. Then `runtime_ua_browse` and one `runtime_ua_read`. Also `runtime_web_connect` to `https://192.168.0.1`.
- [x] **Step 5:** Record the outcome in `docs/verification/runtime-real-server-gate-<date>.md`. If either endpoint is unreachable or refused, stop here and record why. The plan below assumes both answer. Recorded in [the gate record](../verification/runtime-real-server-gate-20261028.md): OPC UA and the Web API both pass; the web login was Anonymous (web authentication disabled).
- [ ] **Step 6 (operator):** Delete the throwaway instance and switch *Online Access* back to Softbus.

**Gate follow-up, endpoint form:**

- [x] The gate found that `runtime_ua_connect` refused the URL as the server advertises it (`opc.tcp://192.168.0.1:4840`) with `UaEndpointSelectionInvalid`. The SDK's `GetEndpoints` reports every advertised URL in `System.Uri` form with the root slash, so only that form can match. `OpcUaRuntimeSessionStore` now canonicalizes the caller's endpoint once in `Connect` and `Binding` (`Canonical`); `Validate`, the stored record, the credential binding and the `GetEndpoints` match stay exact. Test: an endpoint without the slash connects against the fixture server, names the same attempt and the same credential binding, and a query is still refused (`tests/TiaMcp.OpcUa.Runtime.Tests`). [OPC UA runtime](../reference/opcua-runtime.md).

### Task 1: TCP/IP admission in the simulator worker

**Files:**
- Create: `src/TiaMcp.Core/SimulationNetworkAdmission.cs` (namespace `TiaMcp.Simulation`, like `SimulationDiscoveryWait`)
- Create: `tests/TiaMcp.Tests/SimulationNetworkAdmissionTests.cs`; register it in `tests/TiaMcp.Tests/Program.cs` beside the discovery checks (line ~140)
- Modify: `src/TiaMcp.Simulator.V8/SimulatorSession.cs`: `Create` (line 65), `State` `powerOn`/`run` (lines ~196, 216), the `EnsureSoftbus` callers (lines ~263, 304, 433) and `EnsureSoftbus` itself (line 668)

**Interfaces:**
- Produces: `public static string Refusal(string networkMode, string instanceInterface, string requiredInterface)`. It returns `null` when admitted, otherwise `"SoftbusRequired"` or `"TcpIpModeRequired"`. Inputs are the V8 enum names (`Softbus`, `TCPIPSingleAdapter`, `TCPIPMultipleAdapter`, `TCPIP`). `requiredInterface` is the interface recorded at creation.
- Produces: the `create` request accepts `communicationInterface`: `"Softbus"` (default) or `"TCPIP"`. `"TCPIP"` is admitted only when `NetworkMode` is already `TCPIPSingleAdapter`. The instance's `CommunicationInterface` is set before power-on and stored in the creation receipt. `Info` already reports `communicationInterface`.
- As built: `IInstance.CommunicationInterface` is read-only in V8 and follows the machine-wide mode (an Off instance in Softbus mode reports `Softbus`). So `sim_create` records the requested interface in the receipt and never assigns it, and every admission compares the live interface with it. Admission covers power-on, RUN, `sim_write`, `sim_write_owned`, `sim_test` and `sim_identity`; reads, tags and captures stay ungated. Selections without a receipt stay Softbus-only.

- [x] **Step 1: Write the failing tests** in `SimulationNetworkAdmissionTests`:
  - `Refusal("Softbus","Softbus","Softbus") == null`
  - `Refusal("TCPIPSingleAdapter","TCPIP","TCPIP") == null`
  - `Refusal("TCPIPSingleAdapter","TCPIP","Softbus") == "SoftbusRequired"`
  - `Refusal("Softbus","Softbus","TCPIP") == "TcpIpModeRequired"` (the operator switched back mid-window)
  - `Refusal("TCPIPMultipleAdapter","TCPIP","TCPIP") == "TcpIpModeRequired"` (only single-adapter mode is admitted)
  - an unknown mode string is refused rather than admitted
- [x] **Step 2:** Run `dotnet run --project tests/TiaMcp.Tests -c Release`. Expected: the build fails because `SimulationNetworkAdmission` is not defined.
- [x] **Step 3:** Implement `Refusal`. Replace the inline Softbus checks and `EnsureSoftbus` with calls to it, passing the receipt's interface, and map a non-null result to `SimulatorException(code, …)` with the existing Softbus message for `SoftbusRequired`. In `Create`, validate `communicationInterface` and set `instance.CommunicationInterface` from it. `NetworkMode` is never assigned.
- [x] **Step 4:** Run the tests: all pass. Build `src/TiaMcp.Simulator.V8` against the V8 API with zero errors. A source check also pins that the worker never assigns `NetworkMode`, adapter bindings or IP suites.
- [x] **Step 5:** Commit: `Simulator worker admits owned instances in existing TCP/IP single-adapter mode`.

### Task 2: Bindings for TCP/IP endpoints

**Files:**
- Modify: `src/TiaMcp.Simulator.V8/SimulatorSession.cs` (bind result, line ~285): add `communicationInterface`
- Modify: `src/TiaMcp.Core/PhysicalRuntime/PlcsimTargetBinding.cs`: store `communicationInterface`; a binding used as a network endpoint requires `"TCPIP"` and at least one controller address
- Test: `tests/TiaMcp.PhysicalRuntime.Tests` (the binding checks already there)

**Interfaces:**
- Consumes: Task 1's `communicationInterface` in the creation receipt.
- Produces: refusal code `PlcsimEndpointUnreachable` when a Softbus binding backs a `runtime_ua_*` or `runtime_web_*` endpoint. The existing `RuntimeTargetNotOwnedPlcsim` covers an endpoint address outside the binding.
- As built: `sim_identity` reports the admitted interface, so a mode switch mid-window fails every gated write with `TcpIpModeRequired` before the binding is compared. `Covers` also requires `TCPIP`, so the runtime handoff review never calls a Softbus binding `boundOwnedPlcsim`. A changed interface makes the binding stale. Only writes and RUN/STOP take a binding; `runtime_ua_connect` and `runtime_web_connect` do not, so the connect-time half of Review Focus 2 (and any host-subnet check) is still open for Task 4.

- [x] **Step 1: Write the failing tests:** a Softbus binding with endpoint `192.168.0.1` gives `PlcsimEndpointUnreachable`; a TCP/IP binding with addresses `["192.168.0.1"]` and endpoint `192.168.0.2` gives `RuntimeTargetNotOwnedPlcsim`; a TCP/IP binding with a matching address is admitted.
- [x] **Step 2:** Run `dotnet run --project tests/TiaMcp.PhysicalRuntime.Tests -c Release`. Expected: the new checks fail.
- [x] **Step 3:** Implement. The binding's content hash now includes `communicationInterface`, so older bindings stay readable but cannot authorize a network endpoint.
- [x] **Step 4:** Run the tests: all pass.
- [x] **Step 5:** Commit: `PLCSIM bindings record the communication interface; Softbus bindings never back network endpoints`.

### Task 3: `opcua_server_settings_set` change kind

**Files:**
- Create: `fixtures/opcua/server-settings-v21-1511-v41.json`: the native attribute names and observed defaults from Step 1
- Modify: `src/TiaMcp.Openness.V21/OpennessSession.OpcUaChanges.cs`: add `["opcua_server_settings_set"] = { "serverEnabled", "port", "guestAccess", "autoAcceptClientCertificates", "runtimeLicense" }` to `OpcUaPayloadFields`, plus prepare/apply/readback
- Modify: `src/TiaMcp.Openness.V21/OpennessSession.Changes.cs` (OPC UA case list, line ~499)
- Modify: `contracts/feature-catalog.v1.json`: feature `change.opcua_server_settings_set` with the four mutation criteria (owner D04 by prefix)
- Test: `scripts/test_contracts.py` (kind registered) and a native capture runner (Step 1)

**Interfaces:**
- Produces: change kind `opcua_server_settings_set`. Payload: `serverEnabled` (bool), `port` (1..65535), `guestAccess` (bool), `autoAcceptClientCertificates` (bool), `runtimeLicense` (one of the native enum names found in Step 1). Each field is optional and carries `{ expected, value }` like `webServerActive`. Preview lists each native attribute's before and after values. Apply uses the shared `change_apply` route (checkpoint, compile, save, readback).
- As built ([fixture](../../fixtures/opcua/server-settings-v21-1511-v41.json)): `serverEnabled` is `OpcUaServer` on the CPU's `OPC UA` sub-item; `port` and `autoAcceptClientCertificates` are `OpcUaPort` and `OpcUaAutoAcceptAllClientCertificates` there, which exist only while the server is on; `runtimeLicense` is the CPU's UInt64 `OpcUaPurchasedLicense` (0..3 `NoLicense`, `SimaticOpcUaS71500Small`/`Medium`/`Large`, named by the V21 hardware parameter PDF). `guestAccess` has no attribute: V21 models guest access as the UMAC anonymous user holding the `OPCUAServerAccess` device right, so it is refused with `UnsupportedCapability`. V21 refuses to compile an active server unless some user holds that right; `umac_role_set` grants it, and a refused grant now lists the CPU's available device rights. The payload rules are a Core helper (`OpcUaServerSettings`) with offline tests. The kind has its own prepare and apply instead of joining `OpcUaPayloadFields`, because it writes hardware attributes, not the server interface group.

- [x] **Step 1: Capture names.** On a fresh owned scratch CPU 1511-1 PN V4.1, run `opcua_engineering_inspect` and save its `serverSettings` (attributes whose names contain `OpcUa`) to the fixture. Pin the exact attribute names; V21 documents no fixed list.
- [x] **Step 2: Write the failing checks:** `test_contracts` sees the new kind; payload validation refuses unknown fields, a port of 0 and an `expected` that differs from the read value (`ExpectedValueMismatch`).
- [x] **Step 3: Implement.** Write only the pinned attribute names. A missing attribute is `UnsupportedCapability`; nothing is written.
- [x] **Step 4: Native check.** Apply the kind on a scratch, then verify: readback, compile, save, fresh reopen and `change_restore` to the exact before state. `scripts/qualify_opcua_server_settings.py`; [verification record](../verification/opcua-server-settings-20261028.md).
- [x] **Step 5:** Commit: `Add opcua_server_settings_set for the CPU OPC UA server`.

### Task 4: Real-server runner, normal path

**Found while building Tasks 1-3 (resolve before Step 5):**

- The engineering worker still requires Softbus for memory-card generation (`VerifySimulationInstance`), the discovery baseline and credential provisioning. Provisioning a TCP/IP instance needs the same receipt-based admission there; until then step 3 below refuses with `SoftbusRequired`.
- Guest access cannot be set through V21 Openness (see Task 3). Either log in as the scratch's own runtime user (`TiaMcpTest_*`, generated DPAPI password; its role needs `OPCUAServerAccess`, as the Task 3 runner grants), which puts a generated credential into the Host vault, or use an operator-made seed with guest access. This is the user's call.
- `autoAcceptClientCertificates` is settable, so the CPU can accept the Host's client certificate without `certificate_assign`.

**Files:**
- Create: `scripts/qualify_runtime_plcsim_tcpip.py`: reuses `smoke_mcp.Client` and the checks in `scripts/opcua_runtime_checks.py`; writes `verification.json` with `toolCalls` after the whole run passed
- Create: `scripts/verify_runtime_plcsim_tcpip.py`: an independent file-only oracle over the retained artifacts
- Create: `scripts/test_verify_runtime_plcsim_tcpip.py`: oracle refusals on tampered artifacts

**Interfaces:**
- Consumes: Task 1 `communicationInterface: "TCPIP"`, Task 2 bindings, Task 3 kind, plus the existing `webserver_page_set` (`webServerActive`), `certificate_inspect`, simulation provisioning (virtual card) and every `runtime_*` tool.
- Produces: `--mode normal`, with `--host`, `--worker`, `--sim-worker`, `--root`, `--cpu-ip 192.168.0.1`.

- [ ] **Step 1: Write the failing oracle tests:** a value read through UA that differs from the PLCSIM API read is an issue; a server certificate pin taken before compile is an issue; a missing Host exit proof is an issue.
- [ ] **Step 2:** Run `python -B scripts/test_verify_runtime_plcsim_tcpip.py`. Expected: they fail.
- [ ] **Step 3: Implement the runner flow:**
  1. Owned scratch; set the CPU IP to `--cpu-ip`; turn on the web server and the OPC UA server; compile.
  2. Take the UA and web certificate pins from `certificate_inspect` after compile.
  3. Provision to a TCP/IP instance, power on, RUN, bind.
  4. UA: `client_identity`, `connect`, `browse`, `read` (compared against PLCSIM API reads of the same tags), `write` with `expected`, `status`, `subscription_*`, `capture_*`, `disconnect`.
  5. Web: `connect`, `browse`, `read`, `diagnostics`, `status`, `write`, `operating_mode` STOP→RUN, `runtime_capture_*`, `disconnect`.
  6. Exit proofs for every owned process.
- [ ] **Step 4:** Implement the oracle; its tests pass.
- [ ] **Step 5: Native run (TCP/IP window):** run `--mode normal` on a frozen build; the oracle passes. Then the operator switches back to Softbus.
- [ ] **Step 6:** Commit the runner and oracle; curation follows in Task 6.

### Task 5: Recovery modes

**Files:**
- Modify: `src/TiaMcp.Host/HostOptions.cs`: `--runtime-qualification <point>` beside `--watch-qualification` (line 83), requiring `--component-qualification` like it; points `ua.write.afterSend` and `web.write.afterSend`
- Modify: `src/TiaMcp.Host/OpcUaRuntimeTools.cs` and the web write path: at the configured point, flush the intent and exit the Host with code 74
- Modify: `scripts/qualify_runtime_plcsim_tcpip.py`: add `--mode host-loss-capture | server-restart | ua-write-loss | web-write-loss`
- Test: `tests/TiaMcp.PhysicalRuntime.Tests` (the hook parses only with `--component-qualification`; unknown points are refused)

- [ ] **Step 1: Write the failing tests** for the hook's option parsing and refusal.
- [ ] **Step 2: Implement the hook.** It never fires without the explicit flag.
- [ ] **Step 3: Implement the modes:**
  - `host-loss-capture`: kill the Host during a UA subscription and a web capture. A new Host reopens the committed prefix, reports `hostEndedRemoteUnknown` and refuses implicit resume. `disconnect` with `acknowledgeUnknown` releases the reservation; a fresh connect works.
  - `server-restart`: STOP, power off, power on during a live session. The changed server start time invalidates it, the capture records downtime, and nothing reconnects.
  - `ua-write-loss` and `web-write-loss`: the Host exits after sending. A new Host reports the unknown outcome and refuses a retry; an explicit read decides; the PLCSIM API read confirms.
- [ ] **Step 4: Native runs:** each mode once on the frozen build (TCP/IP window), each with the oracle.
- [ ] **Step 5:** Commit: `Runtime recovery modes against an owned TCP/IP PLCSIM server`.

### Task 6: Curate, verify and document

- [ ] **Step 1:** `python -B scripts/curate_evidence.py --id local.runtime.plcsim-tcpip.<date>` over every `verification.json`, with binaries for the Host, engineering worker and simulator worker.
- [ ] **Step 2:** In the catalog, verify `runtime_plcsim_bind` and each `runtime_ua_*`, `runtime_web_*` and `runtime_capture_*` feature whose tools all passed. Leave `runtime_ua_credential_binding` and the authenticated web features unverified unless a credential run happened; see Out of scope. Bump the catalog version.
- [ ] **Step 3:** Run `python -B scripts/release_matrix.py`, `python -B scripts/api_coverage_bindings.py refresh contracts/api-coverage/implementation-bindings.v1.json` and `python -B -m unittest discover -s scripts -p 'test_*.py'`; all pass.
- [ ] **Step 4:** Update `docs/TODO.md`, `docs/STATUS.md`, the reference pages for the TCP/IP path, `docs/wiki/Getting-Started.md` (new prerequisites), and a verification record.
- [ ] **Step 5:** Commit and open a PR.

## Out of scope

- Physical CPUs (`runtime.physical_plc`, bench B05) and other CPU or firmware profiles.
- Authenticated sessions with project users: V21 Openness cannot set UMAC passwords. `runtime_ua_credential_binding` and authenticated web sessions need an operator-made seed archive with one test user, and a credential stored in the Windows vault by the operator. Plan that separately if guest access is not enough.
- Trusting a specific client certificate in the CPU (instead of automatic acceptance): `certificate_assign` writes no OPC UA attributes yet.
