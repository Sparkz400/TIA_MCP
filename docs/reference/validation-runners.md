# Reproducible validation runners

Install Python dependencies with `python -m pip install -r scripts/requirements-test.txt`.
Use the .NET SDK in `global.json`, the .NET 8 runtime for HMI admission checks,
Git on PATH, and Node/npm for explorer checks. Each runner preserves prior output
and requires a fresh evidence directory. A successful offline run does not admit
a native mutation or certify runtime behavior.

`scripts/Test-Offline.ps1 -Suite All` validates contracts and obligation mappings,
current source bindings, Python harnesses, explorer model/client behavior, portable
managed suites, real Git and separate-process persistence, and actual stdio Host
workflows with deliberately absent native workers. It explicitly invokes the
Test Suite JSON Schema harness on fresh managed fixture output; unittest discovery
alone does not run that harness. `-Suite Python` and `-Suite Managed` are the CI
partitions. The managed partition also runs schema and actual MCP checks.
It verifies the normal translation admission refusals, same-binary qualification
visibility and mutually exclusive local mode options with absent native workers.

From the source checkout, `scripts/Test-OfflinePackage.ps1` builds a portable
package without Siemens, verifies strict file/ZIP hashes and dependency notices,
and checks generated configuration, direct Host refusal and Unicode relocation.
It runs supplied-document workflows against the relocated binary, including
actual local Git and saved-review/watch/measurement use. The Windows CI package
job uses this runner. Existing packages can be selected with `-BundlePath`; their
adjacent ZIP and `.zip.sha256` are required. The SDK, Git and Python test
dependencies are needed by the runner, even though deployed Host startup needs
only the shared runtime. Reports are retained under `artifacts/offline-package/`.

Source binding validation uses `--allow-missing-evidence` for clean checkouts:
ignored historical native artifacts are listed as unavailable, with no release
or qualification acceptance. Source files, method anchors and every available
evidence artifact still have to match exactly. Omit that option for strict
validation on a workstation containing the complete retained evidence set.

The Python partition also writes `retained-evidence.json`: each input retains its
expected hash and the evidence IDs/features that depend on it. Missing inputs are
explicit; changed, unsafe or oversized inputs fail the audit. To inventory a clean
checkout or verify a separately recovered tree without copying or modifying it:

```powershell
python -B scripts/audit_retained_evidence.py --out artifacts/evidence-inventory.json
python -B scripts/audit_retained_evidence.py --evidence-root D:\RecoveredTiaEvidence --out artifacts/evidence-recovery-check.json --require-complete
```

The recovery root must contain the original repository-relative `artifacts/...`
layout. Outputs must be new files. Strict checking fails unless every retained
input has an exact known hash match; directories, missing hashes and missing files
never count as matches. Checks reject links/junctions and bound reads to 128 MiB
per file and 512 MiB total. A byte match establishes retained-file identity only:
it does not renew native qualification, admit current binaries, or release gates.

`scripts/Test-OpcUaOffline.ps1` independently freezes the parser/CLI source and
runs metadata, parser, dependency, consumer-impact and immutable-output checks.
The standard managed runner additionally tests the Host OPC UA workflow and six
GRAPH tools through actual stdio with absent native workers. GRAPH covers retained
input-chain integrity, restart, pages and byte-exact artifact transfer.
`npm run test:explorer:browser` exercises the actual browser suite. CI runs these
jobs separately and does not install or start Siemens products. To additionally
exercise a real Host-generated GRAPH report in the browser, pass
`--graph-report <graph-mcp/inspection.json>` to `ui/project-explorer/tests/browser.mjs`.

| Suite group | Runner | Additional prerequisite |
| --- | --- | --- |
| ComponentWindow, GRAPH, HmiRepairAdmission, OPC UA parser/dependencies/workflow | `Test-Offline.ps1` | ComponentWindow and Git tests start ordinary managed child processes. GRAPH uses retained repository fixtures; installed Siemens XSD checks are conditional. |
| WatchAdapter, ComponentQualification, TestSuite.Worker and compiled worker lifetime guard | `Test-WorkstationBoundaries.ps1` | Windows/.NET Framework and installed Siemens V21 reference assemblies. These boundary tests do not create a Portal session. |
| TranslationCategoryProbe, WatchReindex | `Test-RetainedFixtures.ps1` | Explicit genuine four-row translation workbook and two retained watch review directories, including their hash-bound input XML. These are file-only evidence checks, outside the portable CI allowlist. |
| Full legacy workstation regression | `Test.ps1` | Installed engineering products; distinct from the portable aggregate. Native qualification remains a separate admitted workflow. |

The fixture runner requires `-TranslationWorkbook`, `-WatchReorderRun`,
`-WatchDeleteRun`, and a fresh `-OutputDirectory`. Missing fixtures fail before
building; there is no implicit historical workstation path or silent skip.

For a clean source checkpoint including staged, unstaged and nonignored new
files, run `python -B scripts/snapshot_source.py --destination <new-source-dir>`.
It records SHA-256 for every copied input and rechecks the original after copying.
Run the aggregate from the copied tree with output under that tree. The adjacent
source manifest, test receipts and binary hashes identify the tested source;
HEAD alone can omit uncommitted delivery work.

Qualification tool declarations remain in the catalog for complete source
coverage. Normal Host registration excludes every `qualification_` tool; actual
stdio catalog checks enforce that boundary. Only an explicitly configured
qualification build/session may expose the appropriate routes.

AutomationML parser/comparison/store checks run in `TiaMcp.AutomationMl.Tests`.
`Test-Offline.ps1` also runs `test_automationml_host_mcp.py` against the actual
Host with absent Siemens workers. The relocated `Test-OfflinePackage.ps1` flow
runs the same six-tool workflow from the packaged Host. See the
[CAx workflow and bounds](automationml-workflow.md).

The managed aggregate also verifies component-bound Bool and integer/state
text-list generation, diagnostic experiment composition, repeated-run comparison
and combined report export through actual offline MCP Host sessions. Coordinated
generation sets and retained runtime acquisition for diagnostic experiments
extend those workflows with exact original-source and compatibility checks.
These workflows run again against the relocated package. Runtime pressure checks
run through the existing physical-runtime suite, followed by an independent
raw-chain/value/schedule reader in the offline and workstation runners.
`Test-RuntimeCaptureDiskFull.ps1` is a separate bounded-volume qualification
because creating and mounting its dedicated 64 MiB VHD requires Windows privileges
that portable CI and this session do not provide. Capacity refusal and manual-clock
load tests do not substitute for that actual disk-full result.

`qualify_runtime_soak.py --assembly <PhysicalRuntime.Tests.dll> --output <fresh-dir>
--seconds 60` is the opt-in real-clock memory/handle soak and owned worker
crash/restart qualification. Its independent reader is `runtime_soak_checks.py`;
the runtime session remains an explicit test double. `qualify_owned_process_job.py
--output <fresh-dir>` checks actual Windows child containment without TIA. Neither
run changes the unresolved native quarantine or grants native qualification.

`qualify_runtime_soak.py --assembly <PhysicalRuntime.Tests.dll> --output <fresh-dir>
--long --epochs 15 --seconds 60` is the explicit fifteen-minute qualification.
It runs bounded epochs in one original owned process, with separate stores to
respect existing per-store limits, repeated stop/recovery and checks of every
earlier retained prefix. It remains separate from ordinary aggregate execution.

`qualify_runtime_profiles.py --assembly <PhysicalRuntime.Tests.dll> --tls
<generated-runtime-tls> --runtime-directory <Host-directory> --output <fresh-dir>`
checks independently specified advertised-profile and refusal cases against
production client binaries over disposable pinned loopback TLS. The aggregate,
relocated package and workstation runners invoke it after TLS fixture creation.
The supplied runtime directory selects the exact Host/package Core and Graph
binaries; the probe's loaded hashes must match. Local protocol observations do
not establish CPU/firmware compatibility or online program identity.

The separate `TiaMcp.OpcUa.Runtime.Tests` suite exercises the secure UA client
against owned local OPC UA servers:

```powershell
dotnet <TiaMcp.OpcUa.Runtime.Tests.dll> --output <fresh-directory>
python -B scripts/qualify_opcua_runtime.py --host <Host.exe> --server <TiaMcp.OpcUa.Runtime.Tests.dll> --output <fresh-directory>
```

The aggregate runs the managed suite and the actual Host MCP qualifier. The
package runner uses the relocated Host with a separately built local test
server. Test-server packages and binaries are forbidden in published Host
dependencies; retained notices must cover every exact published package.
Fixture certificates and identities are local to the qualification. No physical
endpoint, OS trust-store modification, subscription or write is part of these
checks. `Test.ps1` also includes this local qualification, while retaining its
separate workstation prerequisites.
The fourth offline increment also runs
`test_hmi_set_evolution_host.py` with the managed
`hmi-components/text-evolution/mcp-fixture.json` and
`qualify_hmi_set_evolution_prior.py` against both aggregate and relocated Hosts.
`qualify_diagnostic_quality_offline.py` consumes the corresponding
`diagnostic-samples.json`; its readable reports are separately checked by
`qualify_diagnostic_quality_browser.mjs`. These checks retain prior versioned
records and original bytes. HMI removal and rename effects remain previews.
When actual previous-producer stores are available, also run
`qualify_diagnostic_quality_prior.py --host <exact Host> --source <previous package-validation directory> --output <fresh directory>`.
This separate compatibility check covers the earlier diagnostic, experiment,
repeated-experiment and runtime-acquisition stores. It records all original file
hashes and any new export artifacts separately; the self-contained aggregate
runner does not require historical workstation artifacts.
