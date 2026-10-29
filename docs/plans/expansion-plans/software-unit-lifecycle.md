# D07 software-unit lifecycle: implementation and remaining gates

The complete feasible scope remains required by the [release ledger](../release-obligations-20261015.md).
No Siemens installation, SDK, simulator or controller is available here.

## Existing boundaries and delivered offline increment

`OpennessSession.ProjectModel.cs` captures exact unit/member paths but currently
omits unit relations and namespace presets. `OpennessSession.Inspection.cs`
returns relation types and `RelatedObject` through `InspectionValue`. For V21,
`RelatedObject` is a string, not an exact path. The separate `PlcUnitBase` formatter
does not turn these names into canonical identities.

`SoftwareUnits/SoftwareUnitReview.cs` now compares explicitly supplied relations,
publication and namespace presets against exact saved-review paths. The Core
`SoftwareUnitWorkflowStore` and two Host tools retain the original and before/after
report, support bounded paging/downloads, and verify derivation after restart.
Existing project review/upload/artifact storage is reused. No native resolver,
private project format, parallel identity scheme or new dependency is introduced.

Prepared change kinds `software_unit_create`, `software_unit_relation_set` (SoftwareUnit
relations only, cycles refused), `software_unit_publication_set` (native `Access` attribute)
and `software_unit_delete` (empty, unreferenced units only) now cover part of SU02/SU04/SU05
through the existing checkpoint/compile/readback flow. They are compiled against the V21
API only; native qualification, rename/copy/move and NonUnitDB/TODB relations remain open.
See [engineering workflows](../../guides/engineering-workflows.md#supported-payloads).

The [workflow](../../reference/software-unit-review.md) and its official sources
state the implemented limits. This is a useful first complete offline review flow;
it does not close the full lifecycle or native acceptance.

## Dependency-ordered continuation

| Package | Work and completion requirement | Prerequisite / gate |
| --- | --- | --- |
| SU01 supplied prerequisite review | Exact PLC/unit identity, direct relations, publication, cycles, candidate collisions, sequential proposals, retained originals/restart/schema/package flow. Implemented; see verification. | Supplied facts remain unauthenticated and incomplete. |
| SU02 exact capture | Extend supported native read capture with presets, publication, relation observations and separately evidenced exact endpoint association. Preserve names that cannot resolve uniquely. | Installed V21 public metadata, build and original producer/consumer captures. |
| SU03 namespace effect planning | Model documented separate propagation rules using independently captured before/after fixtures, qualified references and every affected consumer. Distinguish empty/current/prefix namespaces and excluded invalid names. | Genuine fixtures and native setter/UI behavior remain unverified; preset-only review must not imply propagation. |
| SU04 templates/composition | Inventory supported library/master-copy routes; design manifests for explicit source versions, unit members, relations, storage and consumer bindings. Preserve library links and unaffected content. | Copy/instantiation is not identity-preserving movement. Verify exact public APIs and captures before adding an executable adapter. |
| SU05 unit lifecycle/movement | Confirm create/rename/delete/copy/move routes individually, produce exact prepared previews and stale-source checks. Reuse change journal/reservations/checkpoints; deletion must include references and unknowns. | SDK compilation and genuine before/after preservation evidence. Never promote native create backlog from SU01. |
| SU06 integration | Carry exact unit scope into typed operand/type/storage resolution, source import, library installation and Explorer navigation. Bind proposals to frozen review evidence; no same-name root fallback. | Shared contract audit and target fixtures. |
| SU07 recovery and qualification | Run standard and special scopes separately; reconcile lost responses/cancellation, preserve unrelated units, compile affected users, save/reopen, checkpoint restore and runtime behavior. | Licensed engineering station and applicable target profile; no offline substitute. |

## Required acceptance fixtures

Portable tests include A->B->C producer/consumer evidence (no transitive grant),
Unpublished->Published, relation addition/removal, cycle members, duplicate
namespace/name candidates, incomplete relations, absent paths, multiple PLCs,
virtual nodes, OB/safety/protected/generated refusals, strict malformed input,
large bounded pages, original byte preservation, tamper and real Host restart.
Expected outcomes are authored independently as supplied-model checks.

Native follow-up must include independently captured two-unit producer/consumer
compile/runtime outcomes, missing target names, namespace ambiguity and explicit
propagation, referenced-unit deletion, a library-instantiated unit retaining its
version links, unsupported/protected/generated members, interrupted multi-object
operations and unchanged unrelated content. Bind captures to source/binary/profile
hashes, preserve failure receipts, and distinguish compile from physical behavior.

## Open decisions

No supported native move route, identity-preserving template instantiation,
namespace-propagation adapter, full visibility oracle or complete affected-consumer
capture has been proven here. Current source/API presence is not product availability
or native qualification. V21 relations/publication/preset documentation was checked;
Context7 offered V20 indexing only, so official V21 pages supplied the primary API
basis. Remaining API inventory and original capture gaps stay explicit.

[Verification](../../verification/software-unit-review-20261016.md) records exact
portable evidence. The [44-stream queue](../../history/offline-execution-20261016.md) remains active.
