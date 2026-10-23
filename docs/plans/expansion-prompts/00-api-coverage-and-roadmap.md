# Complete API coverage map and expansion roadmap

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](../release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

Copy this entire file into a planning session, or ask the agent to read and follow it as a planning request.

## Planning prompt

You are planning work in D:\03_Entwicklung\TIA_MCP.

This is a planning-only task. Produce an implementation-ready plan; do not implement features, change production code/configuration, deploy, start or attach to TIA, run native tests, connect to a PLC, or interrupt another session. Read-only source/API/documentation inspection and writing the requested plan are allowed.

Read current AGENTS.md instructions where present, README.md, docs/STATUS.md, docs/TODO.md, contracts/feature-catalog.v1.json, the relevant area plans/guides/evidence, and the current 1.0 checklist. Re-check current ownership and results: other Codex sessions may be editing this workspace, historical unchecked boxes may already be complete, and dated process observations are not current ownership proof. Preserve their work and the existing native admission/simulator scheduling boundaries. Do not spawn agents unless the current user instructions explicitly authorize them.

Inspect installed public V21 XML references and schemas under C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21, then check relevant official Siemens documentation. Distinguish documented API, target availability, bridge implementation and actual qualification. Explore the full useful scope, including less obvious operations and workflows; do not stop at an MVP or the bullets below. Do not invent APIs, use private project internals or interpret API presence as verified support. Record genuine unknowns and the smallest later experiment that can settle each.

Objective: Create a comprehensive, evidence-backed plan for extracting the full useful capability of the installed TIA Portal V21 public APIs and related supported Siemens interfaces into TIA_MCP.

Investigate and plan all of the following, expanding this list when the API audit reveals additional useful capabilities:

- Inventory every installed public API module, XML reference, relevant schema and optional-product assembly. Cover Base, Step7, WinCC Classic and Unified, Safety, SafetyValidation, Startdrive, TestSuite, Add-Ins, CFC, DCC and TeamcenterGateway where present. Investigate documented multiuser/project-server, library, hardware, networking, communication, security, online, CAx, VCI and technology services. Treat missing installations as a distinct finding.
- Build a reproducible mapping from public services/types/actions and meaningful attributes/compositions to user workflows, existing MCP tools/change kinds, implementation files and qualification evidence. Deduplicate inherited boilerplate; method counts are not useful feature coverage percentages.
- Cross-check installed metadata with the full official manual, examples and schema-defined import/export capabilities. Identify interfaces omitted from the existing feature catalog. Design future target probes for dynamic metadata that static references cannot establish.
- Cover all seventeen domain prompts in this collection and identify additional domains they miss. Include the user-prioritized bug-description-to-FB-instance-to-native-trace workflow in prompt 17, with an explicit feasibility finding for general native trace automation. Assess native TIA Add-Ins, optional engineering languages/packages, project collaboration and separate adapters for other TIA versions as distinct workstreams, without assuming API compatibility.
- Separate engineering Openness, PLCSIM Advanced, CPU Web API, OPC UA runtime and optional product interfaces. Explain which user outcomes need combinations of them.
- Trace each remaining gap to new implementation, missing qualification, unavailable prerequisites, a genuine public API limitation, or an explicit product decision. Identify capabilities that already exist under a different tool name.
- Produce a maintained machine-readable coverage-registry design and a change-detection process for Siemens updates. Show how it feeds capability discovery, documentation and test selection without converting API presence into support.
- Reconcile cross-domain ownership, dependencies, shared schemas and native qualification capacity into one ordered roadmap. Apply the user's current 1.0 scope decision: every feasible extension is required before release. Resolve feasibility explicitly and keep missing prerequisites visible as blockers.

Include an explicit completeness statement: inventories examined, semantic grouping method, dynamic/uninstalled surfaces not observed, and how remaining blind spots will be closed.

Start the targeted investigation with:

- contracts/feature-catalog.v1.json
- docs/reference/capabilities.md
- docs/plans/overview.md
- installed PublicAPI/V21/net48/*.xml and associated schemas

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

Write the finished plan to docs/plans/expansion-plans/api-coverage-and-roadmap.md. If that plan already exists, read it first and extend it without overwriting concurrent work; if ownership is unclear, save a clearly named proposed revision. Only plan artifacts may be written. Return the plan path, the most valuable complete workflow, the main blockers and the recommended first work package.

