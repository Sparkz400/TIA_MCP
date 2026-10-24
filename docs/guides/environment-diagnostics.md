# Installation diagnostics

Run the metadata diagnostic on the Windows machine that will run the MCP host and workers, in the same Windows user/session. It does not start TIA, PLCSIM, Runtime Manager or any MCP worker, and does not query licenses, edit groups, install dependencies or change machine settings.

```powershell
.\scripts\Test-TiaMcpEnvironment.ps1 -BundlePath 'C:\TiaMcp' -OutputPath 'C:\TiaMcp\environment.json'
```

The script writes a single JSON document to stdout. `-OutputPath` optionally saves the same UTF-8 document; its parent directory must already exist. It can therefore be invoked through an existing authenticated SSH connection as well as locally. A successful script exit means the diagnostic completed, not that all prerequisites passed. Deployment automation must inspect `selectedPrerequisites.status`.

By default, required prerequisites cover the host and offline engineering. `-RequireSimulation` also includes the simulation prerequisites in the selected result. Simulation always has a separate report, so missing PLCSIM Advanced does not prevent an offline-only installation from passing metadata checks.

For non-default installations, use `-DotnetRoot`, `-TiaApiDirectory`, `-AdvancedSimulatorDirectory` and `-AdvancedRuntimeDirectory`. These select diagnostic search paths; they do not change the launcher's configuration. Without an explicit `-TiaApiDirectory`, the probe uses Siemens' registered V21 API location in the 64-bit registry, matching the native worker. Only an absent/empty registration falls back to the setup default; an unreadable registration leaves the probe unavailable. `AdvancedRuntimeDirectory` is the product's Common Files runtime root containing `API\8.0`, not the API-version directory itself. Other default search locations are the known local install paths used by `Probe-Environment.ps1`; this is not a recursive disk search or complete installed-product inventory.

## What the report checks

- A 64-bit Windows environment, the selected `dotnet.exe` PE architecture, and metadata for both `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App`.
- Stable .NET 10 shared framework versions with their runtime marker files. A selected bundle supplies its minimum versions through `TiaMcp.Host.runtimeconfig.json`; custom framework or roll-forward policies remain unavailable to this profile.
- .NET Framework 4.8-or-later runtime registry evidence, independently of developer reference packs.
- V21 assembly identities for `Siemens.Engineering.Base`, `Siemens.Engineering.Step7` and `Siemens.Engineering.Safety` from the installed Openness folder.
- Effective current-token authorization for `Siemens TIA Openness` and `PlcSimUsers`, with the original membership/authorization distinction retained.
- The Advanced V8 x64 API assembly identity and Runtime Manager file, separately from standard PLCSIM.
- With `-BundlePath`, six required host/worker files and x64 executable headers. This is an installation sanity check; use the package manifest verifier for full integrity validation.

The Windows runtime requirements come from this repository's host/worker project files and packaged runtime configuration. Microsoft's [Windows installation guide](https://learn.microsoft.com/en-us/dotnet/core/install/windows) distinguishes the .NET and ASP.NET Core runtimes; the [runtime version selection guide](https://learn.microsoft.com/en-us/dotnet/core/versions/selection) describes framework-dependent version selection. The diagnostic accepts compatible stable patch/minor versions on the requested major and does not treat a different installed major as satisfying the package.

## Result contract

Schema version is `1`. `checks[]` uses stable IDs, a capability (`host`, `offline`, `simulation`), `required`, `status`, `summary`, `action` and bounded `observed` metadata.

| Check status | Meaning |
| --- | --- |
| `present` | The specified metadata prerequisite was observed. This is not successful native execution. |
| `missing` | A conclusive file/registry absence or completed enumeration established the missing prerequisite. |
| `mismatch` | Readable metadata contradicts the required architecture or API/runtime version. |
| `unavailable` | Access, lookup, incomplete evidence or unsupported configuration prevents a conclusion. |
| `unverified` | A behavior was intentionally not exercised, such as attachment, licensing or simulator startup. |

`capabilities.host`, `.offline` and `.simulation` summarize their required checks. `selectedPrerequisites` uses the selected offline/simulation profile. A required `missing` or `mismatch` yields `blocked`; otherwise any required `unavailable` yields `unknown`; all observed required metadata yields `present`. Every capability retains `qualification: "unverified"` because this script performs no functional qualification.

A token can contain the Siemens group SID while denying effective authorization in a restricted process. This produces `unavailable`, not an assertion that the engineer is missing from the local group. Run the diagnostic under the intended launch token before interpreting or changing membership. SSH may create a different session from the desktop; qualify attachment in that actual session afterward.

The report deliberately leaves Windows/VM vendor support, licenses, installed option behavior, Openness attachment, project creation/compile and simulator RUN/read/write unverified. File presence does not resolve native loader failures such as `ErrorLoadingDll (-21)`. `VMware Workstation Pro` on the host does not establish the guest OS, guest TIA installation or guest execution result.

Output is capped at 64 KiB. Runtime directory inspection retains at most 32 stable versions per family, reports at most eight matching versions, and reports `unavailable` rather than absence if truncated enumeration cannot establish a match. Evidence JSON is capped at 1 MiB. The optional report file is the only write performed by the diagnostic itself.

## Deterministic testing

```powershell
.\scripts\Test-EnvironmentDiagnostics.ps1 -ReportPath '.\artifacts\environment-diagnostics-tests.json'
```

The test suite generates supplied evidence, so it runs without TIA, PLCSIM or .NET 10 installed. The doctor accepts `-EvidencePath` with this envelope:

```json
{
  "schemaVersion": 1,
  "probe": { "...": "Probe-Environment.ps1 metadata shape" },
  "host": {
    "is64BitOperatingSystem": true,
    "dotnetExecutable": { "status": "present", "architecture": "x64" },
    "runtimeFamilies": [],
    "package": { "status": "not_requested", "files": [] }
  }
}
```

`Test-EnvironmentDiagnostics.ps1` contains complete fixtures. Evidence mode bypasses all environment probing, is labeled `supplied-evidence`, and rejects installation path overrides. It does not authenticate evidence or certify that a fixture describes the current machine. Missing evidence fields remain unavailable; supplied evidence must not be substituted for destination qualification.

The 29 tests cover installation-path precedence and denied registration, missing runtimes, stable/preview/major/patch selection, wrong architectures and API versions, unreadable/truncated metadata, restricted tokens, optional simulation, incomplete package evidence, output limits and deterministic mode isolation. Path-selection tests load the production resolver with a synthetic registry reader; evidence-mode tests use a probe that refuses execution. No machine registry is changed. Test cleanup deletes only its exact temporary files and empty test directory.

Local reports recorded on 8 October 2026:

- `artifacts/environment-diagnostics-restricted.json`: installed metadata present, effective token authorization unavailable in the restricted environment.
- `artifacts/environment-diagnostics-normal-token.json`: host, offline and simulation metadata prerequisites present under the normal Windows token; all functional qualification still unverified.
- `artifacts/environment-diagnostics-tests.json` and `artifacts/environment-diagnostics-tests-ps51.json`: pure fixture tests under PowerShell 7 and Windows PowerShell 5.1 respectively.

These reports inspect the existing v0.3 release bundle ending `064849_e96b6805`. They are local-machine evidence, not VM results. Run the same script with the actual destination bundle path to collect a fresh report.
