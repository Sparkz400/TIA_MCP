# HMI implementation delivery — 8 October 2026

Historical record. See [current status](../STATUS.md) and [remaining TODO](../TODO.md) for the latest checkpoint and work order.

Both Classic WinCC and WinCC Unified have optional V21 adapters, bounded inspection, exact HMI identities/resolution, evidence-bearing relationships, and an actual shared project-explorer provider. The engineering allowlist and existing prepared-change integration are implemented behind preservation and native qualification gates. **Full H0–H5 acceptance is unfinished; no production HMI mutation is enabled.**

The machine-readable delivery record is `artifacts/hmi-delivery-report.json`. It identifies immutable build/package hashes, validation results and the exact native evidence used below. A package containing these adapters does not imply that all its target profiles or engineering operations have passed native qualification.

## Operations by exact target profile

| Target profile | Observed catalog/runtime | Native read-only qualification | Production engineering |
| --- | --- | --- | --- |
| Classic TP700 Comfort | `6AV2 124-0GC01-0AX0`, 17.0.0.0 | Discovery, connections/tags/tables, regular screens/items, empty script/list inventories, exact identity controls, explorer | Unavailable; standalone screen `BackColor` implementation and independent scalar experiment exist, but complete preservation and production lifecycle acceptance do not |
| Classic RT Advanced | `6AV2 104-0xxxx-xxxx`, 17.0.0.0 | Same independently exercised read-only subset | Unavailable; no inherited Comfort engineering qualification |
| Classic KTP700 Basic PN | `6AV2 123-2GB03-0AX0`, 17.0.0.0 | Same independently exercised read-only subset | Unavailable; no inherited Comfort engineering qualification |
| Classic RT Professional | No installed matching catalog target | Blocked; no native target fixture | Unavailable |
| Unified MTP700 Comfort | `6AV2 128-3GB06-0AXx`, 21.0.1.0 | Discovery, connections/tags/tables, screens/items, empty alarm/script/list inventories, exact identity controls, explorer | Unavailable; internal tag creation, selected item properties, existing faceplate placement and explicit binding bodies require complete preservation and native acceptance |
| Unified PC RT | `6AV2 155-xxxxx-xxxx`, 21.0.1.0 | Same independently exercised read-only subset | Unavailable; independently tracked from Unified Comfort |

The identifiers are observed catalog values, including Siemens placeholders. Engineering assemblies are V21; that does not change a fixture's device runtime version. See the [capability matrix](../reference/hmi-capability-matrix.md) for public APIs, implemented subsets, exclusions and official vendor references. Classic alarms are explicitly unsupported by the inspected public surface. Text/graphic lists are a bounded resource subset; standalone images/fonts/global repositories are not claimed as complete.

## Evidence and explorer integration

`artifacts/hmi-bridge-20261008-root-g.json` passed the read-only gates above on all five available profiles. It exercised actual shared project-model capture, children, exact HMI details, relationships and portable HTML export. Contract validation checked 206 recorded responses/identity/provider rows. Screen-item details are read on demand under the actual HMI hardware/software owner; no PLC ownership or DB-member tree node is fabricated. Native rendering is not provided and project scripts are never executed.

The richer independent f/g fixtures contain two exact PLC owners with duplicate declaration names, integrated connections, scalar PLC tags and DB members, Unified direct/indirect/broken/formula dynamics, a nonempty discrete alarm, and a screen event with script/global source. These native input/readback fixtures alone do not qualify the bridge. The later root-h bridge attempt timed out before fixture retrieval. Root-i received a Siemens `EngineeringSecurityException` (`AccessDenied`, security operation timed out) on the first Classic launch, then successfully retrieved and inspected both Unified profiles. Both passed actual explorer integration; 106 recorded response/identity/provider contracts passed schema validation. This observed per-launch failure is not a family-wide or license conclusion.

The original root-i relationship gate used the wrong response key in the harness (`edges` instead of `relationships`), although the recorded responses contain the native endpoint proofs. It also required complete item metadata despite intentional broken-reference getter failures. The original failed report is retained. `artifacts/hmi-bridge-20261008-root-i-reassessment-b.json` separately proves all seven Unified Comfort relationship/behavior assertions against those captured responses: exact integrated PLC ownership, scalar tag and DB-member declarations, the direct screen binding chain, dynamic/broken retention, and event/global script source. It also verifies Unified PC script/dynamic/broken observations, while retaining its fixture's missing-interface mapping failure. Retained inventories/scalars match; complete metadata coverage remains unverified. Subsequent immutable-binary runs are identified separately in the delivery record.

Relationships retain source properties/artifacts, exact paths, resolution evidence and incomplete/dynamic observations. Unified scalar declarations are resolved only after native integrated connection endpoint equality proves the exact PLC owner. DB members additionally retain native exported declaration evidence. Unsupported Classic binding shapes, indirect expressions, arrays, opaque UDTs and incomplete candidate scopes remain unresolved. Rich faceplate, translation, global-module and resource fixtures are still required for their broader coverage.

## Preservation and recovery

The basic bridge report records passed scalar save/reopen and archive/retrieval comparisons for both Unified profiles, and failed strict comparisons for all three Classic profiles. Classic screen scalar feasibility testing separately read back the intended value and preserved unrelated selected-screen XML, then restored it. These selected-scope observations do not qualify the complete prepared engineering workflow.

The richer f Classic strict recovery comparison failed because screen enumeration order changed. A separate audit found the same scoped screen content, with only the exporter timestamp differing in XML; the original failed strict results remain retained. Both richer Unified fixtures passed their recorded scalar persistence/archive comparisons. Whole-HMI content preservation and interrupted native HMI mutation recovery remain unverified.

Classic public exposure lacks a complete alarm/resource/dependency preservation scope. Unified lacks a complete public screen export or equivalent readback covering all events, dynamics, translations and unknown content. Metadata hashes and archives cannot substitute for those comparisons. The implementation therefore rejects engineering preparation before mutation and uses the existing journal/checkpoint/compile/restore machinery when a future operation becomes eligible. See [engineering gates](../guides/hmi-engineering.md) and the [library handoff](../../contracts/hmi-library-handoff.v1.schema.json); library lifecycle stays in the library subsystem.

## Remaining acceptance criteria

- Obtain successful native access for each final-package profile and complete the richer bridge fixture comparisons and relationship gates; retain per-launch authorization failures separately.
- Provide the missing RT Professional target, native protected/license-failure controls, and nonempty supported faceplate/resource/translation/global-module fixtures.
- Establish a complete preserving route for each engineering operation; qualify precise effect, all unrelated content, native compile, changed-state save/reopen, fresh archive recovery and interrupted-operation recovery.
- Correct fixture runtime prerequisites and obtain successful native HMI compilation. Existing compile failures are retained, including missing Classic start screen and Unified runtime/certificate/reference diagnostics.
- Resume representative visual assessment when authorized. Computer Use was stopped by the user's Escape input; visual acceptance remains unverified.
- Restore access to the configured SSH machine and qualify the current package there. The actual connection attempt timed out on port 22 without remote changes.

Optional module absence and incompatible synthetic assembly controls, pure tests, packaging and local startup evidence are detailed in the delivery record and [optional startup report](../verification/hmi-optional-startup.md). No Siemens assemblies are redistributed and no physical HMI/PLC deployment was performed.
