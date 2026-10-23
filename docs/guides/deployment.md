# Local and VM deployment

Current deployment/reliability work and its exact evidence are tracked in [deployment-reliability-status.md](../verification/deployment-reliability-status.md). New releases use isolated source builds and write `<bundle>.mcp.json` beside the immutable bundle; regenerate that external configuration after relocation. The detailed launch/verification procedure is in [remote-qualification.md](remote-qualification.md).

Run `scripts/Package.ps1` to produce a versioned bundle and ZIP under `artifacts/releases`. It contains the MCP host, isolated engineering/simulator workers, launcher, configuration generator, dependency license notices and SHA-256 manifest. Siemens binaries and licenses are excluded. Copy or extract the entire bundle to the Windows machine running TIA V21.

That full bundle requires the Siemens SDK build environment. On a workstation
with no Siemens products, use the [Host-only build and offline workflows](../../README.md#working-without-any-siemens-installation).
The portable package below is separate from full-bundle installation and rollback.

The destination needs .NET 10 Runtime (ASP.NET Core shared framework, used by the host), .NET Framework 4.8, licensed TIA V21 with Openness, and the engineering user's effective Openness membership. PLCSIM Advanced V8 and its runtime license are needed for simulator tools. Run `scripts/Probe-Environment.ps1` on that machine. Use `scripts/Start-TiaMcp.ps1 -Diagnose` to verify discovery under the actual user token.

Start qualification by listing the exact package's tools and querying `feature_catalog`, which can be read without starting native workers. Tool counts vary with package version and explicit qualification gates; use the actual inventory instead of historical counts. Then use `object_resolve` and `target_capabilities` inside the destination's TIA session. The catalog's recorded evidence remains tied to its tested environment; moving a package does not qualify a different CPU, firmware or installed option automatically.

`<bundle>.mcp.json` is the external MCP stdio configuration for the generated bundle location. Keep it outside the immutable bundle and regenerate it after moving the bundle:

```powershell
.\scripts\New-McpConfiguration.ps1 -BundlePath 'C:\TiaMcp' -StrictBundle |
    Set-Content -LiteralPath 'C:\TiaMcp.mcp.json' -Encoding UTF8
```

For a Windows VM or another engineering PC, keep both workers and TIA on that machine. Use an existing authenticated OpenSSH connection to transport MCP stdio:

```powershell
.\scripts\New-McpConfiguration.ps1 -BundlePath 'C:\TiaMcp' -SshHost 'engineering-vm'
```

The generated client command uses `ssh.exe -T`, batch authentication and strict host-key verification. Configure the host alias, accepted host key and key-based login through the ordinary OpenSSH setup first. The generator does not install SSH, modify firewall rules, distribute credentials, or change the MCP client's settings. Paths and projects resolve on the remote TIA machine. Retrieve exports with `artifact_read` in bounded base64 chunks and verify the returned SHA-256 after reassembly.

Keep project paths short: TIA enforces a 143-character project-directory limit. The configuration generator and launcher accept `-ArtifactsDirectory 'C:\TiaMcpData'` to separate project/checkpoint storage from a deeply nested bundle. The generated checkout-local configuration uses the repository's existing artifact directory. After moving a bundle, regenerate paths for its new machine. `-TiaApiDirectory` and `-SimulatorApiDirectory` on the launcher handle non-default installations; the simulation override is forwarded to both workers.

An SSH login can run in a different Windows session from the graphical engineering desktop. TIA discovery and Openness authorization must be verified in that session; use a worker-owned headless instance there when cross-session attachment is unavailable. Siemens access dialogs may require an initial interactive authorization. A VM deployment is only verified after discovery, isolated project creation/compile and simulator tests pass inside that VM. Local packaging tests alone do not establish that result.

Existing VM proof is accepted for this continuation; repeating it is not a delivery gate. Other destination-specific evidence can be collected with the [conformance runner](conformance-fixtures.md). The Python tooling uses the standard library; current file-hashing qualification helpers require Python 3.11 or newer. `run_conformance.py` defaults to a dry run; `--live --phase engineering` explicitly starts a new owned scratch project.

After installing PLCSIM Advanced V8.0 Update 1 (file version `80.0.299.0`), the required Windows restart is confirmed at 11:08:12 Europe/Berlin on 8 October 2026, after installer completion at 09:07 (`artifacts/runtime-proof-20261008/restart-evidence.json`). The post-restart empty CPU reached STOP with `PowerOn: OK` and successful cleanup (`empty-cpu-startup.json` in that directory). The unchanged protected program still returned `SimulationConfigurationPasswordRequired` / `WarningPasswordForProtectionError` after successful card generation (`protected-program-unchanged.json`). That unchanged run did not reach RUN, typed reads/writes or the eight assertions. The opt-in generated-credential provisioning operation is built under that directory's `build`. Earlier attempts retained runtime/TIA address mismatch evidence and a separate TIA startup authorization timeout. The corrected target-interface route reached `SetPlcMasterSecret` in attempt nine, but the call threw and target acceptance is unknown. Attempt ten failed earlier at `GoOnline`. Attempt eleven crashed the engineering worker with access violation `c0000005` (`protected-program-provisioned-11.json`, `attempt11-application-events.json`); its zero-byte helper report leaves the last native phase unknown. Simulator release passed, while detach recorded `BridgeFaulted`; exit of the exact owned TIA process was separately verified. Attempt twelve verified empty startup and restoration of the unchanged original card, then stopped at `TlsVerificationConfiguration` before opening or submitting the credential (`protected-program-provisioned-12.json`). Helper online disconnect/handler removal failed with `EngineeringObjectDisposedException`, while CPU OFF/card restoration and caller release/detach passed with host exit 0. Protection remains enabled. The nine-family runtime corpus remains gated on the protected baseline passing all eight assertions. Package startup, memory-card generation or pure simulator tests do not establish RUN/read/write/scenario execution.

Attempts thirteen and fourteen then explicitly rejected TLS with `certificate not matching`, before the credential was opened or submitted; helper Offline/OFF/card restoration and caller release/detach all passed with host exit 0. Different fresh-CPU certificate fingerprints prevent reuse of a static pin. After the user authorized first-use trust scoped to each owned empty CPU, attempt fifteen passed credential provisioning and restored-card verification, then protected RUN, typed read/write/readback and all eight baseline assertions. Offline/OFF cleanup, owned release/detach and host exit 0 passed (`protected-program-provisioned-15.json`, `evidence-snapshot-20261008T111753556081Z.json`). This qualifies the recorded local build and satisfies the baseline gate; the nine-family runtime corpus remains pending.

The saved-program validator accepts `--host`, `--worker` and `--sim-worker` to select an isolated build, plus `--provision-runtime-credential` to explicitly attempt generated scratch credential provisioning. The block runner accepts the same provisioning flag only with `--live`; both native block modes require an explicit engineering worker, and live mode also requires the simulator worker. Optional `--trust-owned-empty-cpu-certificate` requires provisioning and explicit first-use authorization. Its default is false; the worker pins only the certificate observed within that attempt after owned-target and validity-date checks. Caller-authorized trust does not independently authenticate certificate identity. See [runtime reproduction](simulation.md#reproduction-and-offline-checks) and [block testing](block-testing.md).

The bundle includes the fixture catalog, conformance scripts and contracts. `smoke_package.py --bundle <path> --report <fresh-report>` verifies its manifest and ZIP, then checks its external configuration and launcher using read-only tools. Use `--configuration <path>` for an explicitly relocated configuration. When running bundled fixtures, select the packaged executable explicitly because the runner's default host path targets a development checkout:

```powershell
python .\scripts\generate_conformance.py
python .\scripts\run_conformance.py --live --phase engineering --host 'C:\TiaMcp\host\TiaMcp.Host.exe'
```

Keep each run's reports, exact manifest snapshots and referenced native checkpoints together. The v0.3 package smoke report is `artifacts/package-smoke-v03.json`; the earlier `artifacts/package-smoke.json` remains historical v0.2 evidence.

Every MCP client connection owns separate workers and session state. Do not point concurrent editing clients at the same project. Bridge interruptions have uncertain outcomes and are never replayed automatically; inspect the change journal and TIA state before resuming.

## Portable package without Siemens

Build on Windows with PowerShell 7 and the .NET SDK from `global.json`:

```powershell
$bundle = ./scripts/Package.ps1 -Offline
./scripts/Verify-TiaMcpBundle.ps1 -BundlePath $bundle -Strict
```

The isolated source snapshot builds only Host/Core and the portable libraries.
It records exact source/output hashes, writes an adjacent `.zip.sha256` checksum
and emits `bundleKind: offline`. No native
worker or Siemens DLL is packaged. Full engineering packaging remains the default
without `-Offline`; isolated build and package kinds must match.

The destination needs Windows and the .NET 10 ASP.NET Core shared framework of
the published Host's architecture. It does not need a Siemens product, license,
.NET Framework worker runtime or SDK. This is framework-dependent packaging,
not a self-contained installer. Python is needed for validation, not Host startup.

Keep mutable data and machine-specific configuration outside the bundle. After
extracting or moving the entire ZIP, generate configuration for the new path:

```powershell
./scripts/New-McpConfiguration.ps1 -BundlePath 'C:\TiaMcpOffline' `
    -ArtifactsDirectory 'C:\TiaMcpData' -StrictBundle `
    -GitExecutable 'C:\Program Files\Git\cmd\git.exe' |
    Set-Content -LiteralPath 'C:\TiaMcpOffline.mcp.json' -Encoding UTF8
```

Run the generator from the extracted bundle's `scripts` directory or use its
absolute path. Omit `-GitExecutable` when not using VCI; Git is not redistributed.
The generated server key remains `tia-v21` for compatibility. Import the external
configuration into your MCP client; generation does not modify client settings.
Direct Host invocation must also specify an external `--artifacts` directory.

The portable binary disables native workers at build time. Native calls such as
`tia_discover` and `sim_status` return `NativeWorkersDisabled`; invalid requests may
still fail earlier validation. The full catalog remains visible with its existing
qualification limits. Native diagnosis/qualification flags are rejected, and the
launcher refuses native API overrides and interactive engineering sessions.
`--no-native-workers` enables the same refusal in a regular development Host.
This setting is not a network sandbox: explicit endpoint inventory tools remain
separate. Supplied documents do not prove native compatibility or authenticity.

`third-party/host-packages.json` matches published Host NuGet IDs/versions to
retained notices and their hashes. Shared-framework and external Git dependencies
are identified as separate prerequisites. Hash manifests establish consistency,
not publisher identity. No bit-for-bit reproducible ZIP claim is made: timestamps,
build paths and package IDs vary.

From the source checkout, with the SDK and Python test dependencies installed, run:

```powershell
./scripts/Test-OfflinePackage.ps1
# Or verify an existing portable bundle and its adjacent ZIP:
./scripts/Test-OfflinePackage.ps1 -BundlePath 'C:\TiaMcpOffline'
```

The runner checks deployment guards, exact ZIP inventory/hashes/notices, generated
configuration, native refusal with existing executable paths, clean Host shutdown,
Git upload/commit/restart, Explorer resource delivery and relocation to a fresh
Unicode path. It reuses GRAPH, OPC UA, VCI, translation and saved-review/diagnostic
MCP workflows against the relocated binary. Reports and data stay outside the
immutable package. CI runs this on a Windows runner without Siemens.
See [the validation record](../verification/offline-package-20261016.md) for exact
executed evidence; native compilation/runtime, installer rollback, remote SSH and
desktop client acceptance remain separate.

## Historical Codex connection evidence

The following records describe earlier engineering-workstation connections.
They do not establish the current workstation's installed products, active client
configuration or availability of the referenced ignored artifacts. The present
offline execution environment has no Siemens products installed.

At the time of the retained authentication-guidance validation, the local Codex user configuration included an enabled `tia-v21` stdio server pointing to the verified isolated build under `artifacts/authentication-build/bin/Release`, with the host in `net10.0-windows` and both workers in `net48`. Active clients had locked the normal Debug and Release outputs, so their processes were left running. Only these three executable paths were changed; the artifact path, 300-second host/tool timeouts, 60-second startup timeout and separate `tia-portal` integration were preserved. A fresh Codex client verified **44 tools**, retaining every original 40 and previous 43; `project_index` is a concurrent checkout addition, independent of the authentication-guidance change (`artifacts/client-connection-authentication.json`). Reconnect or restart an existing client to receive the persistent authentication instructions in the MCP handshake and updated tool descriptions (`artifacts/authentication-guidance-validation.json`). The instructions distinguish target-specific user login from the confidential configuration-protection password and direct entry through TIA's authentication UI; they do not add automatic login or raw-credential tool parameters.

On 8 October 2026, a fresh Codex app-server connection returned exactly **40 tools** for the earlier v0.2 `tia-v21` build through `mcpServerStatus/list`. `artifacts/client-connection-validation.json` preserves that historical tool list, schemas and server identity unchanged.

A subsequent fresh Codex app-server connection verified the then-current v0.3 checkout exposed **43 tools**, including all original 40 plus `feature_catalog`, `object_resolve` and `target_capabilities`. The same inventory was verified again after installing PLCSIM Advanced V8.0 Update 1; `artifacts/client-connection-post-update.json` records the names and schemas, original-40 subset comparison, configured executable path and SHA-256, and the historical report's SHA-256. The earlier `artifacts/client-connection-current.json` and original 40-tool report remain unchanged. This verification read the saved configuration without changing it, creating a chat, calling a model, or invoking engineering or simulator tools. It verifies a fresh Codex client connection; hot reload of an already-running desktop chat was not verified. Reconnect an existing client after updating the host and verify its actual tool list.

Codex uses its shared user configuration for MCP servers; see the [official MCP documentation](https://learn.chatgpt.com/docs/extend/mcp?surface=cli). Use `codex mcp get tia-v21` to inspect this entry. Keep the checkout and its built executables at the configured paths, or update the entry when deploying a package elsewhere.
