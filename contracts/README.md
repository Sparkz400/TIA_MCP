# V1 product contracts

`device-inventory.v1.schema.json` defines immutable device/module pages, explicit availability, configured/actual separation and comparison states. See [device inventory](../docs/reference/device-inventory.md) for bounds and hardware qualification limits.

`opc-ua-workflow.v1.schema.json` defines eight offline Host tools: immutable model/set/report handles, bounded pages/downloads, and explicit false native/completeness flags. The v1/v2 consumer-manifest schemas remain separate. The actual MCP harness validates successful responses and structured refusals against this schema.

`watch-table-authoring.v1.schema.json` defines exact-destination creation/update reviews backed by immutable native instance generations and sequential bounded row/comment edits. See [WT-04 authoring](../docs/reference/watch-table-authoring.md); schema validity does not bypass native mutation-profile gates.

The new offline expansion contracts cover [captured engineering identity](../docs/reference/engineering-capture-identities.md), [watch plans](../docs/reference/watch-table-planning.md), [VCI manifests/conflicts](../docs/reference/vci-planning.md), [translation profiles/reviews/candidates](../docs/reference/translation-review.md), [Test Suite definitions/runs/results/jobs](../docs/reference/testsuite-offline.md) and [source-bound API coverage](../docs/reference/api-coverage-bindings.md). They do not grant native execution or release acceptance.

`engineering-operation.v1.schema.json` describes frozen native-observation plans and durable revisions, resource effects, checkpoint/dispatch/recovery intent and evidence. Runtime transition, hash, ownership and domain-semantic checks remain mandatory; see [the shared substrate](../docs/reference/engineering-operation-journal.md). `translation-inspection-page.v1` and `translation-reconciliation.v1` cover bounded review pages and unapproved reconciliation proposals. `testsuite-scope-coverage.v1` and `testsuite-assessment.v1` bind exact requested/effective scope to independently recomputed result gates. These additive contracts do not close native workflow qualification.

`engineering-mutation-admission.v1.schema.json` describes the shared dispatch reservation for existing change/restore, simulation, library-open, scratch-create and project-retrieve routes. It binds the complete native-resolved scope, request/observation hashes and retained response or reconciliation evidence. `returned` ends dispatch ownership only; it does not assert engineering verification or restoration. Historical unresolved legacy journals require explicit migration/reconciliation.

These contracts connect the feature catalog, object model, capability probes and generated conformance fixtures. They describe bridge support and evidence, not unrestricted support for every TIA editor operation.

`project-model.v1.schema.json` defines the additive explorer snapshot, native/virtual nodes, exact session-bound selections, scoped metadata freshness, separately captured content and evidence-backed relationships. `project-model-provider.v1.schema.json` defines the HMI and typed graphical attachment boundary. Both keep the original `project_index` contract unchanged. See [project explorer](../docs/guides/project-explorer.md) and its recorded qualification results.

Library intake and native changes use separate `library-package.schema.json` and `library-change-journal.schema.json` contracts. A ready package is classified and hashed; Siemens still has to validate the selected native container. Library session IDs expire on detach, while native type/version GUIDs and archived recovery evidence retain their separate identities. A journal's last dispatch may have completed before a lost response; it never permits automatic mutation replay. See [library management](../docs/guides/library-management.md).

Library observation contracts cover identity, lock receipts, preview/change
envelopes, paged inspection, export responses and export manifests through the
`library-*.v1.schema.json` files. `scripts/validate_library_observations.py` checks
original bytes and cross-field consistency using a local schema registry. Export
file paths are retained metadata and are never opened. Exact decimal comparison
prevents float rounding from hiding altered preview payloads. Schema acceptance
does not establish native success. `library-operation.v1.schema.json` adds a
`{kind, payload, result?}` projection for all 18 operation kinds, including the
plan-only PLC recipe and separate Classic/Unified branches. Explicit validator
modes `operation-plan` and `operation-change` apply it to full observations and
check their cross-field relationships. Original `plan`/`change` modes remain
envelope-only. Detailed preview analyses, compiler evidence and HMI library-query
responses remain outside this operation contract.
Unified internal-tag results can retain `defaultConnection`, `plcTag` and
`nativeConnectionResolved` together. Localized labels require matching original
and returned values, no PLC binding and a false native connection lookup; legacy
results without this evidence require an empty/null connection. These retained
observations do not grant native qualification.

`library-hmi-query.v1.schema.json` covers the inner `library_query` HMI request
and its capability, target and Classic/Unified inventory responses. Use validator
modes `hmi-request`, `hmi-capabilities`, `hmi-targets` and `hmi-inventory`.
Cross-field checks preserve module identity/availability, exact compiler owner
scope and distinct inventory truncation boundaries. They do not infer licensing,
complete references, requested limits, native resolution or runtime behavior.

`library-compilation.v1.schema.json` describes library compiler aggregates with
current device-path identity, legacy device-name scope or one HMI target. Use
`compiler` for the aggregate, `compiler-change` for operation plus compiler
validation, and `compiler-lock` for an issued lock's compiler observation.
Errors, unknown states and truncation remain valid standalone observations;
applied project changes and issued project locks require successful complete
diagnostics. Scope binding is reported separately from schema validity and
never establishes native qualification.

`library-update-analysis.v1.schema.json` covers the three project-update preview
sections: `updateImpact`, `updateBaseline` and `updateCleanupGuard`. Explicit
`update-plan` and `update-change` modes add operation validation, dependency/use
binding and graph traversal of recorded reference edges. Changed callers cannot
supply proof edges; all baseline library uses must be reachable from the reported
OB roots. Missing native comparisons retain an incomplete result. These modes
do not execute compiler checks, independently identify native OBs/references,
validate post-update readback or prove arbitrary content preservation. Use the
separate compiler modes when checking compiler evidence.

`library-synchronization.v1.schema.json` covers copy/publication synchronization
rows and effects. `sync-plan` and `sync-change` add operation validation, source
release/type closure checks, non-forcing default selection and returned-to-planned
version/default comparisons. Dependencies of older source releases are included;
master-copy dependency scans do not imply those types were synchronized.
The complete destination-before version inventory is not retained, so local
validation cannot independently reconstruct that side of the union. Native
synchronization and master-copy content verification remain false.

`library-preview-effects.v1.schema.json` describes operation effects, recorded
source identities and recovery/component-content descriptors. `preview-plan`
and `preview-change` combine effect/dependency/selector consistency with the
appropriate specialized preview checks; change mode also validates compiler
evidence. Native selectors and referenced bytes remain unverified.
[Verification and scope](../docs/verification/library-preview-effects-20261023.md).

`library-component-preview.v1.schema.json` covers retained PLC integration plans.
`component-plan` and `component-change` bind dependency/version sets, content-pin
descriptors, interface/tag observations, exact proposed call graphs and recovery
boundaries. They do not read referenced XML, reconstruct omitted inventory or
authorize native execution. [Verification](../docs/verification/library-component-previews-20261023.md).

`library-hmi-preview.v1.schema.json` covers retained Classic copy and Unified
tag/create/bind target previews. `hmi-plan` and `hmi-change` add operation checks,
selected-version/dependency binding, effect/placement consistency and separate
readback/layout reports. Source selector, native interface type, complete collision
absence and preservation claims remain false. See [scope and verification](../docs/verification/library-hmi-previews-20261023.md).

HMI uses `hmi-identity.v2.schema.json`: exact project copy/session, family, hardware owner and HMI software/object paths, with no `plcPath`. Inspection and relationship requests remain version 1 in `hmi-inspection.v1.schema.json` and `hmi-references.v1.schema.json`; an identity version is not a request version. Engineering payloads and the library destination handoff have separate `hmi-engineering.v1.schema.json` and `hmi-library-handoff.v1.schema.json` contracts. A caller-supplied handoff or fingerprint cannot override unavailable server preservation/qualification gates. See [HMI integration](../docs/guides/hmi-integration.md).

Reusable runtime sequences and executor evidence have separate `block-test-suite.schema.json` and `block-test-result.schema.json` contracts. See [block testing](../docs/guides/block-testing.md). A passed application-call sequence is separate from scan synchronization; timing observations retain their host-polling limits. These schemas describe the executor API; offline preparation plans retain `runtimeVerified: false`.

Offline bug/trace diagnostics use `diagnostic-case.v1`, `diagnostic-signal-manifest.v1`, `trace-capabilities.v1`, `trace-plan.v1`, `trace-measurement.v1` and `diagnostic-report.v1` schemas. They describe saved evidence, manual specifications and normalized measurements; they are not Siemens configuration/exchange schemas. Native lifecycle and target observability remain unqualified. See [trace diagnostics](../docs/guides/trace-diagnostics.md).

The additive `trace-csv-options.v1`, `diagnostic-replan-request.v1`, `trace-comparison.v1`, `trace-readback.v1` and `trace-readback-check.v1` contracts cover explicit text mapping, revalidated instance reuse, event-aligned comparison and independently supplied readback observations. A successful readback comparison remains an offline artifact result, not live/native qualification. Automatic signal evidence and signed pre-trigger timestamps are additive to the original diagnostic contracts.

- `featureId` is the exact MCP tool name, or `change.<kind>` for a prepared engineering operation. Backlog features use stable descriptive IDs and have no implemented tool binding.
- PLC object identity v1 contains `projectPath`, `plcPath`, `scopePath`, `objectPath`, `kind` and `name`. HMI identity v2 has its own owner fields described above. Canonical paths are case-sensitive identifiers issued by this worker. Names alone are not unique. Paths can change on rename, move or archive retrieval and are not permanent vendor GUIDs.
- API availability, bridge implementation, qualification evidence and current target availability are separate fields. Recorded fixture evidence does not establish qualification of an arbitrary selected object.
- Target probes are observational. Read/write attribute metadata is not authorization to bypass typed mutation validation, protection, previews or checkpoints.
- Fixture manifests describe inputs and independently specified expectations. Result statuses are `passed`, `failed`, `blocked` or `skipped`. Compilation is evidence of compilation only; expected runtime vectors remain unverified until executed and asserted.
- Schema version 1 is additive only. Changes to identity semantics or existing field meanings require another version. Unknown fields may be preserved; unknown enum values must not be treated as success.

The schemas use JSON Schema Draft 2020-12. Register the included documents by their `$id` in the validator's local schema registry; the catalog references the capability schema's URN. The implementation's contract tests additionally check cross-file feature/evidence references and exact public tool/change-kind coverage. Verified qualification requires passed evidence; failed, blocked or skipped evidence cannot establish verified support. Schema validation alone cannot establish native TIA behavior.

[`release-matrix.v1.json`](../docs/plans/release-matrix.v1.json) is the frozen V21 release matrix, generated from the catalog and the [release-obligation ledger](../docs/plans/release-obligations-20261015.json) by `python -B scripts/release_matrix.py`. Each catalog feature is a row with its support class (shipped, limited, unqualified, unavailable target, deferred) and owning obligation. Each acceptance criterion is a cell, bound to evidence only when the feature is verified, with the SHA-256 of every retained evidence artifact. Regenerate it after changing the catalog, ledger or evidence summaries; `scripts/test_release_matrix.py` fails on a stale copy.

- `technology-profile-review.v1.schema.json`: version-discriminated v1/v2/v3 supplied TO profile/parameter/relationship requests and records, independent source bundles, v3 declared consumers and bounded saved impact context; old v1/v2 records are unchanged, with no native authorization or compatibility claim.

- `safety-evidence-review.v1.schema.json`: supplied exact-PLC safety observations, UInt64 decimal displays, opaque supporting source descriptors, immutable records and bounded pages. Native execution, required signature coverage and safety approval remain false. See [the workflow](../docs/reference/safety-evidence-review.md).
