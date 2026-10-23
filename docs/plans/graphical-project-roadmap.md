# Graphical engineering and project explorer roadmap

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

Status: implementation and qualification roadmap. Date: 8 October 2026. Initial platform: TIA Portal V21. Each workstream records implementation separately from its exact native and runtime qualification; these plans do not imply broader feature-catalog claims.

### Active typed-builder implementation coordination — 8 October 2026

Typed release resources are now required by `Verify-TiaMcpBundle.ps1`: the four
typed schemas, instruction catalog and dependent-instance effects manifest.
The deployment/guest synthetic bundle fixtures include these resources; native
profile source hashes remain checked by the typed loader. This additive packaging
contract shares the existing bundle verifier with the explorer and HMI work.

The LAD-010–070 implementation uses `graphical_typed_edit` through the existing
`change_prepare` lifecycle and `engineering_read` with `scope: "graphical"` for
read-only network data. The instruction graph is schema version 1, separate from
the explorer node/relationship envelope. Exact canonical block/scope identities
cross that boundary; logical instruction keys never become project identities.
Resolution is performed from fresh native declarations during preparation.
Prepared artifacts, declaration effects and dependency exports are hashed and
rechecked before checkpointing. Library type/version selection remains owned by
the library workstream; this layer accepts exact instantiated PLC identities.

Concurrent ownership: typed model/readers/emitters in new `TypedGraphical*` files;
native corpus and qualification runners under `fixtures/typed-graphical` and
`scripts/qualify_typed_graphical.py`; contract schemas and independent behavioral
expectations in their own files; the coordinator alone integrates shared worker
change hooks and capability documentation. Native fixture runs use separate owned
scratch projects and sessions. Implementation and each engineering, preservation,
persistence, recovery and runtime gate are reported separately; this entry does
not promote any qualification claim.

Split the work into **three major tasks**. Typed LAD/FBD authoring, project exploration, and HMI integration have different native adapters and acceptance requirements. They share object identities, evidence, and existing change management. A shared contract milestone connects them without creating a fourth subsystem.

| Task | User outcome | Detailed plan |
| --- | --- | --- |
| Typed LAD/FBD builders | Create and change supported graphical logic using contacts, coils, branches, comparisons, timers, counters, and calls without supplying native XML by hand. | [Typed LAD and FBD builders](typed-lad-fbd-builders-plan.md) |
| Project explorer | Browse and search the project, inspect software and hardware, and follow references through a visual interface and equivalent agent queries. | [Project explorer](project-explorer-plan.md) |
| HMI integration | Inspect supported HMI objects and their PLC bindings, then add qualified editing operations through the same recoverable workflow. | [HMI integration](hmi-integration-plan.md) |

The [library management plan](library-management-plan.md) remains a separate existing workstream. It owns library intake, types and master copies, version lifecycle, publication, and cross-component library integration. The three plans here consume its capabilities when necessary; they do not duplicate library management.

## What exists and what must be added

The bridge already provides exact object selection, bounded project metadata snapshots, software/hardware inspection, call/reference analysis, raw graphical network edits, and preview/checkpoint/compile/readback/recovery. Those are reusable foundations.

The [unified explorer](../guides/project-explorer.md) now implements E0–E4: native hierarchy, exact structured queries, a read-only virtualized viewer, bounded evidence relationships, portable exports and HMI/typed preview provider contracts. Its [qualification record](../verification/project-explorer-qualification.md) identifies the actual native fixtures, packaged-binary checks, performance measurements and pending remote/desktop-host gates. HMI content coverage comes from the HMI provider's family-specific evidence; generic device inventory alone is insufficient. The project index describes selected metadata, not an authoritative revision of all project contents. See [project index](../guides/project-index.md), [object identities](../reference/object-model.md), [engineering coverage](../reference/engineering-coverage.md), and [selected preservation evidence](../verification/engineering-gap-qualification.md).

The typed builder workstream now has an [implemented contract and native adapter](../guides/typed-lad-fbd-builders.md) across LAD-010–070, including stateful families and explicit storage/dependency effects. Native and runtime qualification is still tracked per exact operation/target. The explorer can consume `engineering_read` with `selectors.scope=graphical`; its hierarchy/relationship schema remains separate. Selected-network topology changes expose native UId reallocation in the prepared effect; unrelated XML and IDs remain subject to the strict existing comparison.

## Ownership and integration boundaries

| Concern | Primary owner | Contract used by other tasks |
| --- | --- | --- |
| Project hierarchy, search, selection, details, reference views | Explorer | Versioned nodes and relationships with exact owner identities, coverage, freshness, and source evidence. |
| Instruction semantics, operand types, wiring, instance requirements, native serialization | LAD/FBD builders | Readable network representation and an immutable proposed edit passed to existing change management. |
| HMI family detection, objects, bindings, export, compilation, supported edits | HMI integration | HMI identities and relationship contributions; capabilities per installed family and target. |
| Library types, versions, master copies and updates | Existing library workstream | Exact source version and instantiated-object identities supplied to PLC/HMI integration primitives. |
| Checkpoints, stale-change rejection, journals, recovery | Existing engineering foundation, extended by the task that needs it | One shared change lifecycle; a UI or adapter must not create a second mutation path. |

The explorer's common graph describes containment and engineering relationships. A LAD/FBD network has a separate typed representation for instruction pins and execution semantics. A screen preview has another family-specific representation. They reference the same exact engineering objects without forcing all native content into one generic graph.

The initial explorer is read-only. A later edit action selects the object and prepares a change through the existing workflow. It does not write directly through a browser endpoint. Automation authorization follows the user's request and current session; introducing a viewer does not add a mandatory confirmation to every read or operation.

## Shared contract milestone

Complete these decisions before connecting the three implementations:

1. Keep canonical paths scoped by their actual project copy and owning device/software container. Preserve current PLC identity semantics. Design a versioned extension for HMI ownership rather than inventing a PLC path for an HMI.
2. Separate an object's identity from its current metadata, export/content fingerprint, and snapshot lifetime. Renames and archive retrieval can change paths. A fresh metadata index cannot certify unchanged PLC logic or HMI screens.
3. Return bounded children and relationships with continuation information, query scope, source, timestamp, and explicit incomplete or unresolved results. Give structural display nodes a distinct kind; they are not native objects that an agent can mutate.
4. Define relation semantics such as contains, calls, reads, writes, configured I/O association, and HMI tag binding. Each adapter provides only relations it can substantiate. Unresolved endpoints remain visible and cannot silently become exact links.
5. Reuse the distinction between vendor API exposure, bridge implementation, recorded qualification, and current target availability. A visible object can still have unsupported operations.
6. Specify how new schemas, fixtures and UI resources enter release bundles. Preserve existing MCP callers; introduce a new contract version when field or identity semantics change.

Acceptance: one shared example dataset containing duplicate PLC names/scopes, a hardware hierarchy, an HMI placeholder with explicit unsupported content, an unresolved reference, and an expired snapshot can be consumed consistently by all three plans. This contract fixture proves integration rules, not native Siemens support.

## Delivery sequence

The priority is an early useful explorer while developing authoring and HMI discovery in parallel. Native operations on a worker remain serialized; separate engineering runs use explicitly owned isolated projects and sessions.

| Delivery stage | Explorer | LAD/FBD builders | HMI integration | Exit condition |
| --- | --- | --- | --- | --- |
| Foundation | Define hierarchy/query model and display states. | Define typed network/profile model and preservation boundary. | Inspect installed family APIs and identify an executable fixture target. | Shared identities, evidence and capability contracts agreed; unsupported cases represented. |
| First useful release | Read-only PLC/hardware tree, search, details, source links and freshness. | Boolean LAD/FBD generation and operand changes for an exact supported profile. | Read-only device/software discovery and bounded content inventory for the first qualified family. | Each released slice has its own tests and native evidence; other slices can remain explicitly pending. |
| Relationships and instruction coverage | Call/reference/I/O views with bounded expansion. | Comparisons, FC/FB argument binding, then stateful timer/counter and instance workflows. | PLC connections, tags and bindings; screens and other exposed content. | Human selections and agent queries refer to the same exact objects and evidence. |
| Integration and broader coverage | Consume HMI and supported network previews; add local/remote packaging qualification. | Broaden profiles and verify behavior where simulation is available. | Add separately qualified family operations, optional previews and supported edits. | End-to-end fixtures pass for each advertised target/operation combination. |

An HMI API or license limitation does not block the PLC/hardware explorer. A simulator startup failure does not prevent a native compile/preservation milestone, but runtime qualification remains blocked. Broad HMI support and broad graphical authoring cannot be declared finished on the strength of a minimal read-only or Boolean release.

## Acceptance across all three tasks

Use independently authored fixture expectations and keep the following evidence separate:

| Evidence | What passing establishes |
| --- | --- |
| Schema and pure model checks | Contract compatibility, bounded queries, type/connection validation, and deterministic transformations. |
| UI checks against fixed datasets | Correct selection, navigation, rendering, accessible interaction, and visible partial/stale states. |
| Native inspection checks | Returned objects/relations agree with the inspected target for the recorded API and installation profile. |
| Native engineering checks | Selected changes import/compile, preserve unrelated content, survive saving/reopening, and support the recorded recovery procedure. |
| Runtime tests | Executed stimulus/response assertions for the tested program and simulator, subject to recorded timing limits. |
| Packaging and remote checks | The exact packaged host, resources and adapters work in the tested deployment; local success alone does not qualify a VM. |

The combined demonstration should let the user select a motor FB in the explorer, see its callers and configured signal associations, inspect a supported HMI binding, and prepare a typed LAD/FBD change to the intended network. Applying the change must retain unrelated native content and refresh affected views. An unsupported HMI element or unresolved reference stays visible as such. A simulator result appears only if the corresponding program was actually executed and asserted.

Build this demonstration from separate reusable fixtures. It must not become the only evidence for instruction families, HMI families, or large projects.

## First implementation packages

| Package | Concrete deliverable | Depends on |
| --- | --- | --- |
| Shared contracts | Versioned node/relationship envelopes, owner references, coverage/freshness fields and cross-adapter fixtures. | Review of existing identity and index contracts. |
| Explorer first slice | Bounded hierarchy adapter and read-only tree/search/details over existing PLC/hardware inspection. | Shared contracts; no HMI adapter required. |
| Builder first slice | Typed Boolean graph, native profile, ID allocator, serializer, precise preservation comparisons and isolated LAD/FBD fixtures. | Existing change manager and native export examples. |
| HMI discovery slice | Installed family/API matrix, capability probes, exact fixture prerequisites and one read-only enumeration path where supported. | Installed vendor documentation and a qualifying HMI target. |
| Cross-adapter integration | Viewer and agent queries resolve the same PLC/hardware/HMI objects and explain unresolved relationships. | Explorer first slice plus supported HMI enumeration; network preview additionally depends on the builder reader. |

These packages can be assigned independently after the shared contract review. Changes to existing large worker partial classes should be integrated serially, with new adapters/models isolated into focused files where appropriate.

## Completion and estimation

Each detailed plan contains milestone gates and a full-scope completion definition. Track proposed, implementing, engineering-qualified, runtime-qualified where applicable, and blocked evidence separately. Keep the existing support catalog unchanged until a delivered operation has the evidence its contract requires.

Estimate effort after the first native profile/discovery slices. The largest unknowns are native instruction/version behavior, HMI family exposure and licensing, and real-project traversal latency. A whole-project viewer also depends on how much project content is actually exposed. Record measured effort and native latency at those gates before committing to delivery dates.
