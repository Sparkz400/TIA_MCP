# Release readiness and workstation prerequisites — 3 November 2026

Reviewed `main` at `7cbc8f13`. The matrix consistency check passes; the 1.0 gate refuses:
15 open cells, 6 deferred cells, `releaseReady: false`, and no accepted obligation out of 44.
GitHub's Python/.NET, Explorer, CodeQL and wiki workflows completed successfully on that commit.
This is a backlog reconciliation and prerequisite inventory, not release acceptance.

## Results already recorded

The [2 November completion record](local-completion-20261102.md) supersedes older missing-input
statements: all seven vendor ZIPs were recovered on the qualification workstation, vendor intake
reran, one V20 LGF library upgraded, the bounded FBD OB component route passed, and distributed
OB55/56/57 event counters passed. Full DMC compilation remains bounded by vendor protection,
dependencies and CPU-specific instructions. Vendor intake byte counts do not establish large-workload
performance, and replacement runs do not recreate lost historical reports.

Configuration-event subscriptions are implemented. The IP-change observation passed; software and
hardware configuration-change delivery still need independent native proof. Acyclic data-record
responses and additional cycle-control modes remain implementation work.

## This workstation differs from the earlier qualification workstation

Read-only checks used installed-file metadata, public XML documentation, help archives and Windows
installed-product records. The reproducible [SDK receipt](optional-sdk-inventory-20261103.json)
pins the public assemblies/documentation by hash; `scripts/probe_optional_sdks.py` does not load them.
No Portal process, license check, project change or native qualification was performed by this probe.
Compile-only API-shape probes also build successfully against CFC/DCC V21 and V19 (zero warnings/errors).
They are libraries without an entry point; no method was invoked. Build with
`dotnet build scripts/native/OptionalSdkCompileProbe/OptionalSdkCompileProbe.csproj -c Release -p:ProbeVersion=V21 --artifacts-path artifacts/sdk-probe-v21`
and the corresponding `ProbeVersion=V19`/`artifacts/sdk-probe-v19`. Assemblies are referenced from the installation, never copied into the outputs.

| Area | Observed here | Next bounded check |
| --- | --- | --- |
| CFC/FCP V21 | Function Chart Plus V21 installed; `Siemens.Engineering.CFC.dll` and XML, `ImExCFCOpennessenUS.zip` and CFC help present. `ChartProvider` documents complete/selective export and import. | Resolve the documented service on an owned compatible scratch, establish license/fixture prerequisites, then no-change export/import with preservation, compile, reopen and recovery. |
| DCC V21 | DCC V21 installed; `Siemens.Engineering.DCC.dll`, XML and `DccOpennessenUS.zip` present. Public DCC block creation members are documented. | Map the documented service/drive compatibility, obtain an owned chart/library fixture and qualify offline lifecycle. Physical drive work remains separate. |
| TIA V19 | Portal executable version `1900.5.101.1`; V19 Openness assembly/XML present. Project create/open/archive and Portal construction are documented. Compatibility SDK folders V16–V18 are also present. | Build a separate V19 worker/probe and create an owned V19 fixture. Qualify V19-to-V21 upgrade independently from native V19 support. Compatibility SDK folders do not prove installed V16–V18 Portals. |
| TIA V15.1 / V17 | V15.1 executable `1501.8.401.2` present, no public SDK found under its Portal root. V17 directory has no Portal executable at the checked location. | Verify missing prerequisites before claiming either is usable. |
| Other products | Test Suite Advanced V21, PLCSIM Advanced V8 Update 1 and ODK 1500S V2.5 installed. | Product installation does not establish license availability or successful native operations. |

CFC/DCC and V19 are no longer classified as missing installation/SDK prerequisites **on this
workstation**. Their adapters and native qualification remain open; advertised support is unchanged.
The old workstation's absence observations remain historical evidence.

The documented `D:/tq1102/ui-seed` and `artifacts/local-completion-20261102`/`artifacts/evidence`
locations are absent here. The operator confirmed there is no prepared system-test project.
Recover the evidence/fixtures from their origin or create independent replacements; do not treat
this checkout's missing ignored artifacts as loss of the originals.

Update on 7 November 2026: on the maintainer's workstation the operator seed and the 2 November native runs exist; they moved from `D:/tq1102` to `artifacts/tq1102` (seed: `artifacts/tq1102/ui-seed/TiaMcpScratch_20261102_201944_5eab6efb.ap21`).

## Remaining release work

TS-Q03 native system-test qualification, physical runtime, VMware deployment, broader
drive engineering and other TIA adapters remain the five open/deferred matrix rows. Work outside
those rows includes component/HMI breadth, semantic API coverage, optional-product and service
integration, contract policy, and final frozen-candidate regression/package/install/rollback evidence.
The 13 contract-policy choices and 44 acceptance records still require maintainer review.

The [TS-Q03 exploratory runner](testsuite-system-q03-20261103.md) is now implemented and checked
offline. Its candidate fixture and native result interpretation remain unqualified; no matrix cell
or release obligation was promoted by these checks.
