# Supplied alarm inheritance verification — 17 October 2026

The frozen aggregate and portable package/relocation commands both exited zero.
Existing translation tools now inspect an explicit supplied alarm-text inheritance
policy, refuse inconsistent final mapped languages, and preserve approved repairs.
The accompanying D11 plan covers the full remaining alarm/ProDiag scope. No Siemens
product, genuine alarm workbook or native runtime was exercised.

## Exact source

- Base commit: `a9026cde73695d875545d73f9a7dd6911dd910ce`.
- Tested staged Git tree: `a6d7606e48fc1314affa95e3cc402d773e9effb5`.
- Frozen source: `artifacts/alarm-inheritance-01/source`, 1877 files.
- Source manifest: `artifacts/alarm-inheritance-01/source-manifest.json`, SHA-256
  `5fd2f7284ce670a95d292f4e2cfc5844a7358e00fdccb0c49df55d0066ed1f1c`. Original `source.zip` is retained.
- After validation, only this record, STATUS and the execution queue were updated.
  Production sources, schemas, catalog, bindings, tests and harnesses match the
  frozen tree byte-for-byte; final comparison checks every manifest entry.

## Executed validation

- All 34 managed suites passed. The translation suite now passes 96 cases,
  including 13 new policy/boundary cases; Host translation has 49 cases and
  synthetic translation import has 160. Native admission remains closed.
- 1,280 Python tests ran successfully with one unavailable-native-evidence skip.
- All 24 Explorer model/client checks passed. No new browser or visual acceptance
  was run because this increment does not change the UI.
- All aggregate MCP workflows passed. The extended translation harness passes
  six workflow groups across three clean Host lifetimes, including schema checks,
  three-language inspection, mixed-state approval refusal, a consistent repair,
  independent whole-peer preservation and exact restart retrieval.
- Portable package passes all 14 workflow/verification steps after relocation into
  a Unicode path. Its translation MCP repeats those six groups and three clean
  Host lifetimes. Strict original/relocated manifests cover 1,346 files, no extras.
- Catalog/bindings remain 191 static tools, 55 mutation kinds and 264 features.
  Actual normal Host discovery exposes 171 tools and all 264 catalog entries.
- Retained evidence inventory has six byte matches and 106 unavailable historical
  files; their recorded hashes were preserved. Missing evidence is not a pass.

Policy checks cover all inherited/literal/mixed states, an unedited third language,
missing versus empty fields, exact marker spelling, placeholder/approval/source
transition guards, unknown policies and the schema's 1,000-target limit. A supplied
map never establishes complete native language coverage. Original profile/report
shape remains unchanged when the optional policy is absent.

Before freezing, the new Host reopened both reviews created by the prior frozen
build, including its existing candidate. All 60 original and copied retained files
stayed byte-identical; the new inheritance page was empty for old profiles. This
proves those exact prior receipts remain usable, not every historical release.
Receipt: `artifacts/alarm-inheritance-dev/backward-01/verification.json`, SHA-256
`0c3993d8b64289233524d935d113f157a94e6acf488f549dc4bbc23dd005c5f6`. Initial policy tests passed 95 cases; the final
96-case run added the published target-count boundary check. Targeted MCP was
rerun on the final implementation before the frozen aggregate/package.

## Immutable receipts

Paths in the table are relative to the frozen `source` directory.

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/checks/result.json` | `936e8af002b3a645e401617f670583660e55a1ba30a4f42ccdd750beac9ea7ed` |
| `artifacts/checks/translation-mcp/verification.json` | `adaa00b11a33ae12e54e99fc4589bd4ae5bbbcc4ec6497669a20bdf401e2527a` |
| `artifacts/checks/translation-admission-mcp/verification.json` | `b206a936adac061d28c8d4576b0420b766efbfcfcd638ca95eef6381bc6a78d6` |
| `artifacts/package-checks/result.json` | `4b96ed8283ccf80eff6552afe08dc169c669b3ce44554f36710f4aa158a53132` |
| `artifacts/package-checks/translation-mcp/verification.json` | `db1ed3d9a102f01488453b28efea04502500d61ea7eea3dc410599247d01b1d9` |

Portable ZIP: `artifacts/releases/tia-mcp-offline_20261016_222853_84b7879e.zip` under the frozen source.
SHA-256: `fce7173ad2b6c90759d299ba653c1878728e5448e66bf90c76050f9f63282e00`.
Manifest SHA-256: `344223e63b9ba4256f4f3f609f5429c507be20016fb034dfa92b716f525595b4`.

Executed from the frozen source with local test dependencies available:

```powershell
./scripts/Test-Offline.ps1 -OutputDirectory artifacts/checks
./scripts/Test-OfflinePackage.ps1 -OutputDirectory artifacts/package-checks
```

Console logs remain beside the frozen source as `aggregate-console.log` and
`package-console.log`; detailed logs and binary hashes remain in the output trees.
Expected refusal/negative-test output is not a native qualification. The new plan
and repository guide links resolve; the guide's older scale-100k artifact link is
unavailable here and remains an explicit historical evidence gap.

## Hosted status and next work

Base drive-review commit `a9026cd` passed hosted
Explorer and
Python/managed/package/OPC UA.
These exact base runs do not establish this increment's hosted result; read its
new commit's runs separately after push.

Two separate read-only next-step probes are retained under
`artifacts/alarm-inheritance-dev/al03-probe` and `type-import-probe`. Existing MCP
review comparison follows independently supplied HMI→alarm→supervision→member
edges and a Bool-to-Int metadata change on both snapshot sides (six impact rows,
including two complete three-edge chains). Authenticity/complete-impact flags
remain false. Public import explicitly refuses a type-kind XML document under its
current documented block-only contract. This is a feature gap, not a native error.
These probes guide the next UDT/source/dependency increment; they do not deliver
native alarm relationship capture or close AL-03.

[Workflow](../reference/translation-review.md#supplied-plc-alarm-instance-text-inheritance),
[full D11 plan](../plans/expansion-plans/plc-alarms-prodiag.md), and
[execution queue](../plans/offline-execution-20261016.md) keep genuine format and
placeholder grammar, complete language/identity evidence, atomic inheritance-mode
transitions, PLC/ProDiag lifecycle, full dependency/HMI integration, native import,
compile/reopen/recovery and runtime acknowledgement/substitution open. This
increment adds no native operation or new MCP tool. D11 and the overall goal remain open.
