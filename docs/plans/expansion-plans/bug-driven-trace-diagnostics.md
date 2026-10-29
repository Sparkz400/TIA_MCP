# Bug-driven FB discovery and native trace diagnostics

**Replan template retention, 17 October 2026:** Explicit machine-instance substitution now retains the original role template by ID through the current catalog; source-specific evidence is still resolved or selected afresh. [Verification](../../verification/replan-template-20261017.md). Frozen targeted checks and all 24 package stages pass. Repeated captures and the full TD-09/10 workflow remain open.

**Report exports, 17 October 2026:** `diagnostic_report_export` now presents retained analysis and comparison reports as exact JSON, Markdown and standalone HTML, with full original evidence and explicit unverified claims. [Verification](../../verification/diagnostic-report-export-20261017.md). Frozen Python/Explorer checks, a full 41-suite managed/MCP recheck and all 24 package stages pass. The initial aggregate stopped at an unreproduced manifest-test failure; its failed receipt is retained. This closes only the report-format increment; native acquisition and the full TD-09 workflow remain open.

**Comparison provenance repair — 17 October 2026:** Different declared measurement programs require explicit comparison admission. Analysis and comparison reports retain acquisition contexts and do not authenticate identities or synchronize clocks. [Evidence](../../verification/trace-acquisition-context-20261017.md). The frozen 41-suite aggregate and all 24 package stages pass. The full native/diagnostic scope below remains open.

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](../release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

Planning baseline: **14 October 2026, Europe/Berlin**. Status: proposed priority expansion; no implementation or native acceptance performed. This responds to [prompt 17](../expansion-prompts/17-bug-driven-trace-diagnostics.md). New tool names, modules and schemas below are proposals, not existing capabilities.

Implementation update, 14 October 2026: the subsequent user instruction authorized implementation. A bounded offline increment now exists; see the [route decision and package status](bug-driven-trace-feasibility.md) and [usage guide](../../guides/trace-diagnostics.md). The planning baseline and full acceptance scope below remain historical/proposed where they exceed that increment. No native acceptance was executed.

Expanded implementation update: [current delivered scope, receipts and remaining gates](../../verification/trace-diagnostics-expanded-offline-20261014.md) supersedes the first increment's scope. Eight tools now include bounded dependency/actual-binding selection, explicit CSV import, comparison, instance revalidation and supplied-readback checking. The user explicitly kept native UI work paused. TD-07/08 and native/release acceptance remain open; this is not complete native trace creation.

Later TD-01 research identified the documented [SIMATIC AX CLI route](bug-driven-trace-simatic-ax-feasibility.md). It supplies PLC jobs through a separate YAML/CLI interface; the inspected 2.8.1 documentation excludes save triggers for TIA-engineered PLCs and does not establish full settings readback or TIA project trace persistence. The earlier negative public API finding applies to the inspected V21 Openness surface, not every Siemens product. The next route package is TD-AX-01, requiring package/schema and independent readback evidence. Native UI remains paused.

## 1. Decision and release boundary

Build a diagnostic workflow that turns a reported symptom into an evidence-backed CPU/FB/call-site/instance selection, a minimal justified signal manifest, a reviewable TracePlan, and analysis of an actual measurement against the original hypotheses. Investigate the native automation route first while designing the offline planner independently.

**Verified route conclusion:** ordinary PLC trace creation, configuration, transfer, recording and retrieval are documented TIA user-interface workflows. This audit did **not** establish a general public V21 Openness trace lifecycle API or a supported external configuration-generation schema. Installed Step7 and Startdrive XML references contain no trace/recording/measurement members. Safety Validation has a documented separate trace configuration API; that does not qualify ordinary PLC traces. Absence from inspected references is a bounded negative finding, not proof that all supported integrations are impossible.

The first useful supported deliverable can rank candidates, resolve a bounded class of instances, explain each signal, generate an exact manual setup specification, import a supported exported measurement, and return a diagnostic report. Manual setup and retrieval are **partial fulfillment** of the requested native-creation workflow. A specification, polling demo, or successful creation call must never be presented as complete native trace creation and acquisition.

This is a **priority planning candidate explicitly requested by the user**. The [1.0 checklist](../../history/1.0-release-checklist-20261013.md), [TODO](../../TODO.md) and [expansion index](../expansion-prompts/README.md) do not establish it as an existing 1.0 commitment. Recommend a separately named diagnostic expansion milestone. Admitting it to 1.0 requires an explicit scope decision and its qualification gates. Existing runtime, preservation, recovery, HMI and release commitments remain intact.

### Audit restrictions and provenance

Only source, retained artifacts, installed public reference XML/XSD, relevant local session metadata and official documentation were inspected. No TIA tool was called; no application was started, attached to, stopped or controlled; no native tests, PLC connections, builds, deployments or production edits occurred. No sub-agents were spawned. Existing native UI pauses remain in force. Only this plan is written.

No `AGENTS.md` was found in the hidden-file-aware workspace search or inspected ancestors (`D:\`, `D:\03_Entwicklung`, workspace root). No Git metadata is available. File hashes identify this audit. Re-check instructions, hashes and ownership before implementation.

### Current ownership and results, rechecked

Read-only `state_5.sqlite` metadata for this workspace on 14 October identifies this planning thread (`01a1386f-775a-7eb1-9699-40f8446f7350`), a concurrently updated **Plan OPC UA engineering support** thread (`01a1386f-a6f5-7e53-b0ee-eb4bf531bb88`), and **Review TIA_MCP 1.0 TODO** (`01a133b2-a917-7622-af95-a41e16dc6050`). This establishes overlap to respect, not permission to control those sessions or edit their files. No other trace implementation owner was established. The requested destination did not exist before exclusive file creation.

| Area | Ownership evidence | Latest result and implication |
| --- | --- | --- |
| Integration/native admission | Coordinator **Review project TODOs for 1.0**, `01a1336b-d1a3-79c1-8e3b-ad9caa1fd60c`; current docs assign shared-file/build/native coordination there | Retain one native TIA queue and a separate exclusive PLCSIM window. Last metadata update is 13 October; current availability is unknown. No trace implementation assignment is made here. |
| Typed | Historical `/root/typed`, Kierkegaard; terminal handoff (`artifacts/p17t/workstream-handoff-02.json`) | 38 distinct authored inputs / 45 native reference blocks reconciled; 28 new rows inactive. Original c03 cleanup failed; separate f02 copy persistence passed. Do not revive finished capture/persistence boxes or call these builder/runtime passes. |
| HMI | Historical `/root/hmi`, Noether; current STATUS/TODO and [remaining work](../hmi-remaining-work.md) | Bilingual inspection/bindings/explorer passed within scope. Clean compile, bridge archive retrieval, production mutation and native visual gates remain open. No resumed UI authorization is inferred. |
| Libraries/I/O | Historical `/root/library_io`, Curie; retained p17n reports | native07 independent audit (`artifacts/p17n/native07-independent-audit.json`) passes configured-module lifecycle; channel writes remain unqualified. **Component07 is terminal**, contrary to in-progress STATUS/TODO wording: result (`artifacts/p17n/component-native-07/result.json`) ended 13 October 06:18:23 UTC with `tia_start: AccessDenied`, `cleanupFailure: detach, owned_process_exit`, no first-checkpoint recovery qualification. Admission file `nativeDispatched:false` is stale relative to the report. Preserve all outcomes. |
| Regression/package | combined-validation-02 (`artifacts/p17r/combined-validation-02.json`) | Frozen `4824de5911a3472bb9e30f8264fff9d5`: 654 Python tests, one skip, 657 managed checks, 21 model/client checks, 24 browser checks; 27 simulator selftests without Siemens initialization. Existing evidence only; hosted CI and installed package unchanged. |
| Runtime | [runtime TODO](../../history/runtime-proof-todo.md), current STATUS/TODO | Latest full corpus remains 48/65 passing cases, 371 assertions, one isolation failure and 16 unattempted. Historical zero-address discovery, card preservation and requested runtime stop gates remain unresolved. Trace tests cannot bypass them. |

A fresh process inventory observed Portal PID 20692, Portal.Server PID 10112 and TiaMcp.Host PIDs 7728/18636/41748. These are point-in-time **unowned process** observations, not proof of available capacity, active session ownership or permission to terminate anything. No process was touched. Old PID observations and slot-release receipts cannot grant a new native window. Before a later native experiment, the coordinator must reconcile cleanup/unknown outcomes and admit an exact bounded run. No redundant VM qualification is proposed; accepted VM/SSH/recovery proof retains its demonstrated scope.

## 2. Current implementation and evidence audit

All source paths in this section exist. Openness partial-class filenames are relative to `src/TiaMcp.Openness.V21/` unless stated otherwise.

| Reusable piece | Exact source / contract | Actual capability and limit |
| --- | --- | --- |
| Block export and raw xref | `src/TiaMcp.Host/TiaTools.cs:51,61`; `OpennessSession.cs:244,264` | `block_read` resolves exact block, refuses know-how protection, calls `PlcBlock.Export(FileInfo, ExportOptions.WithDefaults)`, retains XML/hash; inlines only ≤512 KiB. Export completeness is not semantic understanding. `xref_query` calls `CrossReferenceService.GetCrossReferences(CrossReferenceFilter.AllObjects)`; unavailable service refuses. |
| Inventory/declarations | `src/TiaMcp.Host/EngineeringTools.cs:34,49`; `OpennessSession.Inspection.cs`, `OfflineEvidence.cs` | `software_inspect`, `block_interface`, `type_read`, `tag_table_read` expose bounded scopes, comments, declarations, sparse array starts and instance metadata. Declaration output explicitly has `instanceTargetResolved:false`. No offset inference or byte-layout guarantee. |
| Identity/capability | `OpennessSession.ObjectModel.cs`, `ObjectIdentity.cs`, `OpennessSession.Capabilities.cs`, `CapabilityRules.cs`; `contracts/capability-report.schema.json`; [identity guide](../../reference/object-model.md) | `object_resolve` retains saved project path, PLC, scope/unit, namespace and URI-escaped exact path; no fabricated GUID. Individual tags/members are not resolver kinds. 50,000-object traversal bound cannot prove uniqueness after truncation. Target availability differs from catalog support. |
| Calls/instance hints | `OpennessSession.Analysis.cs:17`, `OfflineEvidence.cs:91` | `call_graph` requires native Call/UC/CC plus directional Uses/UsedBy. InstanceDB→FB prefers exact native type relation; name fallback requires an unnamespaced unique FB, complete index and same scope. Namespaced fallback refuses. Call locations remain; no general nested call-context expansion. |
| Offline reference tracing | `EngineeringTools.cs:70`; `OpennessSession.Analysis.cs:82` | `symbol_trace` matches exact native identity/name/address/path, keeps ambiguous matches, readers/writers, instance references and configured I/O. Explicitly does not infer declaration-to-all-instance runtime propagation. No temporal samples. |
| Analysis coverage | `OpennessSession.Analysis.cs:157,284` | One selected PLC; default 500-item bound, block index `min(20000, max(1000, maxItems*4))`. This route can fall back to per-block xref services; raw xref does not. ANY/pointers, dynamic DB/array selection, interlock/dataflow evaluation and runtime proof remain gaps; observations are nontransactional. |
| Persisted search | `src/TiaMcp.Host/ProjectReviewTools.cs`; `src/TiaMcp.Core/ProjectReviewStore.cs`; [review guide](../../guides/project-reviews.md) | Literal case-insensitive saved metadata/XML token/comment search; excerpt offsets are not source lines. Capture default 25/max 200 blocks; relationship default 5/max 25 roots, ≤500 nodes/1,000 edges each. SCL/STL/LAD/FBD/DB capture, protected/safety/other source unavailable. Metadata recheck does not make content atomic. No-result says nothing about omitted scope. |
| Graphical evidence | `OpennessSession.TypedGraphical.cs`, `TypedGraphicalResolution.cs`, `TypedGraphicalModel.cs`; `contracts/typed-graphical-read.schema.json` | `engineering_read(scope:graphical)` exposes versioned LAD/FBD networks, supported scalar operands, opaque coverage and native XML. Reuse readers/identity rules; diagnostic reads must not invoke mutation builders. |
| HMI/I/O/library context | `OpennessSession.HmiReferences.cs`, `.HmiBindings.cs`, `.Hmi.cs`, `HmiInspectionRules.cs`, `.NetworkInspection.cs`, `.Libraries.cs`; host `HmiTools.cs` | Reuse `hmi_inspect`, `hmi_references`, `hardware_inspect`, available explorer/library relationships with exact owner/connection/version evidence. Configured bindings, alarms or I/O alone do not prove online machine identity or cause. PLC alarm/ProDiag expansion belongs to prompt 11. |
| Polling | `src/TiaMcp.Host/SimulatorTools.cs:58`; `src/TiaMcp.Simulator.V8/SimulatorSession.cs:287` | `sim_test`: 1–64 signals, 1–128 steps, interval 20–5,000 ms (default 100), planned ≤60 s/500 snapshots; actual ≤90 s/500 samples including overhead. Sequential reads, host timestamps, explicitly non-atomic/no scan accuracy. Stimuli are possible: never invoke implicitly for a read-only report. Catalog qualification remains unverified. |
| Controlled application calls | `scripts/block_test_program.py`, `block_test_executor.py`, `block_test_runtime.py`, `fixtures/runtime_catalog.py`; [block testing](../../guides/block-testing.md) | Request/ack counts DUT calls while other scans/timer time continue: `applicationCallControl:true`, `scanSynchronized:false`. IEC internals can be opaque; exposed Q/ET/CV preferred. Test-DB state copying is instrumentation, not unchanged-program native acquisition. |
| Recovery/artifacts | `OpennessSession.Changes.cs`, `LibraryJournalStore.cs`; `src/TiaMcp.Core/BridgeProtocol.cs`, `ArtifactStore.cs`, `ArtifactUploadStore.cs`; host `ArtifactTools.cs` | Reuse preview hash/idempotency/checkpoint/journal/status patterns and bounded hashed transport. Bridge ≤4 MiB request/16 MiB response. Offline apply requires `checkpointSave:true` and never authorizes runtime transfer/arm. |

The current 152-entry catalog marks `block_interface`, `call_graph`, `symbol_trace` partial; raw xref implemented; target availability conditional. Its verified labels refer to named fixtures. `drives.engineering` is not implemented/unverified. Source/contracts/scripts search for proposed trace tools, `ttcfgx`, `ttrecx` and `TraceConfiguration` found no general native trace adapter.

**Critical evidence boundary:** analysis-regression-v03.json (`artifacts/analysis-regression-v03.json`) records `instanceDbResolutionObserved:false`. Catalog `local.analysis.v03` covers five native call sites/four expected pairs, with TON/CTU endpoints explicitly unresolved; InstanceDB fallback and duplicate-member cases are pure-test evidence only. This cannot establish automatic exact-instance discovery. `local.conformance.v03` covers 14 engineering scenarios/73 steps on CPU1511-1PN V4.0 across SCL/STL/LAD/FBD, without runtime vectors. [Review/explorer qualification](../../verification/review-explorer-p16-qualification.md) proves bounded dependency evidence and preservation on a pinned build, not interlock reasoning. [Identity qualification](../../reference/object-model.md) proves selected duplicate namespace/unit cases on V4.1, not every runtime member address.

Missing orchestration: symptom model, explainable ranking, call-context-sensitive nested instance/parameter resolution, conservative language-specific slicing, backend observability, TracePlan validation, native lifecycle, measurement ingestion/lineage/quality, hypothesis evaluation, templates and retention. Existing inspection must not be presented as those missing guarantees.

## 3. Public API and trace feasibility

### Installed reference audit

Root: `C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21`.

| File/scope | Observed public surface | Consequence |
| --- | --- | --- |
| `net48/Siemens.Engineering.Base.xml` | `CrossReferenceService.GetCrossReferences` line 3490; trace hits include StackTrace and HW `OperatingMode.Trace` / `TraceTriggerEdge` enums | Real xref service; enums/exception properties are not recording services. |
| `net48/Siemens.Engineering.Step7.xml` | `PlcBlock.Export`, `InstanceDB.InstanceOfName`, `InterfaceSnapshot.Export`; zero member names matching trace/recording/measurement | Exports/snapshots are useful evidence; no general trace lifecycle established. |
| `net48/Siemens.Engineering.Startdrive.xml` | Zero matching trace/recording/measurement member names | No public drive trace lifecycle established in this reference. Do not repurpose parameter access into an undocumented recorder. |
| `net48/Siemens.Engineering.SafetyValidation.xml` | `SafetyFunction.TraceConfiguration` line 1371, `TraceConfiguration` at 2074, `IsTraced` at 2193 and attributes | Test-specific API. Siemens documents `PretriggerTime`, `RecordingDuration`, `Signals`; not an ordinary PLC schema. [Access][S3], [modification][S4]. |
| All 21 `Schemas/*.xsd` | Language/interface/technology/supervision schemas; no `ttcfg`, `ttrec`, `TraceConfiguration` match | `SCL_v4`, `STL_v5`, `LADFBD_v5`, `Access_v5`, `CompileUnitCommon_v5`, `InterfaceSections_v5`, `Interface.Snapshot`, `Graph_v6` help read exports. None establishes trace generation. Publisher XSD concerns add-in publication. |

No DLL loading, running-object reflection or private project storage was used. Later public self-description is an admitted experiment only; discoverability still needs documented supported semantics and target qualification.

### Trace-family capability matrix

Keep **D** documented behavior, **I** implementation, **Q** qualification and **A** target availability distinct. No native target was selected/probed here: A is unknown for every native family. Record exact CPU/order number/firmware/TIA update/product/license/role before making a row executable.

| Family | Documented prerequisites/limits | Route/formats | I/Q and unresolved target facts |
| --- | --- | --- | --- |
| Ordinary S7-1200 | Classic S7-1200 FW 4.0+, two installed traces, 16 signals; no long-term/project trace in cited table. Do not extrapolate to G2. [Hardware][S5], [quantity][S6] | Native Traces editor; `.ttcfgx` config UI import/export; `.ttrecx` measurement import. [configuration export][S1], [configuration import][S2], [measurement import][S13] | Adapter/Q absent. Model/update, engineering entitlement, online trace rights/protection and available slots unknown. |
| Ordinary S7-1500/derivatives | At least four traces depending on CPU; 16 signals, FW 3.0+ row 64 signals. Sample capacity depends on CPU/types/mix; CPU1516 examples do not apply universally. [S6] | Same documented UI; general public lifecycle API not established | Adapter/Q absent. Proposed initial CPU1511-1PN `6ES7 511-1AL03-0AB0`, FW 4.0 aligns existing fixtures but needs separate native-trace proof. Simulator compatibility is not trace support. |
| Long-term S7-1500 | Family listed; STEP7 V18+ at most two simultaneous long-term traces. Configuration has no finite duration/trigger. [S6], [recording UI][S10] | UI; `.ttcfgx`; `.lttcd` plus companions; V20+ recording `.dat`, external analysis through CSV export. ≥4 GB free disk, 2 GB rollover. [Format][S14], [behavior][S15] | Adapter/Q absent. Exact minimum firmware/license/throughput unknown. Closing editor interrupts recording; do not parse live DAT files. |
| Project/multi-device | Listed S7-1500 derivatives FW 2.8+; Open Controller table says V20.8. Same PROFINET RT/IRT subnet/no routing, transfer connection to all; at least one trigger, at most one immediate trigger; per-device quantities. [S5], [prerequisites][S16] | Project trace editor/global trigger. Project-config exchange extension/schema not established by ordinary `.ttcfgx` docs. Combined ordinary measurements import `.ttrecx`. [S18] | Adapter/Q absent. Device clocks, RT trigger delays and IRT phase need measurement; relative trigger alignment alone is not synchronization. [S17] |
| Long-term project | S7-1500, same PROFINET RT/IRT subnet, links to all participants. [S19] | Native UI, `.dat`/CSV; combined measurement `.lttcbmx`; ≥4 GB disk, 2 GB rollover. [S20], [S18] | Adapter/Q absent. Exact firmware/update, host throughput/buffers and available recording settings separately qualify. |
| SINAMICS drives | Supported Startdrive drives; V90 HSP 0185. V21 Basic notes on-device trace for FW 6.x; V20 notes trace templates. [S5], [V21][S21], [templates][S22] | Native Startdrive UI; public trace API/editable template schema not established. Exact format, task/rate, channels, memory and triggers unknown | `drives.engineering` absent/Q absent. Prompt 10 must bind CU/drive/order/firmware, Basic vs Advanced/TEC/license and exact manual before numeric settings. CPU trace in Drive Controller does not qualify its drive trace. |
| Safety Validation | Separate SafetyFunction test trace; exact target, Assistant license/product, rights and test prerequisites unknown. [S3], [S4] | `SafetyFunction.TraceConfiguration`, `IsTraced`, documented attributes | Adapter/Q absent. Prompt 14 owns it; no loophole for ordinary tracing or safety acceptance. |
| Simulator polling | Advanced V8, authorized owned instance, compatible CPU/program/license; section 2 bounds | `sim_tags`, `sim_read`, explicitly requested `sim_test`; JSON/host timestamps | Implemented; sim_test Q unverified. No scan/atomicity/one-scan detection guarantee; preserve isolation rules. |
| Physical polling/subscription | Exact CPU Web API/OPC UA prerequisites belong to prompts 16/04 | Shared runtime adapter only | Absent/Q absent. Buffering or source timestamps alone do not establish scan synchronization. |

**Observability:** Siemens lists process-image I/Q, bit memory, DBs and I/O; excludes FB InOut-area tags. Listed types include BOOL, BYTE/WORD/DWORD, integers, REAL/LREAL, TIME, DATE/TOD and selected 64-bit/time types; LWORD/LINT/ULINT/LTIME/LTOD/LDT excluded on S7-1200, several large types require symbols. Composite support is device-dependent. This does not establish STRING, whole arrays/UDTs, every optimized member or opaque IEC internals. [Recordable variables][S7]. Trigger types have a separate [table][S11].

**Timing:** sampling occurs at the end of the selected OB after user code; nonperiodic levels have undefined recording time. [Recording levels][S8]. Derive task/reduction/count from the phenomenon and verified limits. [Sampling][S9]. Single-tag level/edge, threshold/range/change and bit-pattern events are documented; edge detection needs at least two cycles after activation. [Events][S12]. Arbitrary multi-tag compound triggers are not established. End-of-OB sampling cannot guarantee visibility of an earlier overwritten transient.

**Effects:** enabled traces add execution time and can overflow a highly utilized execution level. [CPU load][S23]. Configurations can survive power cycles while recorded values are lost; address-changing program edits require retransferring configuration. [Lifetime][S24]. Plan these states and effects explicitly.

Additional supported options worth qualifying separately: ordinary monitoring without trigger, and completed measurements stored on the device memory card. The latter is documented for S7-1200 FW 4.2+/S7-1500 FW 2.0+, excludes S7-1500 R/H and project traces, and offers bounded repetition or overwriting oldest measurements; Siemens lists up to 999 measurements. [Recording options][S10]. Default to stop-at-capacity for owned captures. Card wear, reboot behavior and existing measurements need explicit resource/preservation checks; never enable overwriting as an implicit retention policy.

### Route decision matrix

| Route | Finding | Evidence required before advertising support |
| --- | --- | --- |
| Public native API | No ordinary PLC/drive lifecycle established; Safety is separate | Exact official service/method/version, target prerequisites, independent settings readback, preservation/reopen and acquisition. Negative route decision if none can be cited. |
| Public config schema/import | `.ttcfgx` UI interchange documented; editable schema and Openness import not established | Official permitted fields/format compatibility/import route; unmodified then one-field roundtrip on admitted fixture. No generator beforehand. |
| Template generation | Candidate only, including Startdrive templates | Same supported-field contract, template hash/target lineage and full intended-delta native readback. Editing XML/ZIP alone is not support proof. |
| UI assistance | Human interaction documented; automation/surface/readback unqualified | Later explicit authorization lifting relevant pause, enabled supported surface, exact selection, full readback/preservation/recovery. No coordinate-only happy-path acceptance. |
| Manual specification | Feasible offline fallback | Independently verify all TIA signals/settings and return measurement/provenance. Label manual stages and partial native-creation fulfillment. |
| Polling | Simulator exists with limits; physical future | Reject unsatisfiable timing requirements; report achieved cadence/skew/gaps. Never silently downgrade a one-scan requirement. |
| Buffered/scan routes | Research candidates | Public timing/loss/resource contract and independent waveform qualification. User-program buffers are separate instrumentation/code changes. |

Additional useful research: documented Web API `PlcProgram.DownloadProfilingData` returns a ticket for runtime profiling and requires `read_value`. Its format describes block start/end, process-image/direct-I/O events, error IDs and load/cycle time. [Profiling][S25]. Route it through prompt 16; it does not record arbitrary FB-member values or identify instances merely from block numbers. Firmware, setup, retention/loss and target availability require independent qualification. A CPU web measurement viewer/export also exists [S26]; it does not establish a trace-creation JSON-RPC API.

### Workflow-stage capability matrix

| Stage | Exact API/service/schema or interaction | Current state and missing guarantee |
| --- | --- | --- |
|1 Interpret| Saved `project_snapshot_search`; host orchestration | Search reusable; symptom/observation/hypothesis model missing. Ask only materially missing target/timing facts after context search. |
|2 Rank| `PlcBlock.Export`, xref, public SCL/STL/LADFBD schemas, `engineering_read(scope:graphical)`, HMI/I/O/library evidence | Inspection qualified in bounded fixtures; explainable ranking/language semantic profiles missing. Protected/opaque coverage explicit. |
|3 Resolve instance| CrossReferenceService, InstanceDB metadata, InstanceType/TypeInstance and parsed call declarations; object identities | Exact object lookup exists, general nested instance/call-context resolution unqualified. Ambiguity blocks apply. |
|4 Select signals| Interface normalization, Read/Write/RW xref, supported expressions/networks | References exist; hypothesis slicing/competing-writer closure missing. |
|5 Observability| Native signal selector/manual verification, future qualified backend; simulator tags/typed reads | Product rules [S7], not target evidence. Member path/type/access proof and justified substitutes required. |
|6 Prepare| Proposed TracePlan schema/pure planner, existing immutable review/artifact conventions | Missing; critical unknown settings produce non-ready/manual plan. |
|7a Config/readback| Documented native editor; future qualified route; ordinary route excludes Safety API | Generic adapter/Q absent; integrate prepared changes and independent native settings comparison. |
|7b Transfer/arm/record/stop| Documented UI [trace manual][S0]; public adapter not established | Separate authenticated runtime transitions; offline apply never transfers/arms. |
|7c Retrieve/delete| Native save/export/import/device-delete UI | Adapter absent; preserve measurement before optional owned deletion; full resources refuses, unknown outcome never blindly retries. |
|8 Analyze| Supported CSV export/native interchange and artifact transport | Transport exists; parsers/time/quality/lineage/hypothesis analysis missing. Opaque proprietary files retained rather than guessed. |

## 4. Proposed diagnostic workflow and contracts

### Interpretation, discovery and exact instances

Create a DiagnosticCase with verbatim report, expected/actual behavior, affected equipment, mode, reproducibility, duration/frequency, changes and known PLC/FB/tag/alarm names. Distinguish user observations, measured evidence and hypotheses. Each hypothesis states predicted observations, contradictions and information needed to discriminate it. Use available project context first; ask only questions that materially change target or signal choice.

Rank exact equipment/HMI binding/alarm relationships, then instance/call/type evidence, supported expression/dependency matches, comments and names. Return reason components and contrary evidence; scores are not identity proof. A name-only match cannot authorize selection. Keep equally plausible CPUs/instances and ask the specific discriminator. Protected source is an explicit missing edge, never a reason to access private internals.

Language coverage is a matrix: SCL token/control-flow subsets; STL accumulator/instruction/jump semantics only for reviewed instructions; LAD/FBD connectivity, EN/ENO, call pins and qualified instructions; embedded language fragments handled separately. GRAPH/CFC/safety/unknown instructions may supply metadata/native references/export evidence where exposed but remain opaque until separately qualified. Finding a comment does not prove dataflow. A public XSD is not a semantic analyzer.

Resolve **call occurrences**, not only block types. Call-site ID combines caller identity, source hash, compile-unit/network native ID or parsed span, callee identity and occurrence. Traverse from the relevant OB through parameter bindings/static multi-instance declarations to root instance DB and nested member path. Retain library GUID/version when available alongside source/interface hashes. Distinguish declaration, storage and actual parameter binding.

Repeated calls keep separate contexts with a shared `storageIdentity`. A shared instance called from two tasks is not two independent machines. IN/OUT bindings keep direction and copy timing; IN_OUT resolves caller storage and aliases. Dynamic index/ANY/pointer/reference targets stay unresolved absent separately supported exact evidence. `#Enable` alone is never a backend address. Use absolute addresses only when explicitly supplied and validated by a supported route; no optimized-DB offset guessing.

### Minimum sufficient signals and observability

Backward-slice output/feedback dependencies and include a bounded downstream effect. Every signal closes a named question. Roles may include command, produced output, feedback, permissive, state/step, fault, timer/counter public outputs, I/O and competing writer guards. External/HMI/communication writers remain coverage gaps if xref cannot enumerate them. Static write access proves a possible writer, not its execution.

Associate signal coverage with hypotheses; select the smallest distinguishing set inside verified channel/memory budgets. Deduplicate aliases by storage while retaining roles. Display what each removed signal makes indistinguishable. Do not silently drop mandatory roles. Offer repeated captures with common anchors when needed; different captures cannot prove simultaneity.

| Signal class | Required validation/substitute |
| --- | --- |
| FB IN/OUT/static | Exact instance storage/type/native selector; compare actual input with FB input when copy timing matters. |
| IN_OUT/aliases | FB InOut recording unsupported [S7]; resolve exact caller storage and validate it. Record alias/writer/copy-timing differences. |
| Nested multi-instance | Prove each rootDB→member→type→call binding and selector acceptance; matching short names or concatenation is insufficient. |
| Arrays/structures/UDTs | Bounded fixed scalar leaves with declared indexes/types; dynamic selection unresolved; whole-object support separately qualified. |
| Optimized DB | Validated symbolic path/type, no invented byte address; revalidate after layout/type/library changes. |
| TEMP/FC local/indirect | No stable persistent address assumed. Existing persistent downstream substitute must state lost coverage; otherwise unavailable. |
| Timer/counter | Existing exposed Q/ET/CV preferred; opaque IEC memory remains opaque. Adding copies/latches/counters is a distinct code change. |
| Protected/access-restricted | Native visibility/rights respected; external substitute documented; no generic attribute change to expose data. |
| I/O/feedback | Distinguish process-image vs direct/peripheral values, module/channel/task. Configuration does not prove wiring or motion. |

Instrumentation is never implicit: new source revision, execution-time/storage effects, change preview, compile/preservation/runtime qualification and comparison policy are separate from tracing an unchanged application.

### Architecture and MCP surface

Pure planning/resolution belongs outside the Siemens worker. Proposed `src/TiaMcp.Core/Diagnostics/` contains case/manifest/plan validation, ranking, evidence graph and artifact storage. Reusable XML readers must remain vendor-free and compatible with their consumers. `OfflineEvidence.cs` currently uses namespace `TiaMcp.Analysis` but physically lives in the Openness project; there is no existing Analysis project to extend.

Proposed host `DiagnosticTools.cs`/`TraceTools.cs` orchestrate existing reads and bounded artifacts. Create proposed `OpennessSession.Trace.cs` **only if TD-01 establishes a supported Openness route**. UI assistance is a distinct qualified integration, never private project-file editing. Runtime uses shared ownership/transport from prompts 09/16 or the existing simulator. Saved-case analysis must not initialize Siemens.

All names below are proposals. Prefer existing change conventions over another generic mutation manager.

| Proposed contract | Input | Output and effects |
| --- | --- | --- |
| `diagnostic_plan` | Report, optional reviewIds/exact hints/evidence, observation horizon and budgets | Immutable case/plan ID/hash, ranked candidates/reasons, evidence, hypotheses, signals, material questions and gaps; no online connection. |
| `trace_capabilities` | Exact identity/profile/family/backend; explicit target probe only in later authorized session | Per-operation documented/implemented/available/qualified status, types/trigger/tasks/limits, evidence/time/version and unknowns. Static mode never attaches/scans. |
| `trace_prepare` | Plan ID/hash, explicit backend/family and requested settings | Effective validated plan, exact manual artifact or offline changeId/previewHash; proposed vs observed limits distinguished. No transfer/arm. |
| Existing change workflow extension | Proposed `trace_configuration_create/update/delete` kinds after route qualification | Reuse `change_prepare/apply/status/restore`, exact-once idempotency, checkpoint/journal, independent readback/preservation. `trace_apply`, if exposed, is only a thin alias. |
| `trace_capture` | Distinct action `transfer`, `arm`, `stop`, `retrieve`, `deleteOwnedDeviceTrace`; exact config/readback hash, session, precondition/job, idempotency key and bounded capture policy | Durable action/job receipt. No implicit transfer+arm. Retrieval retry only if proven read-only and stable. Deletion requires exact ownership and explicit selection. |
| `trace_status` | Job/config/measurement ID | Persisted state separate from last native observation, connection/uncertainty and permitted reconciliation. Can read journal without native startup. |
| `trace_read` | Measurement/range/signal IDs, or staged artifact/hash + import profile | Typed samples/units/timebase/quality/gaps, lineage and bounded transport. Ingest writes host artifacts only; unknown proprietary format stays opaque. |
| `trace_analyze` | Case/plan/measurement IDs/hashes, optional baseline/fix pair | Offline observations, scoped hypothesis verdicts with sample windows, uncertainty and next capture. |

Annotations must describe real effects: planning is read-only relative to PLC/TIA; transfer/arm/stop/device deletion mutate state. Request cancellation never proves recording stopped. Host budget defaults may be proposed; device sample rates/limits may not be invented.

### Proposed schemas and invariants

Public requests use version IDs, strict enums, bounded depth/size and `additionalProperties:false`. Preserve unknown native fields in raw hashed evidence; do not discard them or infer support. Share target/member identities with prompts 02/09/16.

| Schema | Required fields/semantics |
| --- | --- |
| `trace-plan.v1` | Plan ID/hash/case/time; symptom/observations/hypotheses/discriminators; saved project path/copy identity/session generation; engineering and online CPU identity; unit/scope/namespace/block/type/library identity; source/export/interface/binding/task hash manifest; exact call chain/rootDB/member/storage identity; signals/reasons; alternatives/coverage/questions; backend/family/mode; requested and effective trigger/task/reduction/period/count/pre/post/duration; observed limits/resource inventory; predicted result, freshness/ownership/retention/side effects. Critical null settings carry reason and make plan non-ready. |
| `diagnostic-signal-manifest.v1` | Signal ID/roles/hypotheses; declaration/type/section; actual binding chain; exact native symbolic selector and optional proven absolute address; CPU/scope/rootDB/member/index/storage/call occurrences; units/scaling provenance; xref rows/XML hash+location/parser version; writer coverage; evidence class `nativeConfirmed`, `parsedSupported`, `hypothesis`, `unresolved`; backend observability/reason/time/substitute and coverage loss. |
| `acquisition-quality.v1` | Mode `native_ob_sampled`, `native_long_term`, `native_project`, `drive_native`, `host_polling`, `imported_unknown`, `instrumented_buffer`; task/phase/reduction, device/host clock domains, trigger origin/time, sample index, requested vs achieved cadence, per-signal observation interval/skew, timestamp resolution vs accuracy/sync bounds, gaps/loss/overrun or unknown, partial windows/load/reconnect; atomicity and scan synchronization with evidence. Legacy polling: false synchronization, non-atomic; unavailable bounds stay unknown. |
| `trace-measurement.v1` | Immutable artifact/hash/size/media type; parent plan/config/readback/job/target/source hashes; raw file/companions, format/version/locale/importer; timestamps/units/timebase/quality; program match verified/mismatch/unknown with evidence. Filename is not program identity. Retain original timestamps/units beside normalization. |
| `trace-job.v1` | Owner principal/token/session/native identity; action/idempotency/preconditions; intent persisted before dispatch; config/device/measurement IDs; requested vs observed state/time; cancellation request/journal/reconciliation; outcome not_started/completed/failed/partial/unknown. Name prefix is not ownership. |
| `diagnostic-report.v1` | Original hypotheses; confirmed/contradicted/unresolved verdicts with scope/sample/code evidence; uncertainty, correlation vs cause, reproduction/coverage, alternatives and next capture; source/fix comparison/redaction/retention lineage. `faultNotObserved` never means fixed. |

Freshness invalidates executable plans after project copy/move, rename, source/interface/UDT/layout/call/binding/namespace/library change, CPU/firmware change, online program mismatch, task/trace settings/resources change, uncertain reconnect or capability-profile change. Re-resolve affected closure and compare content hashes before config apply and transfer/arm. Metadata-only snapshot equality cannot certify code. Retain old plans historically. Prompt 09 online comparison/fingerprint evidence is required for verified program association; manual attestations remain lower assurance, and strict capture refuses unknown correspondence.

Reuse `{error:{code, message, details}}`. Proposed details: stage, exact target, plan/job, mutationAttempted, outcomeCertainty, retrySafe, evidence, alternatives, nextAction. Reuse `AmbiguousTarget`, `TraversalLimit`, `ProtectedObject`, `UnsupportedCapability`, `PreviewMismatch` where appropriate. New proposed codes: `InstanceResolutionIncomplete`, `SignalUnobservable`, `ProgramRevisionMismatch`, `TracePlanStale`, `AcquisitionRequirementUnsatisfied`, `TraceResourcesExhausted`, `TraceReadbackMismatch`, `TraceOwnershipConflict`, `TraceOutcomeUnknown`, `MeasurementFormatUnsupported`, `MeasurementLineageMismatch`, `MeasurementIncomplete`. Required-stage failure must not be buried in success.

### Lifecycle and preservation

Separate `planned → prepared → configuredOffline → configurationReadbackVerified` from device `transferred → armed → recording → stopped/completed → retrieved → analyzed`. Any state may become stale/failed/unknown. Save/reopen is not transfer; arming is not trigger; trigger is not complete prehistory; retrieval is not reproduction.

Before mutation record accessible trace/measurement identities, hashes, status and ownership. Allocate a new owned trace without replacing user content. Full slots/memory return resources and a reviewed reduction/manual-selection proposal, never automatic deletion. Same-name user content is a conflict. Updates require exact native identity and old hash.

Readback independently enumerates actual signals/types/addresses where available, task/reduction, trigger and windows through separate native read/re-export/reopen. Comparing outgoing request to itself is not verification. Device-installed readback is separate from offline readback. Unreadable required settings remain unverified and fail strict acceptance.

Timeout after dispatch yields unknown until a fresh authorized session reconciles exact native state and journal; never replay create/transfer/arm/stop/delete blindly. Offline archive restore does not roll back a running device trace. Recovery may require explicit stop/retrieve/owned cleanup, preserving evidence first. Use supported cooperative boundaries and demonstrate actual cancellation semantics. No other-session or blanket Siemens termination. Long-term jobs need disk/connection supervision and gap reporting.

## 5. Measurement processing, templates and shared work

Prefer supported CSV export for numeric analysis while retaining native `.ttrecx`, `.lttcd` with companions, or `.lttcbmx`. An extension is not a parser specification. Initially accept only independently characterized CSV dialects from exact profiles and existing simulator JSON. Verify delimiter/decimal locale, headers, symbol mapping, signedness/64-bit precision, booleans, NaN/infinity, timestamps/units/rollover. Siemens long-term CSV represents TIME/LTIME as seconds without units; never silently interpret milliseconds. Do not assume native DAT parsing; use supported CSV export. [CSV behavior][S15].

Keep trigger-relative, device and host clocks distinct. Align by known anchors/sync evidence, never file creation time. Gaps remain gaps; no interpolated verdicts. Visualization downsampling must preserve extrema/events and must not replace raw assertion data. A transition occupies an uncertainty interval between samples unless stronger timing is proven. Changes inside one interval have unknown order. Alarms/profiling carry independent clocks/latency.

| Template | Core discrimination / settings | Limit or next expansion |
| --- | --- | --- |
| Intermittent stop | Enable, internal/final output, feedback, permissive, step/fault; falling output with prehistory | Add relevant exposed timer Q/ET and competing writer guard. HMI enable is not measured PLC enable. |
| Missed edge | Source, receiver enable/call context, exposed edge result, destination state | Every relevant task occurrence; polling cannot prove one-scan capture; intracycle loss may need separate instrumentation. |
| Timer behavior | Input/preset/exposed ET/Q/reset/cancel/task/state | Enough history for start/cancel/restart; IEC internals may remain opaque. |
| Interlocks | Aggregate permissive and hypothesis-specific contributors, output/fault | Reduce using existing aggregate state; do not add an aggregate variable implicitly. |
| Sequence stall | Step, transition request/guard, command/feedback/timeout | Supported step/timeout trigger; long dwell may require repeats/long-term. |
| Competing writers | Produced/final command, observable writer predicates, task context | End-of-OB sample does not reveal every write order; static evidence plus qualified profiling/instrumentation when needed. |
| Analog threshold noise | Raw/scaled value, threshold/hysteresis, comparator/output/quality | Verified bandwidth informs sampling; supported range triggers only; sparse polling cannot disprove noise. |
| Communication timeout | Request/ack/sequence, last-good age, timeout/fault/connection | Separate partner clocks and data age; distinguish acquisition disconnect from application timeout. |
| Additional restart/load | Existing startup/retained-state markers and execution/cycle evidence | Optional prompt 16 profiling; no implicit restart or added markers. |

Support explicit maximum count/duration/storage for repeated captures; before/after-fix comparison with matched stimuli/load and separate source hashes; exportable Markdown/HTML/JSON reports; machine-instance substitution by re-resolving call/type/binding/capability evidence, never raw string replacement. Different captures cannot establish simultaneity.

Proposed host policy defaults (not device limits): 10 completed captures per job,100 per case,2 GiB per case and 30-day eligible retention, with reviewed overrides/deployment caps. Long-term native recording also needs the documented ≥4 GB free space and its separately chosen quota. At capacity, stop admitting captures. Automatic deletion is opt-in and limited to expired/unpinned host-owned derivatives; preserve raw failed/unknown evidence and referenced lineage until explicit disposition. Never delete user/native/CPU measurements for host retention. Keep raw project data local and record export redactions.

| Related prompt | Shared contract | Ownership boundary |
| --- | --- | --- |
| [02 watch tables](../expansion-prompts/02-watch-and-force-table-engineering.md) | Target/signal manifest, roles/reasons/units | Watch-table engineering consumes same resolver; live monitoring/forcing is separate. |
| [06 Test Suite](../expansion-prompts/06-native-test-suite.md) | Fixture/source/stimulus/expected results, job/measurement quality | Owns test execution; share admission/cancellation without double-running runtime. Test pass is not trace pass. |
| [09 online comparison](../expansion-prompts/09-online-compare-snapshots-upload.md) | CPU/session/program fingerprint and freshness | Owns online correspondence/DB snapshots; snapshots are not waveforms. |
| [10 Startdrive](../expansion-prompts/10-startdrive.md) | Drive/CU/telegram identity, parameter units, family capability | Owns drive-specific product/API/license/firmware qualification; no PLC support extrapolation. |
| [11 alarms](../expansion-prompts/11-plc-alarms-prodiag.md) | Alarm→supervision→member→HMI evidence and events | Owns native PLC alarm semantics; offline definitions are not runtime fault state. |
| [14 safety](../expansion-prompts/14-safety-validation-and-fio.md) | SafetyFunction/test/signature/trace artifacts | Owns Safety Validation; no tool result equals functional safety approval. |
| [16 physical runtime](../expansion-prompts/16-physical-plc-runtime.md) | Sessions/exact symbols/timestamps/quality/gaps/streams | Owns WebAPI/OPCUA transport, authentication/certificates, subscriptions/polling/profiling. No competing transport. |
| 04 OPC UA, concurrently planned | Manifest and declaration→NodeId mapping with revisions | Owns server/interface engineering; coordinate changes through current root, do not edit another plan unsolicited. |

## 6. Worked conveyor example and acceptance oracle

**All symbols, paths, logic and measurements here are hypothetical fixture design, not discovered project data or executed evidence.** TD-02 must bind these roles to actual authored/native fixture paths and retain both authored expectations and native exports. No placeholder hash/address makes this executable.

Report: “Conveyor 2 occasionally stops although its enable command remains true.” Expected: with command/permissives true, motor output remains on and feedback follows. User observation: enable display remained true; actual internal enable and event duration are initially unknown. Use equipment/HMI/PLC context before questions; ask if a materially relevant target/mode/timing fact remains missing.

Hypotheses:

- H1: actual enable drops briefly, missed by the HMI display.
- H2: an internal permissive/timer condition removes the command.
- H3: a separate writer removes final output after the FB produces its command.
- H4: command/output stay true but feedback drops or arrives late.

### Candidate and instance evidence

Fixture design: `TraceDiagFixture.ap21`, PLC `BenchCPU`, standard software scope/empty namespace, `Main` cyclic OB. Root `DB_LineA` is an instance of `FB_Line`; static children `Conv1` and `Conv2` are both `FB_Conveyor`. Main calls DB_LineA and then `FC_OutputOverride`. FB_Line has distinct child call occurrences/bindings. `DB_Plant.Conv2.EnableRequest`, `.Feedback`, `.Permit`, `.OverrideStop` are persistent globals; `Q_Conv2` is the final output tag. DB numbers and absolute I/O remain unresolved until authored/native mapping agrees.

| Candidate | Evidence | Ranking/decision |
| --- | --- | --- |
| DB_LineA.Conv2 → FB_Conveyor | “Conveyor2” HMI/equipment binding to DB_Plant.Conv2; second call uses those actuals and maps output to Q_Conv2 | First, but selected only after complete chain verification. |
| FC_OutputOverride | Native/static writer of Q_Conv2 called after line logic | Co-relevant; correct FB output cannot exclude later override. |
| DB_LineA.Conv1 → same FB | Identical type/code, distinct call/storage/Conv1 bindings | Control candidate, excluded as the selected machine by evidence. |
| Another CPU/unit's Conveyor | Short-name/comment match without selected equipment connection | Unresolved alternative until exact ownership distinguishes it; never first-match. |

Illustrative canonical prefix `P=/projects/TraceDiagFixture/devices/BenchCPU/items/BenchCPU/plc` must be replaced with the actual returned `plcPath`. Expected block identities use `P/blocks/Main`, `/FB_Line`, `/DB_LineA`, `/FB_Conveyor`, `/FC_OutputOverride` under that same `blocks` prefix. Oracle records actual native paths, namespace, numbers and source hashes before testing the resolver; expected answers cannot come from resolver output.

Expected context: `Main:line-call → DB_LineA/FB_Line → FB_Line:conv2-call → DB_LineA.Conv2/FB_Conveyor`. Native export/source locations independently match authored call order. Conv1 has same callee, different storage. A negative variant calls the same child twice/from another task: return separate contexts sharing storage and explicit inability to distinguish them from a single final-state trace.

### Hypothetical logic and signal manifest

FB_Conveyor exposes `RunCmd`, `Step`, `Fault`, `TimerElapsed:Time`, `TimerDone:Bool` and persistent `Enable`. A native timer covers missing feedback/permissive behavior; its public ET/Q are already exposed by this fixture. FB_Line maps Conv2 RunCmd to final output. FC_OutputOverride, later in Main, clears Q_Conv2 when OverrideStop is true. IN_OUT aliasing is a separate fixture variant. Independently specify timer preset/guards/step/fault semantics before running analysis.

| Signal/role | Hypothetical native selector | Type | Inclusion evidence and reason |
| --- | --- | --- | --- |
| S01 requested enable | `"DB_Plant".Conv2.EnableRequest` | Bool | Call actual/HMI binding; tests H1 at source. |
| S02 instance enable | `"DB_LineA".Conv2.Enable` | Bool | Exact input storage; tests copying/repeated-call differences. Dropping it loses that distinction. |
| S03 internal command | `"DB_LineA".Conv2.RunCmd` | Bool | Assignment/backward slice; distinguishes H2 from later override. |
| S04 final output | `"Q_Conv2"` | Bool | Line assignment, second writer/xref, configured I/O; trigger and H3 discriminator. |
| S05 feedback | `"DB_Plant".Conv2.Feedback` | Bool | Actual feedback binding; H4 and timer input. |
| S06 interlock | `"DB_Plant".Conv2.Permit` | Bool | Permissive call actual/output guard; H2. |
| S07 step | `"DB_LineA".Conv2.Step` | UInt | State assignments/transitions; intentional stop/stall. |
| S08 timer elapsed | `"DB_LineA".Conv2.TimerElapsed` | Time | Existing public ET output; timeout buildup/reset without IEC offsets. |
| S09 timer done | `"DB_LineA".Conv2.TimerDone` | Bool | Existing Q output contributing to H2. |
| S10 fault | `"DB_LineA".Conv2.Fault` | Bool | Fault assignments/output guard; fault onset. |
| S11 competing writer | `"DB_Plant".Conv2.OverrideStop` | Bool | FC_OutputOverride predicate and call order; H3. |

Eleven channels fit the documented family-level 16-channel envelope, but target memory, eligibility and free slots remain unverified. Unsupported Enable/timer members require exact substitutes/gaps and revised hypothesis coverage. If timer ET is opaque/unexposed, remove timer-duration verdicts or propose separate instrumentation. Every selected path/type requires native validation; no row is validated by this plan.

### Trigger, sampling and independent measurements

Propose ordinary trace on the **actual Main OB executing both writers**, every invocation/reduction 1, supported falling edge of S04. Confirm S04 true and sufficient prehistory before injection; include the two-cycle edge initialization requirement. This captures internal stops and later overrides. `S01 AND NOT S04` is a post-capture predicate unless compound-trigger support is independently proven.

Derive `Tpre >= max(relevant timer preset, required command history)` and `Tpost >= feedback response bound + fault-latch observation horizon`. For an independently configured **hypothetical periodic 10 ms task**, with 500 ms relevant timer/feedback horizon, 1 s before/1 s after is a candidate: nominal 100 pretrigger samples/about 200 intervals, with inclusive trigger/count semantics verified in native readback. This is not a real-project default. Nonperiodic OB1 uses sample counts/actual timing, not an invented 10 ms interval. If memory/load cannot satisfy requirements, reduce justified signals/history or refuse; do not reduce sampling silently for one-cycle faults.

End-of-OB sampling can show S03 true/S04 false after override. A bit set and reset earlier in the same OB may be invisible. Qualify that negative case.100 ms host polling cannot guarantee a one-scan pulse; its miss is a backend limitation, not a successful fault test.

Four independently assessed acceptance results:

1. **Selection:** authored equipment/HMI/call map selects exact Conv2 CPU/scope/FB/rootDB/member/call context, excludes Conv1. Compare identities, not screenshots/names alone.
2. **Signals:** pre-authored eleven-row type/actual-binding oracle versus native interface/export and resolver; separate IN_OUT/alias variant; preserve alternatives; no unresolved `#local` backend address.
3. **Settings:** independent native readback after creation and fresh reopen agrees on signals/order/types/task/reduction/trigger/count/window semantics/resources; installed-device readback before arm; all peer traces/measurements preserved.
4. **Captured event:** independently fixed stimulus schedule injects OverrideStop=true for three complete selected task intervals after prefill while command/permissive/feedback stay true. Expected S01/S02/S03 true, S11 true and S04 false during injection, running step and timer-not-done/fault false as authored. Restore override and expect S04 true. Predeclare timing tolerance from task/sync uncertainty, do not fit it to results. Also test one-task-cycle injection separately and a no-fault control.

Event truth comes from a separate authored schedule and independently verified persistent event/cycle log in the controlled fixture, or independently timed bench stimulus. Added logging is fixture instrumentation, hash-bound/load-qualified. The diagnostic analyzer's output cannot be its own oracle. Existing request/ack harness can drive stimuli but cannot qualify scan-synchronous acquisition.

### Illustrative diagnostic report, not an executed result

Assume accepted samples show S11 rising/S04 falling at relative sample 0, S01/S02/S03/S06 true through available history, running S07 and false S09/S10; at sample 3 S11 falls/S04 rises.

- **H3 confirmed for the injected fixture condition:** internal/final divergence and writer predicate match independently known post-FB override. Static order plus controlled injection supports that scoped causal conclusion.
- **H1/H2 contradicted at observed sample instants within coverage:** those command/interlock/timer/fault changes do not explain this captured stop. Shorter hidden transitions are not disproved.
- **H4 unresolved as a real-machine cause:** held fixture feedback is not evidence about drive/mechanical behavior; real feedback lag can be consequence or cause.
- Attach exact plan/config/source/measurement hashes, validated symbols, sample phase/gaps and timing intervals. Same-sample changes alone do not prove order.
- Next capture: same verified instance on a naturally reproduced fault; add a justified drive/feedback-quality signal if H4 remains plausible. No fault reproduced means insufficient evidence, never fixed.

## 7. Dependency-ordered work packages

Effort estimates are person-days for one implementer familiar with the bridge, including focused review/offline verification, excluding native-queue waits, paused UI, Siemens response and target/license procurement. Assign current owners before work; historical names are not new assignments. Coordinator owns shared Program/project/catalog/schema/status/change-dispatch edits. No delegation or native scheduling is authorized by this plan.

| ID/dependency | Concrete files/modules (new files proposed) | Inputs → outputs; completion criteria | Effort/conflicts/risks |
| --- | --- | --- | --- |
| **TD-01 Route decision + offline seam**, first | This plan; proposed plan annex `bug-driven-trace-feasibility.md`; installed public references/official operation index | Per-family API/format/target/license decision; exact methods or bounded negative result, experiments and shared plan/manifest seam. Exit with route-specific go/no-go; offline design proceeds regardless. No speculative generic adapter. | 2–4 read-only/design days; +1–3 native feasibility days only after separate authorization/admission. Owner unassigned; coordinator/10/14/16 overlaps; vendor uncertainty critical. |
| **TD-02 Case/identity/fixture contract**, TD-01 design seam | Proposed `contracts/diagnostic-case.v1.schema.json`, `diagnostic-signal-manifest.v1.schema.json`, `trace-plan.v1.schema.json`; `fixtures/trace_diagnostics/manifest.json`, authored source, `expected-mappings.json`; reuse ObjectIdentity | Target/source/call/storage/evidence types and conveyor roles → independent mappings/signals; ambiguity/coverage/freshness specified. Native actual mapping is a later gate, not fabricated. | 3–5 days; coordinate 02/09/16/04 and typed identity. |
| **TD-03 Ranking**, TD-02 | Proposed `src/TiaMcp.Core/Diagnostics/DiagnosticPlanner.cs`, `DiagnosticEvidenceGraph.cs`; host `DiagnosticTools.cs`; reuse ProjectReviewStore/HMI/inventory | Report+saved evidence → explainable candidates/material questions; independently expected same-name/multi-CPU/protected/omitted-language cases; no native initialization for saved inputs. | 4–7 days; existing HMI/library files require coordinated edits; coverage/false confidence risk. |
| **TD-04 Instances**, TD-03 | Proposed Diagnostics `InstanceResolver.cs`, `CallBindingReader.cs`; existing OfflineEvidence/typed readers; proposed `tests/TiaMcp.Tests/DiagnosticInstanceTests.cs` | Native relations+supported XML → exact CPU/unit/namespace/call/rootDB/nested/actual-binding chains. Independent two-conveyor/repeated/shared/duplicate/array/ambiguous tests; dynamic access unresolved. | 7–12 days; typed/library overlap; native declaration→storage evidence critical. |
| **TD-05 Signals/observability**, TD-04 | Proposed Diagnostics `SignalSelector.cs`, `ObservabilityRules.cs`, `TracePlanValidator.cs`; `contracts/trace-capabilities.v1.schema.json` | Hypotheses+resolution+capabilities → smallest sufficient manifest/settings/gaps. Refuse TEMP/IN_OUT/indirect/type/protection/timing mismatch; language-specific opaque coverage. | 6–10 days initial; broad language expansion TD-10; 02/runtime types overlap. |
| **TD-06 Manual vertical path**, TD-05 | Proposed Diagnostics `TraceSpecificationWriter.cs`, `TracePlanStore.cs`; host `TraceTools.cs`; `docs/guides/trace-diagnostics.md` | Validated plan → exact TIA setup/readback instructions/hash/capture spec; required human stages explicit; offline tests complete. Native manual use qualifies in TD-09. | 3–5 days; useful if generic API blocked; must label partial native fulfillment. |
| **TD-07 Native configuration**, TD-01 positive route + TD-05 | Proposed `OpennessSession.Trace.cs` only with public route; existing Changes dispatcher/catalog via owner; otherwise separately scoped UI integration | Prepared request → one owned offline trace, independent readback/preservation/reopen/recovery. Route-specific qualification. No route → blocked/not implemented, no fake change kinds. | 5–10 days documented API, 8–15 separately authorized UI, conditional feasibility; shared changes/UI pause critical. |
| **TD-08 Acquisition jobs**, TD-05, TD-07 for automation | Proposed Diagnostics `TraceJobStore.cs`, `TraceBackend.cs`; `contracts/trace-job.v1.schema.json`, `acquisition-quality.v1.schema.json`; prompt 16 transport/existing simulator | Config+target → separate transfer/arm/stop/retrieve/delete receipts; durable unknown reconciliation, resource/retention/ownership guards. Manual route stores attestations without pretending jobs executed. | 7–12 days for the initial family; transport implementation excluded; coordinator/runtime owner required. |
| **TD-09 Import/analyze/qualify slice**, TD-06; automated branch needs TD-07/08 | Proposed Diagnostics `MeasurementImporter.cs`, `TraceAnalyzer.cs`, `DiagnosticReportWriter.cs`; `contracts/trace-measurement.v1.schema.json`, `diagnostic-report.v1.schema.json`; `scripts/qualify_trace_diagnostics.py`, `audit_trace_diagnostics.py` | Exports → preserved units/time/quality/verdict; independent four oracles, negatives, existing trace preservation/interruption. Separate reasoning/config/acquisition flags. Import work can precede native branch. | 6–10 implementation +3–6 admitted qualification days; real fault capture/target critical. |
| **TD-10 Full supported expansion**, TD-09 | Proposed `fixtures/trace_diagnostics/templates/`, language/parser/profile modules and family evidence | Eight templates, qualified language coverage, instance substitution/repeats/comparisons, long-term/project/drive/safety through separate domain routes; each family own D/I/A/Q rows. | 10–20 general days +4–10 per supportable family +5–10 major language semantics; contingent APIs/licenses. |
| **TD-11 Release acceptance**, applicable prior packages | Existing catalog/docs/package/CI via coordinator; proposed trace evidence/qualification guide | Frozen support+hash matrix; scoped offline/native/runtime/preservation/recovery/compatibility/scale; package resources/error contracts and offline artifact-only use; existing 1.0 obligations preserved. | 3–5 days plus external gates; no blanket VM rerun. |

Critical paths: public native route, exact nested storage, signal eligibility, online program match, independent readback, admitted acquisition/interruption and product/license/UI availability. Offline planner/manual/import path remains useful without resolving every native branch but retains partial native status.

### Unknowns and smallest later experiments

| ID | Unknown | Minimal settling action/result |
| --- | --- | --- |
| U01 | General trace lifecycle API | TD-01 inspect complete official operation/service index for exact trace owner; seek authoritative Siemens clarification if unresolved (sending requires separate authorization). Later admitted read-only public self-description shows exposure only; undocumented invocations remain excluded. No route → explicit scoped negative decision. |
| U02 | `.ttcfgx` generation/template | Find official editable fields/schema/import contract first; then unmodified import/export and one permitted-field roundtrip on disposable project, full independent readback/preservation. Without contract, no file-edit experiment/generator. |
| U03 | Initial CPU/simulator trace support | Confirm CPU1511 order/FW/TIA/license; later admitted two-signal config/readback plus known alternating waveform. Unsupported simulator → authorized isolated physical bench through prompt 16; polling cannot substitute. |
| U04 | Nested/optimized/timer selector grammar | One rootDB/child/optimized scalar/fixed-array/public-Q/ET fixture; compare exact selector/type/readback and known values. TEMP and IN_OUT negatives; actual binding tested separately. |
| U05 | Slots/memory/triggers/sample phase | Native read of bounded 2- then 11-signal config: displayed capacity, tasks/reduction/triggers/free slots/load and installed settings. No constants from another model. |
| U06 | CSV dialect/companions | Short Bool/UInt/Time/Real native export with independently known values/times; compare native cursor/table and expected vectors; English/German selected locale; preserve bytes/companions. No private DAT/project reverse engineering. |
| U07 | Program identity | Prompt 09 exact compare on authorized fixture, deliberate separately controlled online mismatch → refusal before arm. Unknown match refuses strict mode. |
| U08 | Native disconnect/reconciliation | Disposable owned trace; lose response after one selected transition, fresh authorized reconnect, reconcile identity/state without replay. One boundary/run, all peers preserved. |
| U09 | Drive limits/license/API/formats | Prompt 10 chooses exact CU/drive/firmware/manual; complete limits before one-signal native readback/capture. Template feature notes cannot settle permitted fields. |
| U10 | Multi-device accuracy/long-term throughput | Two bench tasks/clocks and independent shared event; measure offset/uncertainty. Bounded long-term quota/rollover/drop/reconnect with known sequence; separate acceptance. |
| U11 | Buffered/intracycle visibility | If needed, separate instrumentation proposal with cycle/time/writer/buffer/drop evidence and measured cost; independently qualify overflow/freeze/export. Never implicit application changes. |
| U12 | Ownership/release scope | Re-read handoffs/admission/hashes; coordinator assigns exact package/file ownership and reconciles component07 cleanup. User/release owner decides separate milestone vs explicit 1.0 addition. Planning grants no native admission. |

## 8. Qualification strategy

Record separate `offlineReasoningVerified`, `nativeConfigurationVerified`, `acquisitionVerified` per backend/target/profile, plus preservation/reopen/recovery/interruption gates. True requires retained exact evidence; skipped/blocked/not reproduced never aggregates into pass. Every qualification binds source manifest, fixture and independent expected-oracle hashes, host/worker/adapter binaries, product/API versions, CPU/order/FW/online identity, program/export/card identity as applicable and all artifacts.

### A. Offline reasoning and protocol

Use independently authored SCL/STL/LAD/FBD exports and expected dependency/instance maps, including opaque/protected cases. Test ranking, multi-CPU/namespace ambiguity, nested/repeated/shared instances, IN/OUT/IN_OUT/alias cycles, bounded arrays/UDTs, pointer/TEMP rejection, missing source, stale snapshots and conflicting selectors. One-scan requirements must fail for polling before a worker starts.

Format tests combine permitted native exports with independent numerical vectors: irregular timestamps, wrong units, duplicate columns, signed/64-bit values, malformed/truncated data, bad quality/gaps/clock resets, lineage mismatch, unsafe paths, size/decompression bounds, DTD/external entities and missing companions. Never execute project scripts/file contents. Saved cases/reports must reopen in a fresh host without Siemens initialization.

Test intent-before-dispatch journals, idempotency lookup versus mutation replay, disconnect unknown state, cancellation-request versus observed-stop state, capacity conflict and owned-only deletion. Fakes qualify protocol behavior only, not native stopping.

### B. Native configuration qualification, later admitted

Fresh owned fixture from frozen source: two conveyor instances, unrelated user trace and retained measurement. Independently map native identities and complete compiler diagnostics for fixture code. Configure via approved route; compare all signals/settings against a pre-authored oracle, export where supported, preserve opaque fields and complete peer content. Save/close with exact owned-lifetime evidence, reopen in a fresh process and read back again. Advertised updates/rename/delete require separate cases. Restore checkpoint into a new copy, compare baseline and peer traces/measurements, then fresh reopen. Archive recovery proves offline recovery only.

Manual/UI route requires native exports and independent full enumeration; a screenshot or successful call alone cannot pass. If a required setting has no independent readback, that gate stays unverified. Compile is required where fixture/program changes demand it; trace-only configuration uses documented validation, not an invented requirement that compiling PLC code verifies trace settings.

### C. Real acquisition acceptance, separate runtime window

Only after coordinator reconciliation, current simulator isolation/preservation/stop prerequisites and target access authorization: verify exact online program, device resource inventory and installed settings; arm/prefill; inject independently defined event; retrieve complete measurement; compare values/quality/timing against predeclared expectations; analyze; preserve or explicitly clean owned resources. Measure CPU/task load against stated operating bounds. No stimuli outside the controlled authorized fixture.

Native acquisition is required for native-trace acceptance; physical bench and simulator are separate profiles. Existing VM proof remains accepted. A new remote artifact/transport change warrants only its scoped new checks, not a blanket rerun. **This planning task executed none of these native gates.**

### Required negative and boundary cases

| Case | Independent expected result |
| --- | --- |
| Other conveyor fault | Selection stays Conv2; Conv1 event cannot validate Conv2 hypothesis. Control signals explicitly separate. |
| Ambiguous equipment/two CPUs/duplicate namespace | Retain bounded alternatives; no first-match selection or mutation. |
| Stale online program/copied project/changed interface | Invalidate plan; strict transfer/arm refuses before effects; old measurements retain old revision. |
| Shared instance called twice/across tasks | Separate contexts/shared storage and sampling limitation; no invented per-call waveform. |
| TEMP/indirect/IN_OUT unsupported | Specific reason; eligible caller-storage substitute only with evidence and reduced coverage; no guessed address/instrumentation. |
| Protected/access/product/license missing | Respect boundary; metadata-only candidate if available, explicit availability/coverage and no bypass. |
| Trace slots/memory/channels exhausted | No existing trace overwritten/deleted; refuse or propose reviewed reduced plan with coverage loss. |
| Short event missed | Polling refuses scan guarantee; native intracycle transient can be invisible and is labeled so; no absence-of-glitch conclusion. |
| Fault not reproduced/trigger never fires | Bounded timeout/stop, preserved partial/no-event evidence, unresolved hypotheses; never fixed. |
| Edge immediately after arm/insufficient prefill | Actual shorter history reported; affected verdicts incomplete; no synthesized history. |
| Retrieval partial/bad quality/disconnect | Preserve raw partial evidence/gaps/unknown outcome; no automatic rearm/download/delete. |
| Same-name user trace/concurrent changes | Ownership/hash conflict; preserve user content. Native identity/content, not names, control actions. |
| Interrupted create/transfer/arm/stop/delete | Reconcile journal with independently observed state; unknown remains until resolved; no blanket kill/replay. |
| Restart/address-changing program download | Revalidate config/samples/program; retained config is not fresh measurement or program-match evidence. |
| Before/after fix | Separate code/config/stimulus hashes and comparable quality; no reproduction after fix alone is insufficient. |

### Scale and compatibility

Proposed initial planner bounds: 100 ranked candidates, 32 call-context depth, 20,000 evidence edges, 256 considered scalar leaves, 64 requested signals further capped by target. These are product policies, not Siemens limits. Truncation is explicit and can block uniqueness. Reuse retained 1,000-function/2,000-tag workloads for their demonstrated metadata/search scope; add independent repeated-instance/alias graphs for this resolver. Measure cold/warm time/bytes/memory; initial offline targets to confirm on a declared workstation are <5 s bounded persisted 1,000-block query and <512 MiB incremental memory.

Stream measurements; exercise at least one million samples, synthetic 64-channel typed import, bounded range queries, retention and gap-preserving downsampling. Synthetic capacity does not qualify a 64-channel CPU. Enumerate native CPU/FW/TIA update/language/storage/family/license cells: initial CPU1511 FW 4.0; optional classic S7-1200 FW 4.x; another S7-1500 profile; optimized/nonoptimized, unit/namespace, fixed array and protected/role variants. Long-term/project/drive/safety are independent profiles. Unavailable targets stay blocked.

## 9. Phases and recommended next package

| Phase | User can accomplish | Remaining manual/gated work |
| --- | --- | --- |
| F0 Feasibility/contracts, TD-01/02 | Review documented/unknown/unavailable routes and exact fixture/contracts | No trace created/recorded; target/native experiments separately admitted. |
| F1 Complete supported diagnostic slice, TD-03…06 + TD-09 import/analysis | Bug → ranked code → bounded exact instance/signals → manual native setup spec → supported export → hypothesis report | Human config/readback/transfer/arm/stop/export; explicitly partial native creation. Unresolved identities/settings block executable plan. |
| F2 Automated native slice, conditional TD-07/08/09 | Same plus qualified creation/readback/explicit transfer/arm/capture/retrieval/report on one target profile | Auth and route-specific human steps explicit. No supported route means F2 unavailable; never relabel F1 complete-native. |
| F3 Full supported expansion, TD-10 | Templates, language profiles, instance substitution, repeats, comparisons, retention/reports and individually qualified families | Opaque/protected/indirect and unavailable products remain gaps; separate instrumentation/safety/drive work. |
| F4 Release acceptance, TD-11 | Versioned package, exact support/evidence matrix, recovery/error contracts | Only passed profiles advertised; existing 1.0 gates remain. |

**Recommended first executable package: TD-01**, beginning with the read-only route-decision annex and shared manifest/TracePlan seam. The smallest later native experiment is two-signal configuration/readback/known-waveform capture on an admitted owned fixture through a documented route. Until route and native/UI boundaries are explicitly resolved, proceed with useful TD-02 offline contract/fixture design only. Do not implement around SafetyValidation, guessed `.ttcfgx` internals or polling as a substitute for ordinary native trace creation.

## Appendix A. Source fingerprints and audit scope

SHA-256 of installed public XML (`PublicAPI/V21/net48/`):

| File | SHA-256 |
| --- | --- |
| Siemens.Engineering.Base.xml | `0bf27f4ab7918c811a7dfde7f7c294b3133169b5f791e507278bfd27182cea43` |
| Siemens.Engineering.Step7.xml | `344f0652a1b10fedca6cd3501434ae8b1a8c4c0a014b3ef0ff1ef444f3fddda0` |
| Siemens.Engineering.SafetyValidation.xml | `c435bbf13890630290d8623b6a398437158d7d7dedc5c95867188f7f9f21da96` |
| Siemens.Engineering.Startdrive.xml | `a7a1f72b0c7c6aebba4187d21db9931690c6609c154e1b4aa358097ad8f4fa73` |

Selected source hashes; no Git revision is available:

| Path | SHA-256 |
| --- | --- |
| README.md | `daf1770b564a57044b65cac5decf013306ca6f13660776156eb67edfe87efb93` |
| docs/STATUS.md | `668455e5b6652fc9dcff78f0f53ac105fb11f52e16978627d02f8bff4c3cf302` |
| docs/TODO.md | `c992472247e8f627155f26e94849fc6e15f3a5fba4b8bbf0e700e9bad34bafb9` |
| docs/history/1.0-release-checklist-20261013.md | `2481b9c8d5a32fcfd2b04be5d2d4c06517105267a0cca9d9db0bf98e8dca3ec4` |
| contracts/feature-catalog.v1.json | `2d17641027afb9535c5b04a74e072de2e68fe0eb90ec7da0b5f3e13d32dde7ac` |
| src/TiaMcp.Host/EngineeringTools.cs | `7102b9b9691c8a555517144c77b6e7addba05fd77238af7bdf6cd19877a1f16e` |
| src/TiaMcp.Host/SimulatorTools.cs | `b2470a51bdbaa6c8677091bff538e45419f78226716e058ea1e70d0e075645b4` |
| src/TiaMcp.Simulator.V8/SimulatorSession.cs | `bee4023d64b101fd5007d0e5b5d71a0aaa95351d82cb8c6d42d7941a7198f972` |
| src/TiaMcp.Openness.V21/OpennessSession.Analysis.cs | `7991c85e4350802f254f10c7c058d5fb3b784f4059d626a0959327fecb6029da` |
| src/TiaMcp.Openness.V21/OpennessSession.Inspection.cs | `8bdbfa4272a1cb9f0172ba033d9559ca81e0b5097158124af8276ba031ebfaa7` |
| src/TiaMcp.Openness.V21/OfflineEvidence.cs | `cdfbace739663e215501c5b3bcc07844ad8ba7292bd8996b18e30744ec9b855c` |

Searches parsed public XML member names and read XSD text for trace/recording/measurement and exchange-format identifiers. Results establish the inspected surface only. Before implementation compare current files, preserve concurrent changes and refresh the audit; never restore these hashes by overwriting another session's work.

Plan validation: all 23 local Markdown links resolved, all official reference labels were defined, and the selected workspace source hashes still matched at final review. These are document/read-only consistency checks, not tests of implementation or native behavior.

## Appendix B. Official Siemens references

Checked 14 October 2026. V21 Trace help is dated November 2025; cited V21 Openness and S7-1500 manual-collection pages March 2026. Documentation is not native qualification. Links support the bounded claims at their points of use; target eligibility remains unobserved.

[S0]: https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function
[S1]: https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function/using-trace-functions/configuring/export-configuration
[S2]: https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function/using-trace-functions/configuring/import-configuration
[S3]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-safety-validation-assistant/getting-the-trace-configuration
[S4]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-safety-validation-assistant/modifying-trace-configuration
[S5]: https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function/overview-of-functions/supported-hardware
[S6]: https://docs.tia.siemens.cloud/r/en-us/v21/s7-1200/1500-cpus-s7-1200-s7-1500/quantity-structure-s7-1200-s7-1500
[S7]: https://docs.tia.siemens.cloud/r/en-us/v21/s7-1200/1500-cpus-s7-1200-s7-1500/recordable-variables-s7-1200-s7-1500
[S8]: https://docs.tia.siemens.cloud/r/en-us/v21/s7-1200/1500-cpus-s7-1200-s7-1500/recording-levels-s7-1200-s7-1500
[S9]: https://docs.tia.siemens.cloud/r/en-us/v21/s7-1200/1500-cpus-s7-1200-s7-1500/configuration-s7-1200-s7-1500/configuring-the-recording-cycle-and-duration-s7-1200-s7-1500
[S10]: https://docs.tia.siemens.cloud/r/en-us/v21/s7-1200/1500-cpus-s7-1200-s7-1500/software-user-interface-of-the-configuration-s7-1200-s7-1500/recording-conditions-s7-1200-s7-1500/user-interface-recording-conditions-s7-1200-s7-1500
[S11]: https://docs.tia.siemens.cloud/r/en-us/v21/s7-1200/1500-cpus-s7-1200-s7-1500/software-user-interface-of-the-configuration-s7-1200-s7-1500/recording-conditions-s7-1200-s7-1500/data-types-for-trigger-tags-s7-1200-s7-1500
[S12]: https://docs.tia.siemens.cloud/r/en-us/v21/s7-1200/1500-cpus-s7-1200-s7-1500/software-user-interface-of-the-configuration-s7-1200-s7-1500/recording-conditions-s7-1200-s7-1500/trigger-event-s7-1200-s7-1500
[S13]: https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function/using-trace-functions/save-recordings-as-measurements/import-measurement
[S14]: https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function/overview-of-functions/measured-value-recording-with-a-long-term-trace/configuration-overview
[S15]: https://docs.tia.siemens.cloud/r/en-us/v21/s7-1200/1500-cpus-s7-1200-s7-1500/long-term-trace-recording-s7-1500
[S16]: https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function/overview-of-functions/measured-value-recording-with-a-project-trace/configuration-overview
[S17]: https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function/overview-of-functions/measured-value-recording-with-a-project-trace/time-synchronization
[S18]: https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function/using-trace-functions/evaluating-the-measurement/compare-measurements-combined-measurement/import-combined-measurement
[S19]: https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function/overview-of-functions/measured-value-recording-with-a-long-term-project-trace/configuration-overview
[S20]: https://docs.tia.siemens.cloud/r/en-us/v21/s7-1200/1500-cpus-s7-1200-s7-1500/long-term-project-trace-recording-s7-1500
[S21]: https://docs.tia.siemens.cloud/r/en-us/v21/what-s-new-in-tia-portal/what-s-new-in-v21/sinamics-startdrive
[S22]: https://docs.tia.siemens.cloud/r/en-us/v21/what-s-new-in-tia-portal/changes-in-previous-versions-of-tia-portal/what-s-new-in-tia-portal-v20/sinamics-startdrive
[S23]: https://docs.tia.siemens.cloud/r/en-us/v21/s7-1200/1500-cpus-s7-1200-s7-1500/cpu-load-through-trace-recording-s7-1200-s7-1500
[S24]: https://docs.tia.siemens.cloud/r/en-us/v21/s7-1200/1500-cpus-s7-1200-s7-1500/lifetime-of-the-configuration-and-recorded-values-in-the-device-s7-1200-s7-1500
[S25]: https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_enus_21/comprehensive-information/cpu-function-manuals/web-server/api-application-programming-interface/reading-and-writing-process-data/plcprogram.downloadprofilingdata
[S26]: https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_enus_21/comprehensive-information/cpu-function-manuals/web-server/web-pages/record
