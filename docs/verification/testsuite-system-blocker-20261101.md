# Test Suite system tests: qualification design and blocker — 1 November 2026

**3 November update:** [TS-Q03 implementation and setup](testsuite-system-q03-20261103.md)
now provides the exploratory Host/worker route and runner. Native fixture, result-profile and
runtime qualification remain open; the earlier "designed, not built" status below is historical.

D06 has one open row, `testsuite_system_plan_prepare` (9 cells). Its reviews are prepared and queued offline, but no
build dispatches one: the production profile gate refuses on Host and worker, and the TS-Q01 and TS-Q02 qualification
admission refuses every system review before a native call. This record designs a system-test qualification mode in
the [TS-Q02](testsuite-application-20261030.md) shape, records why it cannot run on this PC, and fixes an endpoint
check found on the way. No native run is part of this record.

## TS-Q03 design

Same shape and gates as TS-Q02: qualification build (`TestSuiteQualification=true`), Host `--testsuite-qualification`,
one exact owned fixture case per Run in a worker-created scratch, and no operator, since the result is read through
Openness.

- **Profile and fixture:** `v21.owned-plcsim.system.qualification-v1`. Two packaged system cases on the scratch's
  `"TiaMcp_EnableGate_DB".Active` (Main calls the gate with `Enable := TRUE`): `TiaMcp_Q03_Pass` asserts `TRUE`,
  `TiaMcp_Q03_Fail` deliberately asserts `FALSE`. Strict import into the empty system group, then
  `SetScope(serverAddress, StandardSIMATIC)` at prepare, so the Run's own `SetScope` writes the same value and
  preservation can compare the whole fixture, as TS-Q02 pre-sets its scope.
- **Admission** (Host and worker, as TS-Q02): one fixture case, `executionMode: endpoint`, route `systemTestCase`, no
  whole group, a project under the Host's scratch root. The instance comes from `sim_create` with
  `communicationInterface: "TCPIP"`, and `serverAddress` is `opc.tcp://<ControllerIP>:4840/`. The worker's
  `TestSuiteOwnedEndpoint` (ticket ownership, controller address, Windows route through the PLCSIM adapter, TCP/IP
  instance) runs at prepare, at both observations and before `SetScope` and `Run`.
- **Scope and license:** the case's `OPCUAServerAddress` and `OPCUAServerInterfaceType` and its `.tst` export
  (`SERVER_ADDRESS`, `SELECTED_SERVER_INTERFACE`) must name the owned address and `Standard_SIMATIC`. This session's
  `SaveToFile` is the license observation, as in TS-Q02 (`Run` documents `LicenseNotFoundException`).
- **Setup per case:** owned scratch copied from an operator-made guest-access seed (see blocker 2),
  `project_simulation_enable`, the OPC UA server on with the Small runtime license and automatic client-certificate
  acceptance (`opcua_server_settings_set`), the TCP/IP instance, download, generated-state save, credential
  provisioning, power on, RUN, then `qualification_testsuite_prepare` and `qualification_testsuite_execute`.
- **Cases:** `pass`, `fail` (recovery and reconciliation), `afterRun` (lost Run reply) and `released` (the instance
  released between prepare and execute), as TS-Q02.
- **Results:** no system result tree has been observed. The first run is exploratory and fails closed
  (`inconclusive` or `executionError`); the exact tree then gets a profile like
  [`TestSuiteApplicationResultProfile`](../../src/TiaMcp.Core/TestSuite/TestSuiteApplicationResultProfile.cs).

## Why it cannot run on this PC

1. **Network mode.** A system test is an OPC UA client, and OPC UA reaches a PLCSIM Advanced instance only on PLCSIM's
   TCP/IP network. This PC's PLCSIM is in Softbus mode, a machine-wide operator setting that agent sessions do not
   change; `sim_create` with `communicationInterface: "TCPIP"` refuses with `TcpIpModeRequired` outside the existing
   single-adapter mode. The two TS-Q02 runs of 1 November provisioned their instances, which requires that Softbus mode
   (`local.testsuite.job-prepare.20261101`).
2. **No credential argument.** `SystemTestCase.SetScope(String, ServerInterfaces[, DirectoryInfo])` and the three
   `SystemTestCaseExecutor.Run` overloads take no user, password or security policy; the installed V21 PublicAPI
   documentation names none. A scratch CPU's OPC UA server runs under UMAC with a generated runtime user, so an
   anonymous client needs guest access, which V21 models as the UMAC anonymous user holding `OPCUAServerAccess`
   ([OPC UA server settings](opcua-server-settings-20261028.md)). `opcua_server_settings_set` refuses `guestAccess`,
   `umac_role_set` never changes the anonymous user, and the 29 October decision on the real-server login records that
   guest access is not settable through V21 Openness ([TODO](../TODO.md#decisions-for-the-maintainer)). The route left
   is an operator-made seed: a scratch whose anonymous user holds `OPCUAServerAccess`, set in the TIA UI at the desk.
3. **Certificate trust.** Whether Test Suite's OPC UA client accepts the CPU's server certificate without a UI prompt
   is unobserved. Trust decisions are desk work.

The mode is therefore designed, not built: building it now would add a qualification path that no session on this PC
could run, and the seed of blocker 2 decides how the scratch is set up. Running it needs a desk session: PLCSIM in its
TCP/IP single-adapter mode, an operator-made guest-access seed, and an operator to answer a certificate prompt if one
appears.

## Endpoint check fix

While reading the endpoint gate for the audit recheck, the PLCSIM adapter on this PC was observed read-only
(`Get-NetAdapter`, `Get-NetIPAddress`, `Find-NetRoute`): *Siemens PLCSIM Virtual Ethernet Adapter* is up with
`192.168.0.10/24`, and Windows routes `192.168.0.1` through it, although PLCSIM is in Softbus mode. A Softbus instance
keeps its configured `ControllerIP` (`192.168.0.1` in the TS-Q02 runs), so `TestSuiteOwnedEndpoint` would have admitted
`opc.tcp://192.168.0.1:4840/` for a Softbus instance with nothing listening there. It now also requires the ticket's
TCP/IP interface and refuses a Softbus instance with `PlcsimEndpointUnreachable`, after the route check
([`SimulationNetworkAdmission.EndpointRefusal`](../../src/TiaMcp.Core/SimulationNetworkAdmission.cs)), the code the
runtime bindings use for a Softbus binding. `TiaMcp.Tests` covers the rule and its place in the worker (307/307). No physical PLC was
reachable this way: the route still had to go through the PLCSIM adapter.

## Audit recheck

The 31 October audit made Test Suite system endpoints require the PLCSIM route. That call site has no native recheck
and cannot get one yet: no build reaches `TestSuiteOwnedEndpoint`, because every system dispatch refuses at the profile
gate first, and the qualification admission refuses system reviews. The shared route rule ran natively in front of
`runtime_ua_call` (`local.runtime.plcsim-ua-services.20261031`). The Test Suite call site is rechecked with TS-Q03.

## Operator-authorized follow-up — 2 November

Temporary PLCSIM TCP/IP mode and TIA UI fixture creation are now authorized. An owned system-test seed was created and saved; this removes the earlier lack-of-UI-access constraint, not the guest-access or certificate-trust requirements. TS-Q03 remains designed rather than implemented. See the [current seed and remaining steps](local-completion-20261102.md#test-suite-operator-seed).
