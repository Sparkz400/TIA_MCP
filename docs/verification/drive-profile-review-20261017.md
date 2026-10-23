# Supplied drive profile verification — 17 October 2026

The frozen aggregate and portable package/relocation commands both exited zero.
This increment implements supplied parameter observation/proposal review and
completes the D10 documentation/source audit. No Siemens product or physical drive
was used. The input contract is our explicit supplied-evidence format, not a
claimed Siemens export format.

## Exact source

- Base commit: `92726e03662d5b8ab0b1573f0f6c5b048e27598d`.
- Tested staged Git tree: `82aec3582b540d228cada3e60406ea1b92f29e8a`.
- Frozen source: `artifacts/drive-profile-01/source`, 1875 files.
- Source manifest: `artifacts/drive-profile-01/source-manifest.json`, SHA-256
  `9269e14e10aeb0c4c634292b781db12f25dbd35416be3a21312778cfbab59ace`. The original `source.zip` is retained.
- After validation, only this record, STATUS and the execution queue were updated.
  Production sources, tests, harnesses, schemas, catalog and bindings match the
  frozen tree byte-for-byte. Final comparison checks every manifest entry.

## Executed checks

- All 34 managed suites passed; drive profiles contributes 74 checks.
- 1,280 Python tests ran successfully with one unavailable-native-evidence skip.
- All 24 Explorer model/client checks passed. No new browser/visual acceptance
  was run because this increment does not change the Explorer UI.
- All aggregate MCP workflows passed. The drive flow passes 34 checks across two
  clean Host lifetimes with workers deliberately absent. This is actual stdio
  MCP evidence, not desktop-client acceptance or a live Siemens session.
- Static catalog/bindings: 191 tools, 55 mutation kinds, 264 features. The normal
  Host advertises 171 tools; actual catalog checks cover all 264 entries.
- Retained evidence inventory: six byte matches, 106 unavailable historical files.
  Historical hashes remain retained; missing evidence is not a passed check.
- Portable package checks and all 14 included workflow/verification steps pass,
  including relocation into a Unicode path. Drive checks repeat there: 74 managed
  and 34 MCP. Strict original/relocated manifests cover 1,344 files, no extras.

Drive checks cover exact saved hardware selection, profile identity mismatch,
unknown firmware with explicit reasons, original Unicode bytes, scalar text without
numeric coercion, sparse bit names without ordinal inference, supplied parent
chains/cycles/bounds, exact BICO references and unresolved owners, value domains,
read-only contradictions that cannot be erased by changed access declarations,
unknown/writable access without approval, duplicate fields/names, hostile JSON,
input/row/depth/report/page/store bounds, restart after upload release, input and
saved-review corruption, and rehashed report forgery. Oversized page rows are
explicitly omitted and remain downloadable in the complete retained artifacts.
Supplied coverage never promotes any native identity/constraint/qualification flag.

Development validation grew from 67 to 72 to 74 managed cases; final targeted
validation passed all 74 managed and 34 MCP checks after rebuilding Host.
The final frozen aggregate and package use that same source. Expected refusals
and negative-test stderr are not failed aggregate runs or native qualifications.

## Immutable receipts

Paths below are relative to the frozen `source` directory.

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/checks/result.json` | `3aabd3c291c102842268351ace8d7b1eefcd1f2e146182fb552bca7e77205651` |
| `artifacts/checks/drive-profiles/verification.json` | `0a72abcb303dc37e9e4beb3522706a42d5ce7968e99d665c64c7623c974fd569` |
| `artifacts/checks/drive-profile-mcp/verification.json` | `3d72e311a907b8861cff9d945278b266611f596ba83947d367ca455b801d243e` |
| `artifacts/package-checks/result.json` | `b1d00d9a21c6f3a7f26dac26dc039ebc66b9eea173cda5b5d987411d26d92fe3` |
| `artifacts/package-checks/drive-profile-mcp/verification.json` | `4418750d1cbb3a29de7ca2cabcdc97c96afeb5c7d8c6f54ca5222f6a2762a263` |

Portable ZIP: `artifacts/releases/tia-mcp-offline_20261016_220828_7dc5906b.zip` under the frozen source.
SHA-256: `84fd32c03ef977dc30ccc9f5008748078addc151d030287b298d41aba80ddf3f`.
Manifest SHA-256: `26489d05a0ca6a251860c29e384ba62b69f58ca036027dd2fdb7d4879c6d7187`.

Commands executed from the frozen source (repeat with fresh output directories):

```powershell
./scripts/Test-Offline.ps1 -OutputDirectory artifacts/checks
./scripts/Test-OfflinePackage.ps1 -OutputDirectory artifacts/package-checks
```

The local Python environment supplied `scripts/requirements-test.txt` dependencies.
Console logs remain beside the frozen source as `aggregate-console.log` and
`package-console.log`; detailed logs and binary hashes are in their output trees.

## Hosted status and remaining acceptance

The base snapshot-review commit `92726e0` passed hosted
[Explorer](https://github.com/Sparkz400/TIA_MCP/actions/runs/38972607847) and
[Python/managed/package/OPC UA](https://github.com/Sparkz400/TIA_MCP/actions/runs/38972607823).
These runs belong to that exact base commit; this increment's new runs must be
read separately after push.

[Workflow](../reference/drive-profile-review.md),
[full D10 plan](../plans/expansion-plans/startdrive.md), and
[execution queue](../plans/offline-execution-20261016.md) retain genuine captures,
installed SDK compilation, native drive identity, typed/unit/range constraints,
engineering, telegram/TO integration, transfers, activation, device persistence,
security, safety and runtime acceptance as open work. No native write route is
implemented or qualified by this review. D10 and the overall goal remain open;
D11 alarm/supervision documentation and source audit is the next independent domain.
