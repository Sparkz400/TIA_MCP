# Registering the deployed V21 worker

Siemens provides an application-specific AllowList registration method for unattended Openness startup. `Register-TiaOpennessApplication.ps1` implements that V21 method for exactly `workers/openness/TiaMcp.Openness.V21.exe` inside a verified release bundle. This concerns the Siemens application authorization prompt, not Windows network firewall rules.

First inspect the proposal without administrator privileges or registry changes:

```powershell
.\scripts\Register-TiaOpennessApplication.ps1 -BundlePath 'C:\TiaMcp' -ExpectedWorkerSha256 '<worker SHA256 from the verified release>' -InspectOnly
```

To apply it, run the same trusted script under elevated **64-bit** Windows PowerShell and provide a new JSON report path outside the bundle:

```powershell
.\scripts\Register-TiaOpennessApplication.ps1 -BundlePath 'C:\TiaMcp' -ExpectedWorkerSha256 '<worker SHA256 from the verified release>' -ReportPath 'C:\TiaEvidence\openness-registration-unique.json'
```

The report parent must already exist and the report file must not exist. Apply mode requires an explicit report so previous registry values are saved before any change. Adding a report inside the immutable bundle would invalidate strict bundle verification and is rejected. Keep `Verify-TiaMcpBundle.ps1` beside the registration script when staging it separately; do not substitute an untrusted verifier.

The script requires an absolute local bundle path and rejects traversal, device/UNC paths and reparse points. It verifies every bundled file using the sibling verifier's strict mode, then hashes the worker while holding its file open against replacement/writes. The supplied expected SHA256 must match. Bundle manifest consistency alone is not publisher authentication; obtain the expected worker hash from the release whose archive was already verified.

The only registry values written are `Path`, `DateModified` and `FileHash`, all strings, under:

```text
HKEY_LOCAL_MACHINE\SOFTWARE\Siemens\Automation\Openness\AllowList\TiaMcp.Openness.V21.exe\Entry
```

The script uses `Registry64`. `Path` is the final deployed executable path; `DateModified` uses that file's UTC modification time formatted invariantly as `yyyy/MM/dd HH:mm:ss.fff`; `FileHash` is Base64 of its raw SHA-256 digest. It reads the values back and reports success only when they match. Other entry values and other applications are preserved. The report includes the previous Entry's existence and all its value names, types and raw values for review/recovery. A failed or interrupted write must be reconciled using that report before retrying.

The format is documented in Siemens' [V21 Openness firewall documentation](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/tia-portal-openness-firewall). Register **after** deployment: changing the path, modification timestamp or file contents requires registration of the new identity. V21's AllowList is version-independent; the older versioned Whitelist path is not used. See [V21 migration changes](https://docs.tia.siemens.cloud/r/en-us/v21/readme-tia-portal-openness/major-changes-for-long-term-stability-in-tia-portal-openness-v21).

This script neither changes group membership nor starts TIA. The actual worker user still needs `Siemens TIA Openness` membership and a fresh login after membership changes. Siemens' [group setup instructions](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/basics/installation/adding-users-to-the-siemens-tia-openness-user-group) and [service guidance](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/basics/installation/using-windows-service) describe these separate requirements. In a service-style session, use the intended user account and create a new TIA instance without UI. Registration does not establish SSH compatibility, license availability or successful native execution.

Run the read-only guards against a release with `Test-TiaOpennessRegistration.ps1 -BundlePath '<absolute release directory>' -ReportPath '<test evidence JSON>'`. The suite never invokes apply mode and compares the application registry entry before/after. Apply-mode verification is a separate explicitly authorized guest operation.

## Development builds

TIA treats every rebuilt worker as a new application, because its timestamp and hash change, so a saved "Yes to all" only lasts until the next build. Each **Debug** build of `TiaMcp.Openness.V21` therefore runs `scripts/Register-DevOpennessWorker.ps1`, which writes the same three values for the built `bin\Debug\net48\TiaMcp.Openness.V21.exe`. TIA keeps one subkey per allowed path (`Entry`, `Entry (1)`, ...), so the script updates the subkey with the same `Path` and otherwise adds one; the main checkout and each worktree keep separate entries instead of overwriting each other.

Writing those HKLM entries needs permission. Grant it once, from an elevated PowerShell on the development machine:

```powershell
.\scripts\Register-DevOpennessWorker.ps1 -Grant
```

This creates only the `TiaMcp.Openness.V21.exe` application key and lets the current user read, create and set values in its subkeys, including entries TIA adds after "Yes to all". Any process running as that user can then allow its own executable under this name without a prompt, so do not grant it on shared or production engineering machines. Without the grant the build still succeeds and prints a warning. Release builds are not registered. A Debug build only replaces a bundle registration made by the script above if both resolve to the same worker path. Entries for deleted worktrees remain until an administrator removes them.
