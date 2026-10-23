# Shared inspection values and attributes — 17 October 2026

Base: `c4f38864f5633ceb2133ddbd28210d88c074aa72`.
No Siemens product, SDK, simulator or PLC is installed here.

## Repair and boundary

The shared production routines now live in `OpennessSession.InspectionValues.cs`.
Attribute metadata is preflighted before paging: at most 4,096 declarations with
nonempty names of at most 4,096 UTF-16 code units; duplicates cannot authorize a
getter. Missing, malformed, over-bound or failed metadata never authorizes a read.
Sensitive names use the existing exclusion predicate before access/value getters.
Rows retain known readability, runtime type, captured/completeness flags and status.
Every incomplete row adds a warning; existing attribute consumers therefore cannot
report their observed scope complete. Native exception values are not disclosed.
Channel enrichment only follows complete shared reads and sanitizes its errors.

Each rendered value has a 512-node / depth-16 bound, 64 items per collection and
4,096 code units per string. Truncated text does not split a surrogate pair. One
extra collection item may be observed to establish truncation. Exact finite scalar,
enum and system-library Version text contracts remain; arbitrary ToString methods
are never invoked. Nonfinite/unsupported values and failed enumerations are explicit.
Typed engineering objects remain bounded display projections with incomplete
identity; names/positions do not establish an exact native endpoint. Date/time,
duration and GUID objects are explicitly display-only. A native getter or enumerator
can still block internally; these are traversal/output bounds, not timeouts.

This shared renderer reaches inspection, index, Explorer constants, interface text,
TO connection values and software-unit relation fingerprints. Existing fingerprint
coverage remains incomplete; changed display bytes do not prove complete effects.
The catalog marks 73 affected routes unverified while preserving evidence IDs.
The family/encoder qualification harnesses reject nested incomplete projections and
attribute captures; remote-I/O channel selection requires complete readable capture.
Current connected-encoder projections consequently cannot qualify until exact native
identity evidence is implemented and tested. Existing historical receipts stay intact.

## Executed checks

The before log (`artifacts/inspection-values-before.log`) reproduces three bugs
with the original extracted routines: sensitive getters ran, opaque ToString ran,
and an out-of-page duplicate authorized a read. The remainder of that general run
passed (179/182). The repaired run (`artifacts/inspection-values-after-01.log`)
passes 188/188 checks, including nine shared-read groups compiled from the actual
production partial against synthetic native shapes. Cases include readable scalars,
nulls, enums/version strings, nonfinite values, metadata limits/errors/duplicates,
excluded getters, collection disposal/error/width/depth/cycles, surrogate boundaries,
and bounded incomplete engineering projections. This does not compile the Siemens SDK.

Python targeted checks (`artifacts/inspection-values-python-01.log`): 20 family,
10 encoder and 16 remote-I/O tests pass. The 20 family tests also pass with
Python optimization enabled (`artifacts/inspection-values-python-optimized.log`).

Frozen Git tree: `7136f83d0dc42b6d8135d0392441b8e77e6d92d0` (1969 files).
Source manifest (`artifacts/inspection-01/source-manifest.json`) SHA-256:
`ceaab38331b8c5079caf1956f4ab2c3484d9648e51ea952a7eafa30b175a3922`.
Source ZIP SHA-256: `2d680c2f21db73d125a731132a0557fa8c800e70c8121b28d735b4d1f39f321c`.

The aggregate (`artifacts/inspection-01/source/artifacts/checks/result.json`)
passes 39 managed suites, 1,304 Python tests (one unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all offline MCP workflows.
The general executable repeats all 188 checks, including the nine new read groups.
Run: 2026-10-17T06:33:15.9198907Z to 2026-10-17T06:43:56.5280143Z.
Receipt SHA-256: `301d4b0f38ff6f3a7c1e121c99745971781491e5fdde0ea7a2552250a8782d71`.
Console evidence (`artifacts/inspection-01/aggregate-console.log`).

All 22 package stages (`artifacts/inspection-01/source/artifacts/package-checks/result.json`)
pass, including relocation. Bundle `tia-mcp-offline_20261017_063338_099a986f` contains 1391
manifest files. Receipt SHA-256: `2276310b6a4a9aabf47218e3502c51435c4e2c0ef2f595ab75bf2b4bb41659ab`.
Manifest SHA-256: `3839cb3b47e4baac0f7c5013ca3ca97c2a44393561286beca19a69e4baf324c5`.
ZIP SHA-256: `a05e4114c44b87f2afebcf395891aa233c65259fa2c6248d5b4b15c9d1a5f633`.
This offline package excludes native workers; it does not compile the Siemens SDK.
Hashes establish retained byte integrity, not publisher or native authenticity.

The source comparison (`artifacts/inspection-01/final-source-comparison.json`)
allows only completion-documentation changes after testing and verifies all 106
unavailable historical evidence binding rows remain unchanged. The
integration receipt (`artifacts/inspection-01/integration.json`) records the
pushed commit, hosted CI results and cleanup.

## Remaining work

This repairs the shared property-value boundary; the
[full offline queue](../plans/offline-execution-20261016.md) remains active.
TechnologyConnections still needs bounded service/sensor enumeration, independently
reported absent/failed/unvisited services and complete direct-field aggregation.
Other display-only callers do not yet aggregate all value markers into scope-level
coverage; markers must not be treated as lossless identity or content. Native
getters/SDK compatibility, endpoint identities, TO-03–12, complete preservation,
recovery and physical acceptance remain unverified. No new native qualification is
claimed, and no product installation or PLC connection was attempted.
