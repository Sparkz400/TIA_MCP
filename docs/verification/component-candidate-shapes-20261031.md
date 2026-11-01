# PLC component candidate shapes and library instance namespaces — 31 October 2026

Status: builders, offline tests and clean native reruns done. A first native attempt on 31 October is **not evidence**
(both runs ended with a failed outcome); the [clean reruns](#clean-rerun--31-october-2026) passed. `ladCaller`,
`fbCaller`, `earlierPlacement` and `reusedComponent` are verified shapes since then; `multiInstance` and every
combination stay candidates ([combined apply](../guides/plc-library-integration-recipes.md#combined-apply)).

## Component shapes

`PlcComponentQualificationGate.UnverifiedFeatures` names every feature of a plan beyond the verified shape (fresh import,
new global instance DB, FBD FC new or existing after its last network). The preview lists them in
`combinedApply.candidateFeatures`. A production Host refuses the plan with `PlcIntegrationNotQualified`, naming the first
feature that is not verified on its own, or else the unverified combination. A `--component-qualification` Host admits
them only in its own single owned scratch. A plan opens in production only when its exact sorted feature combination
(`ShapeKey`, for example `fbCaller+multiInstance`) is listed in `VerifiedShapes`; each combination needs its own native
evidence. Since the clean rerun it lists `earlierPlacement`, `fbCaller`, `ladCaller` and `reusedComponent`.

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

The native attempt below showed that V21 imports a `NAMESPACE`-wrapped SCL source into a unit and resolves the
un-namespaced UDT by its simple name, but resolves every unqualified name against the root namespace; the caller source
therefore qualifies each namespaced type and block. The runner stops at the stage "import the OB-rooted callers inside
NAMESPACE TiaMcp_LibUnit" with V21's own message if the source is still refused.

A dependency created alongside a namespaced root keeps its type namespace; that stays unexercised.

## Native attempt — 31 October 2026

One owned TIA Portal V21 at a time, owned de-DE-only scratch projects, no PLCSIM. Both runs ended with a failed outcome;
their summaries (`evidence/local.library_integrate.candidate_shapes.20261031.json`,
`evidence/local.library_units.namespace.20261031.json`) are not cited as support. What they found, and what changed:

- Every candidate case first stopped at the import step's checkpoint archive: TIA counted
  `...\ic1\ie6fdb2\changes\change-lib-…\c\changes\change-lib-…\before` as 164 characters, over its 143. The steps now
  live in `<root>/changes/c/<8 hex>` (121 characters for that root), and prepare refuses longer paths
  (`ComponentPathTooLong`). A second attempt on a short drive installed `ladStandalone`, `ladExisting` and
  `earlierPlacement` with every gate.
- `fbCaller`, and the standalone FBD FC first install of `multiInstance`, failed after the import was saved: the FB caller
  fixture took FB2, the component FB imported as FB3, and the captured instance profile compares the reference export
  with its number (`CapturedStorageProfileUnavailable`). Prepare now refuses an occupied number
  (`ComponentBlockNumberOccupied`, naming the block), and the runner moves the Host FB to FB30 before planning.
- `reuse` was refused at `change_prepare`: TIA reported only the "Version comment" as different between the installed
  and source FB version in the de-DE-only project. A comment-only difference is now recorded as `ignoredDifferences`; the
  component readback still proves the exact content.
- Library namespaces: a namespaced UDT root reused by a namespaced FB, and a namespaced master copy, passed per step.
  V21 imports a `NAMESPACE` SCL source into the unit but resolves unqualified names in it against the root namespace, so
  the OB-rooted caller failed. The runner now qualifies every namespaced reference.

## Component storage capture — 1 November 2026 (authored, not run)

The boundary above is a missing profile, not a V21 refusal. Changes, offline only:

- `TypedGraphicalMultiInstanceTemplate.MapDeclaration` and `TypedGraphicalInstanceTemplate.Create` accept a structured
  (UDT) member when a captured reference member or prototype is identical to it, nested declaration included; the
  captured storage is copied as before. Any other structured member keeps its earlier refusal, and start or default
  values stay refused (`TiaMcp.Tests`, "Typed structured members map only onto an identical captured member").
- [`route-capture-component-storage-packet.v1.json`](../../fixtures/typed-graphical/route-capture-component-storage-packet.v1.json)
  holds two `capture_typed_routes_owned.py` runs, which now admit `.udt` sources through `type_source_upsert`:
  - standard route: the component UDT and FB from `fixtures/libraries/component-qualification`, then the FBD FB
    `Tgp_ComponentHost_FBD` declaring the multi-instance `Gate : "TiaMcpComponentGate"`. Extraction yields
    `v21.fw41.component-gate.multi-instance.v1`, the profile the `multiInstance` shape needs;
  - softwareUnit route (`TiaMcp_UnitA`): the same interface as `TgpUnit_ComponentGate` with its instance DB. Extraction
    yields `v21.fw41.softwareUnit.component-gate.global-instance.v1`.
- `extract_typed_graphical_profiles.py` binds both to the captured component FB (`COMPONENT_TEMPLATES`) and skips
  captured types.
- `qualify_library_integrate_apply.py --candidates` no longer accepts a `multiInstance` boundary.

Still plan-only: a multi-instance in a caller FB that has an instance DB (no captured dependent-DB effect for a structured
member), and software-unit destinations. The unit capture removes only the storage cause; component readback still covers
the standard scope only, unit tags stay a V21 boundary, a library instance keeps an empty namespace unless one is set
explicitly while the unit-route template needs the FB in the unit's namespace, and another unit's FB still gets no new
storage (`InstanceCreationRouteUnsupported`).

## Clean rerun — 31 October 2026

Frozen Debug builds of the worker, Host and `LibraryUnitSeeder` under `artifacts/` (no subst drive), one owned TIA
Portal V21 at a time, owned de-DE-only CPU 1511-1 PN V4.1 scratch projects, no PLCSIM, no Openness prompt.

- The candidate run at 367daeb1 installed `ladStandalone`, `ladExisting`, `fbCaller` and `earlierPlacement` but failed
  `reuse` at `change_apply`: the coordinator's contract check read the typed step as the plan's second step, and a
  reused-FB plan has only one (`ArgumentOutOfRangeException`). 0130116e reads the last step; that run is not cited.
- `qualify_library_integrate_apply.py --candidates` at 0130116e passed
  (`local.library_integrate.candidate_shapes.rerun.20261031`): `ladStandalone`, `ladExisting`, `fbCaller`,
  `earlierPlacement` and `reuse` installed with every gate (exact diff, caller insertion proof, call oracle, library
  versions, reopen, 0-error compile, restore of the initial checkpoint); `multiInstance` ended at the expected boundary
  (`InstanceCreationProfileUnavailable` before any dispatch, project unchanged).
- `qualify_library_units.py` at 0130116e passed, 94/94 steps (`local.library_units.namespace.rerun.20261031`): the
  namespaced UDT root reused by the namespaced FB, the namespaced master copy, the OB-rooted caller in the namespace, and
  the PLC-wide update to FB 1.1.0 that keeps every namespaced path after the update and after a fresh reopen.
- The regression of the verified shapes at 0130116e passed (`local.library_integrate.combined_apply.regression.20261031`):
  the standalone and existing FBD FC installs, the OB and FB caller refusals, and the Host without the former flag. It
  ran before `fbCaller` was verified; the runner's refusal cases now use an FB caller with a multi-instance.
