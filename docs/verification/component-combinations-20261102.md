# PLC component combinations and the dependent instance-DB effect — 2 November 2026

Status: every combination of the component route's builder features passed natively, and so did a multi-instance whose
caller FB has its own instance DB, through a newly captured effect. `PlcComponentQualificationGate.VerifiedShapes` now
lists 23 shapes; production runs each. Earlier shapes are in the [candidate shapes record](component-candidate-shapes-20261031.md).

## Combinations

The five features (`ladCaller`, `fbCaller`, `earlierPlacement`, `reusedComponent`, `multiInstance`) form 19 shapes a
plan can have: a multi-instance needs a reused FB in an FB caller, and an earlier placement an existing caller. Before
this run five were verified. `qualify_library_integrate_apply.py --candidates` gains a `COMBINATIONS` table with one case
for each of the other 14 (with both variants of `ladCaller+reusedComponent`, a new and an existing LAD FC, plus
`reuseExisting`) and `--case` to select cases. An existing caller is imported from its fixture source; a two-network caller is that block re-imported over
itself from its own native export with its one network twice, and the call goes after the first network. The LAD FB caller
fixture `callers/TiaMcpComponentHost.lad.s7dcl` is new.

At b3787562 (frozen `artifacts/pc1`, `artifacts/q1/i45cc48`), all 16 cases installed
(`local.library_integrate.candidate_shapes.combinations.20261102`), each in its own owned de-DE-only CPU 1511-1 PN V4.1
scratch: `ladEarlier`, `fbEarlier`, `ladFb`, `ladFbEarlier` (fresh import, new global DB), `reuseExisting`, `reuseLad`,
`reuseLadExisting`, `reuseEarlier`, `reuseLadEarlier`, `reuseFb`, `reuseFbEarlier`, `reuseLadFb`, `reuseLadFbEarlier`
(reuse after a verified standalone install, new global DB) and `multiEarlier`, `multiLad`, `multiLadEarlier` (the
multi-instance `Gate` of the reused FB). Each preview named exactly its combination in `combinedApply.candidateFeatures`,
and each install passed the gates of the earlier shapes: exact installed-object diff, the native caller insertion proof
after the selected network (with the one new Static member for a multi-instance), the call oracle, the instance DB number,
exact library versions, a fresh reopen, a 0-error compile and `change_restore` of the initial checkpoint (the typed step's
own archive for a reused FB) into a new location equal to the baseline after retrieval and after reopen. `reuseExisting`
is the already listed `reusedComponent` key with an existing FBD FC caller, a variant its first evidence did not exercise.

## Dependent instance-DB effect

A multi-instance changes the caller FB's interface, so the caller's own instance DB gains the member too. The typed
route's scalar effect pair (`instance-effects.v1.json`) refuses the component FB's UDT static, so such a plan was refused
before admission (`DependencyProfileUnavailable`). Now:

- **Capture** (`local.typed.component-effect-capture.20261102`, `artifacts/cde`): `capture_typed_routes_owned.py` imported
  the component UDT and FB, `Tgp_ComponentEffect` with its instance DB (start value 42 on `RetainedMarker`), then
  redeclared the FB with `Gate : "TiaMcpComponentGate"` and observed the DB as a native dependent. Sources
  `component-effect-before.scl` and `component-effect-after.scl`. `extract_typed_instance_effects.py --component` checked
  that the DB gained exactly `Gate`, nesting `State` with `Active`, and that everything else, the 42 included, stayed
  equal, and wrote `instance-effects-component.v1.json` with the native caller/DB pair, `qualificationOnly` for the
  qualification run.
- **Typed route**: when the scalar pair refuses a direct instance DB's new member, `PrepareTypedDependentInstance` maps it
  onto the component pair. A `qualificationOnly` pair serves only a `--component-qualification` worker's own component
  scratch. Embedding parents keep the transitive rule, which has no structured capture.
- **Gate**: `CombinedApplyDependentsRefusal` refuses only dependents other than the caller's direct instance DBs. A
  multi-instance whose caller has such a DB is the new feature `dependentInstanceDb`, so it is its own combination.
- **Oracle**: the runner compares the DB's new member with the captured DB member and the rest of the DB with its
  baseline (`verify_dependent_db`).

At 1f539563 (frozen `artifacts/pc2`, `artifacts/q2/i68d047`), `local.library_integrate.candidate_shapes.dependent_db.20261102`:
`multiInstanceDependents` (FBD FB host with its own DB), `multiDependentsEarlier`, `multiDependentsLad` and
`multiDependentsLadEarlier` installed with every gate above plus the DB oracle; `multiInstanceParent` (a parent FB
embedding the host) previewed `eligible: false` with `DependencyProfileUnavailable` naming the parent, and `change_apply`
was refused before admission, with no admission record, the plan prepared and the scratch unchanged.

The verifying change lists the four `dependentInstanceDb` shapes and the 14 combinations in `VerifiedShapes` and deploys
the component pair without `qualificationOnly`. Every native process the runs started had exited afterwards.

## Rerun at the verifying build

At d1037e6b (frozen `artifacts/pc3`), the build that lists the 23 shapes:

- `--candidates` (`artifacts/q3c/i1f342c`, `local.library_integrate.candidate_shapes.verified.20261102`): all 27 cases
  passed. The 26 install cases (the seven earlier ones and the 19 of `COMBINATIONS`) previewed no candidate feature, so
  each runs in production, and installed with every gate; `multiInstanceParent` was refused before admission.
- The verified-flow regression without `--candidates` (`artifacts/q3r/i8fc58c`,
  `local.library_integrate.combined_apply.regression.20261102`): the standalone and existing FBD FC installs, the OB and
  fresh-import multi-instance refusals before admission, and the Host without the former flag.

## Still open

- Software units: plan-only (unit tags are a V21 boundary, component readback covers the standard scope).
- An embedding parent of the multi-instance caller: no transitive effect of a structured member is captured.
- OB insertion, reused instance DBs, partial dependency reuse, DB/UDT-member bindings: unchanged.
- The native05 recovery audit: its artifacts are not on this PC (only the `native05-*` review fixtures are).
- Engineering only; runtime of these shapes is not part of the evidence.
