# PLC component candidate shapes and library instance namespaces — 31 October 2026

Status: builders and offline tests done; native runs on 31 October (below). `ladCaller` and `earlierPlacement` passed and
are verified shapes; the other candidates stay closed on production Hosts
([combined apply](../guides/plc-library-integration-recipes.md#combined-apply)).

## Component shapes

`PlcComponentQualificationGate.UnverifiedFeatures` names every feature of a plan beyond the verified shape (fresh import,
new global instance DB, FBD FC new or existing after its last network). The preview lists them in
`combinedApply.candidateFeatures`. A production Host refuses each one with its earlier `PlcIntegrationNotQualified`
wording. A `--component-qualification` Host admits them only in its own single owned scratch. A plan opens in
production only when its exact sorted feature combination (`ShapeKey`, for example `fbCaller+multiInstance`) is listed in
`VerifiedShapes`, which is empty; each combination needs its own native evidence.

| Feature | Builder | Native case (`qualify_library_integrate_apply.py --candidates`) |
|---|---|---|
| `ladCaller` | Planner power-rail model (unchanged). The captured instance storage profile now serves LAD callers: the instance DB depends on the FB alone (`TypedGraphicalCapturedStorageTemplate`, `CaptureStorageBindings`). | `ladStandalone`, `ladExisting` |
| `fbCaller` | Existing LAD/FBD FB caller with a new global instance DB; the captured profile accepts FB callers. | `fbCaller` (`TiaMcpComponentHost`) |
| `earlierPlacement` | Unchanged readback: the inserted network is removed and every following ID, later networks included, shifts back. | `earlierPlacement` (two-network FC from the peer's native export) |
| `reusedComponent` | One-step coordinator sequence (typed edit only) after the pre-typed readback proves the exact installed versions. The typed step's pre-mutation archive becomes the single initial checkpoint (`CaptureTypedCheckpoint`: archive stage before mutation, bytes match). | `reuse` (second new FBD FC with `TiaMcpComponentGate_2`) |
| `multiInstance` | Fresh preparation must create exactly the reviewed `createMultiInstance` and no instance DB. Readback proves the caller FB gained the inserted network and exactly one new Static member (`PlcComponentCallerInsertion.Verify` with `addedStaticMember`). Refused before the claim when an import precedes it. | `multiInstance` (in `TiaMcpComponentHost`, reusing the installed FB) |

Expected boundary: the typed route's multi-instance profiles (`TypedGraphicalMultiInstanceTemplate.MapDeclaration`) refuse
FB declarations with structured members, and the fixture FB `TiaMcpComponentGate` has the UDT static `State`. The
`multiInstance` case therefore records a pre-dispatch refusal (`InstanceCreationProfileUnavailable`, project unchanged) as
`outcome: "boundary"` unless the native run installs it. Qualifying it needs a captured multi-instance profile for a
UDT-carrying FB, or a scalar component fixture.

Not built, kept plan-only:

- **Software units.** A unit-route instance of the structured component FB has no storage profile (the captured profile is
  standard-route; shipped unit templates refuse structured statics). A caller in another unit than the FB gets no new
  storage on the typed route (`InstanceCreationRouteUnsupported`). Unit tags are a V21 boundary, so mappings would be
  unit DB members, whose component readback does not exist. `ComponentScopeUnqualified` now states these causes.
- **Reused instance DBs** (`existingGlobal`, new `ComponentInstanceReuseUnqualified`), partial dependency reuse, DB and
  UDT-tag member bindings and OB insertion.

Offline evidence: `TiaMcp.PlcLibraryIntegration.Tests` (gate features and admissions, one-step reuse coordinator,
typed-checkpoint order and bytes, lost-response reconciliation, multi-instance preparation and readback refusals, caller
proof with an added Static member including a formerly empty section), `TiaMcp.CapturedStorage.Tests` (a LAD caller gets
the identical instance document; other languages refused), `scripts/test_plc_component.py` (LAD power-rail and
multi-instance call oracles, selected network, member-aware preservation) and `scripts/test_qualify_library_integrate_apply.py`.

## Library instance namespaces

`library_instantiate` now sets an explicit `namespace` on a master copy of a block or PLC data type in a software unit
(`instanceNamespace.masterCopies`); a tag-table master copy is refused. `qualify_library_units.py` adds three native cases
to the 29 October run (`local.library_units.namespace.20261029`):

- a UDT instantiated as its own root in the unit namespace, then an FB that depends on it, with the same namespace,
  reusing it (both under `/namespaces/TiaMcp_LibUnit/`, one use each);
- a master-copy FC instantiated with the namespace;
- an OB in the namespace that reaches every library use of the PLC (the namespaced FB, the dependent FB and its UDT,
  the first instantiation's un-namespaced UDT through a DB member) and calls the master-copy FC, so a PLC-wide
  `library_update_project` to 1.1.0 proves each use reachable. The updated FB must keep its path, namespace included,
  and the dependent FB, its UDT and the master copy must keep theirs after the update and after a fresh reopen. The
  update readback already fails closed when a baseline object path disappears.

Unproven inputs of that case: whether V21 imports a `NAMESPACE`-wrapped SCL source into a unit through `source_upsert`,
and whether a namespaced DB resolves the un-namespaced UDT. The runner stops at the stage "import the OB-rooted callers
inside NAMESPACE TiaMcp_LibUnit" with V21's own message if either is refused.

A dependency created alongside a namespaced root keeps its type namespace; that stays unexercised.

## Native runs — 31 October 2026

One owned TIA Portal V21 at a time, owned de-DE-only scratch projects, no PLCSIM. Frozen Debug builds under the project's
`artifacts/` tree; raw reports in `artifacts/evidence`.

[`local.library_integrate.candidate_shapes.20261031`](../../evidence/local.library_integrate.candidate_shapes.20261031.json)
(outcome failed; the per-case results are the evidence):

- Run 1 (build `a8819fa8`, `--artifacts artifacts/ic1`): every case stopped at the import step's checkpoint archive before
  any mutation. TIA refused the archive path: `...\ic1\ie6fdb2\changes\change-lib-…\c\changes\change-lib-…\before`
  is 164 characters, over its 143. The nested combined-apply directories leave about 20 characters for the Host artifact
  root. `OwnedScratch` now passes the root as given, so run 2 used `Q:/ic2` with `Q:` a `subst` drive over `artifacts/q`
  (build `3e92b2be`).
- **Passed** (each with the exact `candidateFeatures`, installation_verified, the independent export/inventory diff, native
  caller-insertion proof, call oracle, exact project library versions, fresh reopen, 0-error compile and initial-checkpoint
  restore into a new location after retrieval and reopen):
  - `ladStandalone`: a new LAD FC;
  - `ladExisting`: an existing LAD FC after its last network (following IDs 8→D, 9→E);
  - `earlierPlacement`: an existing two-network FBD FC after its first network (seven following IDs, the second network
    included, shifted by 5).
- **Failed, fixture/profile limit:** `fbCaller`, and the verified standalone FBD FC first install of `multiInstance`. The
  FB caller fixture `TiaMcpComponentHost` took FB2, so the component FB was imported as FB3. The captured instance profile
  compares the complete reference FB export, Number 2 included, and refused typed preparation after the import was saved
  (`CapturedStorageProfileUnavailable`, recovery_required in the owned scratch). This also affects the verified standalone
  shape: a target with FB2 taken fails after the import. A pre-claim check of the FB's import number would refuse it first.
- **Failed, reuse proof:** `reuse` was refused at `change_prepare` (`DependencyConflict`): in the de-DE-only project the
  installed FB version's "Version comment" differs from the source version (`ObjectsDifferent`). Nothing was claimed.
- `multiInstance` never reached its typed multi-instance preparation; its expected storage-profile boundary is unexercised.

[`local.library_units.namespace.20261031`](../../evidence/local.library_units.namespace.20261031.json) (outcome failed,
77/79 steps in each run; build `3e92b2be`, then `e52b67f4`):

- **Passed:** every stage of the 29 October run; a namespaced UDT root reused by a namespaced dependent FB (both under
  `/namespaces/TiaMcp_LibUnit/`, one use each, compiled); a namespaced master-copy FC (`instanceNamespace.masterCopies`).
- **V21 behavior:** V21 imports a `NAMESPACE TiaMcp_LibUnit` SCL source into the unit, but resolves unqualified names in
  it against the root namespace. Run 1: the namespaced library FB instances were not found as instance-DB types by simple
  name ("Could not find type \"TiaMcpLibraryGate\""), while the un-namespaced UDT resolved. Run 2, with qualified type
  names: every block generated, but the OB's unqualified references to DBs of the same source failed compilation
  ("Tag _.TiaMcpUnitNamespaced_Gate not defined"). The namespaced FB's later update is therefore unexercised; the next
  attempt qualifies every reference in the caller source.
