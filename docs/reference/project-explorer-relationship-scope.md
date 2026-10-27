# Explorer relationship scope and traversal contract

A graph reports completeness only for the requested traversal over its captured edge records. `relationshipScope.projectCompleteness` is always `notEstablished`. A complete hierarchy, an empty graph, or a complete bounded traversal does not establish whole-project relationship completeness. Native acquisition, provider scope, retention, endpoint resolution and graph traversal remain separate evidence.

## Advertised providers

| Provider / evidence source | Relationship kinds and exact scope | Explicit exclusions / qualification |
| --- | --- | --- |
| `nativeRelationships` / `nativeCrossReference` | `calls`, `reads`, `writes`, `instanceOf`, `usesType`, `dependsOn`; native cross-reference observations in the selected PLC/software scope, projected onto exact captured node identities | Protected, indirect, unobserved and missing endpoints remain unresolved. Reference kind/access and declaration identities govern edges; names do not establish identity. This change adds no native qualification. |
| `configuredIo` / `nativeConfiguredAddress` | `configuredIo`; selected tag address ranges or selected hardware against captured I/O-tag candidates grouped by PLC | Requires exact hardware endpoint and selected-PLC controller ownership. Address overlap alone does not prove physical channels, wiring, cables or topology. |
| `hmi.relationships` | `hmiBinding`; explicit native configured bindings, alarms and retained dynamic observations; maps native screen-navigation observations to `screenNavigation` and faceplate type-version observations to `libraryVersion` when present | Family-specific semantics and exact captured endpoints apply. Dynamic scripts and unknown/indirect bindings remain unresolved; no script execution or generic navigation parser. See [HMI evidence](hmi-relationship-evidence.md). |
| `portInterconnections` | `connectedPort`; `NetworkPort.ConnectedPorts` of a selected port device item, resolved to exact captured port items through `DeviceItemFeature.OwnedBy` and engineering-object equality | Undirected, endpoints in ordinal path order. Configured topology only, not cables or link state. Not natively qualified. |
| `plcLibraryInstances` | `libraryVersion`; captured FB/FC/PLC-type `LibraryTypeInstanceInfo` GUID pairs to captured project-library version nodes | Exact type and version GUID match only; a partial or absent library capture leaves the edge unresolved. No version dependencies or global libraries. Not natively qualified. |
| Saved-model relationships | Preserves supplied kinds, exact endpoints and original evidence; supports all graph kind filters | This reader does not qualify the producer. `contains` is accepted if supplied; native containment is normally the tree and is not synthesized into graph edges. PLC library instance/dependency metadata is not automatically promoted to complete `libraryVersion` graph closure. |

`nativeHierarchy`, general HMI inventory and `typedLadFbd` preview coverage do not certify the relationship providers. A provider's `complete` status applies only to its recorded scope. The Relationships panel shows provider scopes, native coverage and the selected traversal contract; SVG and PNG manifests retain them.

## Independent budgets

| Layer | Limits |
| --- | --- |
| Saved JSON reader | 50,000 nodes, 50,000 edges, depth 64, total 32 MiB. The edge input limit now matches the existing schema/C# snapshot limit. |
| Native observation retention | Unchanged: at most 1,000 edges per observation and 1,000 accumulated relationship observations per snapshot. Export/recapture a narrower scope when full. |
| Graph query | Depth 1–5, up to 500 nodes and 1,000 edges; defaults depth 1, 100 nodes, 200 edges. |
| Graph bytes | At most 512 KiB including envelope, coverage and the additional unresolved-evidence array. C# reserves 64 KiB for native adapter metadata; its detached base graph stays below 448 KiB. |
| PLC native relationship acquisition | `maxVisited` up to 2,000; separate endpoint identity traversal `maxIdentityVisited` defaults 5,000 and caps at 50,000. These limits do not become graph-response limits. |
| Hardware relationship acquisition | `maxIoTags` defaults 200/caps 2,000, `maxIoPlcs` defaults 10/caps 32, native visits cap at 2,000 per PLC. |
| HMI Explorer acquisition | Requests a 200-row, 256 KiB response from the existing bounded HMI provider. Its native capture/export/traversal budgets and unresolved sidecars remain separate; see the HMI evidence reference. |

`relationshipScope` is additive to v1 responses and retained provider coverage. It includes `scope`, `status`, `retainedEdges`, `returnedEdges`, `direction`, `kinds`, and `stopReasons`. Retained provider coverage also stores the seed and requested limits. `retainedEdges` counts the source document's edge records, including disconnected or unanchored observations; `returnedEdges` counts the current selected traversal. A native query can construct its source document from a fresh provider read before retention.

Stop reasons are `depth`, `nodes`, `edges`, `bytes`, `coverageBytes` (portable response omits oversized coverage, with the original snapshot retained) and `adapterBytes` (native response diagnostics were bounded). The seed is preserved; an envelope too large for the requested budget is refused. Existing `truncated` and `continuationAvailable` remain expansion hints, not pagination cursors or proof that automatic continuation exists. Narrow filters, increase budgets within ceilings or select an adjacent object.

Directed traversal follows only matching kinds and direction. Cycles terminate. Distinct edge IDs between the same endpoints preserve independent observations, including self-loops; duplicate edge IDs, mismatched exact paths and missing endpoints without explicit unresolved evidence are refused. Unsupported/unknown kinds in a filter yield an empty scoped result without proving absence elsewhere. Fully unanchored records cannot be reached from a node and stay in the source document.

When a native observation retains only a filtered/bounded/disconnected query subset, its saved provider status is downgraded from `complete` to `partial`; previous edge evidence is not rewritten to claim more coverage. HMI retention annotates only `hmi.relationships`, never unrelated hierarchy coverage. Older valid v1 snapshots need no new field and continue to open; missing scope metadata is explicitly identified by the viewer.

## Reproducible qualification

See [relationship qualification](../verification/project-explorer-relationship-scope-20261021.md). The expected small-graph edge sets are authored independently of either C# or JavaScript traversal. The C# store exports a real retained model which the JS tests and browser file picker reopen. Scale fixtures use actual 1k/10k/50k nodes **and edges**, with an independently expected 500-node/1,000-edge connected component plus disconnected edges, and a separate large connected graph that must visibly truncate. Native scale and ChatGPT desktop-host qualification remain separate.
