# Motion and technology objects - D13 implementation plan

**Configured hardware preservation — 17 October 2026:** All inventoried DeviceItem reads now participate in encoder edit/recovery/reopen/copy preservation. Complete attributes, addresses, channels and configured network associations are compared with exact owner/request bindings and before/after inventories. The native inventory type-identifier omission is repaired. [Evidence](../../verification/encoder-hardware-preservation-20261017.md). The frozen 41-suite aggregate and all 24 package stages pass. Device/project settings, external sources and other unexposed content plus native qualification remain open.

**Software peer preservation — 17 October 2026:** The encoder fixture now checks all exposed software inventory rows, empty user groups and native peer exports across edit/recovery/reopens and copied inspection. Native membership binds selected TO/block aliases; no name-only exclusion is used. Independent auditing and copy admission bind original calls and bytes. [Evidence](../../verification/encoder-software-preservation-20261017.md). The frozen 41-suite aggregate and all 24 package stages pass. Hardware-wide settings, unexposed software/project content and native qualification remain open; this does not close TO-03–12.

The [closed source/copy admission repair](../../verification/encoder-copy-admission-20261017.md)
publishes terminal file snapshots, requires the original report digest, and opens
fresh verified inspection copies with per-attempt admission and independent source
preservation checks. The frozen 41-suite aggregate and all 24 package stages pass. Unrelated engineering-content preservation, native SDK/product behavior
and TO-03–12 remain open.

The [recovery-copy endpoint repair](../../verification/encoder-endpoints-20261017.md)
adds bounded native hardware membership and independently selected inventory/channel
proof in every encoder phase. Cross-copy comparison retains project ownership and
provider evidence while matching admitted logical channels. The frozen 41-suite aggregate and all 24 package stages pass. Closed saved-copy byte admission,
unrelated-content preservation, native SDK/product behavior and TO-03–12 remain open.

Status: implementation-ready scope and source audit, 17 October 2026. Every feasible
capability remains in the 1.0 obligation. No Siemens product, public SDK installation,
PLCSIM or PLC is available on this machine. This plan neither enables native writes
nor promotes historical evidence to current qualification. The active user request
also authorizes implementation; the older planning prompt supplies scope rather than
restricting the whole task to documentation.

## Shared property read boundary

The [17 October repair](../../verification/inspection-values-20261017.md) passes
188 general and 46 targeted Python checks; the frozen aggregate and all 22 package stages pass.
Sensitive/ambiguous attribute getters are excluded before access. Values are bounded
by 512 nodes, depth 16, 64 items per collection and 4,096-character strings. Partial
captures and native display projections are explicit and cannot qualify connected
hardware identities. The connection-service repair below supplies bounded enumeration,
per-service state and direct field completeness. No native SDK or endpoint evidence is available.

## Connection-service observation boundary

The [17 October source repair](../../verification/technology-connections-20261017.md)
passes 196 general and 65 targeted Python checks; the frozen aggregate and all 22 package stages pass.
Four independent service observations distinguish null exposure, failure and skipped
work. Sensors, null entries, fields and attributes consume the same budget; errors
retain successful peers. Exact TO paths and fieldStatus reach both readback callers.
Qualification requires complete explicit service/slot/field coverage, including
empty-list absence observations. Overall complete stays false; native endpoint
identity, additional service families and current SDK qualification remain open.

## Current implementation and evidence

The [fresh baseline increment](../../verification/plc-baseline-20261017.md) adds explicit checkpointed PLC compilation before baseline capture and after recovery, with exact original-call evidence. The frozen 41-suite aggregate and all 24 package stages pass. Native SDK/compiler/signature behavior, copied endpoint correspondence and the full family plan remain open.

The [encoder target repair](../../verification/encoder-compiler-targets-20261017.md) binds software compiles to independent PLC selections and seeder compiles to the authorized complete device set. Original session records, helper output and recovery/reopen ownership are audited; the frozen 41-suite aggregate and all 24 package stages pass. Fresh baseline compilation, copied endpoint correspondence, native SDK behavior and TO-03–12 remain open.

The [runtime-guard repair](../../verification/qualification-runtime-guards-20261017.md) prevents 130 qualification/preservation checks from disappearing under Python optimization. All 37 focused cases pass under `-O` and `-OO`; the frozen aggregate and all 22 package stages pass. This does not establish current native fixture qualification or cross-copy endpoint identity.

The [encoder lifecycle repair](../../verification/encoder-lifecycle-20261017.md) closes the reproduced cleanup false-success defect in portable orchestration. Producer, companion and auditor require original process terminal receipts, and helper uncertainty cannot authorize replay. Sixteen fake-process regression methods pass; the frozen aggregate and all 22 package stages pass. Native behavior, compile completeness and cross-copy endpoint correspondence remain open.

The [compiler-diagnostic repair](../../verification/encoder-compile-evidence-20261017.md) now requires complete explicit diagnostics across encoder and typed-route qualification. The seeder source links the existing pure C# validator, and source prerequisites are rechecked before companion startup. Focused checks pass; the frozen Python aggregate, ten pure C# compiler groups and all 22 package stages pass. This does not establish omitted-target coverage, SDK build or recovery-copy endpoint correspondence.

The [checkpoint-provenance repair](../../verification/encoder-checkpoint-20261017.md) binds failed-seeder recovery to a durable receipt published before seeder mutation. The parent verifies original archive bytes and authorization, and never adopts a replacement archive hash. Targeted publisher/reader and failure-path checks pass; the frozen aggregate and all 22 package stages pass. Native archive/retrieval, whole-project preservation and cross-copy endpoint correspondence remain open.

The [endpoint-reference increment](../../verification/technology-references-20261017.md) uses documented project-provider roundtrips and verified-parent channel composition checks. It passes 202 general and 68 targeted Python checks; the frozen aggregate and all 22 package stages pass. It does not qualify SDK behavior, durable identities or full connection lifecycle.

`src/TiaMcp.Host/EngineeringTools.cs` routes `technology_read` and `engineering_read`
to the worker. `src/TiaMcp.Openness.V21/OpennessSession.Inspection.cs` provides exact
path/name selection, system-library identity, parameter values, optional native XML,
and axis/encoder/torque/measuring-input/output-cam observations. Connections and
parameters share a bounded scan; warnings/truncation mean incomplete evidence.
Its generic value display has a 4,096-character limit and is not a lossless exporter.

`OpennessSession.Engineering.cs` exposes native scalar type, access metadata and
explicit parameter/object truncation. Its creation metadata deliberately has no
CPU-supported-type/version enumeration. `OpennessSession.Changes.cs` implements
`to_create`, `to_parameters`, `to_delete`, checkpointing, compile and selected native
readback. Existing-object previews now bind exact group paths; legacy case-insensitive
names require uniqueness, and apply never falls back from a missing path. Bounded
selection and metadata fingerprints remain revision evidence, not durable identity
or complete service-child coverage. Creation writes
initial parameters before compile. The scalar-access source repair now refuses missing/ambiguous access evidence, resolves
null values only through one explicit supported type, preflights the entire batch and
checks typed readback. Linked synthetic tests pass; SDK/native acceptance is absent. The
fixture harness now requires known write access and a declared type. Generic
parameter conversion does not establish whole-object preservation.

The shared `TechnologyObjects` traversal now visits PLC groups and the documented
OutputCams/MeasuringInputs service children; other service compositions remain unexpanded.
`TechnologyConnections` does not capture every SDR, association, interpreter or cam
service. `OpennessSession.ProjectModel.cs`/`Index.cs` reuse the basic TO observations.
The portable TO-specific review is described below; no general native connection mutation exists. Existing
saved reviews, `EngineeringIdentityGuard`, `NativeTargetGuard`, `EngineeringOperationStore`,
package uploads, artifact hashes, graph/impact reports and DriveProfileReview are reusable.

`fixtures/technology-profiles` contains exact CPU1511 FW4.1/version10 axis and encoder
profiles. `fixtures/encoder-hardware/cpu1511-fw41-tm-posinput2-v10.json` adds a concrete
TM PosInput dependency. `scripts/qualify_technology_families.py` is the scalar fixture
harness called by `smoke_hardware_to.py`; `qualify_encoder_hardware.py`, its native
EncoderSeeder, `verify_encoder_preservation.py` and `audit_encoder_preservation.py`
handle the distinct connected fixture. Keep those paths and failed attempts intact.

The [engineering qualification record](../../verification/engineering-gap-qualification.md)
and [profile notes](../../../fixtures/technology-profiles/README.md) describe an offline
virtual positioning axis, a disconnected encoder compile failure/recovery, and a
separate connected encoder scalar edit with full retained preservation evidence.
Those ignored native artifacts are unavailable here. Their 106 historical binding
rows/hashes are preserved. Their documented scope is not a new executed pass. There
is no broad connected-axis, runtime-motion, G2 or T-CPU qualification.

Current ownership: primary `main` checkout only; no parallel agent or extra worktree.
Shared edit surfaces are Changes/Inspection/Capabilities, saved-review contracts,
feature catalog, graph/Explorer and validation scripts. D08 owns hardware/connection
resources, D10 owns drive/telegram lifecycle, D13 owns the TO-side connection and
coupling. One operation must own the complete effect set across them.

## Public documentation and feasibility

Official V21 pages were checked on 17 October 2026. Context7 resolution returned the
official V20 library, but no exact V21 library; direct Siemens V21 sources below are
the basis here. `C:/Program Files/Siemens/Automation/Portal V21/PublicAPI/V21` is absent.
Public source examples are discovery evidence, not an SDK compile or target test.

| Source | Applicable contract and limit |
| --- | --- |
| [Family/version matrix](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/overview-of-technology-objects-and-versions) | CPU families, TO versions and explicit function exceptions. Never turn a minimum version into a universal article-number/firmware entitlement. |
| [Creation](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/creating-technology-object?contentId=1HYopnU2cpLhvrB6d_0UkA), [parameter writes](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/writing-parameters-of-technology-object?contentId=bqLp3MAE5yG2OTXULpD4LA) | `TechnologicalInstanceDBComposition.Create(name,type,Version)` and writable `TechnologicalParameter.Value`; unsupported type/version/value/access throws. No general supported-pair enumerator was established by this audit. |
| [V21 migration](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/upgrading-to-openness-v21), [SDR encoder](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/connecting-encoders-configured-by-startdrive?contentId=OoU7T5BRsVGAYG3EI90bRQ) | Base/Step7 assemblies plus Startdrive for telegram objects. SDR providers/interfaces own the telegram overloads. The migration table labels its new column `> V21` despite the V21 text; verify actual assembly members instead of treating that label as a version predicate. |
| [Drive connections](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/connecting-drives?contentId=Y1ytjt0rqgDIqoUDN2UFKA), [encoder connections](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/connecting-encoders?contentId=bMrTP1JyvonjNkUFoueyXg) | Axis/encoder interfaces support eligible module/channel/address/DB-member routes; analog tag applicability differs. Drive connection can also change sensor/torque connections. Explicit address routes use bits; do not confuse native readback units with input units. `AllowAllModules` must never become a default. |
| [Child objects](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/creating-and-finding-to_outputcam-to_camtrack-and-to_measuringinput?contentId=VG9FS0UkqhImaR00Tn6ILw), [output](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/connecting-output-cams-and-cam-tracks-to-hardware?contentId=Wde3hmbY5p9fP46_WGpPug), [measurement](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/connecting-measuring-inputs-to-hardware?contentId=~BLpHrbOmiKCysd5_tPO5w) | `OutputCamMeasuringInputContainer` owns child compositions. Output and measurement providers have distinct connection forms and address formulas. Copy-from-master-copy support does not establish arbitrary movement between owners. |
| [Synchronous coupling](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/connecting-synchronous-axis-with-leading-values?contentId=1Wu880l8nEtXklwqCeaypA), [superimposition](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/connecting-with-superimposing-axes?contentId=EVgPJUeikxbEvNFZCzMaXQ), [conveyor tracking](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/connecting-conveyor-tracking-of-kinematics-with-leading-values?contentId=z8qMte6l~bRkKi1wtb6lew) | `SynchronousAxisMasterValues`, `SuperimposingAxes`, `ConveyorTrackingLeadingValues` expose typed associations. Setpoint/actual/delayed routes have different CPU/version restrictions; superimposition requires the documented T-CPU FW4.1/TO10 boundary. |
| [Cam data](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/exporting-and-importing-technology-object-cam-s7-1500t?contentId=tdHJcN~87J9OB7K5mfwG2Q) | `CamDataSupport` handles binary and MCD/SCOUT/point-list forms, separators and sampling. Generic point-list export defaults to 360 samples; a sampled list is not an exact source representation. Save overwrites its destination, so use fresh staged paths. |
| [Interpreter source](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/loading-and-saving-the-source-code-of-an-interpreter-program-s71500t?contentId=YMqAAiXeRTrbsuYnh26Y7w), [mappings](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/configuring-interpreter-mapping-technology-object-s71500t?contentId=U8yYzYOCl6sWWMGpPhqdaw) | `InterpreterProgramSupport.SaveSource/LoadSource` handles MCL text. `InterpreterMappings` has TO and DB-member compositions, aliases, target/member/type/access/start-index fields. The mapping-create example does not assign its target: production code must, then independently verify it. |
| [S7-1200 PTO](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1200-motion-control/connecting-pto-outputs?contentId=04as6xe_b3Cy5ieo49xZaw), [TO topics](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects) | Legacy 1200 routes include parameter-backed PTO and hardware-address/DB connections. PID, counting, Ident and EMC have distinct topic sets; do not apply the 1500 provider model universally. Exact Ident connection members still need SDK/docs resolution. |

## Family-by-operation coverage

`P` = existing partial generic bridge route; `S` = documented specialized route,
not implemented here; `X` = explicitly unsupported generic parameter operation;
`?` = exact profile/route still unresolved. None means native qualification.
Lifecycle includes inventory, group/owner selection, create/delete and compile;
XML exchange remains separately qualified even where generic Export exists.

| Family | Lifecycle / scalar read-write | Connections or dedicated content | Eligibility and remaining coverage |
| --- | --- | --- | --- |
| Legacy 1200 positioning axis/command table | P / P | S: PTO, drive/encoder address or DB routes | Exact legacy CPU/firmware/version; no 1500 API substitution. |
| 1500 speed/positioning/synchronous axes; external encoder | P / P | Partial observation; S: drive/encoder/torque/SDR/DB/tag wiring | Existing selected fixtures cover only their exact profiles; connected lifecycle remains open. |
| 1200 G2 motion family | P applicability ? / P applicability ? | S applicability ? | Matrix refers to 1500 functions, but article/options and T-only examples require independent G2 resolution. |
| Output cam, cam track, measuring input | Root route insufficient / P applicability ? | S child owner/container, dedicated I/O | Capture owner identity and child compositions; no flattened-root creation. |
| Cam, 10k, 600Seg, 6kSeg variants | P applicability ? / reads P, writes X | S cam binary/text import/export | Exact T-CPU/version and format fidelity; no guessed writable parameters or lossy round-trip claim. |
| Leading-axis proxy, kinematics | P applicability ? / P | S leading values, conveyor and axis dependencies | T-CPU/version, peer availability and documented remote coupling restrictions. |
| Superimposed positioning/synchronous axes | Existing axis lifecycle only | S `SuperimposingAxes` associations | Distinct coupling operation, not an invented TO family identifier. |
| Interpreter | P applicability ? / P | S related program/mapping dependencies | T-CPU, exact interpreter version and generated-instance effects. |
| Interpreter program/mapping | P applicability ? / X generic parameter enumeration/read/write | S MCL and mapping services | Never reinterpret an empty parameter array as full coverage. |
| PID Compact/3Step/Temp; CONT/TCONT variants | P / P by exact profile | ? controller I/O and generated members | Compact3.0 has recorded narrow evidence; legacy/modern CPU distinctions, no tuning/runtime claim. |
| High-speed counter, SSI absolute encoder | P applicability ? / P | ? documented parameter/module relationships | Treat counting encoder separately from motion external encoder. |
| Ident / TagFields | P applicability ? / P | ? Ident-device route and device association | Explicit source/target profile and service metadata before implementation. |
| TUN_EC/TUN_ES, PID_CP/PID_ES and EMC AXIS_REF | P applicability ? / some tuning variants X | ? service-specific configuration | S7-300/400 product availability; no invented generic parameter fallback. |

The matrix covers the documented families without copying broad compatibility minima
into an executable allowlist. Maintain exact observed article/FW/TO/version/options
profiles, with source references and per-operation support. Missing/blocked/unknown
capability is distinct from unsupported and from qualified. A documentation search
that finds no enumerator is not proof that no such public API exists.

## Architecture and proposed contracts

Use existing upload commit, immutable saved-review selection and artifact storage.
TO-02 now provides `technology_profile_review` and `technology_profile_report`, backed by
`src/TiaMcp.Core/TechnologyObjects/TechnologyProfileReview.cs` and thin Host tools.
The installed strict
`technology-profile-review-request.v1` carries an exact saved technology reference,
before/after public observations, CPU/article/FW/TO/type/version/option provenance,
full parameter names/types/access/value, explicit connection/coupling/mapping records,
and separately authored proposal values. The v2 extension now adds independently
retained source-artifact references; v3 adds declared consumers and bounded saved impact context.

Initial bounds: 4 MiB request, 4,096 parameter rows and 1,024 declared relationships
per phase, 500 changed proposal records; reject duplicate identities, invalid Unicode, ambiguous
names and silent truncation. Retain original bytes and opaque fields. Unknown units,
limits, enum meanings, arrays and target resolution remain explicit; perform no
invented conversion, address derivation, firmware inference or license check. Reports
expose supplied-evidence differences and contradictions, with `nativeExecuted`,
`targetCompatibilityVerified` and mutation authorization false.

Bind native operations to exact owner and object paths, project/PLC snapshot,
CPU/profile/adapter/source digests and fresh exclusive-access reacquisition. Extend
the existing prepared change workflow only after metadata confirms the route; proposed
mutation families are connection, coupling, child lifecycle, cam content, MCL source
and interpreter mapping. Do not add a universal TO write tool. Separate named endpoint
variants; no overload guessing from string shape. Resolve DB members through captured
executable declarations, not display names or UDT resemblance.

A connection operation includes affected project, hardware/drive endpoints, generated
TO DBs, sensors/torque/peers and source artifacts in its effect set. D10 returns an exact
telegram reference; D13 invokes the TO endpoint while the same operation owns both.
Address input records units explicitly and rejects overflow/ambiguous direction;
prefer supported native object references. Cross-PLC relationships require documented
route-specific eligibility, never implicit same-name joins. Recovery restores to new
locations and independently reconciles each original and external resource; lost replies
retain unknown ownership. Existing credential/session rules apply if a provider needs them.

## Work packages and acceptance

Effort ranges are engineering days for one developer after listed prerequisites;
native product acquisition, licenses and bench access are additional, unbounded gates.
No parallel edits to shared native files are proposed.

| ID / phase | Work, boundaries and inputs -> outputs | Completion evidence / effort |
| --- | --- | --- |
| TO-01 discovery and trustworthy baseline | This plan; existing profiles and scalar qualification helper/tests. Audit false-positive acceptance, stale/incomplete observations and preservation before expanding profiles. | Reproduced defects and portable refusal/success tests; historical evidence unchanged. 1-3 days. |
| TO-02 complete portable review workflow | Core/TechnologyObjects, Host, strict request/report schemas, uploads, saved review, graph handoff, focused managed/MCP tests. Supplied observations -> immutable diff/dependency/contradiction reports. | Real Host intake/report/restart/tamper/limits/Unicode tests, package relocation, no native claims. 3-6 days. |
| TO-03 exact native capture and eligibility | Inspection/Engineering/ProjectModel/Capabilities plus SDK metadata inspector. Genuine SDK/type/version fixtures -> exact owner/service/typed-access profile and bounded capture. | SDK compile, exact metadata bindings, child/association completeness and unknown handling; source-bound target capture later. 3-6 days. |
| TO-04 lifecycle and typed scalar operations | Changes, durable operation adapter, group/child selectors and whole-object oracles. Exact profiles -> create/rename/delete/group/template operations actually exposed publicly. | Initial prerequisites before compile; peer/generated DB preservation, stale/refusal, restart and restore tests. Exact public rename/move availability remains to resolve. 4-8 days. |
| TO-05 connected axis/encoder vertical slice | D08/D10 references, existing encoder recipe, drive/SDR/DB/tag route profiles -> one fully configured eligible axis and encoder graph. | Dependency-ordered creation/connect/compile/readback/save/reopen/recovery with every affected resource reserved; independent connection oracle. 5-10 days plus native gates. |
| TO-06 cam-track/measurement child lifecycle | Child compositions and output/measurement providers, master copies, hardware channels -> owner-bound configured children. | Parent/child identity, exact I/O route/index/units and sibling preservation; failed creation/connection recovery. 3-6 days. |
| TO-07 coupling, proxies and kinematics | Typed associations, peer snapshots, exact T/G2 restrictions -> planned add/remove and dependency effects. | Cycle/self/duplicate/stale-peer cases, association order/whole-set preservation, exact eligibility and fresh native readback. 4-8 days. |
| TO-08 cam artifact lifecycle | CamDataSupport, genuine format specifications/samples -> lossless binary/source retention, explicit sampled exports, staged import. | Format/separator/version matrix, exact bytes/opaque data, no source-fidelity claim from sampling; compile/reopen/recovery. 3-6 days. |
| TO-09 interpreter lifecycle | InterpreterProgramSupport and InterpreterMappings, MCL originals, executable DB/axis identities -> source and mapping review/edit. | Alias collisions, exact member/type/access/index targets, stale references, untouched source/mappings, compile/recovery. No invented MCL parser. 4-8 days. |
| TO-10 remaining controller/counter/Ident/legacy families | Per-family genuine parameter/service profiles -> supported lifecycle/configuration recipes and explicit impossible routes. | Profile-specific complete baselines, typed access, native preservation; physical loop/Ident/counting tests remain distinct. 5-12 days plus prerequisites. |
| TO-11 integration and templates | Graph/Explorer, hardware/drive/TO impacts, library/master-copy provenance, source-reuse manifests -> coherent review and reusable exact-profile recipes. | Rebinding/collision/stale-template/source migration tests; no automatic CPU/version upgrade or current-target certification from a saved template. 3-6 days. |
| TO-12 full expansion and release | Every matrix cell/work package -> source/profile/fixture/binary-bound qualification and immutable native handoff packets. | Requirement-by-requirement audit, existing evidence reuse within scope, actual missing native/bench gates, rollback and delivery acceptance. 3-5 days audit plus external qualification. |

Critical path: TO-01 -> TO-02 -> TO-03 -> TO-04/05 -> integration/qualification.
TO-06-10 remain full release obligations; unavailable products do not silently defer
them. Portable supplied-evidence work can advance before TO-03, but cannot authorize
native changes. New fixture examples must be labeled synthetic unless genuinely captured.

## Most valuable complete workflow

Select an exact saved axis/encoder plus its hardware or drive/telegram endpoints.
Review all dependencies and required target capabilities, resolve type/version/options,
and retain independent baseline exports and typed connections. On an admitted native
fixture, create prerequisites, create the TO with writable initial parameters, establish
the typed hardware connection, configure required sensor/torque relationships, then
compile the full affected PLC/device. Verify every requested value, all untouched
parameters/connections/generated blocks and native XML. Save, independently reopen,
and compare against the expected configured graph. Interrupt each boundary and require
one-use recovery and independent original/copy reconciliation.

Reuse the exact connected external-encoder recipe as an anchor; it does not prove a
connected axis. Add a genuine compatible drive/telegram or documented DB-backed axis
fixture after metadata and native capture resolve eligibility. No guessed virtual
encoder or generic partner is acceptable. The portable precursor is the same explicit
review/dependency/preservation contract using supplied observations, with live and
motion claims false.

## Qualification and open decisions

Retain source tree, fixture/profile, document inputs, SDK/service signatures and loaded
binary hashes. Independently authored expected changes must compare full native XML,
typed values/access, complete connection/coupling sets, siblings and generated layouts.
Artifact availability, bounded capture and native compile status are separate assertions.
Test malformed/duplicate names, same-name different owners, unsupported family/version,
missing access metadata, stale/replaced endpoints, wrong telegram/direction/index/unit,
partial reads, exceptions, reply loss, cancellation, cleanup failure and archive loss.
Never retry a possibly dispatched mutation or release reservations on elapsed time.

Use portable negatives, cross-view mismatch tests, dependency cycles and synthetic scale
now. Later native tests cover exact creation/edit/delete, save/reopen, independent restore,
product/firmware matrix and version migration. Runtime validation separately requires
licensed simulation where supported, real I/O/motion/feedback observation and appropriate
bench acceptance. Offline compilation cannot establish tuning, motion, physical safety,
servo behavior or hardware timing. This plan schedules no redundant VM rerun of accepted
historical fixtures and does not erase failed/blocked attempts.

Unresolved: current installed metadata and genuine exports; supported-pair discovery;
G2 article/options versus family-level docs; exact Ident and legacy routes; cam/MCL format
fidelity; generic rename/move/import eligibility; service-owned child enumeration; full
SDR connection capture and shared operation integration; licensed/simulation/bench access.
The smallest later experiments are metadata-only service/overload inspection, exact
read-only capture of one representative per service, and one isolated profile operation
with independent baseline/reopen/recovery proof. No private project internals or invented
API substitutes are planned.

TO-01 now includes the [qualification-oracle repair](../../verification/technology-oracle-20261017.md): complete dual readback, exact peer/access/type preservation, connection/identity consistency, explicit numeric comparison and failed-state retention. Targeted portable checks pass; final frozen verification is recorded separately.

Next executable work: feasible TO-03 metadata/capture preparation, following the fully offline-checked [shared attribute repair](../../verification/attribute-scalar-access-20261017.md); TO-01 native acceptance is still absent.
All native/provider and TO-03-12 acceptance remains open until its evidence exists.

## TO-02 implementation - 17 October 2026

The [supplied profile/relationship workflow](../../reference/technology-profile-review.md)
implements retained intake, parameter/relationship differences, declared-cycle and
unresolved-endpoint reporting, saved graph context and restart-safe bounded report
pages. `technology_profile_review` and `technology_profile_report` are implemented;
the proposal description above is retained as design context. The before/after
proposal form permits at most 500 changed records rather than a separate redundant
proposal array. Original JSON is retained; external provenance can be documented in
opaque metadata but is not independently authenticated or imported as a native export.
Specialized cam/MCL formats and native whole-object preservation remain separate.

Focused managed and actual Host tests pass. Final frozen aggregate and package
verification are recorded in [the delivery record](../../verification/technology-profile-review-20261017.md).
TO-02 source-artifact bundle retention and declared consumer/impact handoff are implemented. TO-03 exact native capture and the TO-04–12 lifecycle/content/qualification
packages also remain open; supplied context does not establish complete transitive impact.

### TO-02 portable acceptance

- Implemented: retain independently supplied source files, not only provenance strings in request
  metadata. Bind each label to its committed upload and digest, copy original bytes into
  the workflow's owned retention, and keep reopen valid after upload release. Rehash
  every retained source on reopen; a changed source cannot silently inherit its old role.
  Enforce per-file, total-byte and count limits; interrupted publication counts against
  capacity and never grants a successful record. No format/native authenticity claim.
- Implemented: carry explicitly declared parameter/relationship consumers with exact saved object
  selection. Distinguish missing, ambiguous, out-of-scope and captured identities;
  retain before/after associations and unknowns. Reuse existing saved graph impact
  traversal where applicable, with explicit edge/path/depth limits and unresolved
  evidence. Do not infer executable membership or complete impact from display names.
- Passed offline: exercise committed source intake, same digest under different declared purposes,
  duplicate labels, mismatched hashes, corruption/rehashed forgery, upload release,
  Host restart, broken references, Unicode and aggregate/package relocation. Preserve
  existing review records and their original scalar/relationship semantics.

### TO-02 source bundles implemented - 17 October 2026

The v2 request/record path now retains independently committed source uploads with
exact labels, purposes, phases, filenames, byte counts and digests. It keeps v1
records byte-identical, checks every source on reopen, survives upload release and
refuses corrupt or rehashed substituted evidence. Partial publication cannot produce
a completed record and remains counted against quota. The source-retention bullet
above is implemented; the v3 consumer/impact extension is documented below.
See the [source workflow](../../reference/technology-profile-review.md#independently-retained-source-files-v2)
and [verification record](../../verification/technology-sources-20261017.md).
No native format or authenticity claim follows from retaining the files.


### TO-02 consumer handoff implemented - 17 October 2026

The remaining declared consumer/impact handoff above is implemented in v3 packets.
Exact per-phase consumer associations retain missing subjects, ambiguous/mismatched/
out-of-scope/unknown object identities and unverified members. The existing bounded
saved traversal supplies same-PLC context from the selected TO and resolved consumer
objects; it never turns supplied links into captured edges. Depth/path/edge/byte
bounds and excluded evidence are explicit. Both phases use the same saved review,
not a fabricated native before/after capture. Old v1/v2 records remain identical.
[Workflow](../../reference/technology-profile-review.md#declared-consumers-and-saved-impact-context-v3)
and [verification](../../verification/technology-consumers-20261017.md). The final frozen aggregate passes 39 managed suites, 1,295 Python tests (one skip),
24 Explorer checks and all MCP workflows. All 22 package stages pass, including
205 TO managed and 125 MCP checks. Native TO-03–12 acceptance remains open.


### Scalar-write access repair - 17 October 2026

The known-access/typed-value gap is repaired in the shared edit/initial-creation source.
Full batch preflight prevents known later-entry failures from dispatching earlier
setters. Each setter rechecks access/type; readback compares both native type and
value, with no automatic replay after partial effects. Null current values may use
one explicitly declared supported type, but never an invented string fallback.
[Evidence](../../verification/technology-scalar-access-20261017.md): 159 general tests
pass, including five new linked-production scenario groups with synthetic native
objects. Final frozen verification passes 39 managed suites, 1,295 Python tests (one skip), 24 Explorer checks, all MCP workflows and all 22 package stages. A genuine worker SDK
build, native metadata availability and target preservation/recovery remain open;
old native evidence is preserved and the two changed route qualifications are unverified.


### TO-03 parameter-read boundary - 17 October 2026

The shared technology/engineering/detail/evidence reader now excludes sensitive
names before getters, requires unique readable Value metadata, reports exact scalar
types and explicit incomplete/error/display-only states, and bounds filtered scans.
Engineering object traversal records exact paths and bounded completeness. Live
fixture gates require explicit complete readable evidence. [Verification](../../verification/technology-parameter-read-20261017.md)
passes 167 general and 26 Python fixture checks; the full frozen aggregate and all 22 package stages pass.
This is partial TO-03 source work. Exact SDK/service profiles, service-owned children,
associations, supported CPU/version eligibility and full native captures remain open.


### TO-03/04 exact-path mutation selection — 17 October 2026

The source now binds exact paths into existing-object previews and reuses those
paths for fingerprinting, apply and evidence. Legacy names require uniqueness;
contradictions/missing paths and incomplete scans refuse. Creation collisions are
checked twice; deletion verifies selected-path absence without replay. This does
not qualify durable native identity, complete consumer effects or native lifecycle.
[Verification](../../verification/technology-selection-20261017.md) passes 173
general and 51 Python fixture checks; the full frozen aggregate and all 22 package stages pass. Continue
service/child/eligibility capture and the remaining TO-04–12 scope.


### TO-03/06 child inventory and Explorer — 17 October 2026

The documented OutputCams/MeasuringInputs child compositions now share exact
owner-relative identity across inventory, resolution, fingerprints, mutation
selection and Explorer capture. Failed/partial discovery cannot certify selection
or absence. The live viewer's first-child expand control is repaired and tested
through its parent protocol. [Verification](../../verification/technology-children-20261017.md)
passes 179 general, 52 Python fixture and 31 browser checks; the full frozen aggregate and all 22 package stages pass. Native SDK/ownership/alias behavior and child lifecycle/connection/
preservation acceptance remain open. The connection-service source repair is recorded
above; exact endpoint references and TO-03–12/full-queue work remain open.
