# PLC component multi-instance in an embedded caller FB — 7 November 2026

| Evidence | Runners | Result |
| --- | --- | --- |
| [`local.library_integrate.candidate_shapes.embedding_parent.20261107`](../../evidence/local.library_integrate.candidate_shapes.embedding_parent.20261107.json) | `capture_typed_routes_owned.py`, `qualify_library_integrate_apply.py --candidates` | passed |

A reused-FB multi-instance changes the caller FB's interface. Its own instance DBs had a captured effect since 2 November;
a parent FB embedding the caller was refused before admission (`DependencyProfileUnavailable`), because no capture showed
how TIA nests the component's structured member (its static `State` is a PLC data type).

## Capture

`component-transitive-effect-before/after.scl` build the typed route's transitive chain with the component FB:
`Tgp_CompNestEffect`, a parent with `Inner : "Tgp_CompNestEffect"`, a grandparent with `Middle`, and their instance DBs
(start value 42). The after source adds `Gate : "TiaMcpComponentGate"`. In the owned scratch every nested multi-instance
(parent, grandparent and both DBs) appended `Gate` to its nested Static section in exactly the direct DB member's shape
without its top-level attributes, and nothing else changed. `extract_typed_transitive_effects.py --component` checked
that and wrote `instance-effects-transitive-component.v1.json`. The scalar members' generic rule holds for the structured
member.

## Combined apply

With that manifest, combined apply no longer refuses embedding parents; the typed preparation, which runs before any
mutation, decides each dependent. The shape gains the feature `embeddingParent`. `multiInstanceParent` installs the reused
FB as `Gate` in the existing FBD FB `TiaMcpComponentHost`, which `TiaMcpComponentParent` embeds: the preview named
`embeddingParent`, `fbCaller`, `multiInstance` and `reusedComponent`, the parent gained exactly the captured nested member,
everything else was preserved, and reopen, a 0-error compile and the checkpoint restore passed. The first run found that
only the refusal path received the feature, not the preview's `candidateFeatures`; fixed before the rerun.

The shape joins `VerifiedShapes` and the manifest loses `qualificationOnly`, as the direct effect did on 2 November.

Not covered: a parent with its own DB or a grandparent in a combined apply, LAD or earlier placement with a parent, the
caller's own DB together with a parent, other routes than standard.
