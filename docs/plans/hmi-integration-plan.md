# HMI discovery and engineering integration plan

**SiVArc planning audit — 17 October:** The [dedicated D15 implementation plan](expansion-plans/sivarc-hmi-generation.md) now covers documented rule/definition/library/layout/generation routes, preserving regeneration and unresolved API questions. It reuses this adapter and the existing C01 review; no native capability or qualification is promoted by the plan.

**Saved consumer context — 17 October:** v2 of the [portable C01 review](../reference/hmi-component-review.md#saved-consumer-context-v2) now exposes independently scoped incoming component/HMI relationships, original edges, exclusions and traversal bounds. No supplied mapping or cross-snapshot join creates native dependency evidence. V1 remains readable unchanged; complete dependency/consumer coverage and native acceptance remain open. [Evidence](../verification/hmi-consumer-context-20261017.md).

**17 October portable C01 increment:** [Supplied component-to-HMI review](../reference/hmi-component-review.md) now connects explicit component/interface comparisons to exact saved HMI destinations and independently retained output files. It reports conflicts/gaps without native generation or writes. Full C01 install/regeneration, library identity/dependencies, independent ownership/content completeness and native compile/recovery/visual/runtime acceptance remain open. See [verification](../verification/hmi-component-review-20261017.md).

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

Status: implementation and native qualification in progress; exact delivered scope and unfinished gates are recorded in [HMI integration](../guides/hmi-integration.md) and [qualification](../verification/hmi-qualification.md). Date: 8 October 2026. Initial native platform: TIA Portal V21. The acceptance gates below remain requirements, not blanket support claims.

This is the third workstream in the [graphical engineering roadmap](graphical-project-roadmap.md). It supplies HMI objects and PLC relationships to the [project explorer](project-explorer-plan.md), then adds separately qualified engineering changes. The explorer can ship PLC/hardware inspection before this work finishes.

## Outcome and current boundary

A user can select an HMI device, inspect its screens, tags and exposed dependencies, and trace a supported screen binding through its HMI connection to the exact PLC declaration. Missing coverage stays visible. A later release can prepare and apply a small supported change with native verification and recovery.

The [feature catalog](../../contracts/feature-catalog.v1.json) now records available family APIs, partial HMI inspection/reference and guarded engineering implementations, and separate native qualification. Optional adapters provide HMI v2 identities, resolver support and shared explorer contributions without fabricated PLC ownership. Bounded read-only inspection and actual explorer integration have passed on five exact device/runtime fixtures. Production engineering operations remain unavailable because complete preservation and operation-specific native acceptance have not passed; [qualification](../verification/hmi-qualification.md) records the exact evidence and open gates.

## Scope and ownership

| This workstream owns | Boundary or owner elsewhere |
| --- | --- |
| HMI device classification, capability discovery and family adapters | No universal WinCC adapter or assumed cross-family conversion. |
| Connections, tag tables/tags, screens/items, alarms, scripts and resources where exposed | Exact operations depend on family, device/runtime version and installed options. |
| Screen placement and supported tag, parameter and event binding primitives | Library type creation, release, transfer, version selection and update remain in the [library plan](library-management-plan.md). |
| HMI identities, relationship evidence and source artifacts | Shared explorer/index contracts own navigation, filtering, pagination and presentation. |
| Engineering compile, persistence, preservation and recovery | No runtime deployment, physical download, production communication or commissioning. |

Inspect faceplate containers/instances and referenced type/version information where exposed. The library subsystem supplies a resolved eligible type reference and dependency closure; this adapter supplies an exact screen/container destination and validates binding mappings. Both participate in the same prepared project change. Do not create a second faceplate version-management implementation.

## Native discovery before commitments

Installed V21 XML documentation inspected for this plan exposes `Siemens.Engineering.Hmi.HmiTarget` in `Siemens.Engineering.WinCC.xml` and `Siemens.Engineering.HmiUnified.HmiSoftware` in `Siemens.Engineering.WinCCUnified.xml`. The former lists connection, screen/tag-folder and VB-script access; the latter lists connections, screens, tags, scripts and alarm collections. These are API evidence, not bridge support or successful native execution.

Resolve optional assemblies from the configured installation without bundling Siemens binaries. A PLC-only installation must still start. Record TIA build/update, assembly/document identities, installed engineering options, device catalog identifiers, runtime version and operation-specific license/protection errors.

Probe these targets separately:

| Profile | Discovery decision |
| --- | --- |
| Classic Comfort Panel and RT Advanced | Separate device profiles even when APIs overlap. |
| Basic Panel and RT Professional | Establish actual exposure independently; no inherited support. |
| Unified Comfort Panel and Unified PC | Separate native target/version fixtures. |
| Unknown or inaccessible software | Retain hardware identity and explicit unsupported/blocked coverage. |

Siemens documents a [Unified license check](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/checking-license-to-access-unified-device?contentId=KCwfOy6y5JhD0bV~~5i8EQ), including license exceptions and inaccessible software. Its browse-access wording is inconsistent; verify actual enumeration and lookup independently. Do not promise unlicensed inspection or infer a missing device from a null software object.

For every object family, record supported enumeration, properties, references, export, import, creation, editing, compilation and persistence routes. Public native XML/export or documented metadata are acceptable inspection sources; private project databases and guessed APIs are not. Unsupported export is an explicit limitation, not a fabricated XML document.

## Shared data and implemented routes

The following routes are implemented. Their exact supported subsets and native acceptance gates are recorded separately in the operating and qualification documents; route availability does not qualify every family/profile operation.

| Route | Contract |
| --- | --- |
| `hmi_inspect`, request schema v1 | Exact HMI target and bounded section: devices, connections, tags, screens/items, alarms, scripts or resources. |
| `hmi_references`, request schema v1 | Bounded relationships with source identity, evidence, resolution and completeness. |
| `hmi_capabilities` | Separate optional assembly/version/identity diagnostics, implementation, qualification and selected-target availability. |
| `object_resolve` extension | HMI target/object kinds, negotiated schema version and backward-compatible PLC behavior. |
| Shared project-model provider | HMI metadata and relationships under the explorer's versioned snapshot and cursor policy; retain existing `project_index` semantics and avoid a second independent hierarchy/cache. |
| `change_prepare`, kind `hmi_engineering` | Family-specific allowlisted bodies using existing apply/status/restore workflow; unavailable until preservation and native gates pass. |

An HMI identity includes the exact project copy, session generation, native family, device path, HMI software path, kind and escaped object path. Use native identifiers only when observed and with their documented lifetime. Do not synthesize a PLC owner or treat a display name as persistent identity. Duplicate names require exact selection; rename, move, reopen and retrieval invalidate affected references.

Return per-section `complete`, `truncated`, `unsupported`, `unavailable` or `failed` evidence with reasons and observed revision. A complete tag traversal says nothing about screens. Respect existing traversal, depth, row and byte budgets; load screen details/scripts on demand. Cache plain metadata, never native object proxies. Invalidate known mutations; external edits require fresh verification. Metadata revision is not a whole-project or HMI-content hash.

## Relationships and screen presentation

Build edges for screen item to HMI tag, tag to connection, connection to PLC, and supported symbolic binding to PLC declaration/member. Include alarms, static script references, screen navigation and faceplate interfaces where the native source establishes them.

Every edge carries `nativeConfirmed`, `parsedSupported`, `inferred` or `unresolved` evidence, its source artifact/property and target candidates. Same-name tags are not proof. Dynamic scripts, indirect addresses, incomplete UDT mapping and inaccessible remote PLCs remain unresolved. Unresolved references can be displayed but cannot authorize automatic rewiring.

The first explorer integration is a tree, properties and relationships. A reconstructed screen preview is a separate optional milestone after coordinates, templates, fonts, resources and supported dynamics are understood. Label it approximate unless native rendering is verified. Never execute project scripts to generate an inspection preview.

## Bounded changes after read-only delivery

Select the first mutation from the discovery matrix: preferably one supported scalar property or one existing tag binding. Do not advertise general screen design. Later allowlists can add supported tag creation, screen-item properties, placement and explicit bindings, each with its own fixtures.

1. Resolve the exact target, current content revision and complete affected dependencies; prepare a bounded diff and immutable artifacts.
2. Validate mapping types, connection ownership, referenced resources and affected library interfaces. Reject ambiguous or incomplete required scope.
3. Revalidate and checkpoint the project before mutation; record pre-existing diagnostics and unsaved-state policy in the prepared plan.
4. Apply once, read back native state, compile affected HMI and any changed PLC, and compare intended and unrelated content.
5. Save, reopen in qualification, and retain artifact hashes, diagnostics and the actual result. A timeout remains outcome-unknown; never replay automatically.
6. On failure, retain evidence and use explicit recovery. Qualify archive retrieval into a new owned location and fresh reopen without overwriting the failed source.

PLC XML preservation rules cannot automatically validate HMI content. Establish family-specific comparisons retaining events, bindings, translations, resources, type references and unknown payloads. Any permitted native normalization must be narrowly documented and negatively tested. Where export coverage is inadequate to establish preservation, keep that mutation disabled.

Classic faceplate screen import has native prerequisites and [import behavior](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-an-hmi-device/screens/importing-a-screen-with-a-faceplate-instance). Unified has distinct [container/UDT binding](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/screenitems/controls/faceplate-container/assigning-udt-to-faceplate-container) semantics. Neither route proves general cross-family transfer or exact library-version assignment.

## Work packages and acceptance gates

| Phase | Executable packages | Exit gate |
| --- | --- | --- |
| H0 Discovery | HMI-001 inspect installed docs and assemblies; HMI-002 author family/operation matrix; HMI-003 probe optional installation/license failures | Evidence-backed routes and unsupported cells; no family-wide support inferred. |
| H1 Contracts | HMI-010 identities/resolver extension; HMI-011 versioned inspection/reference schemas; HMI-012 optional adapter loading | PLC-only startup and existing identities remain compatible; ambiguity/stale-copy tests pass. |
| H2 Inventory | HMI-020 target/connections/tags; HMI-021 screens/items; HMI-022 alarms/scripts/resources | Separate Classic and Unified read-only fixtures match native inventories; limits and omissions are reported. |
| H3 Relationships | HMI-030 PLC mapping; HMI-031 navigation/faceplate dependencies; HMI-032 index/explorer integration | Known fixture edges resolve correctly; dynamic and broken bindings remain visibly unresolved. |
| H4 Engineering | HMI-040 first allowlisted change; HMI-041 preservation rules; HMI-042 compile/save/recovery integration | Exact intended effect, unrelated-content preservation, reopen and checkpoint recovery pass per advertised profile. |
| H5 Qualification | HMI-050 failure/compatibility matrix; HMI-051 local/SSH packaging; HMI-052 catalog and operating documentation | Published operation-level evidence distinguishes API, implementation, qualification and target availability. |

H0 starts alongside the explorer's first release. H1 must agree with shared identity/index contracts; H2 can proceed independently by native family. H3 depends on H2 and shared explorer contracts. H4 additionally depends on verified export/preservation and durable recovery; library-dependent changes require the library subsystem's handoff. No read-only milestone waits for typed LAD/FBD builders.

## Qualification and completion

Author fixtures under `fixtures/hmi` and a proposed `scripts/qualify_hmi.py` harness using explicit owned scratch projects and exact worker binaries. Include two distinct HMI devices with repeated member names in permitted separate scopes, multiple PLCs, nested screen items, static and dynamic references, translations, faceplate instances and a broken connection. Where native naming rules reject a collision, retain that rejection as evidence rather than assuming the fixture is legal. Record exact target/runtime versions and unavailable prerequisite outcomes.

For each advertised profile, test inspection accuracy, bounded traversal, missing license/module, protected content, wrong-family selection, stale identity and worker interruption. Mutations additionally require baseline/native readback, compiler acceptance, unrelated-resource comparison, save/reopen, archive retrieval and recovered-project equality. Visually inspect representative changed screens in TIA; compilation alone does not prove appearance or runtime behavior.

Completion means a documented set of HMI families can be browsed and related to PLC objects through both agent tools and the shared explorer, with honest omissions. Read-only completion and engineering completion are separate releases. Mark unavailable qualification as blocked, not passed; retain `runtimeVerified: false` unless a separate explicitly scoped runtime qualification supplies evidence.
