# Bug-driven FB discovery and native trace diagnostics

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](../release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

Copy this entire file into a planning session, or ask the agent to read and follow it as a planning request.

## Planning prompt

You are planning work in D:\03_Entwicklung\TIA_MCP.

This is a planning-only task. Produce an implementation-ready plan; do not implement features, change production code/configuration, deploy, start or attach to TIA, run native tests, connect to a PLC, or interrupt another session. Read-only source/API/documentation inspection and writing the requested plan are allowed.

Read current AGENTS.md instructions where present, README.md, docs/STATUS.md, docs/TODO.md, contracts/feature-catalog.v1.json, relevant area plans/guides/evidence, and the current 1.0 checklist. Re-check current ownership and results: other Codex sessions may be editing this workspace, historical unchecked boxes may already be complete, and dated process observations are not current ownership proof. Preserve their work and the existing native admission/simulator scheduling boundaries. Do not spawn agents unless the current user instructions explicitly authorize them. Existing pauses on native UI work remain in force.

Inspect installed public V21 XML references and schemas under C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21, then check relevant official Siemens documentation. Distinguish documented API, target availability, bridge implementation and actual qualification. Do not invent APIs, use private project internals or interpret API presence as verified support. Record unknowns and the smallest later experiment that can settle each.

Objective: Plan a complete diagnostic workflow in which the user explains a bug, the agent finds the relevant PLC code and exact FB instance, selects justified observable variables, prepares and where supported creates a native TIA trace with useful triggering and sampling, then analyzes the measurement against the original hypotheses. The user has explicitly identified this workflow as important. Treat it as a priority planning candidate; distinguish proposed release scope from existing 1.0 commitments.

## Starting evidence to re-check

- Existing block_read, block_interface, software_inspect, project snapshot search, xref, call_graph and symbol_trace provide reusable discovery and static-analysis building blocks. Audit their actual contracts, scope limits and evidence before claiming exact instance resolution.
- symbol_trace follows offline symbol references and readers/writers. It does not record time-series measurements. The current sim_test backend uses wall-clock polling and explicitly reports non-atomic samples across symbols and no scan accuracy. Controlled application calls in the block-testing harness are also not scan-synchronized acquisition.
- The 13 October 2026 source audit found no general native PLC trace creation/configuration/recording MCP adapter. Re-check current work before assigning a new implementation owner.
- The reviewed V21 XML references did not establish a general public STEP 7 trace creation API. SafetyValidation.SafetyFunction.TraceConfiguration is a separate safety-test feature; its presence does not establish general PLC trace support. This is an unresolved capability boundary, not proof that every possible integration route is impossible.
- Siemens documents native trace configuration export as .ttcfgx through the TIA user interface. A file extension and an interactive import/export workflow do not establish a supported generation schema or an Openness automation route.

## Required diagnostic workflow

1. Interpret the report: expected behavior, actual behavior, affected equipment, timing, reproducibility, operating mode, recent changes and any known PLC/FB/tag/alarm names. Separate user observations, evidence and hypotheses. Use available project context before asking questions; request only missing information that materially changes target or signal selection.
2. Find and rank candidate code using names, comments, code expressions, call relationships, HMI bindings, alarms, library/type identities and I/O mappings. Cover supported SCL, STL, LAD/FBD and other language evidence without pretending every language is equally inspectable. Explain why each candidate fits the symptom and expose protected or unavailable source.
3. Resolve the exact CPU, software unit/namespace, block identity, call site, instance DB and nested multi-instance path. Handle the same FB used by several machines or PLCs, repeated calls and shared instance data. Never treat a block-local name such as #Enable as a complete online address. Retain unresolved alternatives instead of silently selecting the first match.
4. Select a small sufficient signal set from backward dependencies and downstream effects: command, feedback, interlocks, state/step, error status, relevant timer/counter state, I/O values and competing writers. Record the code/reference evidence and diagnostic reason for every selected signal. Expand only when a hypothesis or unresolved dependency warrants it.
5. Validate observability per backend and target: supported types, optimized DBs, arrays/structures, IN/OUT/IN_OUT bindings, aliases, indirect access, multi-instance members, TEMP values, protected data and access settings. Do not assume every source variable has a stable traceable address. Report unavailable signals and supported substitutes with changed diagnostic coverage. Any instrumentation proposal is a distinct later code change with timing implications.
6. Produce a reviewable TracePlan containing symptom, hypotheses, exact target/project/program identities, source revision, call-site and instance evidence, signal paths/types, inclusion reasons, unresolved coverage, requested acquisition mode, trigger, pre/post-trigger window, sampling task/cycle, duration, resource limits and expected diagnostic result. Derive settings from the phenomenon and verified target limits; do not invent universal sample rates or compound-trigger support.
7. Where a supported route exists, create/configure the trace and independently read back its signals and settings. Keep offline configuration, transfer to device, arming, recording, stopping, retrieval and deletion distinct. Preserve user-created traces and measurements, handle exhausted trace slots and connection loss, and report unknown outcomes without blindly retrying mutations.
8. Retrieve or import supported measurement formats, preserve timestamps and units, identify dropouts and sampling uncertainty, and align events with the selected code revision and hypotheses. Separate observed order/correlation from proven cause. Return confirmed, contradicted and unresolved hypotheses plus the next useful capture. A capture that misses the fault is not evidence that the bug is fixed.

## Trace backend feasibility and full scope

Inventory documented APIs and supported exchange formats for ordinary PLC traces, long-term traces, project/multi-device traces and relevant drive traces. Record exact CPU/firmware/TIA/product/license prerequisites, traceable data types, trigger features, sample timing, memory limits and acquisition effects. Treat each family as a separately qualified capability; do not extrapolate Safety Validation or Startdrive support to ordinary PLC traces.

Compare the following routes honestly: a documented public native API; a documented external configuration schema and supported import route; separately qualified UI assistance if later authorized; a generated exact manual-setup specification; and polling through the existing simulator or future physical runtime adapters. Template-based generation is only a candidate until its permitted fields, format compatibility, supported import and native readback are established. Do not manipulate private project storage or claim undocumented file editing as public API support.

Build the maximum useful workflow even if native automation is blocked, but mark a trace specification/manual handoff as partial fulfillment of the user's native-creation request. Polling is a separate acquisition mode and must expose its inability to guarantee detection of a one-scan glitch. Reject requirements a backend cannot meet. Any newly researched scan-synchronous or buffered route needs independent qualification.

Plan reusable diagnostic templates for intermittent stops, missed edges, timer behavior, interlocks, sequence stalls, competing writers, analog threshold noise and communication timeouts. Include signal-budget reduction, repeated captures, comparison before/after a fix, reusable machine-instance substitution with revalidation, bounded retention and exportable diagnostic reports. Expand with other useful supported capabilities discovered during research.

Coordinate with prompts 02 (watch tables), 06 (Test Suite), 09 (online comparison), 10 (Startdrive), 11 (alarms), 14 (safety) and 16 (physical runtime). Share symbol/target identities, signal manifests, acquisition quality and measurement artifacts. Avoid implementing a competing PLC transport or conflating watch tables, offline reference tracing and native recordings.

## Concrete acceptance scenario

Use a controlled fixture with two instances of the same conveyor FB. The report is: "Conveyor 2 occasionally stops although its enable command remains true." The fixture includes a nested instance, an interlock or timer condition and a separate writer or feedback path. Require the plan to discover the relevant instance from evidence, map the actual fixture paths and propose signals covering enable, output command, feedback, interlock, step, timer state and fault status where observable. These names describe roles, not assumed project symbols.

Specify independent expected results for correct FB/instance selection, signal resolution, settings readback and a captured injected event. Include negative cases for the other conveyor, ambiguous equipment names, stale online program, duplicate namespaces, unsupported TEMP/indirect values, insufficient trace resources, missed short events, protected blocks, no reproduced fault, interruption and preserving existing traces. A happy-path screenshot or a successful API call alone is not acceptance evidence.

## Required plan output

1. Current implementation/evidence audit with exact source paths, current owners, reusable pieces and missing orchestration. Separate ability to inspect candidates from proven automatic instance and signal resolution.
2. Capability matrix mapping each workflow stage to exact public API/service/schema or interaction route, authoritative sources, prerequisites, implementation state, qualification state and limits. Include unavailable or infeasible operations explicitly.
3. Proposed architecture and MCP contracts. Consider diagnostic_plan, trace_capabilities, trace_prepare/apply, trace_status/capture/read and trace_analyze as proposed names only; prefer the project's existing change/job conventions where appropriate. Define TracePlan, signal-resolution evidence, acquisition quality, artifact lineage, freshness invalidation, ownership and error schemas.
4. Dependency-ordered work packages with stable IDs, concrete files/modules, inputs, outputs, completion criteria, effort ranges with assumptions, ownership conflicts and critical-path risks. The first package should settle the native trace automation route while designing useful bug-to-signal planning independently.
5. Qualification strategy with independent expected mappings and measurements, native readback, reopen/preservation where applicable, source/fixture/binary identities, target compatibility, interruption/recovery and scale. Separate offline reasoning tests, native configuration qualification and real acquisition acceptance. Reuse accepted evidence only within its demonstrated scope and honor current VM/native scheduling decisions.
6. Phases covering feasibility, a complete supported vertical slice, full supported expansion and release acceptance. State exactly what the user can accomplish at each phase and which stages still need manual work. Do not equate a plan or a polling demo with complete native trace creation.
7. A worked example of the conveyor report through candidate ranking, instance resolution, signal manifest, trigger/sampling rationale and resulting diagnostic report. Clearly label hypothetical fixture data. Include unresolved decisions and the recommended next executable package.

Start with src/TiaMcp.Host/EngineeringTools.cs, src/TiaMcp.Host/SimulatorTools.cs, src/TiaMcp.Simulator.V8/SimulatorSession.cs, relevant OpennessSession source files, docs/guides/block-testing.md and the installed Base/Step7/SafetyValidation/Startdrive XML references. Confirm all names against the current source.

Official research entry points:

- [V21 Trace and logic analyzer function](https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function)
- [Native trace configuration export](https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function/using-trace-functions/configuring/export-configuration)
- [Safety Validation trace configuration](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-safety-validation-assistant/getting-the-trace-configuration)

Write the finished plan to docs/plans/expansion-plans/bug-driven-trace-diagnostics.md. If that plan already exists, read it first and extend it without overwriting concurrent work; if ownership is unclear, save a clearly named proposed revision. Only plan artifacts may be written. Return the plan path, verified native automation options, remaining limitations and the recommended first work package. Do not claim this planning task executed any native acceptance.
