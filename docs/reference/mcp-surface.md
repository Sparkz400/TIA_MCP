# MCP surface

What the Host advertises over MCP besides its tools' own contracts: tool groups, parameter
conventions, titles, output schemas, workflow prompts, resources, completions, logging, progress
notifications and its version. The tools themselves are listed by `feature_catalog` and in the
[tools reference](tools.md); the [0.9.0 migration](#migration-to-090) maps retired names.

## Tool groups (`--toolsets`)

Every tool belongs to one group. Without `--toolsets` the Host advertises the `base` and `core`
groups only. All groups, 306 tools on a default Host (the catalog has 333; the rest need a Host option such as `--physical-plc-online`), make the `tools/list` reply about 250 KB
(about 700 KB before the 0.9.0 redesign, when large [output schemas](#titles-and-output-schemas) were inline), and
every advertised tool costs model context (many MCP clients also limit the number of tools per server or
per request). Start the Host with `--toolsets`
to name the groups a task needs, or `--toolsets all` for every tool:

```json
"args": ["--timeout-seconds", "300", "--toolsets", "core,simulation"]
```

The value is a comma-separated list of group names, or `all`; the default is `core`. Unknown names stop
the Host at startup with the list of valid groups. Hidden tools cannot be called, and the server
instructions tell the client which groups are advertised. The `base` tools (`feature_catalog`, `host_build_identity`,
the `artifact_*` upload and download tools and the `review_*` retained-record tools) are always advertised, and `qualification_*`
tools stay governed by their own qualification flags. A bundled installation passes the same
value with `New-McpConfiguration.ps1 -Toolsets core,simulation` (or `Start-TiaMcp.ps1 -Toolsets`).

| Group | Tools |
| --- | --- |
| `core` | Sessions and projects (`tia_*`, `project_*` except the two groups below), `target_capabilities`, `object_resolve`, the `change_*` workflow, `engineering_*`, block, type, tag-table, hardware, technology-object and cross-reference reads, `plc_compare`, `plc_document_inventory`, `plc_loadable_generate`, `plc_alarm_inspect`, `fixture_sources`, and the native reads `safety_inspect`, `access_inspect`, `connection_inspect`, `web_inspect`, `certificate_inspect` and `drive_observe` |
| `explorer` | `project_model*` explorer snapshots and `project_snapshot_*` saved reviews |
| `simulation` | `sim_*`, `simulation_*`, `plc_online_compare`, `plc_station_upload`, `db_snapshot_*` |
| `runtime` | `runtime_*` OPC UA, web and capture sessions, `device_*` network inventory |
| `library` | `library_*` |
| `hmi` | `hmi_*` |
| `watch` | `watch_*` |
| `diagnostics` | `diagnostic_*`, `trace_*` |
| `testsuite` | `testsuite_*` |
| `translation` | `translation_*` |
| `opcua` | `opcua_*` NodeSet and server-interface tools |
| `vci` | `vci_*` |
| `cax` | `automationml_*`, `cax_export` |
| `graph` | `graph_*` |
| `base` (always) | `feature_catalog`, `host_build_identity`, `artifact_*`, and `review_list`, `review_reopen`, `review_page`, `review_artifact_read` for the retained records of the `automationml`, `composite_engineering`, `graph`, `opcua`, `runtime_handoff`, `translation` and `vci` workflows |
| `physical` | `plc_physical_*`: route listing, online compare, station upload and download to a real PLC. Registered only when the Host runs with `--physical-plc-online` |
| `reviews` | Supplied-evidence reviews and the other inspections: `access_*`, `addin_*`, `composite_*`, `connection_*`, `drive_*`, `multiuser_*`, `public_certificate_*`, `certificate_*`, `safety_*`, `software_unit_*`, `technology_profile_*`, `teamcenter_*`, `web_*` |

A Host without native workers (`--no-native-workers` or the portable package) still lists native
tools, which then refuse with `NativeWorkersDisabled`. For supplied-document work there, choose
the document groups, for example `--toolsets opcua,cax,graph,vci,translation,diagnostics,reviews`.
These groups still contain a few native tools, such as `opcua_engineering_export` and
`translation_native_*`.

`testsuite_style_job_execute_next`, `testsuite_style_job_recover` and `testsuite_style_job_reconcile`
are in no selectable group: they refuse every call until a Test Suite style profile is qualified, so no
`--toolsets` value advertises them. A Host started with `--enable-safety-changes` admits the safety writes
(`change_prepare` kinds `safety_import_xml`, `safety_fparameter_set`, `safety_drive_tags` and a safety tag rename);
without it they are refused with `SafetyChangesDisabled`.

Every registered tool must belong to exactly one group and have a title; the Host refuses to start
otherwise, so a new tool needs a prefix in `src/TiaMcp.Host/ToolGroups.cs` and a title in
`src/TiaMcp.Host/ToolTitles.cs`.

## Parameter conventions

- **Paging:** `offset` (0-based) and `count` (maximum items in this reply); a reply that has more
  returns `nextOffset`, null on the last page. No tool takes a `cursor` or `pageSize` any more: every
  former cursor only encoded an offset into an immutable record or snapshot, whose handle already
  binds the page. Caps that bound a single reply without continuation keep their own names
  (`maxItems` on the native inspections, `maxBlocks`, `maxDepth` and the like); the
  `project_model` request object keeps its own `maxItems`/`cursor` fields.
- **Paging totals:** every item pager also returns `totalItems` and `pageCount` beside its own total
  field, which keeps its name and meaning (`total`, `totalRows`, `totalMatches`, `totalChanges`,
  `totalFindings`, `totalObservedHits`, `totalCaptured`, `totalMappedTargets`, `matchedBlocks`,
  `snapshot.totalRows`; `hmi_references` had none). `totalItems` is the same number;
  `pageCount` is `ceil(totalItems / count)` at the request's effective `count` (or `maxItems`,
  `maxBlocks`, `limit`), 0 for an empty result. A byte budget can end a page before `count` rows,
  so `pageCount` is a lower bound on the calls needed and `nextOffset` stays the continuation
  signal. Where the family's total is unknown or partial, both are null and the family's own
  completeness fields say why: `project_snapshot_search` at its 10,000-hit cap (`truncated`),
  `library_query` with `completeness.truncated`, `hmi_references` without `coverage.scopeComplete`,
  `device_inventory` with `coverage.truncated` and `block_export` without
  `completeness.inventoryComplete`. `hmi_inspect` returned both fields before this rule and keeps its
  meaning: the rows of the captured section, so a truncated capture (`coverage.captureComplete`
  false) still counts what it holds. Byte-range readers (`artifact_read`, `review_artifact_read`,
  `vci_file_read`, `artifact_upload_chunk`) page bytes, not items, and report `bytes` and
  `complete` instead; `sim_area_*` `offset` is an address, not a page.
- **Handles:** a retained record is addressed by `recordRef`, an upload by `uploadRef` (`upload:<id>`,
  returned by `artifact_upload_begin` and accepted by every upload tool and every consuming tool once
  committed). Receipts that predate the 0.9.0 redesign keep their stored `recordId` field; pass its value as
  `recordRef`.
- **Names:** a tool that pages a retained review is `<family>_review_page`; `*_report` names remain
  only on tools that export a report artifact (`diagnostic_*report_export`).
- **Limits of the record tools:** `review_list` 1..100 records, `review_page` 1..200 rows within
  128 KiB, `review_artifact_read` 1..65536 bytes, for every record kind.

## Titles and output schemas

Every tool has a short human-readable `title` for client UIs. Tool descriptions stay at most 600
characters (checked by the managed tests); the longer notes are in [tool notes](tool-notes.md).

On a normal build 76 tools declare an output schema, 6 of them inline. Each schema is `anyOf` the tool's result and
its refusal, so both validate: a refusal is `{"error": {"code", "message", ...}}` with `isError`,
described by the contract's own error definition or the Host's generic one. A schema of at most
1 KiB (compact JSON) is advertised inline as `outputSchema`; a larger one, and every `review_*` result,
is served as the resource `tia://output-schemas/{tool}` that the tool names in
`_meta["tiaMcp/outputSchema"]` (for `review_*` the template `tia://output-schemas/review_page.{kind}`).
Clients that validate results read it once. The schemas come from three sources:

| Source | Tools |
| --- | --- |
| A contract's `x-tool-contracts` output in [`contracts/`](../../contracts/README.md) | AutomationML, GRAPH, OPC UA NodeSet, VCI, `project_snapshot_import`, `runtime_ua_subscription_*`, `diagnostic_watch_plan` |
| A supplied-evidence review contract's `receipt` and `page` definitions | The review tool and its page or report tool of the access, Add-In selection, connection, DB snapshot, HMI component, multiuser, public certificate, safety evidence, software unit, Teamcenter, technology profile and web application reviews; a page tool may also return the receipt for its summary section |
| The C# result type, declared with the SDK's `OutputSchemaType` and generated by it | `artifact_read`, `artifact_upload_*` except release, `runtime_ua_connect/status/disconnect/read/browse/credential_binding`, `runtime_web_connect/status/disconnect/read/diagnostics/credential_binding`, `testsuite_style_plan_review`, `testsuite_style_job_status/stop_request/mark_interrupted` |

Each contract-derived schema carries only the definitions it reaches. The Host refuses to start if
one would reference another contract file, or if an inline one would use `prefixItems`, which
draft-07 client validators reject. The composite-engineering and runtime-handoff record contracts
use it and are served only as resources; the diagnostic UA binding/observation/comparison contracts
reference other files and declare no schema yet. `translation` records have no result contract. `drive_profile_*` answers in two
contract versions and has no schema either. Tools that pass native worker results through, and
other results without a contract or a result type, return structured content without a declared
schema.

The offline tests check the schemas against real responses: `scripts/smoke_mcp.py`, the client of
the Host harnesses, validates each structured `tools/call` result, success or refusal, against the
schema the tool advertises inline or names in `_meta`.

## Prompts

The Host offers four workflow prompts. Each lists the documented tool sequence and asks the client
to wait for the user's approval where a step changes something. A prompt is only listed when the
groups of all the tools it names are advertised.

| Prompt | Arguments | Needs groups |
| --- | --- | --- |
| `safe_change` | `goal`, optional `project` | `core` |
| `project_health` | optional `project` | `core`, `explorer` |
| `block_test` | `behavior`, optional `project` | `core`, `simulation` |
| `diagnose_fault` | `symptom` | `explorer`, `diagnostics` |

## Resources

The Host lists read-only resources over data its base tools already return. They need no native
worker and are listed whatever `--toolsets` selects.

| URI | Content |
| --- | --- |
| `tia://feature-catalog` | Every catalog feature with its tools, implementation and qualification status (JSON) |
| `tia://feature-catalog/{featureId}` | One feature with its criteria and evidence, as `feature_catalog` returns it |
| `tia://artifacts/{+path}` | A file under the artifact directory, by path relative to it, at most 4 MiB; JSON, XML, CSV, Markdown, HTML and text files as text, others as bytes |
| `tia://contracts` | The shipped contract files and the change-kind index |
| `tia://contracts/{name}` | One contract by file name, for example `hmi-inspection.v1.schema.json` or `change-kinds.v1.json` (every change kind with its documented payload fields, generated by `scripts/change_kinds.py`) |
| `tia://project-reviews/{reviewId}` | A saved `project-review.v1` document, at most 4 MiB, verified against its content-hash ID |
| `tia://change-payloads` | Every `change_prepare` kind with the URI of its payload schema |
| `tia://change-payloads/{kind}` | The JSON Schema of `change_prepare.payload` for one kind, self-contained; for HMI, library and typed graphical kinds it names the contract and JSON pointer that define the payload |
| `tia://output-schemas/{tool}` | The complete output schema of a tool that does not carry it inline, or `review_page.opcua` style for a record tool and kind |

Artifact and review reads go through the same checks as `artifact_read`: the file must lie under
the artifact directory and no part of its path may be a link or junction. Larger files are read in
chunks with `artifact_read`. The payload schemas come from
[`change-payloads.v1.schema.json`](../../contracts/change-payloads.v1.schema.json), which was derived from the
worker's payload parsers and checked against them; the worker stays authoritative and also checks what a
schema cannot express (native identities, target-dependent allowlists, blank strings). A missing file, review or feature is `ResourceNotFound`. The project
explorer view (`ui://tia-project-explorer/view.html`) is listed only to clients that negotiate MCP
Apps support.

## Completions

`completion/complete` suggests up to 100 values for the resource template parameters: contract file names, feature IDs
from the bundled catalog, artifact paths from a bounded walk of the artifact directory that skips
links, junctions and hidden files, the IDs of saved reviews, contract names, change kinds and the tools with an
output schema resource. It lists names only, never file
contents. Prompt arguments are free text, so they get no suggestions; the Host keeps no list of
open projects without asking the native worker.

## Logging

The Host advertises the `logging` capability. After a client sends `logging/setLevel`, the Host's
log records at or above that level are also sent as `notifications/message`, with the record's
category as `logger`; stderr keeps every record as before. Records below Information are never
sent, whatever the level: the SDK's debug and trace records can carry request and result payloads.
Exception details are dropped for the same reason. The records name methods, tools and resource
URIs and say whether a call failed; they never contain tool arguments. Native worker output
(`[V21] ...` lines and worker exit receipts) stays on stderr only, because the Host does not author
that text and cannot vouch that it holds nothing sensitive.

## Progress

A `tools/call` request that carries a `progressToken` gets one progress notification before the
tool starts and then one every 10 seconds until it returns. The notifications report elapsed time
and, for tools that report one (`project_snapshot_capture`: hierarchy, block n of N, verification), the
current stage; not a percentage. They let clients that reset their request timeout on progress keep waiting
for long native calls such as compiles, exports or simulator startup. Notifications never follow
the result, and a failed notification never changes the outcome of the call.

## Durations

Every tool result carries the whole call's wall-clock time, Host queueing included, in
`_meta["tiaMcp/durationMs"]`. It sits in `_meta` so that declared output schemas stay unchanged. Change
results also carry `stageTimings` with each worker phase's `elapsedMs`.

## Version

`serverInfo.version` is the product version, set once as `<Version>` in
[`Directory.Build.props`](../../Directory.Build.props) (pre-1.0, for example `0.9.0`); `serverInfo.title`
is `TIA MCP`. The bundled feature catalog version, the data contract version that `feature_catalog`
also returns, is advertised as `capabilities.experimental["tiaMcp/featureCatalog"].version`
(for example `0.5.0`). Release bundles record the product version in `manifest.json`
([releasing](../guides/releasing.md)). From 1.0 the [contract freeze policy](../plans/contract-freeze-policy.md)
says which parts of this surface are frozen; the frozen part is recorded in
[`contracts/mcp-surface.v1.json`](../../contracts/mcp-surface.v1.json) and every error code with its class in
[error codes](error-codes.md).

## Migration to 0.9.0

The surface redesign of 0.9.0, the first tagged release (feature catalog `0.5.0`), is a breaking change without
aliases: retired names are not callable. The feature catalog records every retired name in
`toolRenames`, and the catalog's claim checks count evidence recorded under a retired name for its
replacement; no evidence was rewritten and no capability changed its qualification.

| Retired tool | Replacement |
| --- | --- |
| `automationml_list`, `composite_engineering_list`, `graph_list`, `opcua_list`, `runtime_handoff_list`, `translation_list`, `vci_list` | `review_list` with `kind` (translation: `recordType` replaces its former `kind`) |
| `automationml_reopen`, `composite_engineering_reopen`, `graph_reopen`, `opcua_reopen`, `runtime_handoff_reopen`, `translation_reopen`, `vci_reopen` | `review_reopen` with `kind` |
| `automationml_report_page`, `composite_engineering_page`, `graph_report_page`, `opcua_report_page`, `runtime_handoff_page`, `translation_review_page` | `review_page` with `kind`; `section` is optional where the family had a default |
| `automationml_artifact_read`, `composite_engineering_artifact_read`, `graph_artifact_read`, `opcua_artifact_read`, `runtime_handoff_artifact_read`, `translation_artifact_read`, `vci_artifact_read` | `review_artifact_read` with `kind`, `recordRef` and `name` (OPC UA and translation formerly took one `artifactRef`, which is `recordRef:name`) |
| `safety_evidence_report` | `safety_evidence_review_page` |
| `technology_profile_report` | `technology_profile_review_page` |
| `project_health_report` | `project_health_review` |
| `addin_selection_page`, `multiuser_observation_page`, `teamcenter_observation_page` | `addin_selection_review_page`, `multiuser_observation_review_page`, `teamcenter_observation_review_page` |

| Tool | Retired parameter or field | Replacement |
| --- | --- | --- |
| The 13 `*_review_page` tools, `hmi_text_list_author`, `hmi_text_list_generate_set` | `recordId` | `recordRef` (receipts still return `recordId`) |
| `artifact_upload_chunk/status/commit/abort/release` | `uploadId` (bare ID) | `uploadRef` (`upload:<id>`); `artifact_upload_*` return `uploadRef` instead of `uploadId` |
| `library_package_stage` | `artifactRef` | `uploadRef` |
| `feature_catalog`, `project_snapshot_compare`, `project_snapshot_search`, `project_health_review` | `maxItems` | `count` |
| `review_page` (formerly `opcua_report_page`, `translation_review_page`), `translation_inspect`, `device_inventory` | `pageSize`, `cursor`; reply `nextCursor` | `count`, `offset`; reply `nextOffset` |
| `review_list` | per-family maximum 20 or 50 | 100 for every kind |
| `review_page` | OPC UA 500 and translation 1000 rows | 200 rows for every kind |

Clients that validated results against an inline `outputSchema` read the larger schemas from
`tia://output-schemas/{tool}`. MCP Apps `_meta.ui.visibility` entries are sent only to clients that
negotiate MCP Apps.
