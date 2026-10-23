# PLCSIM Advanced V8 adapter

The [reusable block-testing system](block-testing.md) composes this adapter with native interface inspection and generated OB/DB/FB harnesses. `python scripts/run_block_tests.py` prepares reviewable suites; `--live` uses fresh owned instances for qualification. Its request/acknowledgment gate controls individual DUT calls. Host polling and scan synchronization remain distinct, and the new runtime corpus has not yet been qualified on PLCSIM.

The simulator runs in a separate x64 .NET Framework 4.8 worker. Its public API comes from the locally installed PLCSIM Advanced V8 installation; Siemens binaries are not distributed with this repository. Standard PLCSIM V21 is detected by the environment probe but is not this runtime adapter's public API target.

Start the MCP host with its normal TIA worker and the optional simulator worker path:

```powershell
dotnet run --project src/TiaMcp.Host -- --worker <absolute-path-to-TiaMcp.Openness.V21.exe> --sim-worker <absolute-path-to-TiaMcp.Simulator.V8.exe>
```

`--sim-api-dir` overrides the installed simulator API directory for both workers. The default is `C:\Program Files (x86)\Common Files\Siemens\PLCSIMADV\API\8.0`. The engineering worker also loads the V8 API for independent instance verification during memory-card generation. Both workers must run under the same Windows user because ownership tickets use CurrentUser DPAPI. Project, simulator and report paths refer to the Windows machine running the workers. The private named-pipe bridge is local; remote/VM deployment requires running the workers on that Windows host and a separately secured transport for the MCP host. No unauthenticated remote runtime endpoint is implemented.

| Component or deployment | Current compatibility evidence |
| --- | --- |
| TIA Portal V21, x64 Windows, net48 API | Installed API used for build and local engineering integration. |
| Standard PLCSIM V21 | Installation detected; not the public runtime API used by this adapter. |
| PLCSIM Advanced V8.0 Update 1 | Installed runtime file 80.0.299.0 / product 8.0.0.1. The installer-required restart is confirmed at 11:08:12 Europe/Berlin on 8 October 2026. A fresh empty CPU again reached STOP with `OK`; unchanged protected V4.0 startup still returned `WarningPasswordForProtectionError`. The protected baseline later passed RUN, typed I/O and all eight assertions after explicitly authorized credential provisioning (attempt fifteen, reconfirmed by attempt seventeen). The nine-family corpus remains unqualified; recorded failures and remaining work are listed in the [runtime-proof TODO](../plans/runtime-proof-todo.md). |
| Simulated S7-1500 firmware | The installed V8 manual, section 3.4/p25, states that its virtual S7-15xx corresponds to firmware V4.0. Select a catalog-confirmed V4.0 CPU for the simulation fixture. |
| TIA catalog CPU V4.1 | Present and verified for offline engineering. The V8 function manual lists CPU 1511 firmware 1.8–4.1 among supported configurations; this is documented compatibility, not a passing local power-on result. The fixture uses V4.0. |
| Windows VM or separate PC | Run TIA, the simulator and both workers on that Windows host. The packaged SSH launcher provides a secured host transport; actual VM/remote execution and Siemens session/licensing prerequisites still need validation on that host. |
| Physical PLC | No runtime/download path exposed by this adapter. |

## Operations

The simulator MCP tool names match the `sim_*` bridge operations below. Engineering operations include `simulation_download` and the opt-in `simulation_provision_scratch_credential`. Use the MCP tool list for exact host parameter schemas; the host supplies its configured artifact directories and accepts signal/write/step arrays as JSON arrays.

| Bridge operation | Purpose and parameters |
| --- | --- |
| `sim_status` | Initialize/read the local runtime manager, network mode, registered instance IDs/names, selected instances and runtime license status. Does not power a CPU. |
| `sim_create` | `artifactDirectory`, optional exact V8 `cpuType` enum name (default `CPU1511`). Creates a uniquely named `TiaMcp_<GUID>` instance, fresh storage, and a short-lived opaque `downloadTicket`. Does not power the CPU. |
| `sim_select` | `instanceId`, `expectedName`. Explicitly select an existing local simulated instance. Such an instance is not adapter-owned and cannot be unregistered by this worker. |
| `simulation_download` | Engineering operation: `downloadTicket`, optional `projectName`, exact `plcPath`, `credentialMetadataPath` and `credentialLineageJournalPaths` array. The singular `credentialLineageJournalPath` remains available for one hop; do not supply both forms. Compiles the selected in-memory PLC and generates a fresh virtual memory card. |
| `simulation_provision_scratch_credential` | Opt-in engineering operation: the exact `downloadTicket`, required generated `credentialMetadataPath`, optional `projectName`, `plcPath` and `credentialLineageJournalPaths`. Validates the original card and owned scratch/Softbus target, starts the same CPU empty, then attempts `OnlineProvider.SetPlcMasterSecret` with the generated DPAPI credential. Optional `trustOwnedEmptyCpuCertificate` defaults false. One attempt per owned instance; success requires OFF with protection enabled and the original card restored unchanged. Does not establish RUN or live values. |
| `sim_state` | `instanceId`, `action` (`powerOn`, `powerOff`, `run`, `stop`), optional bounded `timeoutMs`. No automatic RUN following download. |
| `sim_tags` | `instanceId`, optional `filter`, `maxItems` and `refresh`. Reads bounded symbolic tag metadata. |
| `sim_read` | `instanceId`, `signals` array of `{symbol,type}`. Returns each value's observation time and quality/error. |
| `sim_write` | `instanceId`, `writes` array of `{symbol,type,value}`. Validates all values before writing and reports completed writes if a later write fails. |
| `sim_test` | `instanceId`, `artifactDirectory`, `signals`, `steps`, optional `intervalMs`. Applies timed stimuli, captures values, evaluates assertions, and retains a JSON report. |
| `sim_release` | `instanceId`, optional `deleteOwned`. Releases the worker's interface. Explicit `deleteOwned:true` powers off and unregisters only an instance created by this worker. Storage files are retained. |

Runtime action calls require an instance created or explicitly selected by the current worker. The worker verifies that the recorded ID/name still exists; it does not substitute another instance if an ID is reused. Power-on requires existing local `Softbus` mode. RUN, writes and scenarios additionally require that the instance reports a Softbus communication interface. This adapter never changes shared adapter bindings/network mode and never uses remote instance discovery or an online physical-PLC download API.

Releasing a client interface and shutting down the worker do not power off or unregister simulator instances. Use explicit owned cleanup while that worker still holds ownership. After a worker restart, existing instances can be selected but are not silently reclaimed as owned. Retained storage can be inspected or removed separately by the user.

## Loading an engineering program

The supported sequence is create → download → power-on → inspect tags → RUN → read/write/test → stop/power-off → owned cleanup.

For a protected generated scratch program, optional credential provisioning runs between download and normal power-on. It is a separate operation with a persistent `.provision.claim`, exact ownership and original-card checks, validated credential lineage, and a singleton local Softbus target. It does not accept raw passwords or arbitrary target addresses. The PLCSIM engineering interface becomes visible only while the virtual CPU is powered; route discovery therefore occurs during bounded recovery startup. Runtime-created `retain.pms` is recorded separately and is never included retroactively in the original generated-card revision. A failed attempt requires a fresh owned instance. Attempt fifteen qualified provisioning and the protected baseline on this machine; attempt seventeen reconfirmed it on the retained binary lineage below.

TLS verification rejects trust by default. Explicit `trustOwnedEmptyCpuCertificate: true` authorizes first-use trust only during this owned empty CPU's provisioning attempt. The worker revalidates ownership and the local route, requires one untruncated certificate within its UTC validity dates, pins its SHA-256 within that attempt and rejects a later change. This is caller-authorized first-use trust; `certificateIdentityIndependentlyVerified` remains false. Matching certificate subjects or validity dates do not authenticate identity. The Python runners require matching authorization, target, date and pin evidence before continuing, then independently recheck the restored original card. The user explicitly authorized this scoped first-use trust, and attempt fifteen verified the route with the protected baseline. This does not qualify arbitrary targets or another machine.

`sim_create` issues a CurrentUser DPAPI ticket valid for 15 minutes. The engineering worker verifies the ticket against the instance's ownership marker and independently opens the local V8 runtime interface. Instance ID, name, storage path, powered-off state and existing Softbus mode must match. A caller-provided name or IP alone is not sufficient.

The selected project must already have `PlcSimulationSettingsProvider.IsSimulationDuringBlockCompilationEnabled` enabled through an explicit engineering change (supported change kind: `project_simulation_enable`). The download operation resolves the exact PLC hardware, compiles the current in-memory project state, and calls only `DownloadProvider.Download(DirectoryInfo, callback)` to generate `SIMATIC_MC` under that instance's fresh storage. `TargetForSoftwareSelections.PlcSimulationAdvanced` is used. Existing cards/reports are not replaced. A persistent `.download.claim` makes an attempted generation one-use; failures after the claim require a fresh instance and retain any partial output.

The callback handles the documented memory-card, target-software, consistent-block and alarm-text selections. A `UserManagementDownload` callback selects `DownloadAllUserManagementDataResetToProject` solely to populate this fresh owned offline card from the selected project's configured users. There is no online target or existing card whose user database can be reset by this route; that selection is recorded in the report. Unknown acknowledgements, arbitrary passwords and safety confirmations are rejected. This route does not provide safety-program acceptance, proof testing or commissioning. CPU family, firmware, simulation support and program protection can still prevent successful generation or power-on.

TIA can request a `PlcMasterSecretPassword` callback when generating the first virtual card. For an adapter-created scratch CPU, provide its generated `credentialMetadataPath` from `scratchSecurity.cpus`. The worker matches credential purpose, CPU name/type and project. A project retrieved to a different path additionally requires `credentialLineageJournalPaths`: 1–16 explicitly selected completed recovery journals must form an unbroken path from the original credential project to the current project. Every recorded archive hash is verified; duplicate, branching, cyclic, incomplete and unused proofs are rejected. The worker does not search for credentials or journals. The credential is decrypted only inside the worker into a short-lived `SecureString`, supplied to the typed Siemens callback and disposed; plaintext buffers are cleared and no secret is returned. Callback failures retain their own error code/details even if Siemens wraps the exception with a generic download failure. This operation validates the existing generated password and does not remove protection.

New scratch CPUs on recent firmware also require a runtime user with the CPU's Full access function right. The scratch-only setup creates a unique password-authenticated `TiaMcpTest_*` user and a unique role assigned to that newly created device. It generates the password locally and stores it only in a CurrentUser DPAPI file with separate nonsecret metadata. It does not enable anonymous full access, disable existing protection, edit an existing user or connect to a PLC. This helper is restricted to the current worker-owned scratch project. It checks the exposed native rights and declines unknown/ambiguous matches. The implementation follows Siemens' [project-user API](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-data-of-the-user-management/functions-for-accessing-user-roles-and-function-rights/project-users) and [device-function-right API](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-data-of-the-user-management/functions-for-accessing-user-roles-and-function-rights/device-function-rights).

`simulation-download.json` contains compile/download diagnostics, target identity, relative card-file hashes and a `programRevision` derived from that generated card manifest. This is evidence of what was generated, not proof of the program currently executing. It is not a source-control revision or a safety signature. Simulator test reports include this evidence when available and explicitly mark runtime readback verification as false. Later external card/program changes are not detected by the retained manifest.

## Authentication prompts

The MCP initialization handshake includes agent instructions for authentication. When the selected project or CPU requires a user login, the agent should identify the exact target and prompt for both the local username and password. Credentials already supplied for that target in the current session should not be requested again. There is no shared default account/password, and credentials for ordinary projects must not be assumed to apply to generated scratch fixtures.

`WarningPasswordForProtectionError` is different: it concerns the password protecting confidential PLC configuration data. The adapter returns `SimulationConfigurationPasswordRequired` with structured `credentialRequest` guidance so the agent can ask for that protection password rather than label it a licensing problem. A separate user-login challenge still requires both username and password.

General project/CPU user login still uses the appropriate TIA authentication UI; MCP tools accept no raw credentials. Generated scratch confidential-configuration credentials have a separate opt-in route: `simulation_provision_scratch_credential` validates the existing CurrentUser DPAPI reference and submits it through Siemens' typed secure API only after the exact virtual target is established. This implementation does not prove successful provisioning or runtime execution; the current live blocker is recorded below. Password values are not stored in server instructions, source, client configuration, documentation or reports. Reconnect the MCP client after rebuilding to receive the updated instructions and tool descriptions.

## Licensing

The adapter does not activate a trial or confirm an Automation License Manager dialog. The V8 API's `LicenseStatus` describes current license use; it does not establish that a floating license is available for a powered-off instance. An explicit bounded `PowerOn` call supplies Siemens' documented licensing result. A license error is returned as a prerequisite failure. Trial or Test Suite-only warnings are rejected; an adapter-owned instance is powered off when a returned warning prevents continued operation. No automatic license purchase, install or activation is attempted.

A floating PLCSIM Advanced license is expected for this direct virtual-memory-card route. A Test Suite-only license has different runtime/download restrictions and is not claimed as compatible. A licensing dialog, if Siemens displays one, is left unconfirmed.

## Values and test scenarios

Supported types are `bool`, `int8`, `uint8`, `int16`, `uint16`, `int32`, `uint32`, `int64`, `uint64`, `float`, `double`, `string`, and `wstring`. Types are explicit and should match the simulator's tag metadata. Integer range checks preserve the full 64-bit range, including JSON values above signed `int64`. Real values must be finite; NaN/infinity produce bad-quality captures or failed assertions. Writes are assignments, not forces; the PLC scan can immediately overwrite them.

Scenarios require an already running selected instance, 1–64 captured signals, 1–128 steps, at most 60 seconds of requested delays and at most 500 snapshots. The interval is 20–5000 ms (default 100 ms). Actual execution including API overhead is capped at 90 seconds between polling calls. Individual vendor API calls may still take their own timeout. Sampling is wall-clock polling, not scan-accurate synchronization, and multi-signal samples are not atomic.

Each step applies optional `writes`, waits/captures for `delayMs`, then evaluates optional `assertions`. Assertions contain `symbol`, `type`, `operator` (`eq`, `ne`, `gt`, `gte`, `lt`, `lte`), `expected`, and optional absolute nonnegative `tolerance`. Strings and booleans support only `eq`/`ne`. Numeric comparisons treat values within tolerance as equal. The entire scenario's value/shape validation precedes the first stimulus write. Failed assertions, runtime failures and bad-quality samples retain a report and return an error with the report details. No automatic write rollback is claimed.

Example bridge `sim_test` arguments after discovering the actual tag spellings:

```json
{
  "instanceId": 1,
  "artifactDirectory": "D:\\03_Entwicklung\\TIA_MCP\\artifacts\\simulation",
  "signals": [
    {"symbol": "TiaMcp_TestIO.Active", "type": "bool"},
    {"symbol": "TiaMcp_TestIO.AnalogOut", "type": "int16"}
  ],
  "intervalMs": 100,
  "steps": [
    {
      "writes": [
        {"symbol": "TiaMcp_TestIO.Enable", "type": "bool", "value": true},
        {"symbol": "TiaMcp_TestIO.AnalogIn", "type": "int16", "value": 7}
      ],
      "delayMs": 500,
      "assertions": [
        {"symbol": "TiaMcp_TestIO.Active", "type": "bool", "operator": "eq", "expected": true},
        {"symbol": "TiaMcp_TestIO.AnalogOut", "type": "int16", "operator": "eq", "expected": 14}
      ]
    }
  ]
}
```

## Verification

The `tia-v21` server is configured in Codex. A fresh actual Codex app-server client verified all **43 current tools** after the update, retaining the original 40 and adding `feature_catalog`, `object_resolve` and `target_capabilities`; see `artifacts/client-connection-post-update.json`. Earlier 43-tool and 40-tool checks remain in `artifacts/client-connection-current.json` and `artifacts/client-connection-validation.json`. The existing chat's tool inventory was not hot-reloaded.

### Post-update check — 8 October 2026

PLCSIM Advanced V8.0 Update 1 is installed (runtime file `80.0.299.0`, product `8.0.0.1`). A fresh empty ordinary-token CPU reached STOP with `PowerOn: OK`, and owned cleanup passed (`artifacts/simulator-startup-update1.json`). The protected V4.0 program compiled and generated its card with zero errors/warnings, then power-on returned `WarningPasswordForProtectionError`, which the adapter rejected as `SimulationPowerOnRejected` (`artifacts/simulation-validation-update1.json`). The earlier DLL error -21 did not recur in these two runs; this does not establish stable execution. RUN, live reads/writes and all eight planned assertions were **not reached**. Both tests retained evidence and completed owned cleanup; see `artifacts/client-and-runtime-post-update.json`.

### Post-restart check and credential investigation — 8 October 2026

The installer-required restart is confirmed: Windows booted at **11:08:12 Europe/Berlin**, after installer completion at 09:07. `artifacts/runtime-proof-20261008/restart-evidence.json` records the boot events and no pending CBS/Windows Update reboot flags. The ordinary-token empty CPU again reached STOP with `PowerOn: OK`; owned cleanup and host exit passed (`artifacts/runtime-proof-20261008/empty-cpu-startup.json`). The unchanged protected-program test still returned `SimulationConfigurationPasswordRequired` / `WarningPasswordForProtectionError` after successful card generation (`artifacts/runtime-proof-20261008/protected-program-unchanged.json`). Restart alone did not resolve that warning. That unchanged run did not reach RUN, typed I/O or the baseline assertions.

The opt-in `simulation_provision_scratch_credential` implementation is built separately under `artifacts/runtime-proof-20261008/build`. The second live attempt reached recovery STOP and discovered the powered PLCSIM interface, but the CPU reported `controllerAddresses: []` (`protected-program-provisioned-2.json` in that directory). The third attempt assigned `192.0.2.1` only to the exact owned virtual CPU through `SetIPSuite` and verified the runtime readback. TIA's PLCSIM access point nevertheless reported `192.168.0.1`; the exact-address guard rejected that mismatch with `SimulationProvisionTargetAmbiguous` before secret submission (`protected-program-provisioned-3.json`). Both attempts preserved protection, returned OFF and completed owned cleanup; original card revisions remain retained. This verifies the bounded virtual address assignment, not credential provisioning or program execution.

The fourth attempt stopped earlier at `tia_start` with `TimeoutOutcomeUnknown` while an Openness access dialog was visible (`protected-program-provisioned-4.json`); it performed no provisioning. Its exact worker and TIA process subsequently exited. The fifth attempt sampled discovery 34 times during a 10-second bounded window; the runtime/TIA address mismatch persisted, and owned release, detach and host exit 0 passed (`protected-program-provisioned-5.json`).

The sixth attempt established the new PLCSIM endpoint through before/after discovery of the sole owned CPU's startup, assigned/read back that endpoint on the exact instance, and obtained an exact address match at `192.168.0.1`, then failed with `EngineeringTargetInvocationException`. Attempt seven stopped at TIA startup with `AccessDenied` after an Openness approval timeout and never created a CPU. After approval, attempt eight using the same binary passed startup and identified the native failure at `ApplyConfiguration(ConfigurationAddress)` (`protected-program-provisioned-8.json`), before any password was opened or submitted.

Attempt nine applied the corrected `1 X1` target-interface route and entered `SetPlcMasterSecret`, which threw before returning. Its report records `credentialSubmissionAttempted: true` and `credentialNativeCallReturned: false`; whether the target accepted the secret is unknown (`protected-program-provisioned-9.json`). Attempt ten instead failed at `GoOnline` before opening the credential; its offline/OFF cleanup passed (`protected-program-provisioned-10.json`). Attempt eleven lost the engineering worker. Windows application events 1000, 1026 and 1001 confirm an access violation `c0000005`; its zero-byte provisioning helper report does not identify the last native phase (`protected-program-provisioned-11.json`, `attempt11-application-events.json`). The simulator was unregistered, but `tia_detach` returned `BridgeFaulted`; exit of the exact owned TIA process was separately verified.

Attempt twelve exercised empty-CPU credential staging: the protected card was staged while OFF, the same empty CPU reached STOP with `PowerOn: OK`, and the original card was restored with matching hashes after power-off. `GoOnline` requested `TlsVerificationConfiguration` before the credential was opened or submitted; the operation returned `SimulationProvisionAuthenticationRequired` (`protected-program-provisioned-12.json`). This is a TLS verification callback, not evidence that a username/password was requested. Helper cleanup powered OFF and restored the card successfully, while its TIA online disconnect and authentication-handler removal each failed with `EngineeringObjectDisposedException`. The caller subsequently unregistered the owned simulator, detached successfully and exited with code 0. Protection remains enabled.

Attempts thirteen and fourteen cleanly rejected the TLS challenge (`verificationInfo: "certificate not matching"`, `appliedDecision: "NonTrusted"`). Each exposed exactly one certificate, but fresh CPUs produced different SHA-256 fingerprints: `4e9fe8ed3b77674c40df6bbb8e57773bef907684526234ee936b8b0f6ea30b38` and `fa5fe41d9d690d33adbf87b75c14fa91044b82ab7c49d22079a3de9f88022d93`. A fixed fingerprint cannot be reused across fresh instances. Neither attempt opened or submitted the credential. Both verified helper Offline/OFF cleanup and original-card restoration, followed by caller release/detach and host exit 0 (`protected-program-provisioned-13.json`, `protected-program-provisioned-14.json`). No certificate trust was granted.

After explicit user authorization for scoped first-use TLS trust, attempt fifteen passed generated-credential provisioning, helper Offline/OFF cleanup and restoration of the unchanged protected card. Independent Python verification matched the original manifest before normal protected power-on. The CPU reached RUN; four typed signals had good quality, direct BOOL/INT16 writes read back correctly, and all eight scenario assertions passed. Owned release/detach and host exit 0 passed (`protected-program-provisioned-15.json`). `evidence-snapshot-20261008T111753556081Z.json` independently checks the exact assertions, identity, TLS evidence, card revision and retained native reports. This was the first successful baseline. Earlier attempt eleven's crash phase remains unknown.

**Attempt seventeen is the current protected-baseline proof.** [Its report](../../artifacts/runtime-proof-20261008/protected-program-provisioned-17.json) again records `completed: true`, `simulationVerified: true`, protected RUN, four good-quality typed signals, BOOL/INT16 write readback and all eight assertions, with unchanged card restoration, helper Offline/OFF cleanup, owned release/detach and host exit 0. The [13:36:02 Europe/Berlin evidence snapshot](../../artifacts/runtime-proof-20261008/evidence-snapshot-20261008T113602982412Z.json) passes every baseline check and retains hashes of the matching native reports and [attempt-seventeen binary manifest](../../artifacts/runtime-proof-20261008/provisioned-17-binaries.json). That manifest records engineering worker SHA-256 `e5a3ac0857c82ab371dbb51b4ca71491404d4d9609112898395200dd087079de` and the exact host/core/simulator files. This identifies the tested build, not arbitrary current binaries. The successful detach reports connection disposal; `processExitVerified` remains false.

The [second nine-family runtime run](../../artifacts/block-tests/runtime-all-20261008-owned-tls-02/index.json) remains unqualified. Project locks, provisioning/restoration/isolation failures, native timeouts, a disconnected engineering worker, a report-persistence failure and one elapsed-time assertion failure are recorded; see the [runtime-proof TODO](../plans/runtime-proof-todo.md). Its completed Boolean report has six passing cases and two `project_open` blocks (`truth-3`, `truth-7`) reporting that the project was already open under the same user and computer. The first corpus run's discovery failure and later native worker loss remain retained in [block-testing history](block-testing.md). Later observations found the audited Boolean TIA PIDs absent, but did not establish their state at the failed opens or prove lock release. Baseline success therefore does not qualify all nine families, graphical profiles or scan/timer accuracy.

### Before Update 1 — historical diagnostics

Build succeeds against the installed V8 x64 API and V21 net48 API. The saved-project pipeline generated a protected CPU 1511 V4.0 memory card with zero compile/download errors or warnings, verified three recovery-journal hops and archive hashes, and supplied the generated configuration password through the native secure callback. The card manifest is retained in `artifacts/simulation-validation-first.json` and later validation reports.

A fresh empty owned CPU once powered on to STOP under the ordinary Windows token with runtime result `OK` and successful cleanup (`artifacts/diagnostics-agent/startup-process-trace.json`). After restoring the diagnostic policy changes, another ordinary-token empty test failed with `ErrorLoadingDll` (-21), while cleanup succeeded (`artifacts/simulator-startup-restored.json`). That success did not establish stable startup. The saved-program pipeline also failed with -21, including after detaching TIA. These earlier runs did not reach RUN, live reads/writes or the eight assertions.

Two loader observations narrow the investigation. Direct loading of the fixed-base Vplc1500 DLL fails with Win32 error 623 in the sandbox, whose ASLR flags are `0xF` (including forced relocation and rejection of stripped images), while the ordinary-token probe reports `0x5` and loads it successfully. Separately, the full-program runtime trace shows private memory allocated across the DLL's required base address `0x18000000` before the failed load. The DLL has no relocation table. This overlap is a concrete probable cause of the full-program failure, not a verified repair or complete root-cause determination. See `artifacts/diagnostics-agent/loader-sandbox.json`, `loader-normal-user.json`, `native-image-properties.json` and `detached-pipeline-process-trace.json`. `scripts/Probe-Environment.ps1` now reports ASLR flags read-only, with an explicit caveat that the probe's policy may differ from worker/runtime policies.

Two temporary per-application allocation-randomization trials, BottomUp plus HighEntropy and BottomUp alone, both failed with `InstanceNotRunning` (-14); see `artifacts/simulation-validation-allocation-randomized.json` and `artifacts/simulation-validation-bottomup.json`. Both changes were fully restored: `artifacts/simulator-allocation-policy.json` records `applied: false` and a completed final restoration transition. No reinstall, binary patch, security-feature disable or license activation was performed. Windows 11 Pro 26H2/build 26300.9550 is outside the V21 [documented OS list](https://docs.tia.siemens.cloud/r/en-us/v21/installation/system-requirements-for-installation/general-software-and-hardware-requirements), which includes Windows 11 Pro 24H2; this remains a compatibility concern rather than proof of the failure's cause.

Before installation, Update 1 was a repair candidate; the earlier support-page retrieval limits and recommended installation prerequisites remain recorded in `artifacts/diagnostics-agent/supported-next-step.json`. The pre-update task outcome is retained in `artifacts/client-and-runtime-verification.json`. Current installed-version and test evidence appears above.

### Reproduction and offline checks

Reproduce isolated native startup with `python scripts/validate_simulator_startup.py --report artifacts/simulator-startup-current.json` under the normal Windows user token. For the saved-program pipeline, run:

```powershell
python scripts/validate_simulation.py `
  --project <scratch.ap21> --credential-metadata <metadata.json> `
  --credential-journal <journal.json> --report <fresh-report.json> `
  --host <absolute-host.exe> --worker <absolute-engineering-worker.exe> `
  --sim-worker <absolute-simulator-worker.exe>
```

Repeat `--credential-journal` for every recovery hop. Add `--provision-runtime-credential` to exercise the generated-credential provisioning operation before normal power-on; use a build exposing that operation. Inputs and reports must remain inside this repository's `artifacts` directory; choose fresh report names to preserve earlier evidence. The runner requires observed RUN, exactly four good-quality typed values, and direct write readback of `Enable=false` and `AnalogIn=0`. Its four-step scenario checks eight assertions: `Active` follows repeated `Enable` switching, and `AnalogOut` equals twice positive, negative and zero `AnalogIn`. It retains structured failures and cleans up its owned instance, including ID zero. Attempts fifteen and seventeen passed these runtime checks on their recorded local builds.

After explicit authorization for first-use certificate trust on the owned empty CPU, add `--trust-owned-empty-cpu-certificate` together with `--provision-runtime-credential`. The trust flag is rejected without provisioning and is recorded separately in the report. Omitting it preserves TLS rejection; there is no default or reusable external fingerprint.

The pure self-test mode exits before resolving or initializing Siemens API assemblies:

```powershell
& .\src\TiaMcp.Simulator.V8\bin\Debug\net48\TiaMcp.Simulator.V8.exe --self-test --self-test-directory 'D:\03_Entwicklung\TIA_MCP\artifacts'
```

Twenty-one checks passed under the ordinary Windows user token: integer precision, JSON unsigned-64 conversion, large real comparison, tolerances, NaN rejection, typed comparisons, complete/incomplete/oversized UTF-8 framing, storage path rejection (including drive-relative paths), DPAPI roundtrip, expiration, ownership mismatch and ciphertext tamper. Reports are retained in uniquely named `artifacts/simulator-selftest-*` directories. These checks do not start a simulated CPU and do not replace the live engineering/runtime integration test.

Primary references: [Siemens V21 offline Windows-folder download](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-downloading-data-to-plc-device/downloading-plc-to-a-windows-folder), [Siemens Test Suite virtual memory-card example](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-test-suite/openness-support/functions-for-application-test/execution-of-test-cases), [V8 API manual](https://support.industry.siemens.com/cs/attachments/109997789/s7-plcsim_advanced_function_manual_API_en-US_en-US.pdf) (operating-state/license results), and the installed V8 manuals in `C:\Program Files (x86)\Siemens\Automation\PLCSIMADV\documents\en-US`.
