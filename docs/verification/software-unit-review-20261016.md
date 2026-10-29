# Offline software-unit review verification - 16 October 2026

The current increment adds two offline Host tools for explicit supplied unit facts
and proposed relation/publication/preset edits. No Siemens software, SDK, PLCSIM
or PLC is installed. All fixtures are synthetic. The full D07 lifecycle and
[44-stream queue](../history/offline-execution-20261016.md) remain open.

Development evidence: `artifacts/software-units-dev/test-03/verification.json`
passes 56 managed checks; `mcp-01/verification.json` passes 34 actual MCP checks
with two clean Host lifetimes and no native worker. The managed checks include
exact/missing/foreign/virtual identities, nontransitive relations, publication and
namespace preservation, cycles/collisions, protected/generated/safety/OB refusal,
coverage contradictions, strict input/size bounds, retention/tamper, 4,096 supplied
dependencies and explicit oversized-row omission with complete artifact retention.

## Frozen aggregate and initial package validation

Frozen source: `artifacts/software-units-01/source`, Git tree
`2b942c7f72e3ab877877d948721703e2c37b2852`, 1,844 source files.
The manifest was written before either test run. Frozen files remain unchanged.
The final diff check found Windows text-decoding damage in the new Unicode test
fixture, the package relocation folder label and a documentation heading. These
strings were restored after this aggregate. Production/Core/Host/contracts are
unchanged; final affected fixture/MCP and package validation are tracked below.

- Full aggregate passes all 31 managed suites, 1,280 Python tests (one unavailable
  historical-evidence skip), 24 Explorer model/client checks, contracts, source
  bindings and every portable MCP workflow.
- Software units: 56 managed checks and 34 MCP checks pass in both the aggregate
  and relocated package. Each MCP run has two clean, unforced Host exits with no
  native workers. Input/report schema negatives run before starting a Host.
- Package: original and Unicode-relocated startup/configuration, exact ZIP and
  checksums, notices, native refusal, every portable workflow and final strict
  verification pass. 1,328 expected files, no extra non-runtime files.
- Retained historical evidence remains six byte matches and 106 unavailable files.
  Missing files are not accepted or regenerated as proof.

| Receipt relative to `artifacts/software-units-01` | SHA-256 |
| --- | --- |
| `source-manifest.json` | `d832a9d0d30b8a12f7f2e1087499757ec4a306942d61c8bad2725e90ab6d0d52` |
| `source/artifacts/checks/result.json` | `5b4c0999f5c509c5d67f515844f10eedca48598e63c43d295fd3e9c940df8f78` |
| `source/artifacts/checks/software-units/verification.json` | `0fb55a93ed67efb927b5499ba658131b6927faf2a190464894605feada87ffc5` |
| `source/artifacts/checks/software-unit-mcp/verification.json` | `1605f1c2a0529c76c006c3b27449d43805c3a33f2d3a884cb609110d00e647db` |
| `source/artifacts/package-checks/result.json` | `ee794b9f6f499801f431e1ae6970f4a64546ef2ea472c7c8bab41e97f33bdde3` |
| `source/artifacts/package-checks/software-unit-mcp/verification.json` | `9f1a5ab9f405f9553f0f9ce1cb7bd30a5b0161973a828d6022f813b320ee6a5f` |

ZIP: `source/artifacts/releases/tia-mcp-offline_20261016_203725_6b1d9105.zip`,
SHA-256 `7e4b8a7aa8871b2fcc21efb76d2f7b0deb6db7a29e298751b9101298d70d6806`.
Package manifest: `2952c722011b289994a5a89f9423a67c7ef03068a41fae0efe08fe077be28fcf`.
Hashes establish byte consistency, not source authenticity or publisher identity.

## Remaining obligations

No native software-unit lifecycle, SDK compilation, visibility/compile/runtime
outcome, live freshness, complete consumer graph, namespace propagation, movement
or library-template preservation is qualified here. The remaining D07 plan keeps
these explicit; this increment does not complete D07 or the overall offline goal.

The prior trace-timing commit `ebe1527` passes hosted Explorer `38961573703` and
Python/managed/package/OPC UA `38961573681`. The new commit must be checked separately.

## Final Unicode correction

Final source: `artifacts/software-units-02/source`, Git tree
`67f685160e1b6550cad558eea5d1ffe8020cdd29`, 1,844 source files.
Only documentation, the exact Unicode text in the synthetic unit fixture and the
relocation folder label differ from the aggregate snapshot. No Core/Host/contract
bytes changed. The first aggregate remains evidence for that exact baseline.

The corrected source passes all 56 managed unit checks, a Host build with zero
warnings/errors, 34 actual MCP checks across two clean Host lifetimes, and the
complete original/relocated portable package workflow. That package run also
repeats the corrected 56/34 checks. The `Über` directory exists with those exact
Unicode code points. Final source and working implementation/test/harness files
match this manifest; subsequent changes only update these three status documents.

| Receipt relative to `artifacts/software-units-02` | SHA-256 |
| --- | --- |
| `source-manifest.json` | `751f85bc303d0afee4c5acb4de6f02c2229440188e4a37a5f346f86bd1f0ea21` |
| `source/artifacts/targeted/units/verification.json` | `0fb55a93ed67efb927b5499ba658131b6927faf2a190464894605feada87ffc5` |
| `source/artifacts/targeted/units/mcp-fixture.json` | `1e7a995c7e9df7c360f794a7996cf0ec191d5be03f11ebae3d8f117f34f7c5cf` |
| `source/artifacts/targeted/mcp/verification.json` | `1c03cf379c8a19e7abbb50dcb04d279f5d2f4c64413ba392f123d905796d1926` |
| `source/artifacts/package-checks/result.json` | `3a01ef1a8f1156d04fa7ceff5888af449dabf159d2ca1fdd13f55e047652ba5e` |
| `source/artifacts/package-checks/software-unit-mcp/verification.json` | `39d974fd472f835d1c7c017aafb7022d1e4f61be61cadcc658aa534c02bf6ecd` |

Final ZIP: `source/artifacts/releases/tia-mcp-offline_20261016_204924_dca375a6.zip`,
SHA-256 `a78dbc6badabb94a177a1602706f3f9d434eea8588db140864cf8afc1a93af04`.
Final package manifest: `76d299a217bae1b1dba8c781f03daa4f7702f7ffbcbd098c30038c73c28d8749`.
Strict verification passes 1,328 files with no extra non-runtime files.
The full aggregate was not rerun for the two corrected test strings; their
executed targeted and package checks are recorded separately above.
