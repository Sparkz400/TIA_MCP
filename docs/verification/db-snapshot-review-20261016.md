# Supplied DB snapshot review verification — 16 October 2026

The frozen aggregate and portable package/relocation commands both exited zero.
This increment implements supplied XML inspection/comparison and completes the
D09 documentation audit. It does not qualify native comparison, capture, upload,
value writes or CPU/program identity. No Siemens product was used.

## Exact source

- Base commit: `4c334c03101b1bbc87ed1dc12d07e63406600729`.
- Tested staged Git tree: `04342d697f2512146a5d21a8c2430387ab1b7570`.
- Frozen source: `artifacts/db-snapshot-01/source`, 1865 files.
- Source manifest: `artifacts/db-snapshot-01/source-manifest.json`, SHA-256
  `9cd812b5749bf3b683fcb81cfba484da34bff8963dab91a2eb0301106dfbe793`. The original `source.zip` is retained.
- After validation, only this record, STATUS and the execution queue were updated.
  Production sources, tests, harnesses, schemas, catalog and bindings match the
  frozen tree byte-for-byte. Final source comparison checks every manifest entry.

## Executed checks

- All 33 managed suites passed; DB snapshots contributes 67 checks.
- 1,280 Python tests passed with one unavailable-native-evidence skip.
- All 24 Explorer model/client checks passed. No new browser/visual acceptance
  was run because this increment does not change the Explorer UI.
- All aggregate MCP flows passed. The new snapshot flow passes 30 checks across
  two clean Host lifetimes, with workers deliberately absent. This is actual
  stdio MCP evidence, not a desktop-client or live Siemens session.
- Static catalog/bindings: 189 tools, 55 mutation kinds, 262 features. The actual
  normal Host advertises 169 tools and the catalog check covers all 262 entries.
- Retained evidence inventory: six byte matches, 106 unavailable historical files.
  Their historical hashes were retained; absence never becomes a passed check.
- Portable package checks and all included workflows pass after relocation into
  a Unicode path. The new flow repeats all 67 managed / 30 MCP checks there.
  Strict original/relocated manifests cover 1,339 files with no extra files.

Snapshot checks cover exact captured DB selection, wrong CPU/case/name/kind,
virtual/missing PLC context, invalid selector Unicode, original UTF-8/UTF-16 bytes,
array/nested paths, unknown types, text-only numeric comparison, missing/changed
structure dates without freshness claims, duplicate paths, hostile/malformed XML,
input/depth/value-count/page/store bounds, rehashed report forgery, input/saved
source corruption, maximum 20,000-value inputs and upload release/restart.
Oversized page rows are explicitly omitted and retained in complete artifacts.

Targeted development initially passed 62 managed checks before five extra checks
were added. The first MCP fixture import failed because the synthetic saved model
lacked its project root. The fixture was corrected and clean rerun passed; no
production import guard was relaxed. The failed receipt is retained at
`artifacts/db-snapshot-dev/mcp-01`. Final aggregate/package tests use the complete
67-check source. Expected negative-test stderr in aggregate logs is not a native
execution or a passed external qualification.

## Immutable receipts

Paths below are relative to the frozen `source` directory.

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/checks/result.json` | `31f8ce95945acc4218f8e15643f5a209d5afee75ee8fc879b37704d65f01f5dd` |
| `artifacts/checks/db-snapshots/verification.json` | `ccb685197471e79c746161badc80d086029fd08f475a052058871f587974355e` |
| `artifacts/checks/db-snapshot-mcp/verification.json` | `41a0594be447fba2a8abc83edc62a2d17f4083f8cfc49947f1c025d7c92c2a4a` |
| `artifacts/package-checks/result.json` | `90c271a1c056a10bb7111e0a9a96d865e562cd4de85922ed30facbb11cbf2a45` |
| `artifacts/package-checks/db-snapshot-mcp/verification.json` | `6a212bc2bf3da2a806792b5b730870909314059ba9b16713a76c955fb43599ff` |

Portable ZIP: `artifacts/releases/tia-mcp-offline_20261016_214552_f7fd5bde.zip` under the frozen source.
SHA-256: `cf81837f57e589f66aed140bb33257d13a0fe5dee9e1e4796106f090058d4ed1`.
Manifest SHA-256: `e3fb7c787ca955d2d15fad573b5da7120879dca86d3c0387c2b37bd4ca49b355`.

Commands executed from the frozen source (repeat with fresh output directories):

```powershell
./scripts/Test-Offline.ps1 -OutputDirectory artifacts/checks
./scripts/Test-OfflinePackage.ps1 -OutputDirectory artifacts/package-checks
```

The local Python environment supplied `scripts/requirements-test.txt` dependencies.
Console logs are retained beside the frozen source as `aggregate-console.log` and
`package-console.log`; detailed logs and binary hashes are in their output trees.

## Hosted status and remaining acceptance

The prior connection-review commit `4c334c0` passed hosted
Explorer and
Python/managed/package/OPC UA.
These runs belong to that exact base commit; this increment's new runs must be
read separately after push.

[Workflow](../reference/db-snapshot-review.md),
[full D09 plan](../plans/expansion-plans/online-compare-snapshots-upload.md), and
[the full execution queue](../history/offline-execution-20261016.md) retain genuine
V21 XSD/export fixtures, native SDK compilation, CPU/program lineage, live freshness,
structural compatibility, capture/export, upload, value writes and target/runtime
acceptance as open obligations. No snapshot XML import or scalar conversion route
was implemented. D09 and the overall goal remain open.
