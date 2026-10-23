# Full workflow completion audit — 14 October 2026

**Result: not complete.** The supported offline implementation has passed its scoped checks. Native creation/configuration/acquisition acceptance remains blocked. The user's original full workflow is retained; a manual handoff, synthetic event or limited parser is not substituted for full native completion.

Current evidence: [expanded implementation](trace-diagnostics-expanded-offline-20261014.md), [latest AX/nanosecond receipt](../../artifacts/trace-diagnostics-ax-audit-20261014/result.json), [MCP receipt](../../artifacts/trace-diagnostics-ax-audit-20261014/host-smoke/result.json), [full offline regression](../../artifacts/trace-diagnostics-full-20261014/regression/result.json), [route research](../plans/expansion-plans/bug-driven-trace-simatic-ax-feasibility.md). Latest diagnostics: 18/18 groups, Release Host build with zero warnings/errors, eight-tool stdio workflow, 17 schema samples. The earlier full regression covered 13 managed suites plus Python/explorer tests. Subsequent changes are isolated capability metadata and timestamp-unit support, covered by the latest focused/MCP checks.

## Requirement-by-requirement result

| Original workflow requirement | Authoritative evidence and delivered behavior | Completion judgment |
| --- | --- | --- |
| 1. Interpret report, separate observations/evidence/hypotheses and ask only material questions | `DiagnosticReportContext.cs`, `DiagnosticPlanner.cs`; defaults are explicitly unverified, source ranking uses captured context, unresolved instance/timing questions retained | Supported offline baseline; semantic equipment association remains caller/agent reasoning, not proven automatic selection |
| 2. Rank relevant code using available names, expressions and relationships; expose language/protection limits | `DiagnosticPlanner.cs`, `DiagnosticCodeReader.cs`; literal metadata/XML/adjacent-edge ranking, protected/omitted source and conservative SCL/FlgNet scope documented | Scoped offline evidence passed; no full language semantic claim |
| 3. Resolve exact CPU/scope/namespace/block/call/instance path with alternatives | `DiagnosticEvidence.cs`; qualified exact names, native-shaped root type edges, nested declarations, explicit call/binding evidence and unresolved alternatives | Authored fixture mapping passed; actual project/native instance qualification missing |
| 4. Select a sufficient small signal set with reasons | `DiagnosticSignalSelector.cs`, independent fixture oracle; explicit seeds, conservative dependencies, simple actual/IN_OUT bindings, captured competing writers, bounded budgets and inclusion evidence | Bounded automatic proposals passed; general minimum/sufficient slicing is not proven |
| 5. Validate backend/target observability | Declaration/type/access filters in `DiagnosticPlanner.cs`, unavailable TEMP/indirect/array cases, `TraceReadback.cs` supplied evidence comparison | Offline filters passed; target observability and native substitutes remain unqualified |
| 6. Reviewable TracePlan with identities, source, signals, requested trigger/sampling and limits | `TracePlanning.cs`, versioned contracts, source hashes, selection coverage and manual steps | Manual specification implemented. Effective native settings/resources remain unknown |
| 7. Create/configure/read back; separate transfer/arm/record/stop/retrieve/delete; preserve user traces and reconcile interruption | No generic Openness adapter. AX CLI is documented separately, not installed/implemented/qualified. `TraceReadback.cs` compares imported observations only | **Not achieved.** No native creation, full settings readback, persistence, lifecycle or recovery proof |
| 8. Import measurement and assess original hypotheses with units/timestamps/quality | `TraceMeasurements.cs`, `TraceCsvReader.cs`, `TraceComparison.cs`, `TraceNumber.cs`; normalized JSON/explicit CSV, exact s/ms/us/ns, quality gaps, prediction verdicts and no causal/fix proof | Offline synthetic analysis passed; actual native export/acquisition acceptance missing |

## Concrete conveyor acceptance

| Required evidence | Current result |
| --- | --- |
| Correct FB and second instance, including nested path | Independent authored `expected-mappings.json` and managed/MCP checks select Conv2 and retain Conv1 alternatives. Not a native fixture mapping. |
| Enable, command, feedback, interlock, step, timer and fault roles | Eleven explicit role signals resolve against synthetic declarations. Automatic dependency tests independently cover command/enable/actual bindings and a separate writer; this is not proof of automatic role discovery on an arbitrary project. |
| Independent settings readback | Synthetic differences in target, sampling, type, missing signal, program-match assertion and exhausted slots are detected. No native settings were read back. |
| Captured injected event | Three predicted condition samples and four transitions in authored synthetic data. **No actual PLC event was injected or captured.** |
| Other conveyor, ambiguity, duplicate namespaces, protected source, unsupported TEMP/indirect members | Scoped offline negative checks passed. Dynamic/opaque cases remain unresolved. |
| Stale online program | Caller-attested mismatch downgrades analysis; no online checksum/comparison was performed. |
| Insufficient trace resources | Supplied readback mismatch detected; no physical slot inventory or exhaustion test. |
| Missed short event / no reproduced fault | No matching condition remains unresolved; polling cannot claim scan accuracy. Detection of a one-scan glitch is unproven. |
| Interruption and preserving existing native traces | Immutable artifact integrity, publication-lock contention and unrelated-file preservation tested offline. Native interruption/reopen/preservation remain untested. |

## Full-scope and release gates

Eight diagnostic templates, repeated captures, explicitly aligned before/after comparison, explicit machine substitution with revalidation, bounded retention and exportable JSON reports are implemented. Shared source/symbol identities and artifact/quality fields reuse the existing project-review/Host conventions. No competing transport or native ownership mechanism was introduced. Native watch-table, Test Suite, online-comparison, Startdrive, alarm, safety and physical-runtime orchestration remains limited by those domains' separate support and qualification; their existing commitments were not claimed complete.

Ordinary PLC, long-term, project/multi-device, drive, Safety Validation, manual UI and simulator polling remain separate capability rows. AX is an additional documented external PLC-job route, not ordinary Openness support. The requested plan at `docs/plans/expansion-plans/bug-driven-trace-diagnostics.md`, its capability/work-package/phase/fixture material, implementation guide and evidence annexes exist. They explicitly leave native and release gates open. No build was deployed and no release acceptance was claimed.

## Blocking conditions and exact resumption inputs

1. The user explicitly keeps native UI work paused. No UI experiment, TIA start/attach, native test or PLC connection is authorized by this audit.
2. No general V21 Openness trace lifecycle API or supported `.ttcfgx` generation contract was established in the inspected references. Public API presence cannot be invented.
3. Siemens documents an AX PLC-job CLI, but the user confirms no licensed installation/package is available. The inspected 2.8.1 documentation excludes save triggers for TIA-engineered PLCs. Full settings readback and persistent TIA trace-object interoperability remain unestablished.
4. No admitted owned target/profile or genuine measurement exists for the required native acceptance. Historical processes and queue receipts cannot establish current ownership.

The next meaningful native-route package is **TD-AX-01** if a licensed supported package/schema becomes available, plus authoritative resolution of its trigger/readback/interoperability boundaries. Alternatively, an exact documented Openness service/schema route could change the decision. UI assistance remains unavailable until the user explicitly changes that pause. Any later native work still requires current scheduling/admission and independent configuration/acquisition/preservation/recovery evidence. These are external changes, not reasons to repeat green offline tests or silently lower acceptance.
