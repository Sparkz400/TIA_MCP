# MCP surface

What the Host advertises over MCP besides its tools' own contracts: tool groups, titles, output
schemas, workflow prompts, progress notifications and its version. The tools themselves are
listed by `feature_catalog`.

## Tool groups (`--toolsets`)

By default the Host advertises every tool, about 290 on a normal build, and the `tools/list`
reply is about 300 KB. Many MCP clients limit the number of tools per server or per request, and every
advertised tool costs model context. Start the Host with `--toolsets` to advertise only the
groups a task needs:

```json
"args": ["--timeout-seconds", "300", "--toolsets", "core,simulation"]
```

The value is a comma-separated list of group names, or `all` (the default). Unknown names stop
the Host at startup with the list of valid groups. Hidden tools cannot be called, and the server
instructions tell the client which groups are advertised. The `base` tools (`feature_catalog`
and the `artifact_*` upload and download tools) are always advertised, and `qualification_*`
tools stay governed by their own qualification flags. A bundled installation passes the same
value with `New-McpConfiguration.ps1 -Toolsets core,simulation` (or `Start-TiaMcp.ps1 -Toolsets`).

| Group | Tools |
| --- | --- |
| `core` | Sessions and projects (`tia_*`, `project_*` except the two groups below), `target_capabilities`, `object_resolve`, the `change_*` workflow, `engineering_*`, block, type, tag-table, hardware, technology-object and cross-reference reads, `plc_compare`, `plc_document_inventory`, `plc_loadable_generate`, `plc_alarm_inspect`, `fixture_sources` |
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
| `reviews` | Supplied-evidence reviews and inspections: `access_*`, `addin_*`, `composite_*`, `connection_*`, `drive_*`, `multiuser_*`, `public_certificate_*`, `certificate_*`, `safety_*`, `software_unit_*`, `technology_profile_*`, `teamcenter_*`, `web_*` |

A Host without native workers (`--no-native-workers` or the portable package) still lists native
tools, which then refuse with `NativeWorkersDisabled`. For supplied-document work there, choose
the document groups, for example `--toolsets opcua,cax,graph,vci,translation,diagnostics,reviews`.
These groups still contain a few native tools, such as `opcua_engineering_export` and
`translation_native_*`.

Every registered tool must belong to exactly one group and have a title; the Host refuses to start
otherwise, so a new tool needs a prefix in `src/TiaMcp.Host/ToolGroups.cs` and a title in
`src/TiaMcp.Host/ToolTitles.cs`.

## Titles and output schemas

Every tool has a short human-readable `title` for client UIs. The 33 workflow tools whose responses are
defined by a contract also advertise an `outputSchema`: the AutomationML, GRAPH, OPC UA NodeSet,
VCI and project-snapshot-import tools. Each schema combines the tool's
`x-tool-contracts` output from [`contracts/`](../../contracts/README.md) with the contract's error
envelope, so both success and refusal results validate; the offline Host tests check every
response against them. Other tools return structured content without a declared schema. The
composite-engineering contract is not advertised yet because it uses `prefixItems`, which
draft-07 client validators reject.

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

## Progress

A `tools/call` request that carries a `progressToken` gets one progress notification before the
tool starts and then one every 10 seconds until it returns. The notifications report elapsed time
only, not a percentage. They let clients that reset their request timeout on progress keep waiting
for long native calls such as compiles, exports or simulator startup. Notifications never follow
the result, and a failed notification never changes the outcome of the call.

## Version

`serverInfo.version` reports the bundled feature catalog version (for example `0.4.70`) until 1.0
chooses a product version; `serverInfo.title` is `TIA MCP`.
