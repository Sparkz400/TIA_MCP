# TIA Openness expansion planning prompts

**Execution update — 15 October 2026:** Prompts **02, 01, 03 and 06** have now produced their standalone plans. See [current wave results](../../verification/ac01-planning-wave-20261015.md) for outputs, owner handoffs and the next implementation packages. Planning completion is not native qualification or release acceptance.

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](../release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

These 18 independent prompts turn the API expansion opportunities into implementation-ready plans. They cover all ten main additions, all five further areas, physical PLC runtime, bug-driven trace diagnostics and an overall API coverage roadmap.

Each file is a complete prompt with its own project context, research requirements, domain scope, required deliverables and plan destination. No shared preamble is needed. Copy a whole file into a planning session, or give the agent its path and ask it to follow the planning prompt.

The prompts authorize planning and plan-document writes only. They preserve existing implementation workstream ownership and require current status checks. Every feasible capability they identify is a 1.0 obligation under the user's 15 October decision. Running a planning prompt still does not launch implementation or native work.

## Prompts

| Prompt | Scope |
| --- | --- |
| [00 Complete API coverage map and expansion roadmap](00-api-coverage-and-roadmap.md) | a comprehensive, evidence-backed plan for extracting the full useful capability of the installed TIA Portal V21 public APIs and related supported Siemens interfaces into TIA_MCP. |
| [01 VCI and Git engineering workflows](01-vci-and-git.md) | complete, maintainable VCI integration and an external Git workflow for supported TIA objects, from discovery and review through guarded synchronization and automated verification. |
| [02 Watch and force table engineering](02-watch-and-force-table-engineering.md) | full supported offline watch/force-table engineering and useful debugger-table generation through MCP, with live monitoring and active forcing treated as separate capabilities. |
| [03 Project text and translation management](03-project-translations.md) | comprehensive project-text extraction, translation review, consistency analysis and preserving reimport for supported PLC and HMI text categories. |
| [04 OPC UA engineering configuration](04-opc-ua-engineering.md) | the broadest documented OPC UA engineering support for eligible TIA targets, including interfaces, namespaces, mappings and supported access/security configuration. |
| [05 AutomationML and CAx hardware exchange](05-automationml-cax.md) | comprehensive public CAx/AutomationML integration for hardware exchange, comparison and controlled reconciliation with ECAD-derived configurations. |
| [06 Native Siemens Test Suite integration](06-native-test-suite.md) | complete supported integration with Siemens Test Suite style-guide, application-test and system-test APIs, complementing the existing custom conformance/runtime harnesses. |
| [07 Software unit lifecycle and modular projects](07-software-unit-lifecycle.md) | full supported software-unit lifecycle and modular project composition, extending the existing ability to inspect and edit objects inside selected units. |
| [08 Communication connection engineering](08-communication-connections.md) | a complete target-scoped adapter for documented communication connections, beyond the existing subnet and distributed-I/O assignment operations. |
| [09 Online comparison DB snapshots and station upload](09-online-compare-snapshots-upload.md) | supported online diagnostic and recovery workflows for exact PLC targets using native comparison/fingerprints, DB snapshots and station upload, clearly separated from current simulation-only online operations. |
| [10 Startdrive and drive engineering](10-startdrive.md) | the broadest useful documented Startdrive adapter for eligible SINAMICS configurations, from offline inspection and configuration through separately qualified online workflows. |
| [11 PLC alarms text lists and ProDiag](11-plc-alarms-prodiag.md) | full supported PLC alarm and ProDiag engineering, including text resources, supervision configuration, dependencies and integration with diagnostics and HMI views. |
| [12 PLC web applications and access configuration](12-web-server-access-configuration.md) | supported PLC web-server engineering and related access/certificate configuration as a precise, auditable subsystem. |
| [13 Motion and technology object expansion](13-motion-and-technology-objects.md) | systematic coverage of documented technology-object families, version profiles, connections and mappings beyond the existing selected scalar/encoder fixtures. |
| [14 Safety validation tooling and F IO configuration](14-safety-validation-and-fio.md) | documented safety inspection, evidence reporting, Safety Validation Assistant integration and narrowly typed F-I/O engineering, with explicit separation from functional safety approval. |
| [15 SiVArc assisted HMI generation](15-sivarc-hmi-generation.md) | the fullest documented SiVArc automation workflow for generating maintainable HMI content from PLC structures, templates and generation rules. |
| [16 Physical PLC monitoring and runtime adapters](16-physical-plc-runtime.md) | a target-scoped physical PLC runtime layer using supported CPU Web API and OPC UA interfaces, beginning with read-only diagnostics and preserving a path to explicitly scoped later operations. |
| [17 Bug-driven FB discovery and native trace diagnostics](17-bug-driven-trace-diagnostics.md) | turn a bug description into evidence-backed FB/instance discovery, signal selection, trace configuration and measurement analysis; establish the supported native automation route before claiming trace creation. |

## Suggested use

Start with **00** to find omitted APIs, resolve overlaps and establish the shared roadmap. The user has identified **17 bug-driven trace diagnostics** as important: investigate its native automation feasibility early, alongside planning bug-to-instance and signal resolution. **01 VCI/Git**, **02 watch tables**, **03 translations** and **06 Test Suite** are also useful early planning candidates. The final order should follow the current project's dependencies and qualified target availability.

Coordinate the linked domains explicitly:

- OPC UA engineering (**04**) owns project configuration; physical runtime (**16**) owns live protocol access.
- CAx (**05**) and communication connections (**08**) share hardware/network identities and preservation rules.
- Online diagnostics (**09**) and runtime (**16**) share CPU identity and connection policy but use different interfaces.
- Bug-driven trace diagnostics (**17**) reuses static discovery and the shared identities from **02**, **09** and **16**; native recording, polling and offline reference tracing remain separate capabilities. Coordinate drive/safety trace scope with **10** and **14**.
- Startdrive (**10**) and motion/TOs (**13**) share drive/telegram/encoder relationships.
- Translations (**03**), alarms/ProDiag (**11**) and SiVArc (**15**) integrate with the existing HMI and library owners.
- Test Suite (**06**) and safety tooling (**14**) preserve separate execution and acceptance scopes.
- Software-unit lifecycle (**07**) reuses the typed and library workstreams' scope and dependency models.

Each resulting plan belongs under `docs/plans/expansion-plans/`. The prompts require exact APIs and source references, target prerequisites, a coverage matrix, proposed tools/schemas, ordered work packages, independent qualification criteria, recovery/scale tests and a concrete first implementation slice.

## Scope and evidence

This prompt collection was prepared on 13 October 2026. It is a planning input, not evidence that a capability is implemented or qualified. Re-read the live [TODO](../../TODO.md), [status](../../STATUS.md) and [release checklist](../../history/1.0-release-checklist-20261013.md) when starting a session.

Useful source entry points are the installed V21 PublicAPI XML/schema files and Siemens' [V21 Openness capability additions](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/what-s-new-in-tia-portal-openness). Every domain prompt requires verification of actual public routes and target limits, including explicit findings when an optional product or API is unavailable.

