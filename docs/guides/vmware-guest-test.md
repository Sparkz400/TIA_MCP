# Run the release qualification inside the VMware guest

The local test VM uses a dedicated VMware shared folder named `TiaMcpTest`, backed only by the host's `artifacts/vm-exchange` directory. This exchange folder is writable so the guest command can return its generated diagnostic reports. This dedicated share does not expose the repository or its other artifacts. Creating this share did not change SSH, guest networking, firewall rules or guest accounts; the separately authorized SSH setup below does change guest configuration.

The expected path **inside Windows in the VM** is:

```text
\\vmware-host\Shared Folders\TiaMcpTest
```

VMware reports that the share was configured. Guest visibility still needs the guest-side command to establish it. If Windows cannot open this path, copy only the prepared ZIP and `Invoke-GuestQualification.ps1` into the guest manually and substitute those local paths below. Do not change SSH/network settings to run this fallback.

The prepared one-command wrapper reads the exact ZIP name and SHA-256 from `release.json` and runs the isolated engineering test. Run this **inside PowerShell in the guest**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "\\vmware-host\Shared Folders\TiaMcpTest\Run-GuestTest.ps1"
```

The wrapper automatically copies only `guest-summary.json`, `environment.json`, `qualification.json` and the console log, where present, to a fresh `results/<GUID>` directory in the dedicated host exchange folder. It validates the returned guest directory and refuses links or unexpected paths. Project data and archives are not copied back.

When `prerequisites/manifest.json` is staged, the same command also prepares the declared portable .NET Runtime, ASP.NET Core Runtime and embedded Python archives under the new guest run's `tools` directory. Each archive must match its SHA-256. Paths, links, duplicates and expanded sizes are checked before extraction; overlapping .NET files must have identical bytes. The original package source URLs and publisher-checksum evidence are retained in the summary. These are per-run tools, not machine installations: the doctor reads the explicit portable .NET root, the qualifier uses the selected embedded Python, and `DOTNET_ROOT_X64`, `DOTNET_ROOT` and `PATH` are overridden only for this process and its children, then restored. The generated MCP configuration includes child-process .NET root overrides so it remains usable after the bootstrap returns. Windows accounts, system/user PATH, registry, firewall and licensing settings are not changed.

For a manually copied release, run the explicit ZIP/hash form instead:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "\\vmware-host\Shared Folders\TiaMcpTest\Invoke-GuestQualification.ps1" -BundleZip "\\vmware-host\Shared Folders\TiaMcpTest\RELEASE.zip" -ExpectedSha256 "RELEASE_SHA256" -EvidenceDirectory "C:\TiaEvidence" -Engineering
```

`RELEASE.zip` and `RELEASE_SHA256` are placeholders in this general documentation; use the concrete delivered command. Omit `-Engineering` for read-only discovery with no TIA instance creation. No administrator elevation is requested; use the normal engineering account whose Openness access should be qualified.

The script:

1. Creates a unique `C:\TiaEvidence\run_<id>` directory and records the machine name, OS, process architecture, time and available machine-model metadata. A VMware model is recorded only if observed on the executing machine.
2. Opens the ZIP read-only, verifies the supplied hash, checks confined paths, duplicate names, links and size limits, then extracts it into a new owned directory. The same open archive stream is used for hashing and extraction.
3. Runs strict bundle-manifest verification, prepares any explicitly supplied portable prerequisite manifest, and writes `environment.json` from the metadata-only deployment doctor using the selected runtime root.
4. Creates a local MCP configuration and writable artifacts directory outside the extracted bundle.
5. Runs the standalone qualifier only when the host runtime prerequisites are established and Python 3.10+ is available. With `-Engineering`, it creates, compiles, exports and releases a separate owned scratch project; it accepts no existing project path.
6. Writes `guest-summary.json`, `qualification.json` when attempted and a small console log. It retains failed or partial evidence instead of deleting it.

The bootstrap performs no machine installation, changes no machine settings, activates no licenses and performs no PLC network operations. PLCSIM is not required for this offline guest test. Missing prerequisites produce **blocked** stages with their actual reasons. A ZIP/manifest/protocol/assertion failure produces **failed** stages. No generic success is inferred from files merely existing. Supplying `-PrerequisitesManifest` to the explicit bootstrap command enables the same portable route without the wrapper.

The final output identifies both the guest evidence directory and, when using the wrapper, the host report directory. If using the explicit bootstrap command without the wrapper, copy back the summary, environment and qualification JSON files yourself. The full guest evidence remains under `C:\TiaEvidence` for investigation.

The generated `mcp.local.json` can also be used by a client running **inside the same guest**. This fallback verifies local guest execution; it does not qualify SSH transport from the host. SSH qualification remains a separate test when an authenticated endpoint is available.

Bootstrap regression tests use synthetic ZIPs and never claim guest/native evidence:

```powershell
python -B scripts/test_guest_qualification.py
```

## Authorized disposable-VM SSH setup

For this explicitly selected disposable VM, `Enable-GuestAccess.ps1` configures Windows OpenSSH, a dedicated public-key login restricted to the host VMnet8 address, and membership of the existing Siemens TIA Openness group. Its machine/model/MAC and administrator guards run before any changes. The private client key stays on the host outside the exchange. Run this in **Windows PowerShell as Administrator inside the VM**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "\\vmware-host\Shared Folders\TiaMcpTest\Enable-GuestAccess.ps1"
```

Reruns skip capability installation if OpenSSH is already installed, preserve existing keys and back up the SSH configuration. Previous `connection.json` reports are retained in `access-history`. The current report includes original service status, binary versions, permission descriptors and recent SSH/service events before applying SSH directory and sensitive-file permissions. Only SYSTEM and Administrators get write access; directory read/traverse permission for Authenticated Users does not inherit into private keys. This addresses the folder-permission failure described in [Microsoft's OpenSSH startup troubleshooting](https://learn.microsoft.com/troubleshoot/windows-server/system-management-components/error-1053-1067-7034-after-update-openssh-doesnt-start); the original failure's exact cause must still be established from guest evidence.

Public host keys are saved before starting the service, so a startup failure does not hide the identity needed for host-side pinning. On failure the report records the full service exception and fresh diagnostics. If the service is confirmed stopped and port 22 has no listener, one hidden foreground diagnostic child runs for at most six seconds before termination of that exact owned child. Its output is recorded, never treated as service or login success. Missing inventory is recorded as unavailable, not mistaken for a stopped service or a free port. No private key contents are exported.

The host must pin a returned public host key and authenticate successfully before remote deployment qualification can begin. A passed setup report alone does not qualify SSH login, TIA authorization or engineering operations.
