# PLC alarms and ProDiag: implementation and qualification plan

Audit date: 17 October 2026. Base source: `a9026cd`. No Siemens product, installed
public SDK, PLCSIM or PLC is available here. This plan preserves all D11 release
obligations in the [release ledger](../release-obligations-20261015.md); supplied
documents and offline tests do not qualify native engineering or runtime behavior.
The current user authorized implementation as well as planning. No other task
checkout or active native session was observed; only the primary `main` checkout
was present. Future native windows still require fresh ownership checks.

## Current implementation and reusable boundaries

- **18 October supplied AL-03/08 increment:** Explicit normalized operand nodes
  and original edge selections are checked against saved declarations, exact
  instances and captured call arguments, then retained/revalidated through
  watch/trace/replan. Full aggregate/package validation and actual prior-producer
  compatibility pass, including on the relocated package. See the
  [workflow](../../reference/alarm-executable-bindings.md) and
  [evidence](../../verification/alarm-executable-bindings-20261018.md).
  This is not a Siemens alarm format or native producer. Complete UDT/consumer
  coverage, runtime acquisition association and remaining AL operations stay open.

- `src/TiaMcp.Translations/TranslationWorkbook.cs`, `TranslationReview.cs`,
  `TranslationInspection.cs` and `TranslationWorkflowStore.cs` already provide
  explicit keyed XLSX mappings, bounded original retention, exact source/target
  digests, independent candidate preservation, paged MCP review, approval and
  restart. The new optional `instance-inheritance-v1` alarm-text policy checks
  all supplied mapped languages and blocks mixed/unobserved final fields.
  It does not recognize vendor workbooks, establish full language coverage or
  enable a native import. See the [workflow](../../reference/translation-review.md#supplied-plc-alarm-instance-text-inheritance).
- `TranslationWorkerAdapter.cs` recognizes a separate project-text profile.
  `TranslationProductionAdmission.cs` binds native admission to exact profiles,
  assemblies and retained qualification. Never reuse that admission for alarm
  text, alarm text-list or supervision workbooks merely because each is XLSX.
- `src/TiaMcp.Openness.V21/HmiInspectionRules.cs` resolves supported Unified
  alarm-to-tag references, including parameter tags, with explicit unknown and
  incomplete results. `OpennessSession.HmiProjectModel.cs` captures corresponding
  HMI nodes/edges. These are HMI-owned alarms, not PLC alarm-instance inventory.
  Historical [HMI evidence](../hmi-remaining-work.md) includes failed compile gates
  and missing retained files here; do not relabel that evidence as D11 acceptance.
- `src/TiaMcp.Core/ProjectReviewStore.cs` and `ProjectReviewStore.Import.cs`
  retain supplied model/source evidence, compare exact paths and traverse bounded
  supported incoming dependency edges. `Diagnostics/DiagnosticPlanner.cs` uses
  captured relationships without inventing missing alarm links. Reuse those
  consumers once an exact alarm/supervision evidence contract exists.
- No dedicated PLC alarm/ProDiag capture, preserving mutation or native runtime
  adapter exists. The retained 14 October inventory contains 392 matching Step7
  alarm/supervision declarations, not current public accessor or target proof.
  Raw current alarm/supervision XSDs and genuine export fixtures were not found.
  `IEngineeringObject.Create` infrastructure declarations alone are not supported
  creation APIs. The catalog's broad domain remains unimplemented/unqualified.

## Documentation findings and capability matrix

Context7 library resolution and two focused lookups were performed. Generated
REST paths in those responses do not match the official C# API and were discarded.
The official V21 leaves below were read directly. Several examples contain typos;
compile future adapters against exact public metadata rather than copying them.
All native rows require a matching Portal/STEP 7 installation, reachable owning
project/PLC or unit, necessary function rights, and product/target availability.
ProDiag engineering/runtime entitlement and family support need separate probes.

| Workflow | Public route and documented constraints | Implementation / remaining qualification |
| --- | --- | --- |
| Alarm instance text export/import | `PlcAlarmTextProvider` on PLC or software unit: `ExportInstanceTextsToXlsx(FileInfo, IEnumerable<Language>, PlcAlarmTextXlsxExportOption)` and `ImportInstanceTextsFromXlsx(FileInfo, IEnumerable<Language>)`. Export options include alarm/info/additional texts 1–9 and class. It updates existing instances/classes; creation is not part of this route. Inactive languages are not activated. Preserve import logs and OK/Warning/Error; warning can be partial. [Source][instance] | Supplied mapping/candidate workflow only. Genuine workbook properties, keys, language columns, inherited fields and native readback remain open. |
| Alarm text inheritance | The exact inherited-from-type marker must occur in all or none of the language fields of one alarm text; mixed input leaves that alarm's texts unimported with a warning. [Source][instance] | Explicit supplied-row guard implemented. Missing mapped fields refuse; unmapped language coverage remains unknown. Atomic inheritance-mode changes and genuine full-language fixtures remain open. |
| PLC text lists | `PlcAlarmTextListProvider` on PLC/unit exports/imports XLSX. Two related sheets use list name/entry parent; custom `FileContent` identifies alarm text lists. Name-filtered export excludes system lists. Import None refuses existing lists; Override replaces their supplied entry sets, rather than merging. Unmentioned lists remain. Invalid entries can reject their list; missing/inactive languages and partial warnings require readback. [Source][lists] | No genuine layout adapter. Preserve complete list entries, ranges/types, comments and peers; never use partial translation rows as a complete replacement list. |
| Text-list lifecycle/library | `PlcAlarmTextlistGroup` exposes user/system collections, IDs, ranges and multilingual comments. User name/range setters, delete, master-copy creation and `CreateFrom` are documented; system mutability is not implied. [Source][library] | Native inventory and preserving edits/copy/delete remain open. No arbitrary public empty-list constructor is established. Scope IDs to their owner; retain library provenance and name-conflict outcomes. |
| Alarm classes | Project service `AlarmClassDataProvider.Export/Import(FileInfo)` uses DAT. Result states/messages and counts matter. Documented import checks include schema 1.0/2.0, class limits and active-language overlap; V16 export compatibility is documented. [Source][classes] | Retain opaque DAT until genuine grammar is established. Separate project-wide class impact from PLC-local text updates; warning is not an exact-effects proof. |
| ProDiag FB and IDB creation | `PlcBlockComposition.CreateFB(..., ProgrammingLanguage.ProDiag)` creates an FB without IDB; `CreateInstanceDB` is separate. Auto-numbering can allocate another number. Unsupported cases throw. [Source][create] | Prepare both exact identities and expected outcomes; native creation/number conflict/compilation/reopen/recovery unimplemented. Deletion must audit incoming references and generated relationships. |
| FB supervision/IDB assignment | `FB.Supervisions`, `GetAttribute(s)`, `SetAttribute`; IDB `AssignedProDiagFB` documented. Multiple writes through `SetAttributes` are restricted on this route. [Source][access] | Current setters, types, names, labels and per-supervision capabilities need metadata and independent target probes. Do not infer create/delete from a composition's existence. |
| Global DB/tag UDT assignment | `Interface.Member` or `PlcTag` dynamic `AssignedProDiagFB` is documented for UDT instances, including nested/array paths. Simple/system/constant and other non-UDT members are unsupported. [Source][assign] | Capture exact member path/type and target FB identity; no suffix/name-only match. Examples include a trailing-space attribute typo; resolve the exact metadata name before implementation. |
| UDT/DB supervision XML | XML import can define Bool UDT supervision and DB UDT-instance ProDiag assignment; the leaf embeds V17 examples under V21 documentation. Non-Bool supervision tags may not throw, so no exception does not prove application. Post-import supervision setting is not provided by that documented UDT route. [Source][udt] | Version-pinned XSD/export/readback required. Preserve Number/Type, operand/status/delay/conditions/categories and every unrelated member/annotation. No invented XML generator is qualified. |
| Global and per-FB supervision | `SupervisionProvider` on PLC/unit and ProDiag FB: export XLSX, separate `ImportSupervisionsFromXlsx` and `ImportSupervisionSettingsFromXlsx`, None/Override; result includes log and Success/Failure. [Global][global], [FB][fbglobal] | Separate tag and settings effects, partial failure and owner scope. Exact supported workbook schema and field setters remain open. Example `X1sx` and pluralization errors are not API definitions. |
| Project supervision settings | Project `SupervisionSettingsProvider.Export/Import(FileInfo)` uses DAT, with states including Success/ErrorRollback and messages/counts. [Source][settings] | Project-wide preserving settings workflow required. An ErrorRollback return needs independent restoration proof; retain preimage and observed state. |
| FB configuration and alarm export | Read `ProDiagVersion`, `InitialValueAcquisition`, `UseCentralTimeStamp`. `ExportProDIAGInfo(DirectoryInfo)` exports CSV for consistent ProDiag FBs; documented identity combines client alarm/supervision IDs in hexadecimal and includes priority. [Attributes][attrs], [CSV][csv] | No inferred writable attributes, CSV column parser or identity splitting until genuine exports establish grammar. Bind files to exact FB/version/capture. |
| HMI/diagnostic/runtime handoff | Existing HMI inspection is a separate owner/family provider. PLC text and supervision configuration alone prove neither active fault nor acknowledgement state. | Add exact association evidence, diagnostic-control type/property probes and UI links; runtime triggering, acknowledgement and substitutions require independent simulation/bench demonstrations. Never execute project scripts during inspection. |

## Architecture and contracts

Keep native calls in the V21 worker and pure validation/diffs in portable code.
Reuse committed uploads, `ProjectReviewStore`, translation retention/paging,
`EngineeringObjectReference`, prepared-change/journal/checkpoint infrastructure and
existing catalog/availability reporting. Avoid a second generic workbook engine.

The currently delivered policy adds no new MCP tool. Each profile row is one exact
text-resource key across mapped cultures; `alarmTextPolicy` is omitted by default
so historical serialized profiles and reports retain their identity. Inspection
shows current state; review adds before/after inheritance counts. All effective
mapped fields are checked, including unedited languages. Approval still binds the
complete digest, and candidate generation independently preserves all peers.
Unknown token grammar remains blocked; a brace grammar is not a Siemens grammar.

Proposed, not implemented: `plc_alarm_inspect` for bounded native owner/resources,
`plc_alarm_review` for retained supplied alarm/supervision relationship evidence,
and prepared-change kinds for exact text-list/class/supervision/assignment/FB
operations. Do not add generic apply-any-JSON or import-any-XLSX tools. Resolve
these proposals against the final worker contract before registration.

An alarm observation must retain project/PLC/unit identity; resource kind; native
ID plus owner and capture; full member path; typed data when actually exposed;
raw parameter/text/label representation; culture and inheritance state; origin,
observation time, artifact hash and incomplete/unavailable reason. Preserve opaque
identifiers, including hexadecimal formatting; no decimal normalization or name
guessing. Cross-capture correspondence requires explicit native identity evidence.
Protected/generated objects, absent products and unknown properties stay explicit.

Dependency records must distinguish alarm instance → supervision, supervision →
member/condition/delay/tag, member → UDT definition, IDB → FB, alarm → text list/class,
and HMI control/tag → PLC source. Each edge carries exact endpoints, evidence
artifact/location and resolution. Do not turn user-supplied edges into native
confirmation, or infer an HMI binding from equal alarm text. Limits should reuse
saved review bounds (64 MiB, 50k nodes/edges) and traversal bounds; new report pages
must be byte-bounded with explicit omissions and complete downloadable originals.

## Required chain and breaking-member example

Independent fixture: PLC A, unit U, `MotorType.Tripped: Bool`, DB instance
`Motor01.State.Tripped`, user FB supervision S17 and assigned ProDiag FB/IDB P1,
alarm instance A42 with German/English text and one parameter, plus HMI alarm
display/tag on HMI B. Each ID is scoped to its exact native owner and captured
generation; example names here are descriptive, not native identifiers.

Evidence chain: HMI B control/tag → exact PLC alarm A42 → supervision S17 → exact
`Motor01.State.Tripped` → `MotorType.Tripped`. Keep alarm-class/text-list and
parameter/delay/condition edges alongside that chain. An alarm and a supervision
must not be equated merely because both expose a numeric ID.

Change fixture: rename/remove `MotorType.Tripped`, or change Bool to Int, while
leaving the dependent configuration unedited. Expected offline report: the old
member is absent from the after capture or its type changed; before-side edges
still expose S17/A42/HMI B as review candidates, the after-side endpoint is
unresolved or incompatible, and affected translations/parameter bindings require
fresh review. No rename rebind, deletion verdict from partial scope, runtime fault
claim or invented compiler error number. Later native compilation must retain the
actual diagnostics and verify generated IDB/FB/text effects and untouched peers.
If HMI B evidence is absent, report that gap rather than completing the chain.

## Dependency-ordered work packages

Effort ranges are focused engineering days after prerequisites exist, excluding
external hardware/licensing wait and failed native experiments. They are planning
ranges, not commitments. Shared translation/model files need serial integration
with D03/D16/F02; no parallel writers are currently authorized.

| ID | Inputs and boundaries | Concrete completion evidence | Effort / dependencies |
| --- | --- | --- | --- |
| AL-01 | Official docs, retained inventory, installed public metadata/XSD and genuine fixtures; this plan and versioned fixture manifests | Exact accessor/owner/format/target matrix, unsupported routes and licensed-product probes; independent export layouts, IDs and placeholder grammar | 2–5; native metadata/fixtures partly unavailable |
| AL-02 | `TiaMcp.Translations`, schemas, Host tests; explicit supplied mappings | Capture/inspection/mixed-state refusal/peer-preserving approved repair/restart and portable package. Initial inheritance policy implemented; final verification record states executed scope. Genuine recognition, all-language evidence and atomic inheritance transitions remain open | 1–3 offline; D03, AL-01 for native format |
| AL-03 | Portable alarm evidence contract, saved review/model/diagnostic consumers | Complete supplied alarm→supervision→member→HMI chain and breaking-member impact, before/after separation, unknown/protected/duplicate/stale IDs, bounded pages and retained originals | 3–6; F02, AL-01; native adapter separate |
| AL-04 | V21 worker/model providers and exact capture/export tools | Native inventory for PLC/unit/classes/lists/supervisions/FBs/IDBs, per-field availability, export hashes, complete scoped peers, actual MCP/Explorer selection | 4–8; installed SDK/product, AL-01/03 |
| AL-05 | Separate alarm-instance/list/class native profiles and translation candidate/admission | Full genuine workbook/DAT preservation, all languages/parameters, list replacement effects, class-wide consumers, exact prepared change/readback/save/reopen/recovery | 5–10; AL-02/04, F03; no reuse of project-text admission |
| AL-06 | Supervision/UDT/DB/member/FB and project settings adapters | Supported creation/edit/delete/assignment, auto-number collision effects, source/type/generated dependencies, compile and exact full-peer preservation | 6–12; AL-04, genuine XSD and settings fixtures |
| AL-07 | Text-list library/template lifecycle | User/system separation, rename/range/comment/delete/master-copy/create-from, duplicate IDs/names and scoped library preservation | 3–6; AL-04/05, library lifecycle gates |
| AL-08 | HMI providers, diagnostics, health, Explorer and C04 | Traceable exact links and gaps, supplied diagnosis/watch handoff, protected/script text inspection without execution, independently accepted UI | 3–7; AL-03/04; family-specific HMI evidence |
| AL-09 | Exact target bench/simulation and diagnostic controls | Triggering, delay/condition behavior, acknowledgement and multilingual parameter substitution under disconnect/reconnect; engineering and runtime results separate | 3–8 per target family; AL-05/06/08, runtime entitlement |
| AL-10 | Final source/binary/profile/package and retained independent receipts | Full bounded/scale/interruption/upgrade/installation checks and every D11 release row reconciled; no unavailable gate treated as passed | 2–5; all feasible rows, release acceptance |

## Qualification and failure experiments

1. Independently author bilingual/multilingual alarms, multiple owners with repeated
   names/IDs, unused peers, inherited/literal/mixed/empty/missing fields and exact
   placeholders. Retain originals and expected effects separately from the adapter.
   Check unselected language cells, custom properties, class IDs and entire lists.
2. Exercise text-list Override with a deliberately omitted existing entry and None
   with an existing list. Verify documented replacement/refusal, partial invalid
   range/type/language cases and per-list logs. A partial warning is not full success.
3. Bind every mutation to selected owner, observation, full relevant preimages,
   approved intended delta, source/profile/binary hashes and exact fixture identity.
   Recheck immediately before dispatch. Refuse stale IDs/member types, protected
   objects, wrong PLC/unit or unsupported family without mutating a peer.
4. Create FB and IDB separately; observe chosen numbers and generated dependencies.
   Test assignment on scalar/non-UDT/system/constant members, nested UDT arrays,
   unavailable ProDiag products and actual labels/conditions/delays/categories.
5. Compile appropriate affected scope, record actual diagnostics, save, close and
   freshly reopen. Independently re-export/read every intended and untouched peer.
   A nonthrowing non-Bool XML import or Success flag alone cannot establish effects.
6. Recover a checkpoint and freshly verify the complete preimage. Test interruption
   before dispatch, during import/generation/save, lost response, corrupt receipt,
   partial warning and ErrorRollback. Reconcile observed effects; never replay an
   unknown mutation automatically. Quarantine an uncertain state for explicit review.
7. Validate XML/ZIP/JSON bounds, duplicate identity/columns/languages, Unicode,
   formula/opaque/rich text, hostile paths, missing products/roles and maximum
   retained/paged inventories. Preserve native files exactly before interpretation.
8. Demonstrate alarm triggering/acknowledgement/text substitution independently on
   the chosen runtime, including delayed supervision, reset and reconnect. Offline
   configuration and green compilation do not prove actual alarm behavior.
9. Reuse prior VM/SSH/HMI evidence only for exact proven routes and hashes; do not
   repeat accepted transport qualification as a substitute for D11 tests. Missing
   retained files remain unavailable. Final MCP host/UI/package acceptance is its
   own gate, not inferred from a parser test or raw stdio response.

## Open decisions and next executable work

AL-02's bounded inheritance flow passes frozen aggregate/package and backward
retention checks. AL-03 now accepts supplied UDT/type XML through the existing
saved-review intake and comparison tools, with separate type coverage counters,
opaque supervision differences and explicit HMI→alarm→supervision→member→UDT
impact paths. See the [review workflow](../../guides/project-reviews.md).
This reuses declared graph evidence; it does not extract native relationships,
infer member identities from XML or certify a Bool-to-Int change as a native
breaking change. Final executed checks belong in the verification record.

AL-03/08 now also retains an explicitly selected supplied alarm/supervision/HMI
node and bounded captured ancestry/direct relationships in diagnostic cases and
watch envelopes, with exact review/node/path validation and explicit replan
lineage. [Workflow](../../guides/trace-diagnostics.md#selected-supplied-alarm-context).
This never chooses executable storage or infers a native alarm-to-storage binding.
Missing/ambiguous storage and source identity remain gaps. Next work includes
independent full-chain member/instance/parameter association and genuine provider
identity evidence, plus native type capture, complete inventory, genuine UDT
schemas and lifecycle under AL-01/04–10. These workflows do not close AL-03 or D11.

Current blockers for native rows: installed public metadata, genuine XLSX/DAT/CSV
and XSD versions, exact placeholder grammar, stable cross-session identity evidence,
available target/product/license/rights and independent native/runtime fixtures.
The smallest next native experiment exports one independently authored multilingual
alarm and supervision from a named PLC/unit, records all original files and public
accessor metadata, and reopens/readbacks without mutation; then a copied-project
one-field change measures full-peer preservation. No native execution occurred here.

Known bounded unsupported routes: alarm-instance-text import does not create
instances/classes; UDT supervision is not added post-import through the documented
UDT route; global DB/tag assignment excludes non-UDT instances; system text lists
are not user-list export/edit targets. Other absent APIs or formats remain unknown,
not declared impossible. All feasible AL-01–10 work remains part of 1.0.

[instance]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/export/import-of-alarm-instance-text
[lists]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/export/import-of-plc-alarm-textlists?contentId=Uj~HCXBceURnWIxp6Ea7oQ
[classes]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/export/import-of-alarm-classes?contentId=uIFhxfIuwL~guvUnJIVf0Q
[library]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/managing-alarm-text-list-in-plc-and-mastercopy-library?contentId=p0ner~Ja6ltpUjFrskDGTA
[create]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/creating-a-prodiag-fb?contentId=nowTykeMAjSz1CsHkXinhQ
[access]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/accessing-supervisions-and-properties-of-prodiag-fb?contentId=Ft0SD5LXmiV8uK1oMlWnmw
[assign]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/assigning-prodiag-fb-for-dbs-tag-table?contentId=30BQ3t9OWpW1lJmV7w3v0A
[udt]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/export/import-of-udt-db?contentId=JLORn~Y9lpToUKfauD~69w
[global]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/export/import-of-prodiag-supervisions-from-global-overview-editor?contentId=zkQtf0EpUw71_IemJ7pn1A
[fbglobal]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/export/import-of-global-supervision-for-prodiag-fb?contentId=0PEuaOtBWDNatpZ0WY6Ifg
[settings]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/export/import-of-settings-for-prodiag-supervisions?contentId=9oJxIFgY_ENcCobP44DTYA
[attrs]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/reading-prodiag-fb-blocks-and-attributes?contentId=sJk7WFjLH41mIcGmzSjDHQ
[csv]: https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/exporting-prodiag-alarm-message?contentId=EJJOqxC6VzWw3z5NX7RJjw
