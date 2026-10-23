# Supplied software-unit review

This offline Host workflow compares explicitly supplied unit facts and proposed
edits against one exact saved project review and PLC. It works without Siemens
products. It does not discover current unit relations, authenticate supplied facts,
compile a project, determine native visibility, or execute any proposed change.

## Workflow

1. Import an existing `project-review.v1` through `artifact_upload_*` and
   `project_snapshot_import`, or select an already saved `reviewId`.
2. Prepare a `software-unit-review-request.v1` JSON document. Every selector must
   use the exact captured path. The schema is
   `contracts/software-unit-review.v1.schema.json` (`$defs.request`). Include
   explicit coverage flags, unit relations, object namespaces/publication,
   declared dependencies and a sequential list of proposed changes. Empty
   collections and unknown/null observations must be explicit.
3. Upload the document with `artifact_upload_begin`, `artifact_upload_chunk` and
   `artifact_upload_commit`. Call `software_unit_review` with the exact
   `reviewId` and committed `uploadRef`.
4. The receipt identifies immutable original input and report-envelope artifacts.
   `artifact_read` accepts their `path` fields and verifies retained bytes.
5. Use `software_unit_review_page(recordId, section, offset, count)` for
   `summary`, `units`, `objects`, `relations`, `dependencies`, `cycles`,
   `collisions` or `changes`. Originals survive upload release and Host restart.
   Reopening verifies the input and saved review hashes and derives the report
   again; altered report content refuses even when rehashed under a new name.

`tests/TiaMcp.SoftwareUnits.Tests` emits a synthetic `mcp-fixture.json` containing
an executable example packet and a saved review. The actual MCP harness imports
that review, uploads the packet, pages and downloads results, then restarts the
Host. These fixtures are explicitly synthetic, not original engineering evidence.

## Interpretation

A unit row contains `objectPath`, nullable `namespacePreset`, `relationsComplete`
and `relations`. Relations contain `relationType`, `relatedObject` (the exact
reported name) and nullable `targetPath`. A name alone never resolves an endpoint.
Unknown types and absent targets remain visible as unresolved observations.

Objects contain exact `objectPath`, nullable `namespace` and `publication`:
`Published`, `Unpublished`, `unknown` or `notApplicable`. Dependencies contain
`sourcePath`, nullable `targetPath`, `targetName` and a supplied `reason`.

Supported proposed edits are `setPublication`, `setNamespacePreset`,
`addRelation` and `removeRelation`. They apply sequentially to the review model.
Publication edits require a captured supported block/type; OBs and known safety,
protected or generated objects refuse. New relations require exact compatible
SoftwareUnit, NonUnitDB or TODB targets. Unit creation, movement, deletion,
templates and native application are outside this tool.

The dependency report distinguishes missing direct relations, incomplete relation
coverage, unknown or unpublished targets, absent paths, unsupported targets and
unsupplied target units. A `declaredPrerequisitesPresent` result means only that
the supplied direct prerequisites match this model. A->B->C does not imply A->C.
Cycles are graph observations; same-kind namespace/name collisions are candidate
ambiguities, not certified compiler errors. Qualified names never select a path.

Changing a namespace preset leaves every supplied member namespace unchanged.
The report does not simulate the separate native action that applies a preset to
existing elements. Consequently collision results describe those retained member
namespaces, even after a proposed preset edit.

Completeness flags are supplied attestations. `unitsComplete` and `objectsComplete`
refuse omissions of already captured standard units/members, but cannot establish
uncaptured project inventory. `dependenciesComplete` is not independently
verifiable. `wholeProjectAnalysisComplete`, `nativeExecutable`, `nativeQualified`,
`sourceAuthenticityVerified` and `liveFreshnessVerified` always remain false.

## Bounds and retained evidence

Input: 4 MiB strict UTF-8 JSON, depth 32, no duplicate/unknown fields or control
characters. At most 128 units, 2,048 observed relations in total, 2,048 objects,
4,096 dependencies and 128 proposed edits. All collections/fields are explicit.
Reports are capped at 16 MiB; retained input/report storage is capped at 512 MiB,
including incomplete publication files. Existing immutable files are never replaced.

Pages contain 1..200 indexed rows within 128 KiB. A single oversized row is marked
omitted, with its original index and a reason; the complete downloadable report
retains that row. Pagination is pinned to `recordId`, with no implicit latest record.
Artifact downloads use the existing bounded `artifact_read` protocol.

## Documented basis and remaining work

V21 documents `PlcUnitRelation.RelatedObject` as a string name. Creating a relation
to an absent name can succeed, so inspection does not itself establish endpoint
existence. [Siemens V21 relations](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-software-units/updating-existing-relations-create/delete-relations).

Publication applies to unit blocks/data blocks except OBs, and PLC types.
[Siemens V21 publication](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-software-units/publishing-software-unit-object).

The preset and applying it to existing elements are distinct actions; member
renaming requires additional native rules and validation.
[Siemens V21 namespace presets](https://docs.tia.siemens.cloud/r/en-us/v21/using-software-units-s7-1500/creating-and-managing-software-units-s7-1500/changing-the-namespace-preset-of-a-software-unit-s7-1500).
The direct-relation/publication access prerequisite is described by the available
[V20 relation guide](https://docs.tia.siemens.cloud/r/en-us/v20/using-software-units-s7-1500/creating-relations-s7-1500/setting-up-relations-s7-1500).
That guide is version-labelled; this workflow has no V21 native qualification.

See [D07 remaining plan](../plans/expansion-plans/software-unit-lifecycle.md) and
[verification](../verification/software-unit-review-20261016.md). No SDK worker
or native mutation route has been added or promoted by this offline workflow.
