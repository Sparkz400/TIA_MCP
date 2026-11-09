# Read-only TIA V19 adapter and V19-to-V21 upgrade (9 November 2026)

Evidence: `local.tia.v19-adapter.20261109`, `scripts/qualify_tia_v19.py --run-native` at `9db55f90` (frozen Debug
build), run `v19_a825a2f7`, 53 stages, on LT-PRG2024-03 with TIA Portal V19 Update 5 (Openness 19.0.0.0) and V21
Update 3. Feature `tia.multiversion` (X12) moves from deferred to limited; `project_upgrade` (X01) gains its first
V19 source.

## Fixture

`V19ProjectSeeder create` built an owned project `TiaMcpV19Fixture` in its own V19 instance: CPU 1511-1 PN V3.1
(6ES7 511-1AL03-0AB0, the newest V19 catalog firmware), tag table `TiaMcp_Tags` (three tags, one constant), UDT
`TiaMcp_Motor`, FC `TiaMcp_IsEnabled`, FB `TiaMcp_MotorCtrl`, global DB `TiaMcp_Data`, instance DB and OB `Main`, and a
software unit `TiaMcp_Conveyor` with UDT, FB, instance DB and tag table, all generated from SCL/UDT sources. It compiled
with 0 errors and 1 warning (I/O outside the configured hardware), was saved, archived as `.zap19` and closed.

## V19 Host (`--tia-version 19`)

- The Host advertised exactly the nine base tools and the twelve V19 read tools, and no prompts. `change_prepare`,
  `project_upgrade`, `tia_create_scratch` and `library_list` were unknown tools. `tia_capabilities` reported
  `tiaVersion: 19`, API 19.0.0.0 and `readOnly: true`.
- In an owned V19 instance `project_open` refused an `.ap21` path (`ProjectVersionUnsupported`) and a missing `.ap19`
  (`ProjectNotFound`), opened the fixture and refused a second open (`ProjectIsolationRequired`).
- `project_inventory` and `software_inspect` listed the same blocks; all 11 seeded objects (7 blocks, 2 UDTs, 2 tag
  tables) were read with `block_read`, `type_read` and `tag_table_read` as V19 SimaticML at their canonical paths, each
  artifact matching its reported SHA-256. `format` `generatedSource` and `simaticSd` were refused with
  `UnsupportedOnTiaVersion`.
- `readOnly: true` opened a secondary project with the same inventory; TIA refused its block export, reported as
  `ReadOnlyProject`.
- While the seeder held the fixture open in its own V19 Portal, `tia_discover` listed that Portal, `tia_attach` read
  the same inventory, and the Portal kept running after `tia_detach`.
- Every owned V19 Portal exited after `tia_detach` (captured process handle).

## Upgrade into V21

- With a V21 Host, `project_upgrade` of the fixture folder while V19 held it open was refused with `SourceLocked`,
  the source unchanged.
- After release, the project folder and the `.zap19` archive each upgraded to `TiaMcpV19Fixture_V21` (two conversion
  logs, succeeded, 0 errors, 0 warnings; `sourceUnchanged`). Each upgraded copy reopened in a fresh Portal with the V19
  inventory below the `/projects/<name>` segment.
- The upgrade does not compile: `Main`, `TiaMcp_IsEnabled`, `TiaMcp_MotorCtrl` and the unit's `TiaMcp_Belt` were
  inconsistent, and V21 refuses to export inconsistent blocks (`BlockExportUnavailable`, found in the first full run).
  A checkpointed `plc_compile` change compiled each copy (0 errors, 1 warning) and left every block consistent.
- Every seeded block, UDT and tag table then equalled its V19 export in the compared content: block kind, name,
  number, language and memory layout with interface members (name, data type, start value); UDT members; tags (name,
  data type, address) and constants (name, data type, value).

## Source files and TIA's search index

The second full run found that opening the fixture in V19 changes files under `IM/SearchIndex/`, which TIA's background
search indexer rewrites. The qualifier now records each open: 7 index files on the adapter's primary open, none on the
read-only open, 5 while the seeder held it. The `.ap19` file, every other project file and the archive stayed
byte-identical.

## Openness approval for V19

TIA V19 does not read the shared `Openness\AllowList` key: an exactly matching entry there still raised its
"Openness access" dialog, and "Yes to all" wrote `HKLM\SOFTWARE\Siemens\Automation\Openness\19.0\Whitelist\<exe>`.
V19 builds now register there ([registration guide](../guides/openness-application-registration.md#tia-v19-builds)).

Not covered: V15.1, V17, V18 and V20 (no adapter; V15.1 has no SDK on this workstation), LAD/FBD/GRAPH, HMI, drives,
safety, technology objects, libraries, namespaces, know-how protection and UMAC-protected sources, network code
comparison, and release bundles (the V19 worker is source-built only).
