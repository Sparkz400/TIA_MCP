# Deployment qualification

The release launcher verifies its bundle before starting the MCP host. The qualification runner uses the **actual MCP configuration**, including the same local PowerShell launcher or OpenSSH command that an MCP client uses. It does not replace SSH with a direct local executable when a connection fails.

Guest engineering, interactive Openness access and guest recovery remain **unverified** until they are exercised in the selected Windows/TIA guest. A local pass, fake transport test, target label or successful SSH connection does not qualify a VM.

## Immutable bundle and writable artifacts

Extract a release into a dedicated directory. Keep projects, exports, journals, reports and newly generated client configurations **outside** that directory. The default artifact location is `%LOCALAPPDATA%\TiaMcp\artifacts` on the engineering machine; pass an explicit shorter directory when native project/archive path limits require it.

Packaging writes a sibling `<bundle>.mcp.json` outside the bundle with its original location. After copying or relocating a release, generate a fresh configuration outside the bundle:

```powershell
$release = 'C:\Tools\TIA MCP'
& "$release\scripts\New-McpConfiguration.ps1" `
    -BundlePath $release -ArtifactsDirectory 'C:\TiaEvidence' `
    -TimeoutSeconds 300 -StrictBundle |
    Set-Content -LiteralPath 'C:\TiaEvidence\mcp.local.json' -Encoding UTF8
```

Create the external evidence/configuration directory first if it does not exist. Do not overwrite a manifest-listed configuration inside the release. Bundled Python utilities should be invoked with `python -B` to prevent creating unlisted bytecode inside the release.

`Start-TiaMcp.ps1` checks:

- Every manifest path is confined to the bundle and unique regardless of case.
- Required host, worker and launcher files exist and match their recorded lengths and SHA-256 hashes.
- Neither the bundle nor its ancestry traverses junctions/symbolic links; entries are inspected before directory traversal.
- No Siemens-named DLL/EXE is redistributed, even if someone adds it to the manifest.
- No unlisted executable, script, Python bytecode, JSON or configuration file is present. `-StrictBundle` rejects **all** unlisted files, including notes.
- Artifact writes are outside the bundle, with no existing reparse-point ancestry.

Run verification separately with:

```powershell
& "$release\scripts\Verify-TiaMcpBundle.ps1" -BundlePath $release -Strict
```

This verifies **hash consistency**, not publisher authenticity: the manifest is not digitally signed. The developer checkout still uses `Run.ps1`; it is not expected to have a release manifest.

## Remote stdio over existing OpenSSH

The remote machine needs the release and its normal Siemens/runtime prerequisites. VMware supplies virtualization; it does not supply TIA, its API or licensing. No guest installation, SSH-server setup, firewall change, account change or host-key enrollment is performed by these scripts.

Use an existing authenticated OpenSSH host alias after independently verifying/enrolling its host identity. Generate the client configuration on the machine running the MCP client; the bundle, API and artifact paths below are **remote Windows paths**:

```powershell
& '.\scripts\New-McpConfiguration.ps1' `
    -SshHost 'tia-vm' -BundlePath 'C:\Tools\TIA MCP' `
    -ArtifactsDirectory 'C:\TiaEvidence' `
    -TimeoutSeconds 300 -ConnectTimeoutSeconds 15 -StrictBundle |
    Set-Content -LiteralPath '.\artifacts\mcp.vm.json' -Encoding UTF8
```

`-TiaApiDirectory` and `-SimulatorApiDirectory` optionally override the target machine's API paths. Omitting them uses the normal installed locations. Paths are passed as distinct arguments locally and as escaped string literals in a UTF-16 encoded PowerShell command remotely. Spaces, apostrophes and shell metacharacters in valid Windows filenames retain their literal meaning.

SSH uses no pseudo-terminal, `BatchMode=yes`, `StrictHostKeyChecking=yes`, a bounded connection attempt and keepalives. The runner also rejects SSH configurations without explicit strict host checking, batch mode and a bounded connect timeout. It never accepts a new host key automatically or asks for a password. Existing SSH configuration controls authentication; no secrets are written into the generated MCP JSON.

The SSH account/session must have its own effective Openness access and usable Siemens environment. A successful desktop attachment in another Windows session is not evidence that the SSH session can discover or attach to it. The report preserves that distinction.

### Explicit logged-in user session

Some Windows setups allow the SSH MCP host and native discovery to run in service session 0, but reject headless TIA startup there. `-SessionMode InteractiveUser` selects a separate deployment route. SSH still carries MCP stdio; a transient task starts the MCP host under the **same already logged-in Windows user**, and three local same-user named pipes relay stdin, stdout and stderr. Direct mode remains the default and never silently falls back.

```powershell
& '.\scripts\New-McpConfiguration.ps1' `
    -SshHost 'tia-vm' -BundlePath 'C:\Tools\TIA MCP' `
    -ArtifactsDirectory 'C:\TiaMcpData' -SessionMode InteractiveUser `
    -TimeoutSeconds 300 -StrictBundle |
    Set-Content -LiteralPath '.\artifacts\mcp.vm.interactive.json' -Encoding UTF8
```

This mode requires an elevated engineering login to register its task, an active login for that same user, and effective Openness membership in that interactive token. It stores no Windows password. It does not create a desktop login or turn session 0 into an interactive session. Sign-out ends the available engineering session; reboot/reconnect requires the user to log in again.

Each connection has a random pipe identity, `CurrentUserOnly` pipe access, an exact user SID check, a 75-second connection bound and an external `sessions/<id>/receipt.json`. The receipt records the actual Windows session, owned host process, shutdown result and zero replays. EOF or SSH loss closes host stdin; the relay permits the configured operation timeout plus 30 seconds for shutdown, then can terminate only its own MCP host. It never kills a Siemens process tree or assumes the native operation rolled back. Read the durable change journal before deciding what happened to an interrupted mutation. The transient task is removed on normal shutdown and by the relay after an abrupt SSH disconnect. Diagnostic files remain outside the immutable release.

The mode supports separate concurrent connections with distinct pipes and tasks. It does not automatically attach those connections to the same TIA instance. Native ownership and scratch/recovery safeguards still apply.

Run the local pipe regression suite against a built bundle in a logged-in Windows session:

```powershell
python -B scripts/test_session_relay.py --bundle 'C:\Tools\TIA MCP'
```

Those checks cover malformed identities/settings, binary transfer, Unicode paths, stderr separation, clean EOF and client termination. They do not create scheduled tasks or call Siemens APIs; actual VM engineering and interrupted-write recovery require the qualification commands below.

Typed LAD/FBD deployment qualification uses the same configuration route. Run the
qualifier from the client-side copy of the exact release, after that release has
been deployed and its strict bundle verification has passed on the guest:

```powershell
python -B scripts/qualify_typed_graphical.py `
    --config artifacts/mcp.vm.interactive.json --engineering-only `
    --case typed-boolean-lad --case typed-boolean-fbd `
    --output artifacts/typed-vm --timeout 330
```

Each selected case creates an owned scratch on the engineering host, imports its
independently authored fixture through the ordinary change workflow, and checks
typed preparation/application, full native XML, save/reopen and checkpoint
recovery. Prepared caller and dependency artifacts are transferred through
`artifact_read` with exact byte counts and SHA-256 checks; guest paths are never
opened on the client. The report records the actual transport and configuration
hash. Configured deployment mode currently requires fresh scratch and
`--engineering-only`; client-local baseline archives and simulator memory-card
paths are not assumed to exist on the guest. These engineering checks do not
establish runtime behavior. An unavailable SSH connection is retained as a
blocked deployment prerequisite; the runner does not start a VM, enroll a host
key, alter networking or replace the selected transport.

For an actual SSH connection-loss check without creating a native project:

```powershell
python -B scripts/qualify_ssh_transport.py `
    --config artifacts/mcp.vm.interactive.json --report artifacts/ssh-transport-vm.json
```

This opens two independent connections, terminates only the first local SSH client, verifies that the second still works, and checks that a fresh connection starts detached. It dispatches no mutations. Combine its report with guest relay receipts to verify cleanup; this test does not qualify an interrupted native write.

## Run qualification

Python 3.10+ is needed only on the qualification client. Running the packaged MCP server itself does not require Python.

```powershell
python -B scripts/qualify_deployment.py `
    --config artifacts/mcp.vm.json --report artifacts/qualification-vm.json `
    --timeout 330 --target-label 'Windows guest'
```

The target label is descriptive, not evidence of VM placement. The report records the actual selected transport (`local-stdio` or `ssh`), the configuration hash, per-stage elapsed time, native API metadata and shutdown results. It excludes the configuration environment values and command arguments. MCP results and bounded stderr tails remain diagnostic evidence; keep reports in the intended evidence directory.

Default stages are read-only with respect to engineering projects:

1. Start the configured command and initialize MCP.
2. Check tool discovery and page through the feature catalog, validating completeness and tool bindings.
3. Read installed native bridge metadata, running TIA process discovery and detached session state. No TIA instance is started or attached.
4. Close stdin and verify clean launcher shutdown.
5. Start a fresh host through the same configuration, check the catalog and detached state again, then close it cleanly. No interrupted operation is retried.

An offline-only installation does not require PLCSIM. Add `--simulation` to read PLCSIM Advanced status explicitly. This may initialize Siemens Runtime Manager; it does not register, power on, run or delete a simulated CPU. A status pass is **not** runtime qualification.

Add `--engineering` to explicitly authorize a separate owned scratch instance. The runner creates a new uniquely named project, compiles its fixed fixture, checks inventory, exports a block, retrieves its bytes through `artifact_read` with per-chunk bounds and a final SHA-256 check, then releases its owned connection. It accepts no existing project or attachment selector. This same workflow uses remote artifact paths over SSH without assuming that those paths are accessible on the client.

For live journal reads, `artifact_read` can return `ArtifactUnavailable` when a path is missing or temporarily locked during atomic replacement. A bounded read may be attempted again; no mutation is retried. `ArtifactPathDenied` still rejects traversal and symbolic links/junctions. Each successful read reports the full file hash so a client can reject chunks taken from different journal versions.

```powershell
python -B scripts/qualify_deployment.py `
    --config artifacts/mcp.vm.json --report artifacts/qualification-vm-engineering.json `
    --engineering --timeout 600
```

Choose a client timeout above the configured server timeout to capture the server's own timeout response. Artifacts and a partially created scratch project are retained for diagnosis on failure. The runner never kills a Siemens process or replays a write. If transport fails, it closes its launcher/SSH process and reports unconfirmed detach or remote exit honestly. Interrupt/recovery qualification is a separate fault suite; fresh-host reconnect alone does not establish recoverability of in-flight mutations.

## Result interpretation

Each stage is `passed`, `blocked`, `failed` or `unverified`. Missing native APIs yield blocked native stages while preserving any successful host/catalog stages. Unrequested engineering/simulation checks remain unverified and do not prevent an offline-only pass. Protocol corruption, wrong response IDs, timeouts, assertion failures or nonzero launcher exit fail qualification.

Exit codes: `0` selected checks passed, `2` native prerequisites blocked selected checks, `1` configuration/protocol/assertion/infrastructure failure. `completed: true` applies only to selected stages. Runtime execution and virtual-machine qualification remain explicit unverified scope fields; combine this report with evidence collected on the actual guest to qualify that deployment.

Automated regression checks:

```powershell
powershell.exe -NoProfile -File scripts/Test-Deployment.ps1
python -B scripts/test_qualify_deployment.py
```

The PowerShell suite checks transport argument boundaries and malformed/corrupted bundles. The Python suite runs a local protocol fake to verify sequencing, error handling, artifact integrity and no replay. Neither suite is native TIA, real SSH or guest evidence.
