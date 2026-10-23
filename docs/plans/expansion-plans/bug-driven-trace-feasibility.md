# TD-01 route decision and first offline increment

Additional route finding: [SIMATIC AX trace feasibility](bug-driven-trace-simatic-ax-feasibility.md) identifies a documented public YAML/CLI path to PLC trace jobs. It is a separate unimplemented/unqualified family, with save triggers excluded for TIA-engineered PLCs in the inspected version and unresolved full readback/TIA project interoperability. The historical V21 Openness negative result below must not be generalized to AX. TD-AX-01 is now the next specific read-only route package.

Current expanded scope and package disposition are recorded in the [14 October expanded verification](../../verification/trace-diagnostics-expanded-offline-20261014.md). The first-increment inventory and next-offline-package paragraph below are historical: bounded dependency/binding selection, qualified names, tag declarations, explicit CSV, comparison/reuse and supplied-readback checks have since been implemented. The route decision remains unchanged, and the user explicitly kept native UI work paused.

14 October 2026. Implements a bounded offline seam from the [full plan](bug-driven-trace-diagnostics.md), after the user requested implementation. This is an expansion candidate, not completion of existing 1.0 commitments.

The planning audit inspected installed V21 net48 Base, Step7, SafetyValidation and Startdrive XML and public schemas, and the official sources linked in the full plan. No documented public general ordinary-PLC trace lifecycle API or supported editable `.ttcfgx` schema was established. Decision: **do not add a generic Openness trace adapter or private file generator**. The negative result is scoped to inspected evidence; it is not proof that every future integration route is impossible.

| Route/family | Current decision | Smallest remaining experiment |
| --- | --- | --- |
| Ordinary PLC public API | Not established; no adapter | Obtain exact public owner/service/method documentation, then separately admitted two-signal create/readback on an owned fixture |
| `.ttcfgx` generation/import | Export/import through native UI documented; schema/generation not established | Establish officially permitted fields/schema/version compatibility and supported import/readback before any generation |
| Manual ordinary trace | Offline exact storage/specification implemented; target settings and traceability unverified | Human native selector/settings readback, followed by admitted known-waveform capture |
| UI assistance | Not implemented; existing pause remains | Separate authorization, ownership/admission and independent preservation/recovery qualification |
| Long-term/project/multi-device | No adapter; family-specific prerequisites/format/clock qualification open | Exact CPU/firmware/product/license matrix and independent configuration/acquisition experiment per family |
| Drive | No adapter; Startdrive support does not imply ordinary PLC support | Exact drive/control unit/firmware/license and supported route, followed by separate readback/capture |
| Safety Validation | Documented separate SafetyFunction.TraceConfiguration, not reused | Remains prompt 14's domain qualification |
| Simulator polling | Existing sim_test stays separate; diagnostic tools do not dispatch it | Cannot satisfy guaranteed one-scan detection; any scan-synchronous route needs independent proof |

Implemented files: `src/TiaMcp.Core/Diagnostics/{DiagnosticStore,DiagnosticEvidence,DiagnosticPlanner,DiagnosticTemplates,TracePlanning,TraceMeasurements}.cs`, additive Host `DiagnosticTools.cs`, versioned schemas, authored offline conveyor fixture and an isolated managed test suite. See [usage and actual limits](../../guides/trace-diagnostics.md).

Package status is intentionally scoped: TD-01 route decision/offline seam complete for inspected routes; TD-02 offline case/fixture/schema subset, TD-03 literal/adjacent ranking, TD-04 bounded captured-storage resolution, TD-05 caller-proposed declaration checks, TD-06 manual specification and TD-09 normalized-import/sample-prediction analysis implemented. This does not complete general semantic instance resolution, signal slicing, native-format ingestion, lifecycle TD-07/08, native TD-09 acceptance or full TD-10 expansion. Eight role templates are guidance only. TD-11 release acceptance remains open.

No native test, TIA start/attach, PLC connection, UI operation, deployment or process interruption was performed. The Host and managed tests build into a separate artifact directory. Existing component, HMI, typed, native cleanup and simulator scheduling work is untouched. No general-native qualification labels were promoted in the feature catalog.

Next executable offline package: extend TD-04/05 with independently expected parameter bindings/direct I/O identities and supported language-specific dependency slicing. Before native automation work, resolve the public route and exact target profile, then obtain the existing admission decision for the two-signal readback/capture experiment. A manual spec or a synthetic report does not discharge that gate.
