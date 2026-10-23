# C01 integer-state generation — 2026-10-20

The retained offline authoring tool now supports one exact integer-state text-list
profile beyond Bool. This evidence qualifies the bounded offline generation,
regeneration, original-evidence dependency review and refusal behavior described
in [the reference](../reference/unified-integer-state-generation.md). It does not
qualify native TIA import, numeric representation, runtime behavior or full HMI
preservation. No native worker or TIA process was launched.

## Focused results

| Check | Evidence | Result |
| --- | --- | --- |
| Numeric managed suite | `artifacts/c01-numeric-wave2-20261020/managed-03/text-numeric/verification.json` | 315 checks passed |
| Actual numeric MCP and independent oracle | `artifacts/c01-numeric-wave2-20261020/mcp-01/verification.json` | 248 checks; two clean offline Host lifetimes |
| Actual prior-producer v1/v2 records | `artifacts/c01-numeric-wave2-20261020/prior-01/verification.json` | 170 checks; four clean offline Host lifetimes |
| Existing Bool generation regression | `artifacts/c01-numeric-wave2-20261020/bool-regression-01/text-generation/verification.json` | 71 checks passed |
| Focused Host build | `artifacts/c01-numeric-wave2-20261020/host-build` | Succeeded, zero warnings/errors |

The final numeric fixture has 46 retained cases: 35 ready candidate pairs and
11 conflicts with no generated files. The default HMI component test entrypoint
includes the numeric suite; its fixture is `hmi-components/text-numeric/mcp-fixture.json`.
`focused-binding.json` in the artifact root records lane implementation, schema,
harness and test binary hashes. This focused binding is separate from the later
shared-source aggregate/package freeze.

The MCP harness removes the tested v3 author records from an isolated copy of the
managed store before uploading each exact original request. It checks newly
generated responses against the expected records, releases uploads, and reopens
every exact record in a second Host. Both worker paths deliberately point to
nonexistent executables. Each Host exits normally without workers.

## Covered behavior

- All seven supported integer types, full-domain intervals and both endpoints,
  including exact `LInt` extrema without overflow. A 256-state reversed-input
  `USInt` template covers its entire domain.
- State additions/removals/changes/unchanged regeneration, explicit renamed
  member selection, Unicode/multiline text, changed language order/default
  language, exact peer bytes and v1 Decimal predecessor migration with a
  missing-lineage gap.
- Full-type coverage requirements, incomplete domains and defaults that cannot
  manufacture coverage; malformed IDs, empty/oversized state sets, unsupported
  types, missing translations, overlapping/reversed/out-of-range intervals,
  fractional/string/null bounds and unsupported open ranges.
- Manual current-byte changes, manual selected ownership, missing companion
  files, removed or changed members, foreign predecessor source, case-insensitive
  peer collisions and an explicitly selected integer type conversion.
- Original saved dependency observations: unavailable, observed empty,
  missing association, repeated unmapped indices, matched and separately matched
  repeated indices, direct contradictions, unrelated contradictions, incoming
  consumer contradictions and incoming roots lacking associations. Matching
  nonempty identity fixtures are explicitly synthetic supported XML; they do
  not assert native provenance.
- Original association contradictions remain selected even if the bounded
  dependency-impact rows are removed in the helper control. Incoming consumer
  roots without any association still contribute their original missing coverage.
- Retained record, input, candidate and peer tamper refusal. Honestly rehashed
  invented coverage/state summaries, modified original review input, modified
  derived review declarations, and changed candidate references refuse.

The Python oracle independently reads original retained review inputs, saved PLC
XML declarations, saved library observations, supplied original export bytes,
and saved graph edges. It derives selected dependency associations/coverage,
state changes, exact interval gaps using Python integers, peer file descriptors
and the complete expected paired YAML trees. It validates the strict v3 schema.
It rejects altered numeric bounds and translation values even when the altered
candidate has an honest new hash.

## Actual prior-producer evidence

This check uses original records from the previous frozen source/package,
`artifacts/pw20src3/artifacts/package-01`, rather than generating fresh v1/v2
fixtures with the new implementation. The adjacent original source manifest is
`artifacts/pw20src3-manifest.json`, SHA256
`453a6f714030ece30b67aae970e73e30f27f758d8c93ad7cbaa859b64b26a7ef`
(2,495 files).

The artifact-only harness is
`artifacts/c01-numeric-wave2-20261020/check_actual_prior.py`. It copies the two
original stores, preserving all original evidence. For v2, it identifies the
three actual previous Host-produced records by the previous harness's exact
uploaded request bytes and expected complete record trees, excluding its forged
negative controls. Expected responses come from the original retained records,
not from the new Host.

| Original source | Records reopened | Original files compared before/after |
| --- | ---: | --- |
| `hmi-component-mcp` | 3 v1 records | 35 project-review + 102 HMI-store files |
| `hmi-text-generation-mcp` | 3 Host-produced v2 ready records + 10 v2 conflicts + 1 v2 migration result | 5 project-review + 151 HMI-store files |

Each group is opened in two new Host lifetimes. Every response equals its original
receipt, and `artifact_read` returns exactly the original request, record,
candidate and retained-peer bytes. All original and copied store files remain
byte-identical. Source verification hashes and complete per-file manifests are
retained in `prior-01/verification.json`.

| Binary | Original producer SHA256 | Focused consumer SHA256 |
| --- | --- | --- |
| `TiaMcp.Host.dll` | `3151ea0bcf7db712f343d04eb164770d33cb79145568d11a395c1aa6a1cac050` | `1f137be7c68a4ff4ae6be06cfc01b0d012fb69de5386e8af9965fd18ab4d69a0` |
| `TiaMcp.Core.dll` | `98e9e0dba5a12a034f5127159522a880458348e7f2da1adc8def47fce5589c1e` | `e61effdb6618dee86bf5a80c0b31f6a9f6334c49c22eb555f678d81aa6cbb0cb` |

The focused numeric MCP and compatibility checks use the same consumer binaries.
The parent aggregate/package validation will bind the integrated final source
and catalog separately.

## Reproduction

Use fresh output paths on every run:

```powershell
dotnet run --project tests/TiaMcp.HmiComponents.Tests/TiaMcp.HmiComponents.Tests.csproj --artifacts-path artifacts/c01-numeric-wave2-20261020/build -- --numeric-generation artifacts/c01-numeric-wave2-20261020/managed-03
dotnet build src/TiaMcp.Host/TiaMcp.Host.csproj -c Release --artifacts-path artifacts/c01-numeric-wave2-20261020/host-build
python scripts/test_hmi_numeric_generation_host.py --host artifacts/c01-numeric-wave2-20261020/host-build/bin/TiaMcp.Host/release/TiaMcp.Host.exe --fixture artifacts/c01-numeric-wave2-20261020/managed-03/text-numeric/mcp-fixture.json --output artifacts/c01-numeric-wave2-20261020/mcp-01
python artifacts/c01-numeric-wave2-20261020/check_actual_prior.py --host artifacts/c01-numeric-wave2-20261020/host-build/bin/TiaMcp.Host/release/TiaMcp.Host.exe --source artifacts/pw20src3/artifacts/package-01 --output artifacts/c01-numeric-wave2-20261020/prior-01
dotnet run --project tests/TiaMcp.HmiComponents.Tests/TiaMcp.HmiComponents.Tests.csproj --artifacts-path artifacts/c01-numeric-wave2-20261020/build -- --text-generation artifacts/c01-numeric-wave2-20261020/bool-regression-01
```

The next native boundary remains closed. Unsupported types/conversions, additional
HMI resource families, transitive dependency closure, consumer completeness,
native numeric/default semantics and preservation after import require separate
evidence and supported implementations.
