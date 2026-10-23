# Typed LAD/FBD builders implementation plan

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

Status: implementation and qualification in progress, 8 October 2026. The additive `graphical_typed_edit` change kind, typed reader, full instruction families and coordinated storage/template implementation are described in [the implementation guide](../guides/typed-lad-fbd-builders.md). Qualification remains per CPU/firmware, language, instruction and route; implementing all packages does not itself satisfy their native/runtime exit gates.

Parent scope and sequencing: [graphical project roadmap](graphical-project-roadmap.md). Complete planned delivery covers every instruction family listed below; the Boolean milestone is an initial release, not completion of this plan.

## Outcome and present baseline

An agent should express contacts, coils, branches, comparisons, timers, counters and calls without manufacturing Siemens XML or IDs. Preparation shows logic, resolved operands, affected declarations and native diff before the existing recoverable apply workflow.

Today, [EngineeringSources.cs](../../src/TiaMcp.Openness.V21/EngineeringSources.cs) implements operand changes, native fragment patches, network insertion, call-argument edits and structural comparison. `GraphicalFixture` creates one fixed contact/coil example, not a general builder. [EngineeringSourcesTests.cs](../../tests/TiaMcp.Tests/EngineeringSourcesTests.cs) treats representative instruction payloads as opaque preservation fixtures.

The [existing workflow](../guides/engineering-workflows.md#ladfbd-editing) validates explicit native fragments. [Native evidence](../verification/engineering-gap-qualification.md#ladfbd-preservation) qualifies bounded edits, not arbitrary synthesis. Extend [OpennessSession.Changes.cs](../../src/TiaMcp.Openness.V21/OpennessSession.Changes.cs), which provides preparation, fingerprints, checkpoints, compilation and readback comparison.

## Scope and delivery order

| Stage | LAD authoring | FBD authoring | Boundary |
| --- | --- | --- | --- |
| 1: Boolean networks | Normally open/closed contacts, ordinary coils, series paths and parallel branches | Boolean AND/OR/NOT nodes and Boolean assignments | Existing scalar Bool declarations; separate language fixtures and emitters. |
| 2: Typed comparisons | Qualified comparison boxes connected to rung flow | Qualified comparison nodes and Boolean outputs | Explicit numeric types/constants, no guessed conversion or overload. |
| 3: Calls | FC calls, then FB calls with verified existing instances | Same call families using FBD wiring | Resolve actual callee interface and instance ownership. |
| 4: Stateful instructions | Qualified TON/TOF/TP and CTU/CTD/CTUD variants | Same families using qualified FBD profiles | Every advertised variant needs its own storage and behavior evidence. |
| 5: Coordinated creation | New blocks/networks, required instance DBs and multi-instance declarations | Equivalent supported FBD creation | Caller/interface/dependent-DB effects must enter one reviewed project change. |

Begin with insertion into existing standard unprotected blocks, then bounded edits of supported nodes and new FC/FB blocks. Missing declarations are explicit effects. Set/reset coils, edge instructions and arithmetic require later profiles and acceptance.

Exclude safety logic, protected/generated blocks, implicit LAD/FBD conversion, arbitrary vendor instruction synthesis, unrestricted graphical layout reproduction, indirect/ANY/pointer operands, and live-machine download or control. Software-unit and namespace routes are separate qualification cells, even when they reuse the same implementation.

## Typed model and preservation

Define a versioned model with language, block reference, network placement, logical node keys, symbolic references, typed constants, ports and connections. LAD represents power flow, series paths and branches; FBD represents function dataflow. Equivalent Boolean results do not authorize language conversion.

Retain the native XML preservation envelope. Supported nodes link their logical keys to native UIds, compile-unit IDs and original fragment hashes. Unknown nodes, attributes, comments and extensions remain opaque native content. Partial graphs report coverage and unresolved connections; omission never means absence.

Replace only selected supported fragments and insert new fragments into the native tree. Reject edits when unknown connected elements prevent proving their boundaries. Preserve unrelated XML structurally and original bytes separately. Keep existing raw XML operations distinct; opaque fragments are not typed instructions.

Allocate IDs deterministically during preparation. Reserve existing document IDs and network-scoped UIds, including opaque definitions and numeric aliases. Retain IDs for untouched content and structurally unchanged selected networks. Native V21 re-export reallocates selected-network UIds when topology changes; prepare that explicit old/new mapping and effect rather than claiming the IDs survived. Unrelated network/document IDs remain exact. Enforce native uniqueness rules and revalidate fingerprints before apply; target changes require a new plan.

## Type resolution and instruction profiles

Resolve operands against actual block interfaces, PLC tags, DB/UDT members, exact software scope and namespace. Reuse [ObjectIdentity.cs](../../src/TiaMcp.Openness.V21/ObjectIdentity.cs), [EngineeringPaths.cs](../../src/TiaMcp.Openness.V21/EngineeringPaths.cs) and existing declaration inspection. Same-name matches are insufficient. Report unresolved types, symbolic array bounds and unsupported indexing explicitly; only supported, validated references may participate in generation.

A profile records instruction family/version, language, supported target conditions, native template provenance, pin direction/type/cardinality, allowed constants/conversions, execution-enable behavior and required instance storage. Seed profiles from independently authored native exports and verify the installed APIs/formats before enabling them. Profile compatibility is an explicit predicate, not a name-based guess.

Semantic validation checks Bool contacts/coils, valid graph topology and execution order, port cardinality, output writability, comparison compatibility, mandatory arguments and instance requirements. It must distinguish constants from writable variables, reject conflicting output connections and preserve language-specific enable/power-flow behavior. These checks catch known mistakes early; Siemens import and compilation remain authoritative for native acceptance.

Resolve FC/FB calls to exact block identities and fresh parameter declarations, including input/output/in-out direction and return values. An FB instance must actually reference the chosen FB. Existing global-instance and multi-instance metadata that cannot establish this identity blocks typed creation; a symbolic name alone is insufficient. New instance declarations require explicit storage scope, names, affected caller/dependent DBs and start-value impact in the preview.

Timer/counter profiles select the exact supported variant and storage representation. Do not fabricate IEC internal members or infer memory offsets. Shared instance storage must be intentional and validated; default to a distinct explicit instance per independently stateful instruction.

## Proposed contract and change integration

Proposed example, **not a callable payload**; finalize fields during contract design:

```json
{
  "kind": "graphical_typed_prepare",
  "schemaVersion": 1,
  "blockPath": "<exact existing canonical path>",
  "language": "LAD",
  "insertAfterNetworkId": "<observed network ID>",
  "logic": {
    "series": [{"contact": "Enable"}, {"contact": "Ready"}],
    "coil": "MotorRun"
  }
}
```

Resolve shorthand symbols during preparation. Prefer an additive `change_prepare` kind; optionally expose read/validate separately. Return existing `changeId`/`previewHash`, model/profile versions, resolved identities, source hashes, ID mapping, diagnostics, native diff and preservation coverage. Do not create an independent mutation path.

Preparation generates immutable native artifacts and a complete effect list. Apply revalidates fingerprints and artifact hashes under exclusive access, saves/checkpoints the project, applies once, compiles, compares native readback, saves when requested and records outcomes in the existing journal. The preview must retain the existing fact that checkpointing saves all current in-memory project edits. Typed changes always require compilation.

Preserve existing retry semantics: completed applies return recorded results; interrupted/failed operations require reconciliation and explicit recovery. Type validation precedes mutation. Compile/preservation failures retain changed state and checkpoint. Coordinated declaration/call changes require extending the change record; several operations do not become an atomic transaction.

## Work packages and acceptance gates

| Package | Deliverable and dependency | Exit gate |
| --- | --- | --- |
| LAD-010 | Baseline native instruction corpus and installed-format/profile probe | Separate LAD/FBD exports; actual pin/storage rules documented; unsupported variants recorded. |
| LAD-020 | Versioned typed contracts, diagnostics and partial native reader; depends on 010 | Roundtrip preserves unknown content; unresolved coverage visible; malformed and ambiguous models rejected. |
| LAD-030 | Exact symbol/type/instance resolver and deterministic ID allocator; depends on 020 | Scope/name collisions, opaque IDs, writable targets and stale declarations handled without guessing. |
| LAD-040 | Boolean LAD/FBD emitters and prepare integration; depends on 030 | Both languages independently pass compile, full readback, save/reopen and checkpoint recovery fixtures. |
| LAD-050 | Comparison and FC/existing-FB call profiles; depends on 040 | Qualified type matrix; direction/instance mistakes rejected; correct caller/callee wiring verified. |
| LAD-060 | Timer/counter profiles and coordinated instance creation; depends on 050 | Storage, dependent declarations and rollback scope verified; engineering and behavior gates reported separately. |
| LAD-070 | Supported edits/new blocks, software-unit profiles, explorer graph previews, catalog and packaging | Every advertised cell has evidence; profile data, schemas and hashes survive local/remote deployment. |

Each package produces a reviewable change and bounded qualification evidence. A labeled Boolean-only engineering release need not await every family. Finishing the parser or pure tests does not complete LAD-040's native gate.

## Independent verification and release criteria

Author expected logical behavior independently of generated XML. Include manually created TIA networks as reference fixtures; do not generate both expected and actual networks with the same builder. Pure tests cover graph validation, exact resolution, profile selection, deterministic IDs, bounds/limits, malformed XML rejection and preservation of decorated unknown content.

Native fixtures exercise both languages, different branch arrangements, negated contacts, multiple outputs where supported, comparison overloads, FC outputs/in-outs, FB global/multi-instances and each advertised timer/counter variant. Include negative target/language versions, wrong types, missing arguments, ambiguous symbols, duplicate IDs, shared storage and stale previews. Record baseline compiler findings before the change and apply an explicit acceptance policy.

For every native acceptance case: inspect the prepared diff, apply through the real MCP server, compile, verify complete expected native XML, save/reopen in a fresh owned session, recover the checkpoint and verify the original. Verify untouched peer blocks/interfaces as well as the selected network. Existing provenance/formatting normalization must not be widened merely to hide a mismatch.

Behavior tests cover full Boolean truth tables, branch combinations, comparison boundaries, FC/FB parameter flow, instance independence, counter edges/reset/limits and timer start/cancel/restart/completion. Use independent expected vectors and observe supported outputs; preserve explicit scan/timing limitations from [block-testing.md](../guides/block-testing.md). Compilation does not establish runtime behavior or exact PLC timing.

The documented [simulator baseline](../guides/simulation.md#post-restart-check-and-credential-investigation--8-october-2026) now establishes protected-program RUN, typed reads/writes and all eight baseline assertions, reconfirmed by attempt seventeen on its recorded binary lineage. This satisfies the shared startup prerequisite; it does not qualify any LAD/FBD builder family or instruction profile. The nine-family runtime corpus remains unqualified: project locks, provisioning/restoration/isolation failures, native timeouts, a disconnected engineering worker, a report-persistence failure and one elapsed-time assertion failure are recorded. See the [runtime-proof TODO](runtime-proof-todo.md). Engineering and compile/preservation results cannot be relabeled runtime-qualified. Publish `engineeringVerified`, `preservationVerified`, `persistenceVerified`, `recoveryVerified` and `runtimeVerified` separately with exact profile evidence.

## Shared boundaries and immediate next work

The explorer consumes read-only instruction graph data, provenance and completeness; its shared project node/relation contract remains separate from this typed language model. HMI owns its adapters. The [library-management plan](library-management-plan.md) owns package/type/version lifecycle and passes exact PLC components to this call/instance layer. Compose through one project change coordinator.

Start with LAD-010 and the LAD-020 contract review in parallel: capture independently authored Boolean LAD/FBD examples and draft the supported/opaque node model. Then implement LAD-030 using existing interface/call readers, followed by one end-to-end Boolean insertion per language through prepare/apply/recovery. Keep timer/counter generation disabled until its profiles, storage and evidence gates are complete.
