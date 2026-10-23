# Technology child discovery — 17 October 2026

Base: `fe657f92beb265a93dc99cd70453829ceeabf47c`.
No Siemens product, SDK, simulator or PLC is installed here.

## Scope and public basis

The shared TO scan previously visited only PLC groups and missed the documented
service compositions for output cams, cam tracks and measuring inputs. It now
follows `OutputCamMeasuringInputContainer.OutputCams` and `.MeasuringInputs`, with
exact owner/composition paths and the existing item/depth bounds. Exceptions or
missing collection objects fail selection/absence/fingerprints; cycles truncate.
Read views, object/capability resolution, index, mutation fingerprints/evidence and
Explorer capture use the same child identities. Existing root paths stay unchanged.
Creation still targets the PLC root; this adds no service-child creation recipe.

Context7 resolved only its V20 library and returned generic device examples, so the
implementation uses the specific [Siemens V21 child-service reference](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/creating-and-finding-to_outputcam-to_camtrack-and-to_measuringinput?contentId=VG9FS0UkqhImaR00Tn6ILw)
and the retained static inventory's exact type/property IDs. The
[extracted member receipt](../../artifacts/to-children-public-members.json) pins the
inventory hash. This evidence establishes documented member names, not a current
SDK build, native ownership or CPU/version eligibility.

The fixture oracle now requires canonical owner/composition/name agreement and
matching read views before mutation. The 17 affected discovery/model/mutation
catalog entries are unverified for this changed source, with old evidence IDs kept.

## Executed portable checks

[Managed evidence](../../artifacts/to-children-tests-02.log): 179 general tests pass.
Five new groups compile the real traversal/selection/fingerprint/deletion routines
against synthetic native shapes; a sixth checks the real Explorer snapshot store.
Cases cover escaped names, distinct owners/compositions, same-name refusal, exact
child deletion with untouched peers, read/service failures, null collections,
item/depth boundaries, cycles, owner moves and retained selection ancestry.
The native Explorer capture method and Siemens SDK are not compiled by these shapes.

[Python evidence](../../artifacts/to-children-python-01.log): 19 family-oracle and
33 conformance tests pass, including owner/composition contradiction before edits.

The [first browser attempt](../../artifacts/to-children-browser-01/browser-results.json)
reproduces a disabled expansion button on a live technology owner with uncached
children. Adding technology objects to the existing lazy-expansion kinds fixes the
flow. The [second browser run](../../artifacts/to-children-browser-02/browser-results.json)
passes all 31 checks: first child request, duplicate-name exact selection/handoff,
existing workflows, layout and 1k/10k/50k tree virtualization. No browser errors or
network requests occur. The [child screenshot](../../artifacts/to-children-browser-02-shots/technology-children-live-fixture.png)
was visually inspected: child hierarchy, selected canonical path, owner/composition
properties and exact handoff are visible. The parent protocol is synthetic; this
is not native engineering evidence or owner visual acceptance.

The [browser source comparison](../../artifacts/to-child-01/browser-source-comparison.json)
confirms the tested browser/harness files match the frozen Git tree byte for byte.

Final frozen Git tree: `2f3f5ce622f8f2e114f30a5b4a9853a0bc4b6343` (1966 files).
[Source manifest](../../artifacts/to-child-01/source-manifest.json) SHA-256:
`8a287519bb5760ffd10113f266ccfe00dcbf32e0df4ca977ecb491b34d3224db`.
Source ZIP SHA-256: `81b2c50c1ab89d7655348b5d6be446fbd5db344615557a989b40b2b8c6fde736`.

The [aggregate](../../artifacts/to-child-01/source/artifacts/checks/result.json)
passes 39 managed suites, 1,301 Python tests (one existing unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all offline MCP workflows. The
general executable repeats all 179 checks and the six new child/store groups.
Run: 2026-10-17T06:08:16.7787818Z to 2026-10-17T06:18:58.3306504Z.
Receipt SHA-256: `d07e8088902034892022514597689d07f3d55d2b9c28b108ead828dd7be89896`.
[Console evidence](../../artifacts/to-child-01/aggregate-console.log).

All 22 [package stages](../../artifacts/to-child-01/source/artifacts/package-checks/result.json)
pass, including relocation. Bundle `tia-mcp-offline_20261017_060840_5399f7bc` contains 1390
manifest files. Receipt SHA-256: `b7ac1c61991f87ed7613c4abf117c0d30c8ec24188cb6acdc671445cdaa53d0f`.
Manifest SHA-256: `f28beba6569014f891ef2c0ceda1b1c2b58009b23509c93cb0cf24de5b360627`.
ZIP SHA-256: `218eb85db5fb446c18e0e883e0365b14bb7830ed43294538a83e06c1ce544af7`.
This offline package excludes native workers; it does not compile the Siemens SDK.
Hashes establish retained byte integrity, not publisher or native authenticity.

The [source comparison](../../artifacts/to-child-01/final-source-comparison.json)
permits only completion-documentation changes after the frozen tests and verifies
all 106 unavailable historical evidence binding rows remain unchanged.
The [integration receipt](../../artifacts/to-child-01/integration.json)
records the pushed commit, hosted CI outcome and cleanup.

## Remaining work

This delivers the documented child-inventory path in source and repairs its live
viewer flow. It does not complete TO-03/TO-04/TO-06 or the full
[44-workstream queue](../plans/offline-execution-20261016.md). Next, audit the
connection-service readback boundary: unsupported/unobserved services, failed or
truncated property values and typed owner/channel identities must remain explicit.
Other service compositions, coupling, SDR, interpreter/cam content, exact CPU/type
eligibility, native alias/ownership behavior, full-effect preservation and recovery,
child creation/connection recipes, SDK compilation and physical motion remain open.
The complete flag remains false; scope completeness applies only to its declared
observations. Hosted CI on the base was refused before steps by account
payment/spending limits.
