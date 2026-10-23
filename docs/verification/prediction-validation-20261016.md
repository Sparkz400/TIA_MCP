# Diagnostic prediction validation repair — 16 October 2026

The watch-planning development run exposed a pre-existing type error in shared
`TraceMeasurements.Conditions`: any type other than Bool was treated as numeric.
A Boolean prediction referencing a missing declaration failed with a misleading
numeric-precision error. A numeric prediction against the same unknown declaration
was accepted if it supplied a unit. Arrays and other unsupported types followed
the same implicit numeric path.

The shared validator now requires one of the diagnostic planner's existing scalar
types before checking Boolean operators or numeric values/units. Missing or
unsupported declarations return `SignalUnobservable` and the exact signal ID.
This covers case creation and measurement analysis without changing sample
arithmetic, adding types or inferring online observability. Known scalar types
with unavailable access still support syntactic prediction validation; trace
preparation retains its separate requirement for observable signals.

The actual MCP harness also now validates its schemas before starting the Host.
A schema-load exception previously occurred outside the shutdown guard after the
Host constructor, so that failure could leave the owned process unclosed. The
regression uses a mocked constructor and verifies it is never called on schema
failure; the normal workflow still exercises actual Host exit/restart behavior.

## Reproductions and development evidence

- `artifacts/prediction-fix-dev/diagnostics-before.log`: both new groups fail;
  Boolean gets the wrong code, and numeric unexpectedly succeeds. Existing groups
  remain passing. The corrected run `diagnostics-after.log` passes all 25 groups.
  It checks missing and array declarations in both `when` and `expect`, Boolean
  and numeric conditions, known inaccessible scalar types, invalid Bool operators
  and capture refusal for unavailable access.
- `artifacts/prediction-fix-dev/harness-before.log`: constructor-order regression
  fails (three other tests pass). `harness-after.log` passes all four checks.
- `artifacts/prediction-fix-dev/mcp01/result.json`: actual offline Host workflow
  passes, including public refusals for both Boolean and numeric unknown-type
  predictions. Original review upload, watch planning, measurement import/analysis,
  exact downloads and three normal Host lifetimes still pass with absent workers.

## Aggregate verification

The final verification uses one full aggregate and a separate frozen Python delta:

| Evidence | Exact scope and result |
| --- | --- |
| `artifacts/prediction-fix-01` | Full `Test-Offline.ps1` passes: all 29 managed suites, 1,264 Python tests (one unavailable-native-evidence skip), 24 Explorer model/client checks and every actual MCP workflow. Diagnostics pass 25 managed groups and 24 MCP steps across three Host lifetimes. |
| `artifacts/prediction-fix-04` | Final Python delta passes all 1,266 tests (one skip), contracts, source bindings and retained-evidence audit. The final package-catalog MCP verifier passes against the frozen `prediction-fix-01` Host with absent native worker paths. |

The full-aggregate tree is `a105d36dbe3ebafca2d943606df6f9dcceb60db0`
(1,781 files); its source manifest SHA-256 is
`29b434b9313115b312b95bc5a430ddfc473f2528b7022ef0a7c32612d33b353a`,
aggregate result SHA-256 is
`57b9ef785b610cdd9627220c35f2ca55f71fc354dca59b84fb18ccdc6a0fa9f8`,
and diagnostic MCP result SHA-256 is
`7cb5b992604a6a659b0bd213ea1b86bda67eecd2c61e6b25efc45d6372dbc11c`.

The final Python-delta tree is `15f47038ab6918414ad6682969cb8178ef61bcd7`
(1,783 files); its source manifest SHA-256 is
`eda99b103e9f88bf88aa6bf12887ba53d2fa40eaa023282932cc0cd38d3bbbe5`,
Python result SHA-256 is `1f13db9738a90303c02787cdf667e649cd3c0a7ab2a3b942c75c914b40dda138`,
and final package-catalog MCP result SHA-256 is
`0f3253571ff594e245e10e884534e3dc95c7a33bc5c248bc04cfbcd1e5e97fc6`.

The only differences between these tested trees are the package catalog Python
reader/verifiers/tests and their verification document. All C# source, project
files, managed tests, contracts and bindings are identical. The final verifier
records its own hash and the actual Host/Core binary hashes. Documentation alone
changed after the final tested tree.

The [package catalog repair](package-catalog-20261016.md) was found while the full
aggregate ran. `prediction-fix-03` retains an unsuccessful Python run: two failures
and one error in older package mocks lacking real pagination/version fields.
After updating those fixtures, all ten package smoke groups pass and the final
full Python delta passes. Snapshot `prediction-fix-02` was prepared but not run;
it is not validation evidence.

The retained native evidence audit still finds six exact matches and 106 missing
files. Prior commit `7c93c8c` passes hosted Python/managed/isolated OPC UA
(`38917212202`) and Explorer (`38917212126`). No Siemens installation, SDK build,
native acquisition, PLC simulation, native bundle validation or release acceptance
is part of these repairs. Browser UI was not rerun locally; UI source is unchanged.
