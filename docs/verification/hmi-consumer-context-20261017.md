# Saved component/HMI consumer context — 17 October 2026

Base: `d835f2dfda472db2b4da1c90075ecf6d9d7bbd38`.

The [v2 workflow](../reference/hmi-component-review.md#saved-consumer-context-v2)
adds bounded incoming relationship evidence for each exact before component, after
component and destination snapshot. It reuses the existing saved-review traversal,
retains original edge evidence and explicit exclusions/bounds, and never joins
snapshots or converts supplied mappings into captured relationships. New/unverified
HMI outputs cannot become consumer roots. The full C01 scope remains open.

Pre-freeze checks pass 130 managed checks and 132 MCP checks across two clean Host
lifetimes. Independently authored graphs cover multi-hop consumers, diamonds,
cycles, unsupported/unresolved/virtual edges, missing after edges, exact review IDs,
all traversal limits including oversized edge evidence, install without a prior
snapshot, no inferred binding edges, v1/v2 version refusal, paging, retained graph
and report tampering, release/restart and false coverage claims.

An actual prior-build compatibility probe reopens three v1 records produced by
`d835f2d`'s frozen aggregate. Original input/report/source and saved-review bytes
remain unchanged; v2-only sections refuse. Final frozen compatibility evidence is
recorded with the aggregate below. Source acceptance never becomes native acceptance.

Frozen tree: `027afd827ca52760fd3459363c94cd430eda948c` (2060 files).
Source manifest (`artifacts/hmi-consumers-01/source-manifest.json`) SHA-256:
`f0162df638ebcf48b40800d014ddb783100e5b28c4dbd61e4964749832fc9628`.
Source ZIP SHA-256: `4f60cb698647d2ec51c902755281753cff0cfabe7aa14ba8dcf2fef68fa9b419`.

The full offline aggregate (`artifacts/hmi-consumers-01/source/artifacts/aggregate/result.json`)
passes all 42 managed suites, 1,455 Python tests (one unavailable-native-evidence
skip), 24 Explorer model/client checks and all public MCP workflows. This includes
130 component/HMI checks, 132 component/HMI MCP checks across two clean Host lifetimes,
226 general checks and 35 diagnostic groups. Contracts/source bindings pass with
204 tools, 56 mutation kinds and 278 features. Run: 2026-10-17T18:06:52.9299294Z to
2026-10-17T18:24:33.0620173Z. Receipt SHA-256: `d5863f0f1b5b714981e47b091be132640586dbe34fedc13ec372f0b794883ddd`.
Aggregate console (`artifacts/hmi-consumers-01/aggregate-console.log`).

All 25 package stages (`artifacts/hmi-consumers-01/source/artifacts/package-checks/result.json`)
pass, including original/relocated MCP, exact ZIP/manifest verification and the new
component/HMI install/update workflow. Bundle `tia-mcp-offline_20261017_180721_8dc4a567` contains
1453 manifest files. Package receipt SHA-256:
`3e3ea382ab47134da39b5ef6a4ac2b7136d48219d4c95dbabac7fcb66bab3d45`.
Manifest SHA-256: `5241c5830e037b33f8d927ef7d6ac1761f13589aaa48d865bf96e0d36be88f65`.
ZIP SHA-256: `f0211ba1ad8566b9ef0405a472aeabd7db663832e2d85b2126542ee00d24b9d2`.
Package console (`artifacts/hmi-consumers-01/package-console.log`).

The frozen v1 compatibility probe (`artifacts/hmi-consumers-01/v1-compat/verification.json`)
reopens three actual prior-build v1 records without changing their original evidence.
The new aggregate Host binary hashes match this probe; v2-only sections refuse.
Compatibility receipt SHA-256: `887ac641a92828dd17dda98e69a4efe4266d0b46f1284d37cfa13e900b00b50d`.

The final source comparison (`artifacts/hmi-consumers-01/final-source-comparison.json`)
allows only completion-document changes after the tested frozen tree and preserves
all 106 unavailable historical evidence binding rows. The
integration receipt (`artifacts/hmi-consumers-01/integration.json`) records exact
Git/CI and cleanup state. Browser visual tests were not repeated: no UI was changed.


Native SDK/product build, TIA/PLCSIM, library identity/dependency closure, generation,
install/update/compile/recovery, complete ownership/content preservation and HMI
visual/runtime acceptance remain unperformed. Browser checks are not repeated for
this code/schema-only workflow change. The full 44-workstream goal remains active.
