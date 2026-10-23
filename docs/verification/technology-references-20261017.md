# Technology connection endpoint references — 17 October 2026

Base: `4c304597e2b9a7d29f2c3b99c10bfcf5af1031e5`.
No Siemens product, SDK, simulator or PLC is installed here.

## Change and limits

Selected technology connection reads and create/parameter mutation evidence now
resolve supported DeviceItem, PlcTag, PlcBlock and PlcUnitBase values through the
selected project's ObjectIdentifierProvider. Each reference checks GetIdentifier,
Find, native equality and repeat identifiers; distinct objects with one identifier
refuse. Same names never establish identity. Project path and selectionPlcPath
identify the read context; selectionPlcPath is not a claim about hardware ownership.
Generic inspection outside these connection readers remains display-only.

Channels are resolved through their verified DeviceItem parent and a fully visited,
unique native channel selector (Number, Type, IoType). Native equality must find
exactly one channel in that parent's composition; aliases, duplicate selectors,
missing entries/compositions and self-containing owners refuse. A DeviceItem
container receives its own verified identifier. The channel has no invented native
identifier. Non-DeviceItem containers are not expanded.

References report engineeringReference/capturedReference, current-read resolution,
identityComplete and a specific identityBasis. Errors retain unresolvedReference
and a bounded reason without native exception text. A separate shared 50,000-read
budget bounds provider operations, channel enumeration and metadata reads. Enumerators
are disposed on failure/exhaustion. Identity text is at most 4,096 UTF-16 code units
and must encode as strict UTF-8. Existing per-value and connection scan limits remain.
Native operations may block internally; operation bounds are not wall-clock timeouts.

referenceCoverage records provider state, context, budget use, truncation and
identifier collisions. persistenceVerified and atomicSnapshot remain false. A
successful roundtrip is neither cross-session acceptance nor an atomic snapshot.
The qualification oracles require the exact context, identifiers, selector basis
and complete reference coverage. Old name-only channel summaries cannot qualify
current source. Original evidence and all 106 unavailable binding rows are retained;
the six affected catalog routes remain unverified. No new mutation route is enabled.

## Documentation check

Context7 resolution and retrieval were followed by the official V21
[Siemens ObjectIdentifierProvider documentation](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-projects-and-project-data/identifying-cross-session-object-for-hardware-software?contentId=20DJ38oyXZa0VsJ3baMHiA).
It documents project service acquisition, GetIdentifier(IEngineeringObject), Find(string)
and supported hardware/software types. Channels are not listed as standalone
supported objects. The retained public member inventory also contains the two
provider methods. Neither documentation nor that inventory substitutes for SDK
compilation, installed-version availability or native channel ownership tests.

## Executed evidence

The [first managed run](../../artifacts/technology-references-tests-01.log) passed
201/202 checks; the channel null identifier needed an explicit JSON null token.
The [repaired run](../../artifacts/technology-references-tests-02.log) passes all
202 checks. Six new groups link the actual reader against synthetic API shapes:
exact/foreign objects, provider/identifier failures, channel ownership/ambiguity,
bounds/disposal, identifier collisions and real connection-attribute integration.
No Siemens assembly or native API was compiled or called.

Targeted Python checks pass [13 encoder cases](../../artifacts/technology-references-python-encoder.log),
[22 family cases](../../artifacts/technology-references-python-family.log) and
[33 conformance cases](../../artifacts/technology-references-python-conformance.log).
The family cases also pass [with optimization](../../artifacts/technology-references-python-optimized.log).
New negative controls remove/change IDs, reference semantics, context, provider
coverage, selector metadata and owner/container markers. These validate supplied
evidence structure; they do not authenticate a supplied report's producer.

Frozen Git tree: `051142536760ee1a74b296fbf8aa652e108520f8` (1975 files).
[Source manifest](../../artifacts/to-ref-01/source-manifest.json) SHA-256:
`c6513a1721158b5fd5c7feb0491ebbd9956d1163e31dd783dca7f319ee658ab5`.
Source ZIP SHA-256: `46a68ddff3020a175c418de0f1810ff027c03f7e1132cbcae9c2c04bbc7d88c6`.

The [aggregate](../../artifacts/to-ref-01/source/artifacts/checks/result.json)
passes 39 managed suites, 1,309 Python tests (one unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all offline MCP workflows.
The general executable repeats all 202 checks, including the six new reference groups.
Run: 2026-10-17T07:26:06.5901105Z to 2026-10-17T07:36:47.0824874Z.
Receipt SHA-256: `9968fca1943cef06e08b4fc7d0f4d21ef13e36965ab870a94990f7c2de398ffb`.
[Console evidence](../../artifacts/to-ref-01/aggregate-console.log).

All 22 [package stages](../../artifacts/to-ref-01/source/artifacts/package-checks/result.json)
pass, including relocation. Bundle `tia-mcp-offline_20261017_072632_46dac327` contains 1393
manifest files. Receipt SHA-256: `e03546355f4f96a3e93edda806d1ccfc3cb9447516274fcc5fa3eb3b9bf62092`.
Manifest SHA-256: `89062d366dba0701bf3425a746116bbc797d0dbc209497888819ba139be600f0`.
ZIP SHA-256: `9debce02ad76314508ec253972383046efd2735a6cb879413a626891f97284a6`.
This offline package excludes native workers; it does not compile the Siemens SDK.
Hashes establish retained byte integrity, not publisher or native authenticity.

The [source comparison](../../artifacts/to-ref-01/final-source-comparison.json)
allows only completion-documentation changes after testing and verifies all 106
unavailable historical evidence binding rows remain unchanged. The
[integration receipt](../../artifacts/to-ref-01/integration.json) records the
pushed commit, hosted CI results and cleanup.

## Remaining work

Continue TO-03–12 and the [full 44-workstream queue](../plans/offline-execution-20261016.md):
service/family eligibility, parent-child lifecycle, connection mutation recipes,
consumer effects and preservation/recovery. SDK compilation, genuine provider
and channel behavior, cross-session persistence and physical acceptance remain open.
No native qualification or release readiness is claimed.
