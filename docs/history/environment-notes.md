# V21 environment and simulator evidence

Historical record. See [current status](../STATUS.md) and [remaining TODO](../TODO.md) for the latest checkpoint and work order.

Inspected on 8 October 2026 using local file metadata, the current Windows token, and installed Siemens documentation. This is an installation probe, not a successful TIA attachment or running simulation test.

## Repeat the probe

From the repository root, run in the Windows user session that will run the worker:

```powershell
powershell.exe -NoProfile -File .\scripts\Probe-Environment.ps1
```

The script emits one JSON document on stdout. It does not write files. To inspect selected fields:

```powershell
$report = .\scripts\Probe-Environment.ps1 | ConvertFrom-Json
$report.currentToken.groups
$report.simulators.advanced.apiVersions
```

Directory parameters allow non-default installations and future VM deployment: `TiaApiDirectory`, `FrameworkReferenceDirectory`, `StandardSimulatorDirectory`, `AdvancedSimulatorDirectory`, and `AdvancedRuntimeDirectory`. The default search is limited to the documented installation locations; it is not a full machine search.

Statuses distinguish `present` or `read` from `missing` and `unavailable`. Permission failures and unresolved group lookups are unavailable evidence, never proof of absence. API assembly names are read from metadata without executing vendor code. The script does not launch TIA/PLCSIM, initialize a runtime API, query licenses, discover PLCs, or change groups, firewall rules, drivers, or network adapters.

The probe was executed successfully with both Windows PowerShell 5.1 and PowerShell 7. It returned parseable JSON. The current sandbox also prints a PowerShell FileSystem provider initialization diagnostic on stderr before scripts run; do not combine stderr into JSON stdout.

## Observed installation

| Component | Evidence | Interpretation |
|---|---|---|
| TIA V21 Openness | 17 `Siemens.Engineering*.dll` files with corresponding XML documentation under `C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48` | Public API files are available for a compile/attachment prototype. |
| V21 Base assembly | Assembly version `21.0.0.0`; file version `2100.0.121.1` | Reference this installed V21 API, not older TIA assemblies. |
| .NET Framework references | `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll`, file version `4.8.3761.0` | The net48 worker can use locally installed reference assemblies. |
| .NET Framework runtime | Registry release `533509` | Runtime meets the net48 minimum; reference assemblies and installed runtime are separate checks. |
| Siemens TIA Openness group | SID is present in current process token; `WindowsPrincipal.IsInRole` returns false | Membership evidence exists, but this restricted token is not reporting effective authorization. Do not assume a group change is required. |
| PlcSimUsers group | Same token result as Openness | Recheck in the ordinary worker session before diagnosing a simulator permission issue. |
| Standard PLCSIM V21 | `C:\Program Files\Siemens\Automation\PLCSIM_V21\S7PLCSIMV21.exe`, file version `2100.0000.0912.0001`, product version `2100.0.91.11777` | Installed; not started or tested by this probe. |
| PLCSIM Advanced | UI at `C:\Program Files (x86)\Siemens\Automation\PLCSIMADV\bin\Siemens.Simatic.PlcSim.Advanced.UserInterface.exe`, product version `8.0.0.0`, file version `80.0.259.0` | V8 application binaries are installed, not just a leftover directory. |
| Advanced runtime | Runtime Manager executable under `C:\Program Files (x86)\Common Files\Siemens\PLCSIMADV` | Runtime files are present; manager connectivity remains untested. |
| Advanced public API | x64/x86 DLLs and `SimulationRuntimeApi.h` under `C:\Program Files (x86)\Common Files\Siemens\PLCSIMADV\API\8.0` | Public native and managed API files are available. Assembly version `8.0.0.0`, file version `80.0.259.0`. |
| Older API directories | `3.0`, `4.0`, `4.1`, `5.0`, `6.0`, `7.0` | Compatibility API files exist. This does not imply multiple separately usable Advanced installations. |

`Get-LocalGroup` failed inside the current restricted session. The probe instead reads only the two relevant group SIDs and their presence/effective authorization in the current token. It does not disclose unrelated group memberships or license serials.

## Public simulation API boundary

The installed **S7-PLCSIM Advanced Function Manual V8.0, 11/2025, A5E37039512-AK**, section 3.2, page 22, lists a C++/C# co-simulation API for Advanced and no such API for standard PLCSIM Vxx. A copy of `Siemens.Simatic.Simulation.Runtime.Api.x64.dll` also exists inside the standard V21 app's `resources\bin\wwwroot\assets\lib\runtime` assets. Bundled implementation files alone do not establish a supported standard-edition automation API.

The installed **S7-PLCSIM Advanced API Programming Manual V8.0, 11/2025, A5E53246567-AE** documents the public `IBaseRuntimeManager`, `ISimulationRuntimeManager`, `IInstance`, and remote runtime interfaces. Its C# entry point is `Siemens.Simatic.Simulation.Runtime.SimulationRuntimeManager` (section 4.3.1, page 43). The native header also contains instance registration, typed read/write, and license-status interfaces. This is concrete API evidence, but not a claim that a runtime call has succeeded here.

API initialization is intentionally excluded from the read-only script. The API manual, section 3.2, page 23, states that initializing the API automatically starts the Runtime Manager. Even reading the first static property can initialize the managed API. License availability and trial state therefore remain `unverified`; no trial was activated by this probe.

The same section documents distinct runtime outcomes for a full license, trial mode, and a Test Suite license. A Test Suite-only license must not be assumed to support a general-purpose simulation session. The next simulation milestone should record the actual API status returned for a dedicated test instance without collecting license keys.

Use the x64 API with an x64 adapter process; the manual specifies architecture matching. Keep simulator types outside the .NET 10 MCP contracts. The first runtime adapter should be isolated so dependency/framework compatibility can be verified independently from the engineering worker.

## Next feasibility checks

1. Run the worker under the ordinary logged-in engineering user and verify V21 attachment. Current sandbox group authorization is inconclusive.
2. For a dedicated simulation test, load the installed Advanced V8 API, record runtime initialization success/failure, and enumerate only local simulated instances. This is a separate explicit runtime operation, not part of environment metadata.
3. Create one owned test instance for an installed, supported CPU/firmware. Record license/trial/Test Suite status, test download, and verify RUN/STOP plus a bounded read/write scenario.
4. Prove symbol access and input/output synchronization semantics with a small test FB. A DLL export or successful compile is not behavioral evidence.
5. Repeat the same checks inside a Windows VM when VM support is implemented. The local Softbus path is confined to one PC/VM; distributed runtime communication has separate adapter and network requirements.

CPU coverage must be taken from the V8 manual's supported-CPU table and the selected firmware. Its table includes S7-1500/ET 200 families and S7-1200 G2; this is not a promise to simulate every S7-1200 generation or every hardware feature. For example, the manual documents R/H simulation in RUN-Solo rather than redundant system operation.

Local source files:

- [Advanced V8 Function Manual](<C:/Program Files (x86)/Siemens/Automation/PLCSIMADV/documents/en-US/s7-plcsim_advanced_function_manual_en-US.pdf>)
- [Advanced V8 API Programming Manual](<C:/Program Files (x86)/Siemens/Automation/PLCSIMADV/documents/en-US/s7-plcsim_advanced_function_manual_API_en-US.pdf>)
- [Advanced V8 native API header](<C:/Program Files (x86)/Common Files/Siemens/PLCSIMADV/API/8.0/SimulationRuntimeApi.h>)
