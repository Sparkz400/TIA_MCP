# Verification history

Historical record. See [current status](../STATUS.md) and [remaining TODO](../TODO.md) for the latest checkpoint and work order.

The [9 October continuation](continuation-20261009.md) records the clean stopping
checkpoint: 79 public tools, 149 catalog features, a verified package, passing full
regression and 28 newly completed standard LAD/FBD engineering cases. The full
runtime corpus stopped incomplete after 48 passing cases and 371 assertions.
Earlier dated results below retain their original binary and scope limits.

## 8 October 2026 — v0.3 general-purpose increment

The current host exposes **43 MCP tools** and **79 catalog features**. The three added tools are `feature_catalog`, `object_resolve` and `target_capabilities`. The catalog distinguishes vendor API evidence, bridge implementation, target availability and observed qualification. Its backlog entries are visible requirements, not extra callable tools.

A fresh actual Codex app-server client loaded the configured `tia-v21` server after the simulator update and verified all **43 tools**, including every original 40-tool entry and the three additions (`artifacts/client-connection-post-update.json`). This qualifies discovery in the current client/build; the existing chat's tool inventory was not hot-reloaded. Earlier client evidence, including the 40-tool build, is preserved.

The generated corpus passed **14 live engineering fixtures with 73 successful steps** on TIA V21 / CPU 1511-1 PN V4.0. Exact manifests, source hashes, native responses and clean detach evidence are retained in `artifacts/conformance/live-first`. The final integrated Release solution builds with zero errors/warnings, and **105 pure tests pass: 63 C#, 21 Python conformance and 21 simulator validation cases**. Feature-contract, deployment and worker-lifecycle checks also pass. See the [current evidence ledger](generalization-verification.md) and [fixture specification](../guides/conformance-fixtures.md) for the precise boundaries.

The catalog endpoint passed without native workers available. Read-only target-capability probes passed on standard and F-CPU scratch copies, preserving the observed inventory, software/hardware metadata and sampled native block content. That is observational preservation evidence, not full project binary equivalence. Software-unit mutation qualification is a separate test obligation; root-scope corpus success does not prove it.

After call-identity hardening, `artifacts/analysis-regression-v03.json` verified four exact caller/callee pairs and five native call sites on a read-only reopen of the owned corpus project. This includes both multi-instance calls and the standard Main/FB/FC chain. TON/CTU instruction endpoints remain explicitly unresolved because Siemens provides no engineering-block object for them. The observed FB calls resolved directly from native identities; this regression does not claim to exercise the InstanceDB fallback branch.

VM execution is unverified. Individual array-element start values are not normalized in parsed block-interface sections, although original native XML is retained. Broader graphical instruction editing, protected/safety scope combinations and additional hardware/TO families require their own fixtures.

## Post-update runtime check — 8 October 2026

PLCSIM Advanced V8.0 Update 1 is installed: runtime file `80.0.299.0`, product `8.0.0.1`. The fresh empty ordinary-token CPU reached STOP with `PowerOn: OK` and successful cleanup (`artifacts/simulator-startup-update1.json`). The saved V4.0 program compiled and generated its protected card without errors/warnings, then power-on returned `WarningPasswordForProtectionError` (`artifacts/simulation-validation-update1.json`). DLL error -21 did not recur in these checks; stable execution is not established. RUN, live reads/writes and all eight planned assertions were not reached. The combined result is `artifacts/client-and-runtime-post-update.json`.

The required restart is now confirmed: Windows booted at **11:08:12 Europe/Berlin on 8 October 2026**, after installer completion at 09:07 (`artifacts/runtime-proof-20261008/restart-evidence.json`). The post-restart empty CPU passed STOP startup with `PowerOn: OK`, owned cleanup and host exit 0 (`empty-cpu-startup.json` in that directory). The unchanged protected baseline still returned `SimulationConfigurationPasswordRequired` / `WarningPasswordForProtectionError` after card generation (`protected-program-unchanged.json`); it did not reach RUN, typed I/O or the eight assertions.

The guarded opt-in `simulation_provision_scratch_credential` route is implemented and built under `artifacts/runtime-proof-20261008/build`. The second live attempt established that the PLCSIM interface appears while powered at recovery STOP, but the CPU exposed no controller address. Attempts three and five verified runtime assignment/readback of `192.0.2.1`, while TIA exposed `192.168.0.1`; the exact-target guard rejected the mismatch before secret submission. Attempt five retained 34 discovery samples in a 10-second window (`protected-program-provisioned-5.json`). Attempt four separately timed out at `tia_start` with an Openness access dialog visible and never reached provisioning; its worker and TIA process subsequently exited.

After exact owned-target matching was established, attempt eight isolated a failure in route application. Attempt nine applied the corrected `1 X1` target-interface route and entered `SetPlcMasterSecret`, but the native call threw; target acceptance of the submitted secret is unknown. Attempt ten failed at `GoOnline` before the credential was opened and completed offline/OFF cleanup. Attempt eleven ended with `WorkerDisconnected`; Windows application events confirm access violation `c0000005` (`protected-program-provisioned-11.json`, `attempt11-application-events.json`). Its provisioning helper report is empty, so the last native phase is unknown. Owned simulator release succeeded, `tia_detach` recorded `BridgeFaulted`, and exit of the exact owned TIA process was separately verified.

Attempt twelve verified empty startup while the protected card was staged, then OFF and restoration of the unchanged original card hashes. It stopped at `TlsVerificationConfiguration` during `GoOnline`, before the credential was opened or submitted (`protected-program-provisioned-12.json`). Helper online disconnect and authentication-handler removal failed with `EngineeringObjectDisposedException`; helper CPU power-off/card restoration and caller simulator release/TIA detach succeeded, with host exit 0. Protection remains enabled. At that stage the investigation had narrowed to TLS verification; attempt eleven's unknown native phase remains unresolved. [Earlier loader diagnostics](../guides/simulation.md#before-update-1--historical-diagnostics), including the probable fixed-base memory overlap and fully restored policy trials, remain historical evidence.

Attempts thirteen and fourteen each reported `certificate not matching` and explicitly selected `NonTrusted`, before opening or submitting the credential. Helper Offline/OFF/card restoration and caller release/detach passed with host exit 0. Their certificate SHA-256 values differ across fresh CPUs, so matching subjects/dates and a previous fingerprint do not authenticate a new instance. The `--trust-owned-empty-cpu-certificate` option requires provisioning and explicit first-use authorization; it pins only within each attempt and preserves `certificateIdentityIndependentlyVerified: false`. Default trust is rejected.

After user authorization, attempt fifteen passed the protected baseline: successful generated-credential provisioning, unchanged card restoration and independent hash verification, `PowerOn: OK`, observed RUN, four good-quality typed signals, BOOL/INT16 write readback, and all eight scenario assertions. Helper Offline/OFF cleanup, owned simulator release, TIA detach and host exit 0 passed. `artifacts/runtime-proof-20261008/evidence-snapshot-20261008T111753556081Z.json` binds that first successful baseline to matching retained reports.

**Attempt seventeen reconfirmed every protected-baseline gate** in the current report (`artifacts/runtime-proof-20261008/protected-program-provisioned-17.json`). The 13:36:02 Europe/Berlin snapshot (`artifacts/runtime-proof-20261008/evidence-snapshot-20261008T113602982412Z.json`) verifies RUN, typed reads/writes/readback, all eight assertions, scoped TLS trust, unchanged original-card restoration, retained report agreement and successful cleanup/host exit. It also hashes the binary manifest (`artifacts/runtime-proof-20261008/provisioned-17-binaries.json`), whose engineering worker SHA-256 is `e5a3ac0857c82ab371dbb51b4ca71491404d4d9609112898395200dd087079de`; the manifest records the host/core/simulator files separately. Detach establishes owned connection disposal, not independently verified TIA process exit.

The second nine-family runtime run (`artifacts/block-tests/runtime-all-20261008-owned-tls-02/index.json`) remains unqualified. It finished at 16:01 Europe/Berlin with 32 passed, 30 blocked and 3 failed cases; 104 assertions passed, one failed and 372 authored assertions had no evaluated result. The final snapshot (`artifacts/runtime-proof-20261008/evidence-snapshot-20261008T140210455644Z.json`) reconfirms the protected baseline and keeps full-corpus qualification false. Project locks, provisioning/restoration/isolation failures, native timeouts, a disconnected engineering worker, a report-persistence failure and one elapsed-time assertion failure are recorded in the [runtime-proof TODO](../plans/runtime-proof-todo.md). Its completed Boolean family has six passing cases and two project-open lock blocks. The first corpus run retained a discovery failure and later native worker loss; those outcomes are not erased by the baseline pass. All nine families, their retained-state sequences and timing observations still require their own successful runtime evidence; see [block testing](../guides/block-testing.md).

## Earlier v0.2 engineering and simulation evidence

The earlier five-area prototype exposed 40 MCP tools. This historical table distinguishes tested operations from API coverage and outstanding deployment/runtime checks. Its package and client counts refer to that build, not the current 43-tool host. All mutation tests used task-created scratch projects or their recovered copies; no physical PLC was connected.

| Area | Verified result and evidence |
|---|---|
| Build and automated checks | Debug solution builds with zero errors/warnings. 42 bridge, artifact, native-evidence, source/XML and recovery-path tests; 21 simulator validation tests; deployment-configuration tests and the compiled process-lifetime guard all pass. |
| Standard inspection | Scratch inventory, DB/FB interfaces, tag tables, UDT members, hardware addresses and TO parameters read through actual MCP. Native XML and SHA-256 artifacts retained. Software-unit and detailed channel coverage remains API-dependent. |
| Call structure and cross-references | Native call graph resolves `Main → TiaMcp_EnableGate → TiaMcp_IsEnabled`. Static trace and change-impact identify the caller and instance dependencies. The tested CPU needs per-block cross-reference services; the worker reports that fallback and bounded coverage. |
| Four languages | SCL, AWL/STL, KOP/LAD and FUP/FBD fixture generation/import, compile, export and idempotent replay passed. LAD/FBD symbolic operand changes and checkpoint restoration passed. General graphical rewiring is outside the structured-edit implementation. |
| Recoverable writes | UDT source, standard block rename/delete, offline IP/subnet edits and simulation compilation settings passed. Each apply creates an archive and retains its outcome journal; rename/graphical edits were successfully restored into new directories. |
| Write conflict guards | Wrong preview hashes and a stale second preview were rejected before checkpoint/mutation. Same-key replay left the outcome journal unchanged; a different key was rejected. The 19-step live test passed in an archive-retrieved scratch copy. See `artifacts/write-guards-final.json`. |
| Restart recovery | A fresh MCP host read the persisted change journal, rejected an incorrect archive hash, retrieved the valid archive into a new owned project and exported its restored FC. See `artifacts/recovery-restart.json`. |
| Hardware and TOs | Catalog-selected DI module plug/address inspection/rename/delete, PID_Compact 3.0 create/parameter read-back/export/delete and read-only parameter preflight passed. UDT/tag-table native XML roundtrips passed. See [hardware/TO evidence](../verification/hardware-to-verification.md). |
| Safety | Native runtime-group/signature inspection and an unchanged user F_FBD FB XML roundtrip passed on CPU 1511F-1 PN. Standard-source overwrite of that F-block was rejected. This does not validate altered safety logic or commissioning. See [safety evidence](../verification/safety-verification.md). |
| Simulator download and startup | Before Update 1, V4.0 compile/card generation passed with zero errors/warnings, including the protected password callback and three recovery-journal links. One empty ordinary-token CPU reached STOP (`artifacts/diagnostics-agent/startup-process-trace.json`), but the post-restoration empty retest (`artifacts/simulator-startup-restored.json`) and full-program pipeline failed with native `ErrorLoadingDll` (-21). These runs did not verify RUN, live reads/writes or eight scenario assertions. Historical loader evidence and fully restored per-application trials are retained in [simulation verification](../guides/simulation.md#verification); the current post-update result appears above. |
| Packaged launcher | Release build passed with zero errors/warnings. The generated PowerShell/MCP configuration launched the actual packaged host and both workers; all 40 tools, TIA discovery and simulator status passed. Manifest file hashes matched, with no Siemens DLLs redistributed. See `artifacts/package-smoke.json`. |
| Codex client connection | A fresh actual Codex app-server client discovered all 40 tools from this historical build (`artifacts/client-connection-validation.json`). The separate current 43-tool client verification is recorded above. The existing chat's inventory was not hot-reloaded. |
| VM deployment | Local/SSH stdio launchers, configuration generation and packaging are implemented. No VM was supplied; execution inside a VM is unverified. |

The main offline evidence is `artifacts/milestones-sixth.json` (64 recorded steps, no engineering failures). That historical runner set `completed` based on engineering alone; its recorded simulation limitation must not be interpreted as successful runtime execution. The runner has since been corrected to require verified simulation behavior unless explicitly invoked with `--skip-simulation`.

Native integration uncovered and corrected several real API constraints: generated safety system blocks cannot be exported like user blocks, graphical V21 XML requires its namespace attribute element, recovery directories must fit TIA's path limit, writable TO parameters need native access checks, and scratch CPUs need both protected confidential configuration and a password-authenticated runtime user for hardware compilation. Generated scratch credentials remain Windows CurrentUser DPAPI-protected and are not returned as plaintext through MCP.

Build/test artifacts and reports are local and ignored by source control. They are evidence for the specified installed CPU, firmware, TIA and PLCSIM versions, not a guarantee of every device, technology object, programming construct or safety operation.

## Initial prototype verification (historical)

The initial V21 MCP host and Openness worker have passed an end-to-end test on this engineering PC. This establishes the current prototype's behavior, not completion of the entire implementation plan.

| Check | Result |
|---|---|
| Full solution build | Successful; zero warnings/errors |
| Package-free bridge tests | 31 passed |
| Compiled worker lifetime guard | Passed; no destructive process-proxy disposal |
| Actual MCP initialize and tools/list | Passed; 11 tools exposed |
| V21 discovery | Passed in the normal Windows user session |
| Separate worker-owned TIA startup | Passed |
| Catalog-selected CPU | CPU 1511-1 PN, `OrderNumber:6ES7 511-1AL03-0AB0/V4.1` |
| Scratch block generation | FC, FB, instance DB and cyclic Main OB generated from SCL |
| Scratch software compilation | Successful; zero errors and warnings |
| Scratch save and exports | Project checkpoint saved; four block XML files exported |
| Native cross-references | Expected FC-to-FB reference observed |
| Read-only attachment to an existing project | Passed |
| Existing standard-block inventory | 299 blocks; covered scope not truncated |
| Existing language exports | KOP/LAD and SCL exported successfully |
| Detach from existing project | Passed; TIA still discoverable and open afterward |
| MCP host shutdown | Exit code 0 |

The live integration run completed 17 recorded steps. Its detailed report is `artifacts/smoke-integration.json`. It contains local project metadata/source exports and is deliberately excluded from source control. Scratch files are under `artifacts/scratch/TiaMcpScratch_20261008_042302_476a3076`. Original exports include SHA-256 hashes.

FUP/FBD and AWL/STL samples were not observed among eligible blocks in the inspected inventory. Their export paths remain unverified. The inventory reports its omitted scope explicitly: software units, tags/types, detailed channels, TO parameters, safety administration, libraries, and snapshot/revision consistency. These gaps are not represented as empty project data.

The installed PLCSIM Advanced V8 public API and standard PLCSIM V21 were verified through file metadata. Simulator initialization, licensing, download, live values and simulated execution have not yet been tested. No physical PLC connection or download was performed.

Two integration defects were found and fixed before the passing run. An SCL Main OB required explicit optimized access; its corrected version compiled successfully. More seriously, the initial discovery code incorrectly disposed `TiaPortalProcess` objects, which closes the associated TIA instance. This closed the user's existing TIA window. The user confirmed there were no unsaved changes, and the saved project was verified open again. The code now confines those proxies to a non-disposing boundary, with a compiled-IL regression guard and unsafe synthetic fixtures. The passing integration test verifies preservation of the existing process after discovery, attachment and detach.

The restricted execution sandbox rejects current-user-only named-pipe connections even for the test's fake workers. Protocol unit cases and compilation run there; process transport and TIA integration were verified in the normal Windows session through reviewed execution. Pipe permissions were retained. No Windows groups, TIA allowlist, firewall rules or network configuration were modified.

Reproduce with `scripts/Build.ps1`, `scripts/Test.ps1 -SkipBuild`, and the explicit integration commands documented in the repository README. A fresh `--scratch` run creates a fresh project; it does not modify the prior fixture or an existing project. The runtime bridge never automatically retries a timed-out or failed write.
