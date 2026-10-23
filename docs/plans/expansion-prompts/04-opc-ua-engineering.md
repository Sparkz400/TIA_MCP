# OPC UA engineering configuration

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](../release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

Copy this entire file into a planning session, or ask the agent to read and follow it as a planning request.

## Planning prompt

You are planning work in D:\03_Entwicklung\TIA_MCP.

This is a planning-only task. Produce an implementation-ready plan; do not implement features, change production code/configuration, deploy, start or attach to TIA, run native tests, connect to a PLC, or interrupt another session. Read-only source/API/documentation inspection and writing the requested plan are allowed.

Read current AGENTS.md instructions where present, README.md, docs/STATUS.md, docs/TODO.md, contracts/feature-catalog.v1.json, the relevant area plans/guides/evidence, and the current 1.0 checklist. Re-check current ownership and results: other Codex sessions may be editing this workspace, historical unchecked boxes may already be complete, and dated process observations are not current ownership proof. Preserve their work and the existing native admission/simulator scheduling boundaries. Do not spawn agents unless the current user instructions explicitly authorize them.

Inspect installed public V21 XML references and schemas under C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21, then check relevant official Siemens documentation. Distinguish documented API, target availability, bridge implementation and actual qualification. Explore the full useful scope, including less obvious operations and workflows; do not stop at an MVP or the bullets below. Do not invent APIs, use private project internals or interpret API presence as verified support. Record genuine unknowns and the smallest later experiment that can settle each.

Objective: Plan the broadest documented OPC UA engineering support for eligible TIA targets, including interfaces, namespaces, mappings and supported access/security configuration.

Investigate and plan all of the following, expanding this list when the API audit reveals additional useful capabilities:

- Inventory OpcUaProvider, communication groups, server-interface and reference-namespace APIs, supported XML schemas, generation options and target-specific configuration services.
- Plan discovery, exact selection, create/read/update/delete, import/export, enable/disable, rename and node generation wherever public APIs support them. Record differences between SIMATIC and other supported server-interface categories.
- Map exposed nodes to exact PLC declarations and supported methods/types, including arrays, structures, namespaces, NodeIds and data-type compatibility. Detect unresolved mappings and identity drift.
- Investigate documented server security policies, roles/user access, trust/certificate handling and supported client-side engineering/certificate features. Keep certificate lifecycle, credential storage and permissions explicit; do not weaken security to make a fixture pass.
- Define compatibility profiles for CPU/firmware, licensing, interface formats and optional installations. Capability probes must explain missing services and target limitations.
- Reuse project identities, dependency impact, native compilation, checkpoints and journals. Plan previews that expose changes to externally consumed names, node identities and access.
- Qualify native import/export, exact mappings, intended effects, unrelated interfaces, save/reopen, recovery and interrupted changes. Add schema/version mismatch, collisions and stale PLC declarations.
- Separate offline OPC UA configuration from runtime browsing/subscriptions/value access. Define the handoff to the physical-runtime adapter rather than implementing competing connection/session code.

Include a versioned machine-information-model workflow with an intentionally breaking node change and an impact report for external clients.

Start the targeted investigation with:

- Siemens.Engineering.Step7.xml: SW.OpcUa
- contracts/feature-catalog.v1.json
- docs/reference/capabilities.md
- physical runtime planning prompt in this collection

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

Write the finished plan to docs/plans/expansion-plans/opc-ua-engineering.md. If that plan already exists, read it first and extend it without overwriting concurrent work; if ownership is unclear, save a clearly named proposed revision. Only plan artifacts may be written. Return the plan path, the most valuable complete workflow, the main blockers and the recommended first work package.

