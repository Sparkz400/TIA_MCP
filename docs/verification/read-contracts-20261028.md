# Read-tool identity and completeness — 28 October 2026

Evidence [`local.read_contracts.20261028`](../../evidence/local.read_contracts.20261028.json) and
[`local.project_index.20261028`](../../evidence/local.project_index.20261028.json).
The [27 October audit](release-matrix-audit-20261027.md#closest-to-closable-by-owner) left 46 unqualified
rows whose only open criteria were `AC-identity`, `AC-completeness` and `AC-evidence`. Many of their tools
answer in nearly every native run, but incidental use never asserted their own contract. This change
adds a runner that does, fixes the five defects it found, and verifies 36 of those rows.

Everything ran in owned TIA Portal V21 Update 3 instances and owned scratch projects; nothing touched a
user project, a PLC or PLCSIM.

## What the runner asserts

[`scripts/qualify_read_contracts.py`](../../scripts/qualify_read_contracts.py) covers 34 features. A
feature passes only when the run records all three of these for it:

- **Identity:** a passed call whose response names exactly the requested project, PLC, software scope,
  block, type, hardware item, device, library, version or change. Each is compared with an identity the
  run created or read on its own, not with the tool's own echo.
- **Refusal:** a typed refusal of an unknown, ambiguous, stale or invalid selector. Ambiguity comes from a
  second PLC and from a name shared by the CPU item and its PLC. Staleness comes from a library revision,
  a closed library ID, a tampered inventory cursor and a changed preview hash. Two features have no
  selector and are exempt: `library_list` and `library_capabilities`.
- **Completeness:** flags that keep complete, empty-and-complete, truncated and warning-qualified partial
  results apart. Truncation uses `maxItems`/`pageSize` 1 (and `maxXmlBlocks` 1 for `instance_lookup`).
  Paged library queries and device inventories must equal their full reads. Exported documents,
  loadables, inventories and text workbooks must match their reported SHA-256, and must be exactly the
  files in their directories.

The fixture is a CPU 1511-1 PN V4.1 scratch project, seeded through prepared changes:

- de-DE active next to en-US;
- a UDT;
- an FB with a single-instance DB and two multi-instances, plus the FC that calls it;
- an empty block group;
- a software unit holding an FC;
- a released project-library FB type and an empty global library.

A CPU 1518-4 PN/DP is added later. It makes unqualified selectors ambiguous, and it is the documented
target of loadable generation. The change tools end the run: prepare, prepared status, a refused stale
preview hash, apply, applied status, then a restore into a new directory that no longer has the change.

`scripts/qualify_project_index.py` already asserted `project_index`'s identity and paging contract, but it
recorded no tool calls, so its runs could not be curated. It now records them like the shared harness does.

## Defects found and fixed

| Tool | Before | Now |
| --- | --- | --- |
| `software_inspect`, `plc_document_inventory` | An unknown `scopePath` returned an empty result marked scope-complete | `ObjectNotFound` through the shared `RequireOne` check `plc_alarm_inspect` already used |
| `engineering_read` (`hardware`/`network`) | An unknown `hardwarePath` returned no items and no error | `HardwareNotFound` |
| `block_read`/`type_read` with `format: simaticSd`, `plc_document_inventory` exports | Every SIMATIC SD export failed: "The argument 'directoryInfo' cannot be a relative path" | The export gets a `DirectoryInfo` built from the full path. On .NET Framework, the object `Directory.CreateDirectory` returns keeps only the leaf name as its original path, and Openness passes that on |
| `library_open` with a path | A missing file raised an untyped `EngineeringError` in the shared admission step, before the tool's own check | `PackageInvalid` in admission too |

Three things are recorded as behavior, not defects:

- `plc_loadable_generate` refuses on CPU 1511 with `UnsupportedCapability`. Siemens' help documents loadables only for
  CPU 1518 and its subtypes from firmware V2.8, so the PLC software there has no `LoadableProvider`. The message and the
  [guide](../guides/engineering-workflows.md) now say so.
- `tia_create_scratch` creates the project before its catalog lookup, because Siemens needs an open
  project for catalog access. An unknown `cpuTypeIdentifier` is therefore refused (`CatalogItemNotFound`)
  with the partial project named, and that Portal must be discarded. A second creation in the same
  Portal is refused (`ScratchIsolationRequired`).
- `library_compare` has a fixed 200-row bound with no `maxItems`.

## Runs

| Run | Build | Result |
| --- | --- | --- |
| `rp_f64525c5`, `rp_88298204` | `b12` (`26a09a7`) | Exploratory probes (scratchpad script) that recorded raw responses and found the defects. Not cited |
| `rc_4e464455` … `rc_6b90f6d6` | Live Debug build with the fixes | Runner development; each stopped at one wrong field or code guess. Not cited |
| `rc_6a046127` | `b13` (`df30b58`) | Passed, 152/152 steps, all 34 features covered. Cited |
| `ix_d78e2db2` (index) | `b14` (`ce021da`: tool-call recording) | Passed, 81 steps; owner and observer Hosts exited 0. Cited |

`D:\wp\b13` and `D:\wp\b14` hold `git archive` sources with `source-manifest.json`, plus the Debug Host
and worker built from them. Each worker was registered in the Openness AllowList by its own post-build
step, and the runners ran from the frozen `scripts/` copy.

## Verified

Catalog `0.4.50` verifies:

- **Project and changes:** `tia_create_scratch`, `project_properties`, `change_prepare`,
  `change_apply`, `change_status`, `change_restore`, `target_capabilities`, `object_resolve`.
- **PLC software:** `software_inspect`, `plc.document_formats`, `plc_document_inventory`,
  `plc_loadable_generate`, `plc_alarm_inspect`, `instance_lookup`, `engineering_read`, `plc_compare`.
- **Hardware and inventory:** `hardware_inspect`, `device_inventory`, `device_inventory_export`.
- **Libraries:** `library_open`, `library_list`, `library_query`, `library_compare`,
  `library_update_check`, `library_close`, `library_capabilities`, `library_export`.
- **CPU configuration and texts:** `opcua_engineering_inspect`, `project_text_export`,
  `vci_workspace_inspect`, `connection_inspect`, `access_inspect`, `certificate_inspect`,
  `web_inspect`.
- **Index:** `project_index` and `project.index_paging` (`local.project_index.20261028`).

Each row lost the "compiled against the V21 PublicAPI only; not natively qualified" limitation in favor
of the profile above. Rows also lost citations of unretained October records; the records stay in the
catalog as history.

Still open from the audit's 46 rows:

- the eight `project_model*` rows: their E1 acceptance in the
  [explorer plan](../plans/project-explorer-plan.md) needs `qualify_project_explorer.py --two-plcs` with
  units, nested groups and budget exhaustion;
- `hardware_gsd_export`, which needs an I-device;
- `library.types`, which binds no tool.

## Limits

- Families absent from the fixture were observed only as empty, complete results: safety, technology
  objects, HMI, connections, VCI workspaces, PLC documents and know-how protection.
  `connection_inspect`, `plc_document_inventory` and `vci_workspace_inspect` have no rows here to
  truncate, and the fixture does not reach `library_compare`'s bound.
- `plc_loadable_generate` is positive only on the CPU 1518, with target `plc`, for one non-unit block.
  `plcSim` targets and software-unit loadables are not qualified.
- These are engineering reads only: no runtime, online or physical-device observation.

## Reproduce

```powershell
python -B scripts/qualify_read_contracts.py --host <Host.exe> --worker <TiaMcp.Openness.V21.exe> --artifacts D:\wp\rc --run-native
python -B scripts/qualify_project_index.py --host <Host.exe> --worker <TiaMcp.Openness.V21.exe> --artifacts D:\wp\pi --report <report.json> --cpu "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"
python -B scripts/curate_evidence.py --id local.read_contracts.20261028 --report <qualification.json> --binary <Host dir> --binary <worker dir> --scope ... --limitation ...
```
