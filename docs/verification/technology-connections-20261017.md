# Technology connection-service observations — 17 October 2026

Base: `a8ff263bfead8a19995cc3b5a23c52cc046a9f32`.
No Siemens product, SDK, simulator or PLC is installed here.

## Change and limits

`OpennessSession.TechnologyConnections.cs` now probes the existing axis, encoder,
measuring-input and output-cam services independently. A service failure no longer
suppresses its peers. The existing scan budget is consumed before each service,
slot, sensor collection, direct field or attribute capture; null sensor entries also
consume it. Enumeration stops at exhaustion and is disposed. One lookahead may be
observed to detect exhaustion. The fixed four-service report remains visible even
when no budget remains. Fixed unvisited slot rows may accompany truncated scans.
Native getters/enumerators may still block internally; these are traversal bounds,
not wall-clock timeouts or guarantees of a consistent native snapshot.

`hardwareConnectionCoverage` has schemaVersion 1, the exact technologyPath, four
service observations, counts, scopeComplete/truncated and complete=false. Service
states distinguish notVisited, notExposed, observed, incomplete and readError.
notExposed means a null service/optional slot was observed; it does not prove target
capability or physical absence. A null sensor element or collection is incomplete.
Each row has a service, role, owner-relative observationPath, status, scopeComplete
and fieldStatus. Direct failures retain successful fields and peer services; native
exception text is omitted. Attribute failures and display-only native projections
cannot become complete connection evidence. Scope flags remain conservative after
a shared-budget truncation. Sensor positions distinguish observations within one
scan and are not durable native endpoint identities.

`technology_read` and create/parameter mutation evidence retain the same coverage;
mutation evidence now uses the canonical selected path instead of the object name.
The family/encoder oracles require all four service observations, exact owner/slot
paths, matching counts, complete fields and nested values. Empty lists require
explicit observed service absence. Coverage participates in before/after comparison.
Historical reports lacking the new coverage cannot qualify current source; original
receipts and all 106 unavailable evidence binding rows are preserved. Six affected
catalog entries remain unverified. The implementation uses already referenced native
members; the retained public member inventory is not a substitute for SDK compilation.

## Executed evidence

The [baseline run](../../artifacts/technology-connections-before.log) reproduces
unbounded null-sensor enumeration and skipped peer services (188/190 checks pass).
The [first repaired run](../../artifacts/technology-connections-after-01.log) and
[final targeted run](../../artifacts/technology-connections-after-03.log) pass all
196 checks. Eight groups link the actual production reader against synthetic native
shapes: service states, null sensors, getter/budget ordering, independent failures,
enumeration disposal/errors, optional slots, exact paths, attribute errors and typed
projection refusal. This does not compile the Siemens SDK/native worker. The final targeted and frozen runs include the exact-technology-path assertion.

The [first Python attempt](../../artifacts/technology-connections-python-01.log)
exposed the oracle mistaking fieldStatus.attributes for an attribute list. Validation
now checks attribute lists at the connection-row boundary. The
[repaired targeted checks](../../artifacts/technology-connections-python-03.log)
pass 22 family, 10 encoder and 33 conformance tests. The 22 family cases also pass
[with Python optimization](../../artifacts/technology-connections-python-optimized-02.log).
Adversarial cases include missing/duplicate services, false absence, foreign owner,
missing/duplicate sensor positions, failed field declarations, invalid state types,
nested incomplete identities and cyclic/oversized supplied evidence.

Frozen Git tree: `d299a8a4665e2a901fa53bdb9252c885677e50cb` (1972 files).
[Source manifest](../../artifacts/to-conn-01/source-manifest.json) SHA-256:
`899cb9893b141948d5327f14221eaf8095a6bb39ac687c4a62f793458c265d14`.
Source ZIP SHA-256: `eab3afcf1b1963759cbe6908de519a5c69802ec261eca3ccb6820583be61da48`.

The [aggregate](../../artifacts/to-conn-01/source/artifacts/checks/result.json)
passes 39 managed suites, 1,306 Python tests (one unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all offline MCP workflows.
The general executable repeats all 196 checks, including the eight new connection groups.
Run: 2026-10-17T06:57:17.1139223Z to 2026-10-17T07:07:57.4790448Z.
Receipt SHA-256: `120ec381ae4453790ad835975329f9c16b69c0906289f7e09122b5cf85047f37`.
[Console evidence](../../artifacts/to-conn-01/aggregate-console.log).

All 22 [package stages](../../artifacts/to-conn-01/source/artifacts/package-checks/result.json)
pass, including relocation. Bundle `tia-mcp-offline_20261017_065739_02f08338` contains 1392
manifest files. Receipt SHA-256: `13cd775b1a5c8bc1f51ac3867a286d6a956909cb77f6dabada71f2b1b47ee003`.
Manifest SHA-256: `5a9a84d15167120cd283932f360b5c30a3dacbea68086b26d7dc1cb31fb662bb`.
ZIP SHA-256: `9ff0bb88d14de01fb75670edbcf6da69ea8ad65757cd5288fbe9006d79bd9c3f`.
This offline package excludes native workers; it does not compile the Siemens SDK.
Hashes establish retained byte integrity, not publisher or native authenticity.

The [source comparison](../../artifacts/to-conn-01/final-source-comparison.json)
allows only completion-documentation changes after testing and verifies all 106
unavailable historical evidence binding rows remain unchanged. The
[integration receipt](../../artifacts/to-conn-01/integration.json) records the
pushed commit, hosted CI results and cleanup.

## Remaining work

The [full queue](../plans/offline-execution-20261016.md) remains active. Next resolve
native endpoint references to exact, bounded hardware/tag/block owner identities
without promoting name-only projections, then continue TO-03–12. Other service
families, type/CPU/firmware eligibility, connection mutation recipes, full-effect
preservation and recovery remain open. SDK compilation, native service availability,
real composition behavior, persisted/reopened identities and physical acceptance are
unverified. Other display consumers still need complete-value aggregation where
appropriate. No native qualification or release readiness is claimed.
