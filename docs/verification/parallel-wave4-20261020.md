# HMI set evolution, diagnostic quality and OPC UA runtime — 20 October 2026

The fourth parallel increment is complete within its agreed scope. Explicit HMI
set evolution, diagnostic measurement quality/clock epochs and a secure read-only
OPC UA client pass focused qualification and the combined frozen aggregate,
relocated-package and browser validation. This is not completion of the full
44-workstream release or physical/native qualification.

## Delivered workflows

| Lane | Delivered behavior | Evidence |
| --- | --- | --- |
| C01 / D15 / X08 | Versioned explicit output additions/removals/renames and same-type member rebinding; complete prior/current/proposed accounting, retained peers, manual-edit conflicts, publication-failure retention and mixed v1/v2 lineage. Removal/rename effects are previews; no native deletion or installation. | [Workflow](../reference/unified-text-list-set-evolution.md), [lane qualification](hmi-set-evolution-20261020.md). Focused managed qualification passes 213 checks over 45 cases; final Host workflows retain the same independent output and compatibility checks. |
| C04 / D17 | Original measurement v2 bytes, bad/uncertain/absent readings, truncated captures, irregular timing and explicit clocks/epochs survive import, analysis, comparison, single/repeated experiments and JSON/Markdown/HTML reports. Unsupported timing boundaries cannot become transition or behavioral proof. Existing v1 semantics remain compatible. | [Workflow](../reference/diagnostic-measurement-quality.md), [lane qualification](diagnostic-measurement-quality-20261020.md). 83 new managed quality checks, 160 focused MCP checks and actual prior-record replay. |
| D16 / RT-04 / C03 | Explicit encrypted pinned OPC UA connect, bounded browse, exact typed scalar reads, local retained status and disconnect; anonymous/protected username authentication, exact namespaces/status/timestamps, unknown-outcome reservation, no reconnect/replay and captured-transport cleanup across record faults. | [Workflow](../reference/opcua-runtime.md), [lane qualification](opcua-runtime-wave4-20261020.md). Actual SDK servers and actual Host/DPAPI workflows with independent value/service mutation controls. |
| Integration | Shared Host/solution wiring, 248 declared tool bindings, 56 mutation kinds, 322 feature entries, exact dependency notices and complete frozen validation. | Original process, source, binary, package and browser receipts under `artifacts/pw20w4s1/artifacts`; coordinator bindings under `artifacts/parallel-wave4-20261020`. |

## Combined validation

The source checkpoint `artifacts/pw20w4s1` contains **2,675 files**.
Its adjacent manifest SHA-256 is
`308bd94382509a17bd6ea5e32554dd973b3883a5eb006c9a7a69a7dbfadfb8cf`.
All frozen bytes remain unchanged. The working implementation matches this
checkpoint; only TODO, STATUS, the execution plan and this final note were
updated afterward, with their exact final bytes retained separately.

| Validation | Passed result |
| --- | --- |
| Complete `Test-Offline.ps1 -Suite All` | 45 managed suites, 1,591 Python tests (1 skip), 25 Explorer model/client checks and every aggregate Host workflow. |
| Complete `Test-OfflinePackage.ps1` | 48 stages, including both original/relocated MCP smoke tests, exact ZIP contents, all existing workflows and the new HMI/diagnostic/UA workflows. |
| Actual prior diagnostic replay | 162 checks against each final aggregate and relocated Host; 289 original files remain unchanged. Nine earlier compatible export additions are accounted separately. |
| HMI compatibility | Actual retained v1 sets and mixed v1/v2 lineage, prior v1/v2/v3 authors, exact original-byte preservation, independent parsed-output checks and 15 altered-output controls. |
| OPC UA aggregate | 334 managed checks and 64 actual Host/MCP checks. |
| OPC UA relocated package | 334 managed checks and 64 actual Host/MCP checks. |
| Independent UA audit | 12 altered-evidence controls for each final managed result, complete final service allowlists, loopback-only listener proof, exact loaded-runtime hashes and original owned-process exits. |
| Browser checks | 38 Explorer, 26 repeated-report, 29 acquisition-report and 44 quality-report checks; retained desktop/narrow screenshots inspected, with no page errors, remote requests or unexpected dialogs. |
| Source/evidence integrity | Both original runner processes exited 0 without forced termination; source hashes matched before/after. Root audit matched all 112 retained inputs. The source-only checkpoint correctly reports 6 matched and 106 unavailable historical artifacts. |

The aggregate Python stage completed before package validation started. The
complete aggregate and package runs both passed on their first attempt without
source or timeout changes. Browser report checks consume final packaged exports.
Final prior-record replay uses actual earlier producer stores, not regenerated
lookalike fixtures.

The final package manifest is `409c52d926d50e58ef07a65b4eaa88f91467484abfaca41442bc231cea3d1482` and its ZIP is
`4f43efd79e37201533a36b12e5c53148202f8384f9e68aca39855232134e8ebc`. Independent verification checks original and
relocated manifests, every file/ZIP byte, runtime dependency versions, matching
license/nuspec bytes and the absence of the fixture Server assembly from the Host.

| Runtime | Host DLL SHA-256 | Core DLL SHA-256 |
| --- | --- | --- |
| Aggregate | `c3cbee08eacfb708256556bf7769320b06265724ec954b12d94d997a15920bd3` | `fff51ef8cc1a500e536e842ff65b2a4124776759b4fd164b309fd652cbcd5509` |
| Packaged/relocated | `f27ffbc507088aa4af8c32a6f42387a929cc3d96e49b2f6cd5244ddf99d599dc` | `1008002fac1682ac41a66c2526d7277192c628ed65b5703406cd02924eb5268a` |

Exact runtime-module/Graph hashes and complete evidence references are bound in
the independent summaries and coordinator `final-verification.json`. Final
reporting copies are under `artifacts/parallel-wave4-20261020/final-reporting`.

## Dependency disposition and retained failures

The client pins the supported OPC Foundation 1.5.378.182 maintenance line.
Five SDK runtime packages, BitFaster.Caching 2.6.0 and Newtonsoft.Json 13.0.4 have
exact upstream package/license/source provenance. The test Server dependency is
excluded from the shipped Host. The final eleven-package Host graph matches the
dated official NuGet advisory audit: twelve exact packages including the test
server, zero matching advisory ranges at collection. This is a point-in-time
observation, not future vulnerability or physical-device certification.
[Retained provenance](../../third-party/opcua-runtime/README.md).

Earlier failures remain retained in the lane receipts. The preliminary combined
Host build exposed an incorrect namespace import; it was fixed before freezing.
An artifact-only package-summary formatter initially assumed every check count
was a list. Its failed verifier/log are retained; corrected reporting rechecks
the same successful package without changing source or rerunning its workflows.
The aggregate summary also corrected a label that selected 78 MCP checks instead
of 117 managed checks. Its correction receipt binds the unchanged original
console and prior summary hash/value; full superseded summary/helper bytes were
not retained. Neither reporting correction changes the validation outcomes.
Diagnostic browser qualification exposed narrow-width overflow, repaired for
v2 without changing v1 report bytes. UA development exposed cancellation,
conversion, cleanup and oracle gaps; final actual-server runs and independent
negative controls cover those repairs. Earlier DNS-name fixture runs bound
wildcard listeners and remain development evidence only. Final runs use literal
loopback endpoints and verify their actual listener addresses.

The third wave's completion receipt and exact four reporting documents were
verified and preserved before this wave. Its failed attempts and bounded Web
soak evidence remain retained with their original source/binary scope; this
wave does not claim a new fifteen-minute Web soak.

## Remaining boundaries

The native dispatch quarantine for the uncaptured diagnostics-reporter lifetime
remains enforced. The configured Classic archive is unchanged, SHA-256
`e6f1ae79c105d5fd473399d121f80d75e86ce494a8370dab486c6e5b6b3f3498`.
The user's existing prerequisite-setting authorization remains valid, but cannot
supply the missing original lifetime evidence. Unified settings, native
preservation/reopen/recovery and physical acceptance await admissible continuation.

Actual disk-full remains unqualified after the original VHD mount privilege
failure. No mount retry, resource exhaustion, native start or physical PLC
connection occurred in this increment. UA subscriptions/captures, broader HMI
formats/type conversion, independent online program/address/instance association,
device/firmware/license profiles, hosted CI billing, vendor-material publication
and the full 44-workstream release remain separate obligations.
