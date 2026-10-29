# Trace timing and explicit comparison selection - 16 October 2026

## Scope and reproduced defects

D17/C04 supplied-measurement audit, base `a7cd7c276397bb3baca0bf973fb6dec98bcfa0dd`.
No Siemens product, SDK, simulator, PLC or acquisition backend was used.

- An expected interval of `1e-28` with consecutive sample times `0` and `2e-28`
  missed a gap: decimal `interval * 1.5` rounded the threshold upward. A negative
  pre-trigger time followed by a tiny positive timestamp can also round the
  elapsed interval. Gap decisions now compare scaled integers against the exact
  rational threshold; equality with 1.5 intervals remains non-gap.
- Subtracting a tiny anchor from a large valid timestamp (or the reverse) could
  round relative time before window selection. Such comparisons now return
  `MeasurementFormatUnsupported`. Exact large offsets and ordinary nanosecond
  alignment still work; no resampling or loss of original times is allowed.
- Null/non-array `signalMap` silently selected matching IDs automatically.
  Unknown option/mapping fields and invalid Boolean flags were also ignored.
  Core and the options schema now reject these requests explicitly; valid
  one-to-one cross-storage selection remains available.
- Already retained imports can contain the old rounded gap assessment. Analysis
  and comparison now recompute structural quality from retained samples. Original
  imports/reports remain immutable; regenerate reports to get corrected assessments.

Changes reach the existing `TraceNumber`, import/analysis and comparison paths,
Host tool description, comparison options schema, catalog/source bindings, managed
regressions, actual MCP harness and workflow documentation. No new tools,
dependencies, native adapters or mutation kinds are introduced.

## Development evidence

`artifacts/trace-time-dev` retains failures as well as passes:

- `before.log`: 26/28 groups; missed gap and unrefused rounded alignment reproduced.
- `options-before.log`: 28/29; malformed selection incorrectly accepted.
- `retained-before.log`: 29/30; stale cached assessment persisted into analysis.
- `after-complete.log`: 30/30 diagnostic groups. Includes exact threshold/equality,
  negative pre-trigger crossing, both anchor sides, reverse magnitude, legacy
  reassessment, immutable original evidence and malformed/explicit selection.
- `host-build.log`: successful Host build.
- `mcp-01/result.json`: 43 workflow steps with four clean, unforced Host exits;
  absent native workers. Includes matching samples becoming unresolved because
  of the exact gap, valid six-sample large alignment, either-side precision
  refusal, schema/Host malformed-selection refusal and explicit cross-storage mapping.

## Frozen validation

Frozen source: `artifacts/trace-time-01/source`, Git tree
`341a2384f3af90ad5df9f3a46214068cd1f0a296`, 1,834 source files.
The source manifest was saved before execution. All implementation, contracts,
tests and harnesses still match it; only final status/verification and next-step
documentation changed after the freeze.

- Complete aggregate: all 30 managed suites, 1,280 Python tests (one unavailable
  native-evidence skip), 24 Explorer model/client checks, contracts, bindings and
  every portable MCP workflow pass.
- Diagnostics: 30 managed groups and 43 MCP workflow steps, four clean unforced
  Host lifetimes in both aggregate and packaged runs. No native workers started.
- Package: original and relocated startup, exact ZIP/checksums/notices, native
  refusal, retained workflows and strict final verification pass; 1,323 files,
  no extra non-runtime files.
- Retained source evidence remains six byte matches and 106 unavailable inputs.
  Missing historical native evidence is not verified.
- Base commit `a7cd7c2`: hosted Explorer `38959490734` and all Python/managed/
  package/OPC UA jobs `38959490571` pass. The new commit's CI is tracked separately.

| Receipt relative to `artifacts/trace-time-01` | SHA-256 |
| --- | --- |
| `source-manifest.json` | `5b543fd49d544d872602c4df7a7c51a537c6827351b312ed41833192e88d7329` |
| `source/artifacts/checks/result.json` | `8a1149bb2cd149eb3032dca8174db8ccbd75ad8c5bc9e6549fb6d8b8e01eebfe` |
| `source/artifacts/checks/diagnostic-mcp/result.json` | `55394a38e05f28ee09c796295bcc1b8146da81a4afe81cb974a75a2748bac832` |
| `source/artifacts/package-checks/result.json` | `9f6b3278486abf73418b478bbd346a881fda9444218926612d09dfbbbac0d0f2` |
| `source/artifacts/package-checks/diagnostic-mcp/result.json` | `7bbcb025b8fa8da59412f14cd2338013342304bec64a0cc7dec018ca473f86a7` |

ZIP: `source/artifacts/releases/tia-mcp-offline_20261016_201125_cbb9d967.zip`,
SHA-256 `dc241a9569641334a2c332b8303808c4c702b8ddfeb767ec5f0d1f7a424da084`.
Package manifest SHA-256: `fc8a6c03d4e5a73f9205839b5b922526fc5fe28f1c05b56ec8f4aac95f086c21`.
Hashes prove retained byte consistency, not source authenticity or native acceptance.

## Remaining obligations

Native acquisition, clock/sample-task accuracy, original export authenticity,
source-program freshness and physical event capture remain unverified. Historic
reports are not rewritten or retroactively promoted. Full D17/C04 scope and the
[44-workstream queue](../history/offline-execution-20261016.md) remain open.
