# D09 — online comparison, DB snapshots and station upload

Audit date: 16 October 2026. All feasible capabilities remain 1.0 obligations per
the [release ledger](../release-obligations-20261015.md). No Siemens product,
SDK, schemas, PLCSIM or hardware is installed here. This plan separates portable
implementation from documented APIs, installed metadata and actual qualification.
Missing prerequisites remain open; they are not a reduced release scope.

## Current source and evidence

- `src/TiaMcp.Openness.V21/OpennessSession.Changes.cs` already captures offline
  block fingerprints with partial coverage and rejects online project mutation.
  That provider is not the portal-level quick-station provider below.
- `OpennessSession.SimulationProvision.cs` has exact adapter-owned simulator
  selection and local-softbus restrictions. Real-target work must use a separate
  admission/session model; no change to these guards is authorized by this plan.
- `src/TiaMcp.Core/Engineering/EngineeringIdentity.cs` distinguishes exact object,
  observation and content identities; equality proves captured consistency only.
- `ProjectReviewStore.cs`, `ArtifactUploadStore.cs`, `ArtifactStore.cs` and
  `PackageStorage.cs` provide saved observations, bounded intake, hashes,
  restricted paths and immutable publication. Engineering jobs provide the
  existing journal/reservation/recovery foundations.
- `OpennessSession.SimulationOnline.cs` adds `plc_online_compare` and
  `plc_station_upload` for a ticket-bound owned PLCSIM Advanced instance only
  (OC-03/OC-04/OC-06 for simulation, not real targets). They reuse the
  provisioning identity route and fail-closed challenges; see
  [Online compare and station upload](../../guides/simulation.md#online-compare-and-station-upload).
  Neither is natively qualified. Real-target admission remains open.
- No existing ValueService or InterfaceSnapshot adapter was found. No snapshot XSD or genuine snapshot XML fixture is retained here.
  Existing source fingerprints do not establish current online program identity.
- The first portable increment adds `DbSnapshots/DbSnapshotReview.cs`,
  `DbSnapshotWorkflowStore.cs` and `Host/DbSnapshotTools.cs`: bounded supplied
  XML inspection/comparison, retained originals, exact saved DB selection and
  restart/tamper-safe paging. [Workflow](../../reference/db-snapshot-review.md).
  Its source authenticity, live freshness, program identity, structure
  compatibility and native execution flags remain false.
- No concurrent checkout was found at the audit baseline. Shared files needing
  coordination are Program.cs, catalog/registry/bindings, aggregate/package
  scripts and STATUS. No parallel agent work or live native operation ran.

## Verified documentation and capability matrix

Context7 resolved the official V20 index. Two searches did not establish full
V21 coverage; a generated REST example was rejected as unsupported. The following
official V21 pages were read directly. Web documentation is not an installed
assembly inventory or proof that a service exists for a particular target.

| Workflow | Public route / source | Implementation and remaining acceptance |
| --- | --- | --- |
| Software comparison | `PlcSoftware.CompareTo(ISoftwareCompareTarget)` / `CompareToOnline()` [software comparison](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-downloading-data-to-plc-device/comparing-plc-software?contentId=47e4e9QtUcxQ_6bI3WCTWw) | Owned-PLCSIM adapter only (`plc_online_compare`, bounded left/right/state/detail/children tree); no real-target adapter. Preserve result tree, left/right names, ComparisonResult and DetailedInformation, including folder-content versus object state, missing-side and irrelevant states. Exact source/target association and per-target availability need installed metadata and native fixtures. Names alone cannot supply identity. |
| Quick station comparison | `TiaPortal.GetService<FingerprintDataProvider>()`, provider Configuration, `GetFingerprintData(ConfigurationAddress, OnlineConfigurationDelegate)` [fingerprints](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/accessing-fingerprint-for-quick-station-compare?contentId=KynDsDm9COhsdQPzFqPEjQ) | Owned-PLCSIM adapter only: `plc_online_compare` reads items and a hash before connecting. Documentation requires an attached portal, open project and offline PLC before the method establishes target communication. Retain item identifiers and values with coverage; do not copy the example's BinaryFormatter serialization. CPU/program comparability and unsupported/null provider handling remain unqualified. |
| Existing snapshot export | `InterfaceSnapshot.Export(FileInfo, ExportOptions)` [snapshot export](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/exporting-dbs-with-snapshots?contentId=07el5p41rgyCA1BBWkfJ3w) | Documents global, instance and array DBs and `SW.Interface.Snapshot.xsd`. Separate from ordinary block export; exported XML cannot be imported. Portable shape reader is implemented; vendor XSD, export options/accessor metadata and genuine captures are still required. Documentation examples span older export versions, so shape support is not a V21 compatibility certificate. |
| Capture current values into a snapshot | `block.Interface.GetService<ValueService>().CreateSnapshot()` [DB services](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/accessing-data-blocks) | Online operation stores values on the engineering block. It needs source preservation, reservation/checkpoint and unknown-outcome handling, even though it reads PLC values. No adapter or native qualification exists. |
| Snapshot/start values to actual values | `ValueService.LoadSnapshotAsActualValues()` / `LoadStartValuesAsActualValues()` [DB services](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/accessing-data-blocks) | Two separate online writes. Export/capture permission and a document hash cannot admit either. Explicit target, program, structure and write qualification must precede any enabled worker route. |
| Snapshot to start values | [DB member access](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/accessing-db-value-parameter-without-export?contentId=qG6zkYPe3We_Uq9_Y_qchA) documents writable dynamic StartValue; it does not establish a safe bulk snapshot-to-start route | Feasibility remains open. Dynamic snapshot/default attribute typing in examples is insufficient to invent a conversion. Obtain public member metadata, nested/array/instance applicability and preservation fixtures before implementing a prepared offline change. No snapshot-XML import shortcut. |
| Station upload | Project `GetService<StationUploadProvider>()`, provider-owned ConfigurationAddress, `StationUpload(address, delegate)` [station upload](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-downloading-data-to-plc-device/uploading-plc-device?contentId=9JysmxwZM2kTIknx3xCvNQ) | Owned-PLCSIM adapter only (`plc_station_upload`); no real-target adapter. Create a new owned destination project first. Device-group destination is unsupported. The documented route uploads hardware/software/files automatically; do not offer invented subset options. Retain UploadedStation plus all nested messages, states and error/warning counts. Missing products, callback failures and null station cannot be a clean success. Routing/CPU applicability requires exact profiles. |
| Authentication and secure target sessions | ConnectionConfiguration `OnlineLegitimation`, [UMAC](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/supporting-umac-on-plc-device) | Separate target/session admission for OnlineProvider, FingerprintDataProvider, DownloadProvider and StationUploadProvider. Resolve public callback types with installed V21 metadata. Handle credential requests and TLS trust explicitly, redact secrets, fail closed on unsupported challenges; no automatic certificate acceptance. |
| Interface/discovery selection | provider Configuration Modes/PcInterfaces and `GetAccessibleDevices()` [accessible devices](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/accessing-configuration-accessible-devices) | Discovery itself uses the network. Names/address/MAC/device-series fields can be generic or absent, particularly for non-Ethernet interfaces. No implicit selection by display name/IP and no claim that a discovery tuple is a CPU identity. |
| Download / CPU state control | Existing simulation-specific boundary only | Separate capability, authorization, recovery and qualification from diagnosis/value writes. D09 does not extend simulator restrictions into a real-CPU route. Public scope and firmware prerequisites need their own inventory. |

Product prerequisites include an installed compatible TIA/Openness SDK and
licensed engineering features, supported CPU/firmware/security configuration,
selected interface and reachable controlled fixture. R/H systems need explicit
station/member/role evidence and a failover test; a logical address cannot stand
in for both CPUs. Safety/protection access and prohibited export categories must
be returned as unavailable or partial, never empty or silently bypassed.

## Architecture, contracts and authority

Delivered tools: `db_snapshot_review` and `db_snapshot_review_page`, using
`contracts/db-snapshot-review.v1.schema.json`. Both are local-artifact operations.
No proposed native tool below is registered by this increment.

Proposed native read jobs: `online_target_observe`, `online_compare_start`,
`db_snapshot_export_start`; proposed capture job `db_snapshot_capture_start`.
Proposed owned-project job `station_upload_start`. Keep result/page/download
delivery in existing bounded job/artifact APIs instead of unbounded responses.
Snapshot/start-to-actual and snapshot-to-start changes need separate change kinds
only after supported routes and exact preconditions have been established.

Future target observation must bind project/PLC/scope/object selectors, public
native identity evidence and unavailable reasons, mode/interface/address,
firmware, R/H member role, certificate/security context, session epoch,
observation timestamp and supported program fingerprint identifiers. Credentials
are transient handles, never artifact fields. Record program identity unknown
when the service cannot establish it; a user assertion cannot upgrade that state.
Hashes establish artifact integrity, not authenticity, atomicity or live freshness.

Keep five value/source domains separate: offline StartValue, online actual value,
stored snapshot value, saved project metadata and program fingerprint evidence.
Never infer an actual value from a stored snapshot or rename an unknown timestamp
to acquisition time. Future acquisition records need start/end times and whether
the observed set was atomic, partial or potentially inconsistent.

Before native use, reobserve exact CPU/program/DB structure/session identity;
refuse unknown or mismatched evidence and require a new review. Epoch, content or
structure change invalidates admission. Comparison can report stale evidence as
historical data, but cannot reuse it as native authority. The current supplied
review has no admission output and always reports live identity/freshness false.

Worker jobs need target/project resource reservation, bounded result trees,
deadlines and explicit cancellation state. A timed-out/terminated call after
dispatch is unknown-outcome until separately reconciled. Do not reconnect and
repeat a write, capture or upload automatically. Capture preserves original block
and project snapshot metadata before execution. Station upload reserves a new
owned directory/project; errors preserve the partial destination and report all
messages. Never overwrite a user's existing project or auto-delete unknown work.

## Ordered work packages and completion criteria

Estimates are focused engineering days with valid fixtures/access available,
excluding product procurement, owner scheduling and native runtime. They are not
completion promises. Implementation and qualification are separate outputs.

| ID | Inputs / concrete boundary | Completion / remaining work | Estimate |
| --- | --- | --- | --- |
| OC-01 | Official docs; installed Base/Step7 XML and public assemblies when available; `scripts/public_api_metadata` | Documentation audit delivered here. Pin SDK/member/schema inventory and target availability for every table row; resolve accessor/option discrepancies before native compilation. | 1–3 + access wait |
| OC-02 | Documented XML shape; saved reviews; Core/Host/store/contracts | Portable supplied inspection/comparison delivered by this increment; exact originals, bounded output, schema/MCP/restart/tamper/Unicode tests. Genuine XSD/capture compatibility remains open under OC-01/OC-08. | portable increment; 1–3 for genuine profiles |
| OC-03 | Native metadata + target fixtures; new target-session module, identity schema, authentication callbacks | Distinct real-target admission, secret-free artifacts, precise unavailable/ambiguous states, epoch/program/structure revalidation, fake transport interruptions and actual identity/trust tests. No modifications to simulator-only admission. | 4–8 |
| OC-04 | OC-03; V21 comparison/fingerprint adapters, Core result normalizer | Complete bounded comparison tree and fingerprint coverage without boolean collapse. Bind all artifacts to exact sources, report changed program/partial results, integrate diagnostic/Explorer navigation. | 3–6 |
| OC-05 | OC-03; ValueService/InterfaceSnapshot adapter, job journal, export fixture profiles | Export existing snapshot first; then preserved capture plus export with checkpoint/unknown-outcome recovery. Retain original snapshot dates and acquisition interval separately. | 4–8 |
| OC-06 | OC-03; upload provider, owned destination manifest and job | Upload to newly created owned project, retain complete nested result and partial products, handle credential/certificate/missing-device/product failures, fresh reopen and explicit recovery. | 4–8 |
| OC-07 | Proven write/member routes + OC-03/05 | Separate prepared offline start-value change and online writes; exact typed applicability, whole-effect preview, conflict refusal, independent postcondition/preservation and nonreplay recovery. Unsupported route stays explicitly blocked. | 5–10 after feasibility |
| OC-08 | All source/binary/schema/fixture/profile identities | Independent qualification matrix below, catalog/admission reconciliation, user documentation and release evidence. No current offline test satisfies this package. | 4–10 + bench scheduling |

The critical path is SDK/schema and authentic target evidence → exact target
session → comparison/export → capture/upload → separately qualified mutations.
Current portable document review is useful before that path, but does not shorten
identity/trust/native qualification. Additional offline work can exercise proven
contracts and fake job failure paths once OC-03's public metadata is pinned.
Avoid a fictional transport API or manufactured native proof packet.

## Qualification and independent expectations

Bind every run to source tree, binaries, public metadata, schema, fixture project,
target model/firmware and profile hashes. Preserve failed attempts. Reuse retained
accepted evidence only for its exact compatible scope; do not repeat an old VM
campaign merely to obtain another green result.

1. Compare independently prepared identical and changed programs, missing objects,
   folder-only differences, irrelevant objects and inaccessible/protected blocks.
   Verify every tree row/counter and complete/partial coverage against native UI
   or independently captured reports, including result-size truncation.
2. Test same-name DBs on two CPUs/programs, renamed/moved DBs, changed firmware,
   changed online program after preview, unknown fingerprints and R/H failover.
   No address/name/time-only fallback may preserve admission.
3. Capture global/instance/array DBs, nested structures, sparse/empty values,
   strings/Unicode, date/time, large integers, unsupported types and changed
   structures. Obtain the genuine XSD and test actual exports without assuming
   the older manual sample is the installed version's output.
4. Export existing snapshots without modifying engineering state; capture with
   original snapshot metadata preserved and accurately reported changed state.
   Verify values independently and record non-atomic/partial observations.
   Never treat compilation as evidence of observed PLC values.
5. Authentication denial, credential challenge, certificate change, wrong
   interface, connection loss before/during/after dispatch, process death and lost
   response must preserve exact job ownership and unknown-outcome semantics.
   Confirm no credential/certificate secrets appear in logs/artifacts.
6. Upload into a fresh owned project with clean, missing-product/device, warning,
   error and partial outcomes. Preserve the preexisting user project unchanged.
   Verify uploaded station inventory, software/hardware/files and nested messages;
   compile/reopen where supported, with failures retained rather than hidden.
7. Each enabled mutation needs an independent before/after oracle, unrelated
   content preservation, fresh reopen, first-checkpoint recovery and explicit
   proof no duplicate action occurs after interruption. Online writes need actual
   controlled PLC observations; offline fake tests cannot qualify them.
8. Scale and bounded paging, artifact release/restart, hostile XML/JSON, corrupted
   receipts, package relocation and real MCP-host usability remain separate
   checks. Passing the stdio client is not desktop-host visual acceptance.

## Remaining decisions and next executable work

OC-01 still needs genuine V21 SDK declarations and snapshot XSD/captures; exact
fingerprint comparability and member/start-value conversion routes remain unknown.
Snapshot XML import is explicitly unsupported by the documented route. R/H,
protection, firmware and routed upload support require target-specific admission;
do not infer broad support from one example.

OC-02 is the first complete useful portable workflow. Its verification receipt
must identify the exact tested source and limits. Next, inspect remaining offline
consumers and the next expansion domain while keeping OC-01/03–08 open. Where
genuine artifacts become available, run OC-02 compatibility tests before extending
the parser profile. No new native operation, compile, connection or acceptance
has been executed by this audit or portable implementation.
