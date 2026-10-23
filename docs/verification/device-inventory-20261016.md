# Device inventory verification — 16 October 2026

The four new tools are implemented: `device_inventory`, `device_online_browse`,
`device_identify`, and `device_inventory_export`. Configured capture and native
export were exercised through the real stdio MCP Host. Actual identification
has protocol-fixture coverage, **not physical-hardware qualification**.

## Passed checks

- `TiaMcp.Inventory.Tests`: 30,266 checks, including a 30,000-row snapshot over
  221 bounded pages, cursor replay/tampering/cross-snapshot refusal, immutable
  reads, unavailable fields, firmware/article comparisons, literal vendor `X`,
  CSV formula neutralization, XLSX text serials and incomplete-scope metadata.
- Protocol fixtures cover supported/missing API methods, wrong module identity,
  denied permission, unsupported I&M, malformed/oversized responses and wrong
  response IDs. The Host workflow proves successful and failed module reads can
  coexist in a new snapshot, failed refresh clears stale actual data, the source
  snapshot stays intact, and wrong controller IPs are refused before transport.
- `TiaMcp.Tests`: 144/144 existing checks passed.
- `TiaMcp.HardwareEngineering.Tests`: 46/46 existing preservation checks passed.
- `test_contracts.py`: 124 MCP tools, 55 mutation kinds and 197 feature entries
  passed catalog/contract checks. API binding-scanner tests: 26 passed.
- Host and V21 worker compilation passed without warnings or errors in isolated
  artifact output directories.

## Native offline observation

Final receipt: `artifacts/device-inventory-native-20261016-04/report.json`.
The earlier `-01` through `-03` runs remain retained; the final run includes
managed assembly identities and the UTC-normalized capture timestamp.

The existing open V21 project yielded **51 rows** over two pages (requested
page size 37), no traversal warnings, no truncation. Three independently scoped
device captures yielded **32, 8 and 11 rows**, matching the full inventory.
`Project.IsModified` was false both before and after capture. This is a read-only
observation, not a transactional preservation or concurrent-edit guarantee.

CSV and XLSX exports contain all 51 rows. An independent Python CSV/XML reader
compared every cell between the two exports, parsed all workbook XML parts and
confirmed the absence of formula cells. Excel application rendering was not
tested. All actual fields remain explicitly `notRead` in these offline exports.

Final build identities:

| Component | SHA-256 |
| --- | --- |
| Host managed assembly | `e3feffe9395eaf25908d827fd7e18462700c6e947ae68820eaeb950ac6ddad34` |
| Core managed assembly | `93ff8fe7341a244cf6eff394c4ff62e04f197fcb7541f7f37c39d4417e50cd5a` |
| V21 worker executable | `3ab64611448cf974291ec993e221ea91aae02fe8d9839af839b53bde97af5e8f` |

The harness's Host and worker exited normally without forced termination. The
existing user-owned TIA UI was left open. The separate discovery attachment was
also detached. User-facing exports are under
`output/device-inventory-20261016/` with metadata and verification receipts.

## Hardware validation remains pending

The configured CPU interface at `192.168.1.2` and Festo CPX-AP-I station at
`192.168.1.15` both timed out on ICMP and TCP/443 checks. The workstation adapter
configured on this subnet was disconnected. The exact observations are retained
in `artifacts/device-inventory-native-20261016-01/reachability.json`.
No interface settings, controller configuration, credentials or CPU state were
changed. No network-wide scan was performed.

No actual serial number, hardware revision or installed firmware was obtained,
and no TIA **Online & diagnostics** comparison passed. Firmware/API availability
on this software controller is unverified. The direct Festo route and older
Siemens families are not implemented by this first Web API adapter. Complete
the [hardware procedure and coverage matrix](../reference/device-inventory.md)
before claiming support for those targets or expanding coverage.

## Existing repository validation gap

The exact catalog-to-release-obligation check fails on **23 previously added**
watch, translation and qualification/operation features that lack obligation
mappings. All four new inventory features have mappings. This change does not
rewrite the other streams' release classifications. The regular feature/tool
contract checks pass; neither result promotes release or hardware acceptance.

The generated implementation bindings include the four new tools. A subsequent
whole-tree freshness check found concurrent changes in
`TranslationImportAdapter.cs` and `TranslationImportWorkflow.cs`; that broader
binding-freshness check is not a pass. Inventory qualification above refers to
the recorded binaries, not every later working-tree change.
