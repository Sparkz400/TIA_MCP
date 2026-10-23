# AutomationML and CAx hardware exchange

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](../release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

Copy this entire file into a planning session, or ask the agent to read and follow it as a planning request.

## Planning prompt

You are planning work in D:\03_Entwicklung\TIA_MCP.

This is a planning-only task. Produce an implementation-ready plan; do not implement features, change production code/configuration, deploy, start or attach to TIA, run native tests, connect to a PLC, or interrupt another session. Read-only source/API/documentation inspection and writing the requested plan are allowed.

Read current AGENTS.md instructions where present, README.md, docs/STATUS.md, docs/TODO.md, contracts/feature-catalog.v1.json, the relevant area plans/guides/evidence, and the current 1.0 checklist. Re-check current ownership and results: other Codex sessions may be editing this workspace, historical unchecked boxes may already be complete, and dated process observations are not current ownership proof. Preserve their work and the existing native admission/simulator scheduling boundaries. Do not spawn agents unless the current user instructions explicitly authorize them.

Inspect installed public V21 XML references and schemas under C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21, then check relevant official Siemens documentation. Distinguish documented API, target availability, bridge implementation and actual qualification. Explore the full useful scope, including less obvious operations and workflows; do not stop at an MVP or the bullets below. Do not invent APIs, use private project internals or interpret API presence as verified support. Record genuine unknowns and the smallest later experiment that can settle each.

Objective: Plan comprehensive public CAx/AutomationML integration for hardware exchange, comparison and controlled reconciliation with ECAD-derived configurations.

Investigate and plan all of the following, expanding this list when the API audit reveals additional useful capabilities:

- Inventory CaxProvider import/export variants, transfer results, merge options, supported AML schemas and documented object/attribute coverage.
- Cover whole-project and device-level export, supported pruned/partial exchange, devices/modules/submodules, racks, networks, port topology, addresses, tags and library references.
- Investigate documented GSD/GSDML attributes, IO-Link, extension racks, F-I/O representations, custom attributes and companion files. Define exact import/export support by profile rather than claiming universal AML compatibility.
- Plan read-only AML inspection and comparison before mutation, with exact native identities, external identifiers, intended additions/changes/deletions and conflict explanations.
- Handle catalog/HSP/GSD prerequisites, unknown type identifiers, replacement mappings, duplicate names, address overlap, topology conflicts, absent referenced objects and unsupported vendor extensions.
- Integrate with current hardware/network operations and preservation manifests. Identify when native CAx merge can have wider effects than individual prepared changes and require an appropriate whole-project checkpoint.
- Plan native roundtrips with independent expected topology/configuration, source-package preservation, complete transfer diagnostics, full affected compilation, save/reopen and recovery.
- Define bounded large-file parsing, external-reference handling, traversal/expansion limits, payload storage and artifact transfer. Distinguish configured topology from observed physical wiring.

Include an ECAD-to-TIA update example, a TIA-to-ECAD export example and a conflict case in which an externally changed module would invalidate existing I/O mappings.

Start the targeted investigation with:

- Siemens.Engineering.Step7.xml: CaxProvider and CaxImportOptions
- installed V21 CAx schemas and hardware parameter descriptions
- docs/guides/network-inspection.md
- docs/reference/engineering-coverage.md

These are starting points, not an exhaustive API list. Confirm names and applicability against the installed version.

Required plan output:

1. Current implementation and evidence audit, with exact source paths, overlaps with existing sessions and reusable components.
2. Capability matrix: user workflow; exact public API/service/schema and source; target/version/product prerequisites; existing implementation; remaining work; qualification state; limits. Include missing and infeasible capabilities.
3. Proposed architecture and MCP contracts: tools, prepared changes where appropriate, schemas, identity/freshness, capability/error reporting, bounded artifacts and host/worker boundaries. Label new tool names as proposals.
4. Dependency-ordered work packages with stable IDs, concrete file/module boundaries, inputs, outputs, completion criteria, effort ranges with assumptions, and critical-path risks. Separate implementation from qualification and list ownership conflicts before suggesting concurrent work.
5. Native/protocol qualification plan using independent expectations: exact effects, untouched-content preservation, compile where applicable, fresh reopen, checkpoint recovery, interruption/unknown outcomes, scale and target compatibility. Reuse accepted evidence within its exact scope; do not schedule a redundant VM rerun contrary to the current project decision.
6. Phases: discovery/feasibility, first complete useful workflow, full supported expansion, and release acceptance. Treat every feasible capability as a 1.0 obligation; missing prerequisites and unresolved feasibility stay open rather than becoming deferred scope.
7. Specific blocked decisions, unavailable prerequisites, impossible public-API routes and a recommended next executable work package. Make reasonable assumptions for routine design choices and label them; ask only questions that materially block the plan.

Prefer concrete engineering decisions and testable outcomes over generic task lists. Bind future qualification to source, fixture, profile and binary identities; skipped or blocked work never counts as passed. Do not claim this planning task has executed any native acceptance.

Write the finished plan to docs/plans/expansion-plans/automationml-cax.md. If that plan already exists, read it first and extend it without overwriting concurrent work; if ownership is unclear, save a clearly named proposed revision. Only plan artifacts may be written. Return the plan path, the most valuable complete workflow, the main blockers and the recommended first work package.

