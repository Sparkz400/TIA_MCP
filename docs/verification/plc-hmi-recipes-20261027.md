# Combined PLC/UDT/HMI recipes — 27 October 2026

**Status:** implemented as plan-only `library_integrate` actions. Offline checks pass.
The native prepare-only probe is authored; its result is recorded below once run.
Apply stays refused (`PlcIntegrationNotQualified`). No HMI preservation gate is opened.

## What was added

Contract: [plc-hmi-integration-recipe.v1.schema.json](../../contracts/plc-hmi-integration-recipe.v1.schema.json),
examples in [fixtures/libraries/plc-hmi](../../fixtures/libraries/plc-hmi). Guide:
[combined recipes](../guides/plc-library-integration-recipes.md#combined-plcudthmi-recipes).

- **`plc_hmi_component_plan`** (install). The existing PLC recipe, unchanged, plus a
  Unified `hmi` section (profile, exact HMI path, connection, tag table, tag mappings,
  screen placement) and `expectedCompilerOutcomes`. The worker validates the whole
  recipe before it selects a project or touches TIA. It then runs the existing PLC
  planner and reads the HMI side natively: the connection's PLC owner (same
  `CommunicationManagement` proof as `external_tag_create`), tag-table existence,
  tag-name conflicts, root PLC tag types, instance members flattened from the pinned
  FB/UDT exports, and screen size/item names. Root-tag mappings become proposed
  `external_tag_create` steps. Instance-member mappings, screen items and the closed
  Unified preservation gate are explicit blockers.
- **`plc_hmi_update_plan`** (field-changing update). One selected UDT version for one
  PLC. The worker exports the installed use and the selected version, and walks every
  Unified HMI tag whose connection proves to be that PLC. Each symbolic `DB.member`
  path that passes through the selected UDT gets a row: `unchanged`, `typeChanged`,
  `memberRemoved` or `structureChanged`, with the before/after type and the direct
  screen-item consumers. Any breaking row refuses with `HmiBindingWouldBreak`; any
  binding that cannot be proven (unproven connection owner, Classic HMI, nested
  opaque UDT, missing member, other profile) refuses with `HmiImpactIncomplete`. The
  impact report is in the error `details`. Only a clean preview proposes a fresh
  `library_update_project` step.
- The internal PLC component coordinator and component window refuse combined plans.

## Offline results

| Check | Result |
| --- | --- |
| `dotnet run --project tests/TiaMcp.PlcLibraryIntegration.Tests` | 296 passed (11 new combined-recipe tests) |
| `python -B -m unittest test_plc_hmi_recipe_contract test_plc_hmi_recipe_probe test_hmi_qualification test_component_text_contracts test_library_operation_observations` (in `scripts/`) | all passed |
| V21 worker build against the installed SDK | succeeded, 0 warnings |
| HmiSeeder build against the installed SDK | succeeded |

The probe tests use a fake bridge. They check the probe's own logic, not native behavior.

## Native probe

`HmiSeeder qualify <new-dir> unified-comfort positive plchmi` adds, by raw Openness
(not the bridge): `MotorStatus` 1.0.0 (`speed : Real`, `running : Bool`) used by
`PLC_A`, `MotorDB.status`, Unified tags `MotorSpeed`/`MotorRunning` bound to its
fields, a rectangle whose `Left` is dynamized by `MotorSpeed`, `MotorDrive` 1.0.0
(FB using `MotorStatus` 1.0.0) and `MotorStatus` 1.0.1 with `speed : LReal`. Its
temporary 1.0.1 test instance in `PLC_B` is deleted again.

```powershell
python -B scripts/qualify_hmi.py --native-report <seeder>/native-report.json `
  --host <exact-host.exe> --worker <exact-worker.exe> `
  --report artifacts/<new>/plc-hmi-recipe.json --timeout 600 --probe-plc-hmi-recipe
```

Expected, per profile row `plcHmiRecipe`: the install plan into `PLC_B`/`HMI_RT_2`
prepares with the three HMI blockers and its apply is refused; the update plan for
`PLC_A` to 1.0.1 is refused with exactly one breaking row (`MotorSpeed`,
`Real` → `LReal`, consumer `MotorSpeedConsumer.Left`) and `MotorRunning` unchanged; a
Classic recipe is refused before native work; library and HMI tag inventories are
unchanged.

## Not covered

Apply of any combined step, compile, save/reopen, recovery and runtime. Classic
HMIs, Unified PC natively, HMI-owned UDTs (no V21 API), UDT-typed PLC tag-table tags,
UDTs nested inside other UDTs, scripts/alarms/faceplate ports as consumers, and
creation of member-bound HMI tags or screen items.
