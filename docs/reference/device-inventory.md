# Device inventory and online identification

`device_inventory` consolidates configured hardware into one row per native
device or device item, including built-in interfaces and submodules.
`device_inventory_export` exports the entire captured scope to CSV or XLSX.
`device_online_browse` and `device_identify` use a separate, read-only Siemens
Web API route for actual identification. Physical CPU and remote-I/O
qualification remains **unverified**; see the
[verification record](../verification/device-inventory-20261016.md).

## Offline capture and paging

Attach to the intended open project, then call `device_inventory` with
`{"pageSize":100,"maxVisited":50000}`. Continue with the returned
`snapshotId`, `cursor` and optional `pageSize`. Omit project selectors on
continuation. For larger projects, an exact `devicePath` from a `kind: device`
row narrows a new capture to that device. Canonical paths distinguish duplicate
names in separate groups.

The collector traverses project devices, ungrouped devices, nested groups and
device items. Bounds: 50,000 visited objects, depth 64, 48 MiB native spool,
96 KiB per native row. Group/device visits count toward the traversal bound.
`coverage` identifies truncated or failed traversal. Missing fields are separate
from traversal completeness. Capture is not a TIA transaction and cannot detect
every concurrent external edit.

The host retains four immutable snapshots at most, with 128 MiB per snapshot and
256 MiB total serialized string storage. Pages have at most 200 rows and 256 KiB
of row JSON; byte limits can shorten a page. Export before eviction or host
restart. Cursors bind to the snapshot and host, permit replay, and never re-read
a changing project. Returned metadata and warning arrays cannot mutate retained
capture coverage.

## Fields

Every optional field has `value`, `state`, `source` and `reason`. Available fields
have values; missing fields have null values and explicit reasons. Empty,
unexposed, failed and unattempted reads remain distinct. Configured values never
populate actual fields.

| Field | Source and meaning |
| --- | --- |
| Name / device name | Unmodified native names |
| BMK | `PlantDesignation`, `LocationIdentifier`; `ProductDesignation` is unavailable if absent, never parsed from names |
| Configured MLFB / firmware | `OrderNumber`, `FirmwareVersion`; GSD identifiers retained as returned |
| Configured hardware revision | Exposed `HardwareRevision` only; commonly unavailable |
| Slot / position | `PositionNumber`, not an inferred rack/subslot mapping |
| Configured IP | Native node addresses, or aggregated descendant interfaces with source paths; no peer-module inference |
| Object / project path | Canonical path and exact `.ap21` path, not permanent vendor GUIDs |
| Actual identity | I&M0 MLFB, serial number, hardware revision and software revision |

Field names follow Siemens' [Openness device-item attributes](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-device-items/mandatory-attributes-of-device-items).

## Online coverage

The adapter calls only `Api.Browse`, `Modules.Browse`, and
`Modules.ReadIdentificationMaintenance` with `number: 0, type: actual`.
The [Siemens I&M API specification](https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_enus_21/comprehensive-information/cpu-function-manuals/web-server/api-application-programming-interface/reading-information-about-device-configuration/modules.readidentificationmaintenance?contentId=bELOMF_w6a39hWqfvu7ZNQ)
defines the fields, `read_diagnostics` permission, and unavailable-I&M errors.

| Family / route | Status |
| --- | --- |
| S7-1500 / S7-1500V Standard/T/F and S7-1200 G2 advertising the required methods | Implemented; protocol fixtures tested; hardware unverified |
| ET 200 and other attached I/O exposed by a compatible controller | Controller-mediated I&M0 at explicit hardware IDs; individual support and hardware unverified |
| Festo CPX-AP-I in the observed project | Offline capture verified; direct Festo adapter not implemented; routed I&M applicability unverified |
| Older S7-1200, S7-300/400, unsupported S7-1500 firmware, R/H, other direct vendor interfaces | No qualified adapter in this change; never fabricate actual identities |

The [Siemens method/version matrix](https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_eses_21/comprehensive-information/cpu-function-manuals/web-server/api-application-programming-interface/supported-clients?contentId=SBfK8bjxl0TSJ9~Z9OfKqA)
lists these module methods from V4.1 for the listed non-R/H families. Runtime
`Api.Browse` decides availability. The software-controller version `V40.1` in
the observed project must not be treated as a hardware CPU's `V4.1`.

`device_online_browse` takes one explicit HTTPS controller origin and an optional
hardware ID for a subtree. [Modules.Browse](https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_enus_21/comprehensive-information/cpu-function-manuals/web-server/api-application-programming-interface/reading-information-about-device-configuration/modules.browse?contentId=T7WqQeb3cwl2qgEBx7Qa~Q)
supplies central/distributed module identifiers. This is controller configuration
metadata, not actual physical identification and not a network scan.

`device_identify` takes `snapshotId`, `endpoint`, `controllerRowId`,
`controllerHardwareId`, and 1–32 explicit `bindings: [{rowId, hardwareId}]`.
The endpoint IP must match an interface under that CPU. The online CPU name/type
and selected module names must match. IDs are scoped to that controller; mapping
is explicit and recorded. The controller hardware ID is reserved for the selected
CPU row, even when another row has the same name. Null bindings and conflicting
controller assignments refuse before transport. Downloaded configuration equivalence is not certified.
The result is a new snapshot with per-row actual timestamps. Selected failed
rows lose stale actual values; unselected rows retain their previous state.
Malformed payloads and incomplete response reads become explicit per-module
failures; malformed controller discovery fails every selected row before I&M
reads. JSON-RPC envelopes require version 2.0, the exact request ID, unique
properties and exactly one result or error. A new explicit identification request
can retry after connectivity or authorization is restored; ancestors retain their
original observations. Caller cancellation propagates without publishing a child
snapshot. [Offline regression evidence](../verification/inventory-response-20261016.md).
Limits: 10 seconds per complete response,
60 seconds per identification request, 256 KiB per response.

TLS certificate validation remains enabled; redirects/proxies are disabled.
For authenticated targets, establish an authorized Web API session externally.
A host environment variable named `TIA_INVENTORY_<target>` can hold a JSON record
with `endpoint` and `token`; pass only its name in `sessionEnvironmentVariable`.
The endpoint must match exactly after URL normalization. Tokens are never
returned, exported or forwarded to another endpoint. The adapter does not log
in, try defaults, enable web servers, download, or change CPU state. Missing or
expired sessions remain explicit authentication/permission failures.

## Export and mismatches

Call `device_inventory_export` with `snapshotId` and `format: csv` or `xlsx`.
The full captured scope is exported, independently of the last page viewed.
Artifact and metadata paths include SHA-256 and byte counts; `artifact_read`
transfers them from a remote host/VM.

Both formats retain configured/actual columns, `[state]` markers, missing-data
reasons, provenance, timestamps and comparisons. Only available pairs are
compared. MLFB whitespace, firmware `V` prefixes and omitted zero patches are
normalized. Catalog wildcard articles are not comparable. A mismatch is a
value difference, not a firmware compatibility verdict.

CSV uses UTF-8 BOM, quoting and formula-prefix neutralization; import columns as
text in Excel to retain leading zeros. XLSX uses literal string cells, frozen
headings, filters and a capture-details sheet. SpreadsheetML escaping preserves
literal `_xHHHH_` sequences, CR/LF, tabs and XML-prohibited control characters;
cell limits apply before escaping. XLSX rejects cells over Excel's
32,767-character limit; CSV preserves those values. Failed exports attempt to
remove only files created by that call, including failures in the capture-details
sheet. Filesystem deletion failures remain errors; this is not a crash-atomic
publication guarantee. Missing rows or incomplete capture warnings are never
silently removed.

The offline inventory tests emit `excel-fixture.json` beside their exports.
On a workstation with Excel installed, run
`scripts/Test-InventoryExcel.ps1 -FixtureDirectory <export-artifact-directory>`
to open those generated workbooks read-only in a separate hidden Excel process.
The harness disables macros/link updates, compares exact inventory name cells and warning
text, checks unchanged file hashes and records owned-process cleanup. It requires
Excel but no Siemens product. [Export preservation and application evidence](../verification/inventory-export-20261016.md).

## Hardware validation procedure

Start with one CPU, one remote head module and one replaceable I/O module.
Record build hashes, project path, controller endpoint, adapter, hardware IDs,
slots and observation times. Compare each actual field with TIA **Online &
diagnostics → Identification** for that same module and retain the diagnostic
evidence. An offline Properties view is not a reference for an actual read.

Include matching data, a firmware mismatch, unavailable I&M, denied permissions,
disconnected modules and duplicate names. Expand coverage only after per-target
checks pass. `scripts/qualify_device_inventory.py` proves offline native
collection and export and owns only its MCP processes, not the open TIA UI.
No current record claims that the physical online comparison passed.
