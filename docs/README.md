# Documentation

**AutomationML / CAx document review - 16 October 2026:** Six offline MCP tools inspect CAEX 2.15/3.0 against pinned XSDs, retain exact originals, compare declared identities and report conservative hierarchy/link/class-consumer effects. The [workflow](reference/automationml-workflow.md), [remaining native plan](plans/expansion-plans/automationml-cax.md) and [verification record](verification/automationml-workflow-20261016.md) separate document evidence from native importability. The full baseline passes 30 managed suites, 1,280 Python tests (one skip) and 24 Explorer checks. Final source passes 77 targeted checks, 69 actual MCP checks, Python and portable package/relocation checks; the verification record separates the exact sources. Prior inventory-export commit `c2a6bb2` passes hosted runs `38940028093` and `38940028137`. D05/C02 and the full offline queue remain open.

**Inventory export fidelity — 16 October 2026:** XLSX preserves escape-looking text, CR/LF, control characters and Unicode; failed exports clean up only their own files. The [frozen evidence](verification/inventory-export-20261016.md) passes 29 managed suites, 1,277 Python tests (one unavailable-evidence skip), 24 Explorer checks and all portable MCP workflows. Independent readers compare all six export pairs; Excel 16.0 build 20430 verifies 30,022 name cells and warnings while preserving another controlled workbook and exiting cleanly. Native Siemens, other-format and visual acceptance remain separate.

**Inventory response and identity repairs — 16 October 2026:** Malformed responses and interrupted reads now become explicit failed observations; successful peer rows and ancestor snapshots remain intact. Returned warnings cannot rewrite retained coverage, and controller IDs cannot bind to other rows. [Evidence](verification/inventory-response-20261016.md) records 30,315 targeted assertions, including 30,000-row paging, authentication retry, cancellation and CSV/XLSX failure exports. The frozen aggregate passes 29 managed suites, 1,273 Python tests (one unavailable-evidence skip), 24 Explorer model/client checks and all portable MCP workflows. Inventory response commit `d2b57ad` passes all hosted jobs: Python/managed/package/OPC UA `38936287994` and Explorer `38936287930`. The [full offline queue](plans/offline-execution-20261016.md) and native qualification remain open.

**OPC UA annotation preservation — 16 October 2026:** NodeSet comparisons now expose XML annotation changes, including affected declared consumers, and bound XML reader events before DOM allocation. Annotation-only edits do not falsely change method-argument text; text offsets remain significant. [Exact split-source evidence](verification/opc-ua-preservation-20261016.md) records the passing 29-suite baseline and final checks: 38 parser, 28 dependency/CLI, 24 workflow, 53 actual MCP and 1,273 Python tests (one unavailable-evidence skip). OPC UA commit `6bf024e` passes all hosted jobs: Python/managed/package/OPC UA `38933806994` and Explorer `38933807041`. Native qualification and the [full offline queue](plans/offline-execution-20261016.md) remain open.

**Watch document identity — 16 October 2026:** Content hashes and independent preservation checks now include XML annotations outside the root; opaque payloads cannot gain timestamp/whitespace normalization by using native-looking element names. Seven genuine capture hashes remain unchanged. The [frozen aggregate](verification/watch-document-identity-20261016.md) passes 29 managed suites, 1,273 Python tests (one unavailable-evidence skip), 24 Explorer model/client checks and all portable MCP workflows. Watch identity commit `3326931` passes all hosted jobs: Python/managed/package/OPC UA `38930866502` and Explorer `38930866322`. Native qualification and the [full offline queue](plans/offline-execution-20261016.md) remain open.

**Watch scalar preservation — 16 October 2026:** Captured row values and comment language identities now refuse opaque markup instead of flattening or discarding it. Direct comment edits preserve opaque text in other languages. [Exact split-source evidence](verification/watch-preservation-20261016.md) records a passing 29-suite aggregate, then final-source checks: 35 document/planner groups, complete engineering recovery/authoring tests, 1,266 Python tests (one unavailable-evidence skip), a clean Host build and five actual Host boundary checks. Watch scalar commit `71154ca` passes all hosted jobs: Python/managed/package/OPC UA `38928381550` and Explorer `38928381546`. The [full offline queue](plans/offline-execution-20261016.md), native adapter build and native acceptance remain open.

**Translation preservation repairs — 16 October 2026:** Supplied-workbook edits now preserve accepted row identities and opaque extensions, reject text/index content that would be discarded, and independently verify selected-cell metadata during reconciliation. [Scope and exact evidence](verification/translation-preservation-20261016.md) distinguish the passing full baseline from final targeted checks: 83 library, 49 Host, 160 synthetic import/recovery, 41 actual admission checks and the expanded normal/relocated MCP workflows. Translation commit `7142f5e` passes all hosted jobs: Python/managed/package/OPC UA `38926062639` and Explorer `38926062735`. The [full queue](plans/offline-execution-20261016.md) and native/vendor acceptance remain open.

**Portable Windows package — 16 October 2026:** `Package.ps1 -Offline` now builds without Siemens, disables native workers in the binary and emits a ZIP, checksums, dependency notices and external MCP configuration. [Deployment](guides/deployment.md#portable-package-without-siemens) and [exact evidence](verification/offline-package-20261016.md). Package validation passes 133 deployment checks and real workflows before/after Unicode relocation. The frozen aggregate passes 29 managed suites, 1,266 Python tests (one unavailable-evidence skip), 24 Explorer checks and all MCP flows. Portable commit `2f5f65c` now passes all hosted checks, including the new package job (`38922940258`, Explorer `38922940170`). The [44-stream queue](plans/offline-execution-20261016.md), native/full-bundle and destination acceptance remain open.

**GRAPH offline delivery — 16 October 2026:** The [Host/Explorer workflow](reference/graph-host-workflow.md) connects supplied XML inspection/comparison, immutable artifacts, paged reports and exact-selection Explorer attachment. [Validation](verification/graph-host-workflow-20261016.md) records portable evidence separately from native gates. The previous package/CI repair commit `23cb3c7` passes hosted offline and Explorer workflows (`38900119464`, `38900119552`). The full [offline queue](plans/offline-execution-20261016.md), D01 branch/remote delivery and X06 other-language research remain open.

**Validation:** [Reproducible offline, workstation-boundary and retained-fixture runners](reference/validation-runners.md).

**Translation native packet — 16 October 2026:** [Concrete Siemens binding, native no-op/edit and recovery qualification](verification/translation-native-import-20261016.md), [disabled durable native-import contract](reference/translation-native-import.md). The narrow packet passed; production import and restricted category profiles remain disabled.

**Device inventory — 16 October 2026:** [Paged device/module inventory, actual identification and CSV/Excel exports](reference/device-inventory.md). [Verification](verification/device-inventory-20261016.md) separates the native offline capture from pending hardware qualification.

**Production Host qualification — 16 October 2026:** [Catalog, exact startup, owned Bool workflow and actual process-loss recovery](verification/production-host-qualification-20261016.md) pass on one frozen Host/worker build. Later working-tree additions are outside that native qualification.

**WT-04 implementation — 16 October 2026:** [Diagnostic watch authoring](reference/watch-table-authoring.md) connects exact-instance native capture and the planner to complete creation/update review, preserving XML editing and durable recovery. [Validation and limits](verification/wt04-authoring-20261016.md) distinguish managed checks from pending native lifecycle verification. New mutation profiles remain gated.

**Latest recovery expansion — 15 October 2026:** [Interruption hardening, shared mutation admission and parallel lane evidence](verification/recovery-expansion-wave-20261015.md). This supersedes the earlier work order below while preserving its accepted scoped receipts. New-worker startup is unresolved, expanded watch edits remain gated, and the remaining runtime corpus is scheduled conditionally in its exclusive window.

**Latest delivery — 15 October 2026:** [Production watch workflow and native recovery](verification/production-watch-wave-20261015.md), [preserving watch documents](reference/watch-table-workflow.md), [durable identity/admission/recovery](reference/engineering-operation-journal.md), [Git journal callbacks](reference/vci-git-runner.md), [native VCI profiles](reference/vci-native-profiles-20261015.md), [translation category findings](reference/translation-category-filtering-20261015.md), [Test Suite availability](reference/testsuite-native-availability-20261015.md) and [current owner handoffs](plans/active-handoffs-20261015.md). The [foundation wave](verification/expansion-foundation-wave-20261015.md) and [earlier implementation](verification/expansion-implementation-wave-20261015.md) remain historical evidence.

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](plans/release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

Start with [current status](STATUS.md) and the [remaining TODO](TODO.md).
The [9 October checkpoint](history/continuation-20261009.md) contains the exact
build, reports, cleanup evidence and resume instructions for that historical
delivery. Four coordinated workstreams resumed on 13 October; the current TODO
and runtime corpus remain incomplete.

| Folder | What belongs here |
| --- | --- |
| [Guides](#guides) | How to use, configure and operate the MCP |
| [Reference](#reference) | Contracts, supported scope and integration details |
| [Plans](#plans) | Intended features and detailed acceptance criteria |
| [Verification](#verification) | Tests and evidence for exact targets/builds |
| [History](#history) | Dated checkpoints, earlier results and handoffs |

## Guides

- [Deployment](guides/deployment.md), [installation diagnostics](guides/environment-diagnostics.md), [Openness registration](guides/openness-application-registration.md)
- [Remote qualification](guides/remote-qualification.md), [VMware guest setup](guides/vmware-guest-test.md)
- [Engineering changes](guides/engineering-workflows.md), [typed LAD/FBD builders](guides/typed-lad-fbd-builders.md)
- [Simulation](guides/simulation.md), [isolated block testing](guides/block-testing.md), [conformance fixtures](guides/conformance-fixtures.md)
- [Library management](guides/library-management.md)
- [HMI integration](guides/hmi-integration.md), [HMI engineering](guides/hmi-engineering.md)
- [Offline Unified text-list authoring](reference/unified-text-list-authoring.md)
- [Project explorer](guides/project-explorer.md), [metadata index](guides/project-index.md), [persistent reviews](guides/project-reviews.md)
- [Configured network inspection](guides/network-inspection.md)
- [Offline bug and trace diagnostics](guides/trace-diagnostics.md)

## Reference

- [Target capabilities](reference/capabilities.md), [object identities and scopes](reference/object-model.md)
- [Engineering coverage](reference/engineering-coverage.md), [native explorer extraction](reference/project-explorer-native.md)
- [Supplied technology profiles and relationships](reference/technology-profile-review.md)
- [Supplied safety evidence](reference/safety-evidence-review.md), [full safety/F-I/O plan](plans/expansion-plans/safety-validation-and-fio.md)
- [HMI capability matrix](reference/hmi-capability-matrix.md), [relationship evidence](reference/hmi-relationship-evidence.md)
- [HMI/explorer integration contract](reference/hmi-explorer-handoff.md), [library HMI support](reference/library-hmi-support.md)
- [Machine-readable contracts](../contracts/README.md)

## Plans

Plans describe intended scope; an unchecked historical item is not automatically
a current blocker. Use [TODO](TODO.md) for the current order of work.

- [Overall implementation plan](plans/overview.md), [graphical/project roadmap](plans/graphical-project-roadmap.md)
- [Library plan](plans/library-management-plan.md), [library acceptance backlog](plans/library-management-todo.md)
- [HMI plan](plans/hmi-integration-plan.md), [HMI acceptance backlog](plans/hmi-remaining-work.md)
- [Typed LAD/FBD plan](plans/typed-lad-fbd-builders-plan.md), [typed acceptance backlog](plans/typed-lad-fbd-todo.md)
- [Project explorer plan](plans/project-explorer-plan.md)
- [Runtime proof and failure history](plans/runtime-proof-todo.md)

## Verification

Each result applies to its recorded fixture, target and binary. Successful
inspection, compilation and runtime execution are separate claims.

- [Typed LAD/FBD ledger](verification/typed-lad-fbd-qualification.md)
- [Current workstream results](TODO.md#resumed--13-october-2026), [typed capture/persistence continuation](verification/typed-p17-qualification.md), [review/explorer acceptance](verification/review-explorer-p16-qualification.md)
- [Previous parallel increment](verification/parallel-fifth-20261012.md), [advanced typed engineering](verification/typed-p16-qualification.md)
- [Engineering gap qualification](verification/engineering-gap-qualification.md), [hardware/technology objects](verification/hardware-to-verification.md), [safety](verification/safety-verification.md)
- [HMI native qualification](verification/hmi-qualification.md), [optional HMI startup](verification/hmi-optional-startup.md)
- [Explorer qualification](verification/project-explorer-qualification.md), [configured I/O](verification/project-explorer-io-qualification.md), [large projects](verification/project-explorer-large-qualification.md)
- [Metadata index tests](verification/project-index-store-tests.md)
- [Deployment reliability](verification/deployment-reliability-status.md), [session recovery](verification/session-reliability.md)

## History

These retain dated facts and prior failures. Use [STATUS](STATUS.md) for the
latest checkpoint instead of treating older counts or checklists as current.

- [README snapshot before repository cleanup — 15 October](history/readme-before-repository-cleanup-20261015.md)
- [9 October continuation and clean stop](history/continuation-20261009.md)
- [Verification history](history/verification.md), [v0.3 evidence ledger](history/generalization-verification.md)
- [8 October environment observations](history/environment-notes.md), [HMI delivery checkpoint](history/hmi-delivery-status.md)
- [Typed model handoff](history/typed-lad-fbd-remaining-model.md), [native-profile handoff](history/typed-lad-fbd-remaining-native.md), [test handoff](history/typed-lad-fbd-remaining-tests.md)

## Maintaining these docs

Update STATUS and TODO when work resumes or stops. Keep usage in guides, API and
scope details in reference, acceptance requirements in plans, and observed
results in verification. Preserve dated evidence when an outcome changes.
Native reports, frozen builds and release bundles stay under `artifacts/`.

This reorganization preserved the [original document bytes](../artifacts/docs-reorganization-20261009/before/docs)
and [old-to-new path map](../artifacts/docs-reorganization-20261009/path-map.json).
Older evidence manifests may name the former document paths; their originals
remain in that backup. Release bundles and native evidence were not rewritten.
