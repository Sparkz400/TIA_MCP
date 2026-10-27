# Bug and trace diagnostics

A workstation without Siemens products can now start from an uploaded saved
`project-review.v1` through `project_snapshot_import`, then run the diagnostic
workflow below. `trace_read` accepts either the existing hashed `artifact` or a
committed `uploadRef` (exactly one); uploaded measurement bytes are retained after
upload release. See [saved-review intake](project-reviews.md#use-saved-reviews-without-siemens-installed)
for limits and the distinction between supplied observations and verified native
behavior. No fresh project capture or live acquisition is performed by this path.


Ten MCP tools turn saved project evidence into an explicit diagnostic case, an offline watch plan, a manual trace specification and a report against a normalized measurement. They do not start TIA or connect to a PLC. **Native trace creation/acquisition is not implemented or qualified.** A manual handoff is partial fulfillment of the native-creation workflow.

Use `trace_capabilities` first. It separates documented routes, implementation, target availability and qualification, and supplies eight role-based templates. Ordinary PLC, long-term, project/multi-device, drive and Safety Validation capabilities are separate. See the [route decision](../plans/expansion-plans/bug-driven-trace-feasibility.md) and [full expansion plan](../plans/expansion-plans/bug-driven-trace-diagnostics.md).

The separate [SIMATIC AX route](../plans/expansion-plans/bug-driven-trace-simatic-ax-feasibility.md) is documented as a YAML/CLI PLC-job interface. It is reported as `simaticAxCli` / `axPlcTrace`, implementation unavailable and qualification unqualified. The inspected version excludes save triggers on TIA-engineered PLCs; full settings readback and TIA project trace persistence remain unresolved. This is a positive route candidate, not an enabled adapter or a reason to lift the UI pause. Explicit nanosecond timestamps (`ns`) are supported by normalized import/comparison without claiming native export qualification.

## Start from captured project evidence

Obtain a reviewId through `project_snapshot_import` for supplied saved evidence, or `project_snapshot_capture` on an engineering workstation, using the [review workflow](project-reviews.md). Capturing new native evidence requires the existing session/admission rules; these diagnostic tools only read saved reviews. Ensure exports include the root instance DB, parent FB, selected FB, callers and signal storage DBs, and that native relationship capture contains the exact DB-to-FB `usesType`/`instanceOf` edge. Missing evidence produces alternatives or an explicit error, not a guessed name match.

Call `diagnostic_plan` with a JSON `request`. Begin with the report, literal search terms drawn from available context, and hypotheses. Keep user observations in `observations`, expected/actual/timing/mode/recent changes in `context`, and conjectures in `hypotheses`. Use project context to supply these before asking the user. If searchTerms or hypotheses are omitted, the tool extracts bounded literal report terms and supplies four explicitly unverified generic alternatives. This is a starting point, not semantic equipment identification or causal reasoning; refine it with project evidence.

```json
{
  "reviewId": "<saved review hash>",
  "report": "Conveyor 2 occasionally stops although its enable command remains true.",
  "searchTerms": ["Conveyor", "Conv2"],
  "templateId": "competingWriters",
  "observations": ["Operator reports enable remains true; no measurement yet."],
  "context": {"timing": "Shortest relevant event duration is not yet known."},
  "hypotheses": [{"id":"writer","statement":"A later writer overrides the final output."}]
}
```

Ranking is deterministic literal matching: captured node metadata +10, XML text +3, one matched adjacent relationship +2 per distinct term. At most 25 candidates and 100 adjacent edges per node are used. Scores explain matches; they are not confidence probabilities. Protected/omitted source remains visible through status/metadata. Captured HMI, library, I/O, read/write and call edges can contribute adjacent evidence; missing relations are not reconstructed. SCL/STL/LAD/FBD XML can be searched as text, without claiming equal semantic inspectability.

Refine the request with exact `blockPath` and `instance:{dbPath,memberPath:[...]}`. Empty memberPath selects a direct root instance. The current resolver requires:

- Exact captured, native-confirmed root DB-to-FB type relation and endpoint node IDs.
- Exported root DB/FB content and supported explicit call evidence.
- For nested FBs, matching embedded DB storage and a static member with an exact qualified exported FB type name unique in the same scope, plus an explicit local call.
- CallInfo from SCL StructuredText/v4 or LAD/FBD FlgNet/v5. Simple symbolic SCL arguments and directly wired FlgNet parameters are supported. Exact fully qualified namespace/type names are retained. STL call semantics, expression arguments, array instances, cross-unit aliases and indirect access remain unresolved.

Traversal stops at 1,000 visits, depth 8 or 200 instances. `complete:false` always remains explicit. Call sites, repeated calls and shared storage are retained; captured storage resolution does not identify runtime invocation or prove that the reported machine uses that storage. Duplicate names/namespace alternatives are never silently collapsed into the first match. The caller must resolve equipment association using captured context. Missing root call evidence blocks `trace_prepare`.

### Look up instances natively

On an engineering workstation, `instance_lookup` reads the open project for one exact FB or FC (`blockPath` or `blockName`, optional `plcPath`). It returns:

- **Instances:** single-instance DBs, multi-instance static members and arrays of instances (with declared bounds) and their owning root DB chain.
- **Call sites:** native xref call locations, plus exported CallInfo with compile-unit ordinal, instance components and the actual operand per formal parameter.
- **Operand references:** references into those instance DBs, from native xref and from exported code.

Every row names its source: `nativeXref`, `interface` or `simaticMlExport`. `languageSupport` lists SCL, LAD, FBD, STL, GRAPH and every other language met:

- SCL, LAD and FBD are `parsed`.
- STL and GRAPH are an `unqualifiedScan` of exported symbolic Access with no binding reader. Their native xref Open, Actions, Transition, Interlock and Supervision rows are kept.
- Any other language is `unsupported`; its compile units are skipped, never searched silently.

`maxItems` bounds the rows, a fixed 2000-item native scan budget bounds the xref traversal, and `maxXmlBlocks` (max 50) and `maxDepth` (max 8) bound exports and nesting. `completeness` reports each truncation, every unresolved candidate and the protected or omitted exports.

The tool is read-only and not natively qualified. Each non-array instance row carries a `diagnosticInstanceSelector` `{dbPath, memberPath}` in the shape `diagnostic_plan` takes as `instance`. The planner does not trust the live row: it re-resolves the selector against its saved review and refuses a mismatch. A live lookup therefore narrows the choice, but the saved review still has to contain the root DB, the FB exports and the call evidence. Array instances need an element selector the planner does not support yet.

## Resolve a justified signal manifest

Supply up to 64 `signals` (host request budget, not native trace capacity). Each request requires `id`, `role`, `reason`, `hypothesisIds` and exact `memberPath` components from an exported DB. Optional `storagePath` defaults to the selected root DB. Other DBs must be in the same CPU. Every inclusion needs code/reference reasoning. Optional autoSignals derives a conservative bounded dependency set from exact seed symbols; see below.

```json
{
  "id":"run",
  "role":"internal command",
  "reason":"Compare the selected instance's command with final output to distinguish an internal stop from a later writer.",
  "hypothesisIds":["writer"],
  "storagePath":"<exact root DB object path>",
  "memberPath":["Conv2","RunCmd"]
}
```

These symbol names are illustrative, not assumed actual project names. The [authored conveyor oracle](../../fixtures/trace-diagnostics/expected-mappings.json) covers source enable, instance enable, internal command, final output, feedback, permit/interlock, step, timer elapsed/output, fault and separate writer guard. Its XML and measurements are synthetic offline fixtures.

Supported declaration types are Bool, Byte/Word/DWord/LWord, signed/unsigned 8/16/32/64-bit integer names, Real/LReal and Time/LTime. This is a parser subset, **not a target traceability guarantee**. Arrays, whole structures, private/restricted members, missing/protected source, TEMP and FB IN_OUT storage remain unavailable. For IN_OUT, simple captured actual arguments can resolve caller storage; the alias itself is not treated as stable storage. Captured scalar PLC tags with exact node/type/access metadata are supported, including their saved logical-address evidence. Live I/O addressing, general parameter expressions and timer private layouts are not inferred.

Each resolved row records exact storage path/components, type and hypothesis reason, plus a source hash/XML line for DB members or exact saved node metadata for tags. `onlineAddress:null` and `traceability:requiresTargetValidation` are deliberate: optimized access, namespaces, firmware and access settings need native checking. Substitutions or instrumentation require a new plan; instrumentation is a separate code change.


### Produce an offline watch plan

After selecting an exact instance and justified signals, call `diagnostic_watch_plan`
with `planId: diagnostic_plan.id` and this `request`:

```json
{
  "tableName": "Conveyor 2 diagnosis",
  "tiaVersion": 21,
  "maxRows": 64,
  "externalStorage": [{
    "objectPath": "<exact external DB or tag object path from the case>",
    "reason": "Compare the final output with the selected instance command."
  }]
}
```

Omit `externalStorage` if every signal belongs to the selected instance DB.
Each different external storage object requires a separate explicit reason;
duplicate, redundant or unused allowances fail. Signals in another nested
instance of the selected DB remain refused. The planner rereads the immutable
case and saved review, checks their source inventory, and preserves captured
object kinds, PLC/scope paths and known XML hashes. It does not infer identity
from names or reconstruct missing source.

`tiaVersion` is mandatory caller-supplied metadata, labelled
`callerSuppliedUnverified`. Its allowed integer range of 1..100 is an identity
format bound, **not** a list of supported Siemens versions. `maxRows` defaults
to 64 and accepts 1..64. Unresolved declarations and row-budget omissions stay
in the full artifact with their original signals and reasons. Review both the
summary counts and `truncated` before using the plan.
The retained envelope also shares the diagnostic store's 8 MiB document and
256 MiB total retention limits; exceeding them fails without a successful receipt.

The response includes a bounded summary, `planSha256`, and a hashed `artifact`.
Download the full `diagnostic-watch-plan.v1` envelope using `artifact_read`;
its `watchPlan` follows [the existing watch planning contract](../reference/watch-table-planning.md).
Repeating an identical request reuses the same artifact ID, including after a
Host restart. The document remains unauthenticated supplied evidence with no
native XML, live addresses, modify values or native apply admission. Existing
`watch_table_generate`/prepare/apply tools still require their native workflow.

Watch planning, manual trace preparation and diagnostic replanning re-derive the
retained case's project/source inventory, selected instance and complete call
bindings, scalar declarations and optional alarm context from its exact saved
review. A new valid content hash does not make altered derived fields trustworthy:
symbols, types, XML locations, live/address claims or call evidence that disagree
with the source refuse with `CaptureMismatch`, before publishing a result.
Replanning therefore requires the predecessor's saved review as well as the new
review. Keep both original reviews with the case lineage. Incomplete but unchanged
declarations remain unavailable; these checks do not establish runtime identity,
source authenticity, full program semantics or an alarm-to-member association.

### Bounded automatic signal selection

After resolving the exact instance, add the following to the diagnostic request (names are hypothetical fixture symbols):

```json
{"autoSignals":{"seeds":[{"scope":"LocalVariable","components":["RunCmd"],"reason":"Follow the command that unexpectedly disappears."}],"maxSignals":16,"maxDepth":4}}
```

Seeds must come from captured source. SCL assignments retain all symbolic references in the compile unit as a conservative dependency superset, including guards. FlgNet support follows simple call bindings, not arbitrary instruction semantics. Explicit input/output bindings retain both instance and actual storage; IN_OUT resolves the actual. Global actuals bring in captured competing writers from the same scope. Local symbols in other FBs require an exact caller instance context; missing contexts are coverage gaps. Nested member suffixes and namespace identities remain explicit.

Every automatic inclusion carries selectionEvidence; repeated evidence is retained with bounds. Budgets are 1..64 signals, depth 0..8, 256 visited symbols, 5,000 traversal edge visits and 20,000 collected graph references. `automaticSelection.complete:false` always remains explicit; truncation and unresolved dependencies must be reviewed. These are host budgets, not trace slot limits. Reduce seeds/depth or prioritize hypotheses when native capacity is smaller. Unsupported symbols remain reported gaps, never guessed online addresses.

## Prepare the manual specification

Use the returned diagnostic `id` as `planId` in `trace_prepare`:

```json
{
  "backend":"manual",
  "family":"ordinaryPlc",
  "reviewId":"<same saved review hash>",
  "settings":{
    "task":"<task established from code and target evidence>",
    "sampleEvery":1,
    "preSamples":100,
    "postSamples":100,
    "rationale":"Illustrative counts only: replace using phenomenon duration and verified target limits.",
    "trigger":{"signalId":"final","kind":"fallingEdge"}
  }
}
```

There are no implicit rates or time windows. Counts above are an example request, not verified CPU limits. A single Bool edge or numeric above/below threshold is expressible. Extra acquisition requirements are rejected, not ignored. Actual compound triggers, native settings, scan accuracy and available slots are not inferred. `effectiveSettings:null`, `nativeExecutable:false` and `partialManualHandoff` remain in the result.

The artifact contains exact selected storage, signals, hashes, requested settings, unresolved coverage and a native validation/readback checklist. A human must verify target/program freshness, observability, limits and sampling effects, configure the trace, read it back, transfer, arm, record, stop and export. These stages remain distinct. Preserve existing traces/measurements; exhausted slots require a separate decision, never automatic deletion. Connection loss requires inspection, not mutation retries. No trace status/apply/capture/delete tools are registered because no supported adapter was established. The UI pause and native/simulator scheduling boundaries remain unchanged.


### Check independently supplied readback

`trace_check_readback(tracePlanId, artifact)` accepts a hashed [trace-readback.v1](../../contracts/trace-readback.v1.schema.json) JSON observation and retained hashed evidence files. Copy observed target/project identities, every signal's storage components/type, settings and observability from an independent native readback. Include target/program identity, observation method, program-match result, trigger/task support, and observed resource limits. `requiredSampleCapacity` and `sampleWindowSemantics` must explain native pre/trigger/post counting; no universal counting rule is invented.

The tool compares identities/settings and reports missing/unexpected signals, unavailable observability, unverified supplied program-match assertions, exhausted new-trace slots and capacity/reduction mismatches. A successful comparison means only `matchesSuppliedReadback:true`: it cannot authenticate supplied evidence, probe the live target, authorize transfer/arming or qualify native behavior. Synthetic tests exercise comparison logic only. User-created traces are never touched. The user's 14 October instruction explicitly keeps native UI work paused.

## Import and analyze a measurement

`trace_read(tracePlanId, artifact:{path,sha256})` reads a file already inside the host artifact root (use existing artifact upload facilities). It accepts [trace-measurement.v1](../../contracts/trace-measurement.v1.schema.json) normalized JSON or explicitly mapped UTF-8 CSV. It does **not** decode `.ttrecx`, `.lttcd`, `.dat`, `.ttcfgx` or automatically recognize Siemens CSV dialects. Retain the native original and use an independently understood conversion; never infer timestamps or units from column positions.

Required fields are schemaVersion, exact tracePlanId/reviewId, mode (`synthetic`, `polling`, `manualNativeExport`), clock description, timeUnit (`s`, `ms`, `us`, `ns`), programIdentity, quality, units by exact signal ID and samples `{time,values:{signalId:value}}`. Quality requires atomicAcrossSignals, scanSynchronized, dropoutsKnown, reportedDroppedSamples and programMatchVerified. Optional expectedInterval uses the same time unit. All quality/program claims are **caller attestations**; the tool only performs structural and consistency checks. `manualNativeExport` also requires a hashed host-owned originalArtifact, conversion description and settingsReadbackEvidence. No native qualification is inferred from these fields.

Every sample must contain every selected signal; use null for unavailable values. JSON Bool and exactly representable finite decimal-range numbers are accepted, with integer type/range checks. Overflow, underflow and precision loss are rejected; full IEEE Real/LReal range is not supported. Negative pre-trigger timestamps are retained. No string coercion or resampling occurs. The importer retains original timestamps and units, rejects resets/duplicate timestamps and flags gaps larger than 1.5 times the supplied expectedInterval. Expected interval is not assumed for nonperiodic tasks. Maximum 10,000 samples and 8 MiB normalized artifact; original native export verification supports up to 32 MiB. Polling cannot claim atomic or scan-synchronized quality.

To enable mechanical hypothesis checks, put an explicit `prediction` on a hypothesis before preparing the trace. Conditions are ANDed, with no arbitrary expressions or script evaluation:

```json
{
  "id":"writer",
  "statement":"The internal command remains true when a later writer clears final output.",
  "prediction":{
    "when":[{"signalId":"final","op":"eq","value":false}],
    "expect":[
      {"signalId":"run","op":"eq","value":true},
      {"signalId":"override","op":"eq","value":true}
    ]
  }
}
```

`trace_analyze(measurementId)` compares recorded samples against these original predictions and exports a report. Operators: eq/ne/lt/le/gt/ge; Bool supports eq/ne. Numeric conditions also require `unit`, which must exactly match the imported signal unit; no implicit conversion is performed. Results are `supportedPrediction`, `contradictedPrediction` or `unresolved`; they never prove cause. Reports include up to 200 transition intervals (change occurred after one timestamp and by another), counts, up to 20 examples per hypothesis, units and quality. Non-atomic samples, missing values, reported/detected dropouts or uncertain program matching prevent definitive prediction verdicts. No applicable condition means unresolved, not fixed. Short events between samples remain possible even with clean imported data.

Every prediction condition requires a supported captured scalar type. Missing
declarations, arrays and other unsupported types return `SignalUnobservable` with
the affected signal ID; a numeric value does not establish an unknown type.
Resolve the declaration or revise the prediction in a new diagnostic case.
Signals without a dependent prediction may still be retained as unavailable.
A known scalar type can be checked even if access is unavailable, but preparing
a trace still requires every selected signal to pass the existing observability
checks. This distinction does not establish live access or native qualification.


### Explicit CSV mapping

Pass optional `csvOptions` to trace_read when the artifact is CSV. [The contract](../../contracts/trace-csv-options.v1.schema.json) requires delimiter (comma/semicolon/tab), decimalSeparator (dot/comma), zero-based headerRecord (0..100), exact timeColumn, timeUnit, columns `[{signalId,column,unit}]`, mode, clock, programIdentity and quality. Every selected signal must map exactly once to a distinct column. Optional missingValues lists exact unavailable-value tokens. Bool accepts 0/1 or true/false; numeric scientific notation is accepted only within exact decimal precision. Invalid/hex IEEE tokens require an explicit missing-value mapping, never silent coercion. Quoted fields are parsed, but duplicate headers, row-width changes, broken quoting and trailing garbage fail. Units are supplied explicitly because text exports may omit them.

The original CSV and mapping are preserved. For manualNativeExport retain settingsReadbackEvidence as well. This is an explicit text mapping service, not qualification of any particular native export dialect. A real target export is still required to qualify end-to-end native ingestion.

### Compare captures and reuse an instance plan

`trace_compare(beforeMeasurementId, afterMeasurementId, options)` requires reason, beforeAnchorSeconds, afterAnchorSeconds and windowStartSeconds/windowEndSeconds relative to those explicit anchors. It converts s/ms/us/ns time units and subtracts event anchors only when the resulting times are exactly representable; precision loss returns `MeasurementFormatUnsupported` and never resamples. Unknown options, null/non-array `signalMap`, non-Boolean `allowChangedSource` and malformed mapping rows return `InvalidRequest`; omit `signalMap` only when automatic matching by signal ID is intended. Changed source reviews or different declared measurement `programIdentity` values require allowChangedSource:true; changed storage requires explicit one-to-one signalMap `[{before,after}]`. Signal types and units must match. Results preserve both source/plan/measurement identities, acquisition quality/settings differences, sample/missing counts, Boolean counts, numeric ranges and bounded transition intervals. Different sample rates and coverage can explain count differences. `bugFixProven:false` remains unconditional.

Current analysis reports retain `acquisitionContext` with the original `mode`, `clock`, `timeUnit`, supplied `programIdentity` and `identityVerified:false`, plus `clockSynchronizationVerified:false`. Analysis and comparison share the same context formatter. Current comparison reports retain `beforeAcquisition` and `afterAcquisition`, each with the original `mode`, `clock`, `timeUnit` and supplied `programIdentity`. `sourceChanged` continues to describe saved review IDs; `programIdentityChanged` separately reports different measurement program labels. Neither equal labels nor `quality.programMatchVerified` authenticate the programs: each context keeps `identityVerified:false`. Explicit event anchors do not establish synchronization or simultaneous events; `clockSynchronizationVerified:false` remains unconditional. Older v1 reports without this context remain readable but do not establish these checks. Rerun `trace_compare` on retained imports to obtain a current report; historical reports are not rewritten.

Gap detection compares consecutive timestamps against exactly 1.5 times the supplied `expectedInterval`, without rounding either side. Analysis and comparison recompute missing-value and interval-gap assessments from retained samples, including older imports. Historical imports and reports remain immutable; rerun `trace_analyze` or `trace_compare` to obtain a corrected report. Exact arithmetic does not establish acquisition authenticity or native timing qualification.

`diagnostic_replan(planId, request)` requires the new reviewId, blockPath, instance, reason and signalMappings `[{id,storagePath,memberPath,reason}]` covering every old signal exactly once. It resolves all declarations and instance evidence anew, rejects changed types, retains hypothesis IDs and records prior-plan lineage. The original role template is selected again from the current template catalog; a case without a template remains without one. Template guidance never substitutes for new instance, declaration or target evidence. No substring substitution is performed. New equipment observations/context must be supplied explicitly. Native traceability always needs revalidation.

## Persistence, repeated captures and current limits

Diagnostic cases/specifications/imports/reports are content-addressed JSON in `artifacts/trace-diagnostics`; artifact receipts work with the existing artifact reader. Repeating an identical save reuses its hash, and reopening verifies content integrity. Publication uses a cross-process storage lock, with ArtifactBusy on contention. Limits are 8 MiB per stored document and 256 MiB retained files; selected artifacts must be explicitly archived when full. No automatic eviction/deletion touches user measurements. A source-review, target, instance, binding or firmware change requires a new plan and revalidation; no claim of live freshness is made.

Reuse the same prepared specification for repeated captures only while its assumptions remain valid. Compare reports with explicit source/plan/measurement IDs and unchanged units/settings. Use trace_compare for explicit event-aligned before/after capture comparison and diagnostic_replan for explicit machine-instance substitution with declaration revalidation. Full language dependency slicing, general aliases/arrays, native binary decoders and lifecycle automation remain expansion work. Template roles help reduce signal budgets but never auto-create addresses. Watch tables/offline symbol tracing are not recordings; the existing sim_test wall-clock polling route cannot guarantee one-scan glitches and may apply stimuli.

Offline validation: `dotnet run --project tests/TiaMcp.Diagnostics.Tests --configuration Release --artifacts-path <fresh repository artifact directory>`. This suite is included in `scripts/Test-Offline.ps1`. Native configuration readback, reopen/preservation, real injected acquisition and interruption recovery remain unexecuted acceptance gates.


## Supplied Test Suite findings

After [importing supplied results](../reference/testsuite-result-review.md), optionally
select those receipts in `project_health_report.testSuiteResults`. Each returned
`testSuite.reports` entry preserves the exact receipt used below. Pass an exact
receipt and finding index to the existing case planner:

```json
{
  "reviewId": "<saved-review SHA-256>",
  "report": "Investigate the retained Conveyor finding against this captured project.",
  "testSuiteFinding": {
    "relativePath": "<receipt relativePath>",
    "sha256": "<receipt SHA-256>",
    "index": 0
  }
}
```

The index explicitly selects a finding's captured block. It never selects an
instance or signal set. Add the normal instance, signals, hypotheses and context
when justified by captured evidence; then call `diagnostic_watch_plan` and the
manual trace/measurement workflow using the returned case ID. The case retains
`testSuiteEvidence`: exact original/report/JUnit/receipt references, finding index,
original native state and counters, and false source/native qualification flags.
Input uploads may be released after import; the retained artifacts survive restart.

A conflicting block, another review, unresolved candidate, absent/non-block object,
invalid index or changed artifact refuses before a case is saved. Selecting a
finding does not prove a root cause, identify the running program, authorize a
native action or make its source authentic. Scope/result coverage gaps remain in
the retained summary even when the exact finding can support investigation.

On `diagnostic_replan`, old Test Suite evidence stays in the previous case reached
through `lineage.previousPlanId`. It is absent from the new case unless the caller
supplies `testSuiteFinding` again and exact new-review/block validation passes.
This holds even when the same name/path appears in a later capture. A new source
requires genuinely corresponding evidence; rewriting old snapshot IDs is invalid.

[Verification and remaining D06/C04 work](../verification/testsuite-diagnostics-20261016.md).

## Selected supplied alarm context

`diagnostic_plan` optionally accepts `alarmContext` alongside the normal report,
hypotheses and explicit block/instance/signal selections:

```json
{
  "reviewId": "<saved-review SHA-256>",
  "report": "Investigate this captured alarm with the selected machine instance.",
  "alarmContext": {
    "reviewId": "<the same saved-review SHA-256>",
    "nodeId": "<exact captured node ID>",
    "objectPath": "<exact captured object path>"
  }
}
```

The selector must identify a nonvirtual `alarm`, `supervision` or `hmiAlarm` node.
`alarm` and `supervision` are explicit supplied-model labels in this workflow;
they do not recognize a Siemens PLC export or provide a native PLC alarm inventory.
The existing HMI provider uses `hmiAlarm`. Equal alarm numbers/text/names in another
PLC or software unit never substitute for the exact node/path and saved review.
Wrong snapshots, missing identities, other node kinds and extra selector fields
refuse before a case is saved.

The immutable case retains `alarmEvidence`: the complete selected node, up to
64 captured ancestors, and up to 100 direct relationship observations with their
original array indexes and unchanged edge objects. The combined context is bounded
to 256 KiB. An oversized selected node refuses; oversized ancestors/relationships
are omitted with explicit status/counts. Original full evidence stays in the
content-addressed saved review, downloadable through its artifact receipt.

`ancestryStatus` distinguishes a chain reaching the captured project from missing
parents, cycles and limits. A chain can contain virtual navigation ancestors; this
is captured structure, not proof of native semantic ownership. Direct relationship
statuses distinguish exact supported endpoints, unresolved evidence, mismatched
endpoints and unsupported relationship kinds. Counts cover all observed adjacent
rows, including omitted ones. The original protected/unavailable access metadata
is retained; no protected content is acquired. `coverageComplete`,
`sourceAuthenticityVerified`, `nativeQualified` and `storageSelected` stay false.
No direct relationships does not prove no consumers. Use saved-review comparison
for [transitive dependency impacts](project-reviews.md#explain-dependency-impacts).

Alarm context never selects an executable block, DB instance or signal, nor proves
that the separately selected storage explains this alarm. Keep the normal explicit
storage/hypothesis checks; missing or ambiguous storage still prevents watch/trace
preparation. After `diagnostic_watch_plan`, the downloadable watch envelope retains
the exact alarm evidence after revalidating it against the original saved review.
Case IDs retain its provenance through trace preparation and measurement reports.

On `diagnostic_replan`, old alarm evidence remains only in predecessor lineage.
Supply `alarmContext` again to select evidence against the new exact review; an
old selector cannot be relabeled automatically. Both the case and watch envelope
survive Host restart. This is offline context composition, not alarm-to-storage
inference, native generation, acknowledgement or runtime qualification.

## Export a retained report

For prebound OPC UA polling/subscription captures, use the separate
[retained UA observation workflow](../reference/diagnostic-ua-observations.md):
`diagnostic_ua_observe` and `diagnostic_ua_report_export`. These reports retain
original acquisition evidence and unresolved hypotheses and can be opened with
**Open capture** in portable Explorer.

Call `diagnostic_report_export` with the `id` returned by `trace_analyze`,
`trace_compare` or `runtime_capture_analyze` and `format` set to `json`, `markdown` or `html`.
The [capture analysis route](../reference/runtime-captures.md#analyze-sequential-observations)
groups sequential reads by cycle, retains separate timing/missing values and always
keeps behavioral verdicts unresolved. It requires an explicitly associated capture,
its exact saved diagnostic case and caller units.

Example export:

```json
{"reportId":"<retained report SHA-256>","format":"html"}
```

Download the returned `artifact.path` through paged `artifact_read`; check the
returned byte count and SHA-256. JSON is the exact original report, with the same
ID and bytes. Markdown and standalone HTML show results, acquisition context,
quality, source lineage and the complete source JSON. HTML needs no network or
scripts; narrow screens scroll tables horizontally and the complete JSON can be
opened with the keyboard. Source URLs/markup remain literal text. Historical
reports without acquisition context explicitly show it as missing.

The `diagnostic-report-export.v1` receipt binds the original `sourceReportId`,
`sourceSchemaVersion` and `sourceArtifact` to the output. It always declares
`nativeQualification:false`, `sourceAuthenticityVerified:false` and
`analysisRecomputed:false`. Export does not repeat analysis, authenticate the
source or prove that the fault is fixed. Its 8 MiB output and shared 256 MiB
retention limits apply; use exact JSON if a readable export exceeds its bound.
Changed source bytes or cached output refuse instead of being silently repaired.

## Explicit alarm operand bindings

For supplied alarm-to-declaration and call-argument associations, see
[alarm executable bindings](../reference/alarm-executable-bindings.md). These
optional selections are revalidated on watch/trace handoff and replanning;
unresolved selections refuse. They do not establish a native alarm format or
runtime acquisition association.
