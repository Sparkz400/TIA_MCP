# TS-Q03 system-test runner — 3 November 2026

TS-Q03 is now implemented as an **exploratory qualification-only route**. Neither its candidate
fixture grammar nor native execution/result interpretation is qualified. The nine system-test
matrix cells stay open, and production Test Suite execution remains refused.

## Implemented boundary

- The Host and worker admit one named `TiaMcp_Q03_Pass` or `TiaMcp_Q03_Fail` system case,
  the exact profile `v21.owned-plcsim.system.qualification-v1`, and no group/list/exclusions.
- The project must be the current worker-created scratch under this Host's artifact root.
  This does not adopt a supplied seed or the user's project. The exact four-block fixture,
  two cases, empty other Test Suite groups, empty project library and root software scope are checked.
- The endpoint is a canonical `opc.tcp://IPv4:4840/` address of the ticket-owned TCP/IP simulator.
  The existing instance/controller/Windows-route checks run at prepare, observation and again
  immediately before each `SetScope` and `Run`. A released/foreign instance or a non-PLCSIM route refuses.
- Candidate definitions import strictly. Native scope getters and `.tst` export fields must agree
  on the endpoint and StandardSIMATIC interface. After `SetScope`, export bytes must still match
  the reviewed definition; mismatches refuse before execution.
- The durable job retains a checkpoint and raw results, compares preservation, compiles and reopens
  the scratch. Recovery and reconciliation reuse the existing no-replay workflow.
- No system result interpretation has been invented: even an all-success tree stays `inconclusive`.
  Errors stay `executionError`, including a supplied assertion label. A qualified result profile
  needs independently reviewed actual native pass/fail trees, including UI-language boundaries.

The fixture aliases `TiaMcp_EnableGate_DB.Active` without initializing it. Pass expects TRUE; Fail
expects FALSE. Its native importability is deliberately not inferred from generated text or a build.

## Run preparation

The operator confirmed that no suitable project is prepared on this workstation. The runner creates
its own fixture and keeps that worker/session alive while the operator configures it; it does not
promote an externally opened project to scratch ownership.

Build separate binaries; do not overwrite the running MCP worker:

```powershell
dotnet build src/TiaMcp.Host/TiaMcp.Host.csproj -c Release -p:TestSuiteQualification=true --artifacts-path artifacts/ts-q03-build
dotnet build src/TiaMcp.Openness.V21/TiaMcp.Openness.V21.csproj -c Release -p:TestSuiteQualification=true --artifacts-path artifacts/ts-q03-build
dotnet build src/TiaMcp.Simulator.V8/TiaMcp.Simulator.V8.csproj -c Release --artifacts-path artifacts/ts-q03-build
```

Use `scripts/qualify_testsuite_system.py --help` for the exact arguments. Supply the three resulting
executables, a short fresh artifact root (for TIA's path limit), `--case explore-pass`, `--run-native`
and the explicit `--trust-owned-empty-cpu-certificate` opt-in used by isolated credential provisioning.
The latter applies only to the runner's own empty simulator; it does not authorize arbitrary targets.

Before running, configure PLCSIM TCP/IP single-adapter mode and the isolated virtual adapter.
The runner does not change these machine settings. At its first pause, use only the newly opened
qualification project: grant the anonymous user `OPCUAServerAccess`, verify the requested CPU IP,
preserve its four blocks and empty test groups, and save. Do not disable security on another project.
The runner enables the scratch OPC UA server and its Small runtime-license setting; a usable license
is still a prerequisite. Configure appropriate client trust explicitly; the runner does not enable
automatic acceptance of all clients.

After download/provision/RUN, an independent simulator read must observe Active = TRUE. The second
pause is for the isolated server's certificate trust in Test Suite. The operator must remain available
for a trust prompt. Both acknowledgements are retained as operator statements, never proof that a
security setting actually took effect. Console prompts use the runner's artifact root and CLI address;
they do not render native responses carrying credential metadata. If trust cannot be prepared without a native attempt, retain
that failure and restart with a fresh fixture; do not replay an uncertain test.

The 15-minute simulator ticket deadline applies to setup and dispatch. The runner does not renew it
or weaken ownership checks after a slow operator step. Other cases are `explore-fail`, `afterRun`
(injected lost reply) and `released`. Each requires a fresh invocation. Successful exploration means
raw capture and recovery completed, **not** a passing system-test qualification.

## Validation and remaining work

The qualification Host and worker build against the installed V21 APIs. Managed checks cover
Host/worker admission, changed/duplicate scope fields, endpoint/path boundaries, production refusal,
conservative results, raw retention and recovery without replay. Validation: 785 managed Test Suite checks; 142 worker fake/reflection checks; 40 focused TS-Q03
admission/result checks after the final result-handling change; six Python runner tests (including
four dispatch/recovery scenarios, prompt isolation and operator refusal); 12 checks against the actual qualification Host with absent
workers. Qualification Host/worker builds pass; the Host reports five existing nullable/deprecated-MCP
warnings. No TIA project or simulator was started for these checks.

Next: run the operator-prepared isolated fixture, retain the exact native `.tst` readback and result
tree, correct any fixture mismatch, then add a narrowly evidenced result profile and rerun pass,
deliberate failure, response-loss/recovery and released-target cases. Only then consider promotion
of catalog evidence and matrix cells. Existing TS-Q01 and TS-Q02 evidence is unchanged.
