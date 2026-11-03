# HMI engineering and preservation

## Bounded Classic screen editing

`change_prepare` kind `hmi_classic_screen_edit` accepts `classic_xml_import` of one
existing, unlinked regular screen on `classic-rt-advanced`. It uses the existing
change lifecycle and the same payload schema as `hmi_classic_import`. It does not
create screens, edit templates, import tag tables or modify graphic lists.

This is an explicit narrower contract: every regular screen of the selected HMI
is exported before preparation, immediately before applying, after compile/save,
and during recovery. Only the selected screen may differ. Its native readback
must match the prepared XML under the existing Classic import comparison.
A separate `scopePolicy` (`classic-regular-screens-selected-hmi-v1`) participates
in the content hash, so a screen-only hash cannot stand in for a whole-HMI hash.
A failed export or a changed/added/removed peer screen refuses verification.
The stale-change fingerprint binds project path, last save time, unsaved-state flag
and the declared screen hash. It does not fingerprint PLC or hardware payloads,
so an inconsistent unrelated PLC block does not demand a PLC compile for a screen
edit. Those contents are outside this workflow's equality claim.

The preview and manifests list excluded coverage: graphic lists and image content,
tag tables, connections, scripts, text lists, screen templates/popups/slide-ins,
global/overview screens, alarms, libraries and other HMIs. Their references remain
in screen XML, but their content is not compared. `complete` remains false and
`wholeHmiPreservationVerified` is false; a successful scoped comparison is not
whole-HMI preservation, dependency closure or runtime qualification.

Preparation retains the native screen backup and candidate. Apply still requires
`checkpointSave=true`, `compile=true`, `save=true`, the exact preview hash and an
idempotency key. It archives the project, compiles before and after import, rejects
changed compiler errors/warnings, and verifies the target XML and peer-screen preservation after compile and again
after save. The Classic comparator ignores only the observed timestamp/byte-count
header in its exact container position (the original diagnostic evidence is kept).
Real error/warning text, full owner hierarchy and aggregate/target counters must
match. Unknown headers, leaf diagnostics, malformed trees and truncated evidence
remain significant or refuse comparison. Archive
recovery remains restricted to worker-owned TIA instances; project authentication
requirements still apply. Profile-wide native qualification is reported as
unverified; local fixture evidence must be reported separately.

### Screen failure recovery without closing TIA

`hmi_classic_screen_edit` has a bounded in-place recovery path in both attached and
worker-owned projects. After a returned import, readback or compile failure, the
worker attempts **one restoration of the retained pre-import screen**, while it
still holds the original native exclusive-access lease. It does not retry the
candidate. The checkpoint and original XML hashes must match, and every peer
screen must still equal the pre-import capture before restoration starts.
The restored screen is re-exported, the HMI is compiled against the recorded
baseline diagnostics, all regular screens are compared, and the restored project
is saved and compared again. Only then does the journal report `rolled_back` and
permit a new preparation in the same TIA process. The original failure remains in
the response and journal. A verified failure before the import reports
`not_applied`. Missing artifacts, changed peers, incomplete native reads or a
failed restoration remain `recovery_required`; no unrelated edit is discarded.

A client timeout is different from an import failure: TIA may finish after the
client stops waiting. Inspect the retained change journal before taking any next
action. For a terminal `applied` or verified `rolled_back` Classic screen change,
`engineering_admission_reconcile` can run attached to the **still-open original
project**. It rechecks the terminal stages and compiler evidence, retained
checkpoint/input/export hashes, complete declared screen scope and exact project
save fingerprint under native exclusive access. Unsaved edits, changed saved
state, a nonterminal journal or incomplete evidence keep the lock held. The
reconciliation performs no import, save or replay. `ResourceBusy` includes the
blocking admission IDs and the reconciliation tool name. Other operation kinds
retain their existing isolated recovery rules.

For large screen sets, use `--timeout-seconds 900` and a client tool timeout longer
than that (for example, Codex `tool_timeout_sec = 960`). Checkpointing, full HMI
compilation and native preservation exports can exceed five minutes. The final
fingerprint reuses the just-verified saved capture under the same exclusive lease;
it does not repeat another full export with no intervening write. Longer waiting
does not authorize a retry or weaken verification.

The [recovery verification record](../verification/classic-screen-recovery-20261103.md)
separates managed failure checks from the native still-open-project lock-release
test and records remaining evidence limits.

### Preparing the screen change

The `hmi_classic_screen_capture` tool returns the scope hash directly. The
older single-screen route also supports a two-pass preparation pattern: a first
prepare with a deliberately nonmatching 64-character hash returns `StaleRevision`
and the captured baseline, without applying an edit. Reprepare the reviewed
candidate with that baseline's `contentHash`. Use the full current screen identity
from `hmi_inspect`, including project-copy and session fields.

Local checks and native-copy evidence are tracked in the
[3 November verification record](../verification/classic-screen-edit-20261103.md).

### Capture, composition and batching

The [workflow continuation](../verification/classic-screen-workflow-draft-20261103.md)
records native two-screen success, later-member failure with reverse-order
restoration, lost-response reconciliation in the still-open project and a fresh
read-write reopen on an owned RT Advanced fixture. This is bounded fixture
evidence; profile-wide, visual and runtime qualification remain unverified.

1. Call `hmi_classic_screen_capture` with the exact project and HMI identity. It
   returns the regular-screen `contentHash`, native XML artifacts and a hashed
   capture receipt. `includeTags=false` omits structured-tag exports.
2. Optionally call `hmi_classic_screen_compose` with that receipt, `screenPath`,
   and explicit `components`/`removeItems`. Components clone native screen items;
   replacements are limited to existing plain scalar attributes, tag references
   and captions. XML annotations or formatted content refuse replacement.
3. Prepare `hmi_classic_screen_edit` with the capture hash and the usual first
   `target`/`xml`, optionally adding one to seven `additionalScreens` entries.
   Every member must be an existing regular screen of the same HMI, project copy
   and session. Each `xml` may instead be `xmlArtifact: {path, sha256, bytes?}`,
   including the complete artifact reference returned by composition. The optional
   integer byte count must match the retained file. The path
   must be absolute and inside the Host artifact root. Changed bytes, malformed
   references, symbolic links and junctions refuse before native preparation.
4. Review and apply the single prepared change. One project checkpoint and one
   baseline/final compile pair cover the batch. Each attempted member has a
   durable dispatch and readback receipt. Failure restores the attempted prefix
   in reverse order, then verifies the complete declared screen scope and save.

Capture/composition and recovery also refuse linked retained artifacts. Terminal
reconciliation requires every batch member receipt; removing the list cannot
select the legacy single-screen compatibility path. A failed or incomplete
restoration retains ownership.

`change_status(progressOnly=true)` reads diagnostic phase timing without waiting
for the worker. It always reports `outcomeVerified=false` and
`automaticRetryAllowed=false`. Normal status is compact; use
`includeDetails=true` for retained XML and full preservation evidence. Neither
progress nor cached observations authorize retry. Observation reuse ends with
the native exclusive-access lease and is invalidated by imports, compilation
and saves.

## Temporary Classic graphic-list export containment

Classic `GraphicList.Export` is temporarily blocked with `HmiNativeExportBlocked` after
two V21 Update 3 diagnostic reports identified the same `InvalidCastException` in
`HmiOpenLinkService.TryConvertToHmiOpenLink` during a read-only graphic-list export.
The reference conversion attempted to cast `System.String` to `HmiOpenLink`; no
repair or safe list-name discriminator has been natively qualified.

Whole-HMI preservation checks all discovered Classic HMI targets for graphic lists before any
preservation export. A nonempty composition refuses the capture and the change,
including a Unified change in a mixed project. This does not skip lists or accept
partial preservation. Classic graphic-list `hmi_inspect` source exports and the
generic Classic object exporter are blocked too; metadata inspection and Unified
graphic-list exports are unaffected by this particular guard. There is no caller
override. The separate bounded screen workflow never requests graphic-list export.

Each capture writes `capture-progress.jsonl` beside `manifest.json`. Start records
are flushed before native dispatch; completion records follow successful operations.
A missing completion identifies an operation whose result is not established, not
permission to retry it. The manifest uses project identity cached before capture,
retains the first exception and partial artifact set, and never grants a content
hash after failure. Diagnostic write failures are retained separately where possible.
Preparation refuses a failed capture before further target reads or exports.

The offline regression tests cover refusal before dispatch, loss of the project
getter, diagnostic I/O failures, partial-scope rejection and unchanged successful
hash rules. The compiled-worker metadata guard also rejects any remaining direct
Classic `GraphicList.Export` call without loading Siemens assemblies. These checks
contain the known route; they do not qualify a native fix, prove other exports safe,
or reopen this route. Any native investigation must use an explicitly authorized
disposable fixture or project copy.

On 3 November 2026, two explicitly authorized tests on a disposable project copy
reproduced the same Siemens invalid cast using a standalone Openness program,
without the MCP Host or worker: `WithUserInterface` with `ExportOptions.None`, and
`WithoutUserInterface` with `ExportOptions.WithDefaults`. Both terminated only the
test TIA instance. Both diagnostic reports recorded read-only export and
`IsModified=False`. Excluding defaults and running headless are therefore **not
qualified remedies**. These tests did not exercise screen import. The separate bounded screen workflow
now has local native import, compile, save/reopen and authenticated checkpoint
readback evidence in the verification record linked above.

Offline inspection identifies a plausible converter contract violation: the
installed library converter can return a string for a requested `HmiOpenLink`,
while its caller casts the returned value to that reference type. This explains
the observed exception but does not establish a supported connector workaround,
project corruption, or a particular vendor update as a fix. Siemens binaries and
the active connector installation were not changed. Private project data, crash
archives and vendor implementation excerpts stay in local investigation artifacts.

Classic ordinary screen inspection now uses the six public screen scopes and retains optional `screenRole` metadata (`regular`, `template`, `popup`, `slideIn`, `globalElements`, `overview`). `coverage.screenScopes` distinguishes complete, absent, unavailable and truncated scopes. Explorer nodes remain `hmiScreen`; their exact paths separate roles and folders. Additional Classic roles route details to screen metadata, while regular screens retain the existing item route. Added-role item parsing and ordinary source export remain unsupported/unqualified; these observations confer no mutation authority. See [offline verification](../verification/classic-screen-inventory-20261023.md).

The `hmi_engineering` change kind uses the existing `change_prepare`, `change_apply`, `change_status` and `change_restore` lifecycle. Payloads follow [hmi-engineering.v1.schema.json](../../contracts/hmi-engineering.v1.schema.json). This implementation contains family-specific operation bodies and fail-closed preservation gates. An implemented body is not native qualification or current target availability.

Unified text lists are imported by the separate change kind `hmi_text_list_import`, the first HMI write the closed preservation gate admits in production (31 October 2026; natively qualified on `unified-comfort` and `unified-pc`); see [importing a text-list candidate](../reference/unified-text-list-authoring.md#importing-a-candidate-into-a-project).

Several `hmi_engineering` operations have their own change kinds, which take the same payload and fix its family and operation; a mismatch refuses with `InvalidRequest`. Each passed native qualification and production admits it on these profiles ([1 November](../verification/hmi-gate-route-20261101.md), [2 November](../verification/hmi-unified-authoring-20261102.md)):

- `hmi_classic_import`: Classic `classic_xml_import` of a screen or tag table, and the screen `BackColor` `scalar_property_set`, on `classic-comfort`, `classic-rt-advanced` and `classic-basic` (the last two since 2 November, qualified on seeded fixtures without a start screen). Templates, popups, slide-ins, global elements, overview, scripts, lists, cycles and connections stay refused.
- `hmi_external_tag_create`: Unified `external_tag_create` on `unified-comfort` and `unified-pc`.
- `hmi_screen_create`: Unified `screen_create` and `item_create` on `unified-comfort` and `unified-pc` (2 November 2026). Preservation lets only the created node's own typed subtree appear.
- `hmi_dynamization_create`: Unified `dynamization_set` (one new tag dynamization of `ProcessValue`, or `Visible`/`Enabled` by a Bool tag) on `unified-comfort` and `unified-pc` (2 November 2026). A PLC-backed tag needs its proven owner and declaration; a tag without PLC tag, address and a connection of its HMI is internal. Only the dynamization's typed node may appear; the item's node and every other node stay identical.
- `hmi_event_handler_create`: Unified `event_handler_set` (one new script handler of a screen or item, for an event without a handler) on `unified-comfort` and `unified-pc` (2 November 2026). The script is stored with CRLF line ends, the form V21 keeps, and never executed; only the handler's typed node may appear.

Every other profile, operation and Classic object type stays behind the gate; `--hmi-change-qualification <profile>` ([Host options](deployment.md#host-options)) remains for qualification runs and changes no qualified profile's admission. Under kind `hmi_engineering` every operation stays refused. As for the text list, the capture is accepted with exactly the family's standing coverage notes (Classic: one export note per HMI; Unified: the two tag-table and typed-readback notes), and a capture failure, any other note or an HMI of the other family refuses. Apply compiles the HMI after the checkpoint and captures the compiled state (stage `hmi_baseline_compiled`), journals `hmi_native_write_started` before the write, and compares preservation with the compiled capture. The compiler's errors and warnings must stay as the baseline compile found them; `hmi_engineering` keeps requiring a successful compile. Native qualification ran through `scripts/qualify_hmi_gate_route.py` and the `ci-*`/`et-*` scenarios of `scripts/qualify_interruption.py` (`local.hmi.classic-import.route.20261101`, `local.hmi.classic-import.interruption.20261101`, `local.hmi.external-tag.route.20261101`, `local.hmi.external-tag.interruption.20261101`). WinCC RT Professional is not exposed by V21 Openness ([record](../verification/hmi-rt-professional-20261025.md)).

## Implemented candidates

| Family | Operation | Native implementation and boundary |
| --- | --- | --- |
| Classic | `scalar_property_set` | Exact standalone screen `BackColor`, canonical decimal RGB such as `224, 240, 255`. Export the whole screen, patch its exact export-local root/property, import through its original typed screen composition, then re-export and compare all content. Native library type instances are rejected: Classic screen XML alone cannot preserve their library identity. Rename is deliberately outside this scalar allowlist. Change kind `hmi_classic_import` carries this operation on `classic-comfort` (production since 1 November). |
| Classic | `classic_xml_import` | `xml` is a complete native export of exactly the target object: one UTF-8 `Hmi.*` root of the same native type and exported `Name`, no DTD. Targets: screens, templates, popups, slide-ins, global elements and overview (`hmiScreen`), tag tables (`hmiTagTable`), text/graphic lists (`hmiResource`), VB scripts (`hmiScript`), connections (`hmiConnection`) and cycles. Cycles have no inspection kind: address them as `hmiCycle` with objectPath `<hmiPath>/cycles/<escaped name>`, their preservation-manifest key. Prepare retains the current export as backup. Apply imports with `ImportOptions.Override` through the owning composition (`ScreenComposition`, `ScreenTemplateComposition`, `ScreenPopupComposition`, `ScreenSlideinComposition`, `HmiTarget.ImportScreenGlobalElements`/`ImportScreenOverview`, `TagTableComposition`, `TextListComposition`, `GraphicListComposition`, `VBScriptComposition`, `CycleComposition`, `ConnectionComposition`), re-finds the same identity and requires its re-export to equal the supplied document apart from `DocumentInfo`, export-local object IDs and formatting (the PLC import readback rule; IDs are serialization numbers of one export). The whole-HMI preservation check then requires every other export to be unchanged apart from `DocumentInfo/Created`. Library-linked screens and VB scripts are refused. Under `hmi_engineering` the operation is refused for every profile; change kind `hmi_classic_import` admits screens and tag tables on `classic-comfort` (production since 1 November). |
| Classic | Other operation kinds | Explicit unsupported outcome. Typed screen-item and primitive tag creation APIs are absent from the inspected Classic V21 surface; separate preserving XML adapters and dependency qualification remain necessary. |
| Unified | `scalar_property_set`, `item_properties_set` | Exact native item `Enabled`, `Visible`, and bounded `TabIndex`, using native current types and precise readback. |
| Unified | `tag_create` | Exact native tag table; primitive internal tags only; reject duplicate HMI namespace names and verify name/type and absent external connection/PLC tag. |
| Unified | `external_tag_create` | Exact native tag table, exact same-HMI `hmiConnection`, and either an exact root PLC `tag` identity or an exact root global DB identity (`kind: "block"`) with an unquoted `memberPath` such as `["Motor", "Speed"]`. Prepare proves the PLC owner from native `CommunicationManagement` endpoints, plus the exact scalar type; DB members are read from the DB's native export and must be unique with no array on the path. Apply sets `Connection` then `PlcTag` (`DB.Member` spelling), and never writes the natively derived DataType/PlcName. Readback must show `SymbolicAccess`. Arrays, quoted names, whole UDT values, instance/array DBs and software-unit declarations are not mapped. Change kind `hmi_external_tag_create` carries this operation on `unified-comfort` and `unified-pc` (production since 1 November). [Verification](../verification/hmi-external-tag-create-20261026.md) (prepare-only, root tags). |
| Unified | `connection_create` | Target the HMI itself (`kind: "hmiDevice"`), `name`. Prepare requires the name to be new and exactly one valid native `CommunicationManagement` HMI endpoint pair with that `LocalConnectionName` and one partner PLC. Apply calls `HmiSoftware.Connections.Create(name)`; `Partner`, `Station` and `Node` are read-only, so readback must show `Partner` equal to the proven PLC name and a nonempty `CommunicationDriver`. Non-integrated driver/address authoring is not offered. |
| Unified | `object_create` | Target the HMI itself, `composition` (`DiscreteAlarms`, `AnalogAlarms`, `DataLogs`, `AlarmLogs`), `name`, optional `properties` from that family's allowlist. Uses the composition's documented `Create(name)` and sets each property through its typed setter with exact readback. |
| Unified | `object_properties_set` | Exact alarm (`hmiAlarm`), data/alarm log or runtime settings (`hmiModelNode` at its `hmi_inspect` `section=model` path, e.g. `<hmiPath>/runtimeSettings`), `properties`. Allowlists: alarms `RaisedStateTag`, `RaisedStateTagBitNumber` (discrete), `TriggerMode` (discrete), `Condition` (analog), `AlarmClass`, `Priority`, `Origin`, `Area`; logs `Settings.LogMaxSize`, `Settings.StorageFolder`; runtime settings `StartScreen`, `ScreenResolution`. `RaisedStateTag`, `AlarmClass` and `StartScreen` must name an existing tag, alarm class or top-level screen of the same HMI. Text/graphic lists and script modules have no V21 `Create` or writable typed property and are not offered. |
| Unified | `screen_create` | Target the HMI itself (`kind: "hmiDevice"`), new `name`, `properties` with exactly `Width` and `Height`. The size must equal that of an existing top-level screen of the same HMI; no device-resolution table is guessed. Apply calls `HmiSoftware.Screens.Create(name)` and sets both typed setters with exact readback. Screen groups are not searched or targeted. |
| Unified | `item_create` | Exact `hmiScreen`, new `name`, `itemType` (`HmiRectangle`, `HmiText`, `HmiButton`, `HmiIOField`, `HmiBar`, `HmiGauge`, `HmiSlider`, `HmiToggleSwitch`) and a `placement` rectangle wholly within the screen. Apply calls `ScreenItems.Create<T>(name)` and sets `Left`/`Top`/`Width`/`Height` with exact readback. Other item types, faceplates and styling are not offered. |
| Unified | `dynamization_set` | Exact `hmiScreenItem`, one `bindings` entry: `property` (`ProcessValue`, or `Visible`/`Enabled` with a `Bool` tag), exact same-HMI `tagIdentity`, `dataType`. Create-only: refused when the property already has a dynamization. Apply calls `Dynamizations.Create<TagDynamization>(property)`, sets `Tag`, and requires readback of the property, type `Tag`, the tag name and no indirect addressing. PLC-backed tags need the same proven owner and declaration as faceplate bindings. Script, resource-list, flashing and expression dynamizations are not offered. |
| Unified | `event_handler_set` | Exact `hmiScreen` or `hmiScreenItem`, `eventType` (a member of that object's native event enum, such as `Tapped` or `Loaded`) and `script`. Create-only: refused when the event already has a handler. V21 has no system-function list for Unified handlers, so `script` is untrusted raw JavaScript: at most 16384 characters, no control characters except tab/CR/LF, stored verbatim as `Script.ScriptCode` with exact readback. The bridge never executes, evaluates or syntax-checks it. Property-event handlers and custom web/widget container events are not offered. |
| Unified | `item_place` | Existing top-level faceplate container, explicit integer rectangle wholly within its native screen; exact rectangle readback. Nested coordinate systems and faceplate type lifecycle require separate qualification. |
| Unified | `existing_binding_set`, `explicit_binding_set` | Existing faceplate interface and exact same-HMI tag identity; native interface existence, existing-value guard, tag type and ownership checks. A PLC-backed tag (nonempty `Connection`/`PlcTag`) is bound only when the bridge proves its connection's single native endpoint owner, `SymbolicAccess`, and the exact root tag or global DB member declaration and type; every other nonempty connection stays blocked. Caller assertions and matching names never authorize binding. |

No request field can override native qualification or preservation availability. Unknown fields, mixed operation payloads, client qualification flags, stale identities, metadata hashes and incomplete required scope are rejected. Basic, Comfort, RT Advanced, RT Professional, Unified Comfort and Unified PC remain separate profiles; no support is inherited merely from shared API types. Diagnostics retain observed profile, catalog identifier, runtime version and native assembly identity where available.

## Preservation

Classic capture exports every exposed screen, template, popup, slide-in, global element, overview, tag table, connection, cycle, script and text/graphic list through public APIs. Slide-ins have no name and are keyed by `SlideinType`, as in inspection paths. Folder traversal, file count and bytes are bounded. Capture spans all discovered HMI targets, so another HMI with repeated member names is a separate preservation identity. Native failures retain partial artifacts, exact failure information and an incomplete/failed manifest; they never become a complete content fingerprint. Scripts are exported as content and never executed.

The native-export budget is 2048 files, 64 MiB per file and 512 MiB in total across
the project. The XML parser uses the same 64 MiB document bound. This accommodates
the independently observed 49.79 MiB tag-table export; exceeding a bound still
refuses the entire preservation scope. These capacity changes do not repair the
graphic-list export failure or qualify a screen-editing workflow.

Raw SHA-256 is retained for every export. The sole current Classic content normalization of the whole-HMI comparison changes `/Document/DocumentInfo/Created`, with its observed scalar timestamp shape, to a fixed export-time marker. Only the `classic_xml_import` target's own readback uses the broader import rule above. Unchanged V21 Update 2 native exports before repeated read and fresh reopen established that this field is export metadata. No GUIDs, IDs, unknown nodes, events, bindings, translations, library version references, resource content, whitespace or product metadata are removed. Nested `Created` fields remain content. Pure negative tests enforce these boundaries. An unexpected native normalization fails preservation.

Unified tag tables use documented native composition export. Installed V21 metadata exposes no `HmiScreen.Export`. The Unified manifest therefore also records a SHA-256 for every node of the typed object-model walk that `hmi_inspect` `section=model` uses. The keys are the node's inspection path, and each target's `typed-model.json` is retained. The walk covers screens, items, dynamizations, events with hashed script code, parts, translations, tags, alarms, logs, audit trails, runtime settings, groups, lists and script modules. It is bounded to 45000 nodes and 64 MiB; a truncated walk fails the capture. The existing preservation rules decide what may differ:

- `tag_create` and `external_tag_create` (also under `hmi_external_tag_create`): only the target table's export files and the created tag's own node subtree.
- Classic `scalar_property_set` and `classic_xml_import` (also under `hmi_classic_import`): only the target object's own export; every other export and the artifact set must stay identical.
- `connection_create` and `object_create`: only the created object's own new node subtree (for example `<hmiPath>/connections/<name>`).
- `screen_create`, `item_create`, `dynamization_set` and `event_handler_set`: only the created node's own subtree: `<hmiPath>/screens/<name>`, `<screen>/items/<name>`, `<item>/dynamizations/<property>` or `<owner>/eventHandlers/<eventType>`. The owning screen or item node must stay identical.
- `object_properties_set`: only the target node; parts such as log `Settings` are part of that node's readback.
- Item property and placement operations: only the target item's node.
- Binding operations: the target item and its bound `interface` entries.

Every other node and export must be identical. Typed readback cannot observe payloads without a public getter, and failed getters are listed as missing coverage. The manifest therefore stays incomplete and these mutations remain unavailable under `hmi_engineering` until exact-profile native fixtures qualify the coverage; the three separate kinds accept exactly the standing notes instead. This readback is compiled against the V21 PublicAPI only and is not natively qualified.

The Classic HmiTarget surface has no public alarm composition. Full resource and dependency coverage and operation-specific stable exports must still be established before claiming a complete affected preservation scope. An archive is a recovery artifact, not proof that all unrelated native content was compared.

## Change lifecycle and recovery

Preparation validates the exact project/session/HMI/object identity, immutable expected native-content fingerprint, operation shape, dependencies and typed destination, then retains the before exports and intended full-document/scalar effect. It does not obtain write eligibility from explorer metadata. Apply requires `checkpointSave=true`, `compile=true` and `save=true`; the existing lifecycle revalidates target fingerprints and prepared file hashes, durably records the idempotency key, saves current edits and creates a hashed recovery archive before dispatching one mutation.

After native mutation, precise readback, the affected HMI compiler, unrelated-content comparison and save are required. State and artifacts flow through the existing journal. Native failures retain the changed project and archive in `recovery_required` unless the bounded Classic screen recovery above verifies an in-place restoration; a worker interruption has an unknown outcome and is never automatically replayed. General archive recovery retrieves the archive into a new owned location and compares complete recovered HMI content with its recorded baseline before reporting HMI equality. Native qualification must separately prove save/fresh-reopen, recovery/fresh-reopen and visual behavior.

## Library handoff

[hmi-library-handoff.v1.schema.json](../../contracts/hmi-library-handoff.v1.schema.json) is the explicit boundary. The library subsystem supplies an already-resolved source type/version, family, library revision and complete dependency closure. The HMI adapter validates the exact destination, placement, interface/tag mapping and preservation requirements. When a resolved native version is supplied by the library executor, the guard compares its actual type/version GUIDs with the handoff. A handoff remains a prerequisite and cannot override unavailable preservation. Type lifecycle, transfer, release, publication and updates remain in the library subsystem and execute in its existing resource-aware change.

## Validation and acceptance

Operation-specific `requiredGates` also include a clean baseline HMI compile, stale target/dependency rejection, idempotent apply and interrupted-write recovery. Scalar and item updates require exact property deltas; tag creation requires namespace uniqueness and internal-tag isolation; placement requires screen bounds and preserved faceplate identity; bindings require exact typed ownership and either the existing-value guard or dependency closure. Reporting these prerequisites does not qualify or enable any operation.

The scalar patch and sole timestamp normalization accept a single plain XML text node with no attributes. Comments, processing instructions and CDATA inside those fields are rejected because replacing the value would erase unqualified native payload. Such nodes elsewhere remain significant content.

Run `dotnet run --project tests/TiaMcp.Hmi.Engineering.Tests/TiaMcp.Hmi.Engineering.Tests.csproj` for the Siemens-independent request, ownership, strict XML patch/preservation and negative normalization suite. These tests do not qualify native engineering. Native acceptance records belong to `artifacts/hmi-native-*` and the HMI qualification report, with exact binary, catalog/runtime and input/output hashes.

Open engineering gates remain operation-specific: complete preservation/dependency scope, exact scalar or binding effect, all unrelated content, native compilation, save/reopen, archive retrieval equality, representative visual inspection, stale-target behavior and interrupted-write recovery. The capability result continues to publish `engineeringVerified`, `preservationVerified`, `persistenceVerified`, `recoveryVerified` and `runtimeVerified` separately. Missing or blocked evidence is never a passing gate.
