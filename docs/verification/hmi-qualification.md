# HMI native qualification



The implementation has independently created and exercised Classic Comfort, RT Advanced, Basic, Unified Comfort and Unified PC V21 project fixtures. **H0–H5 acceptance is not complete:** production mutations remain unavailable, RT Professional is not installed as a creatable catalog target, and several content/visual/recovery gates remain open. Neither blocked tests nor empty inventories count as full feature qualification.



## Reproduce and inspect evidence

As of 12 October 2026, use an explicit fourth seeder argument to separate prerequisite fixtures: `qualify <fresh-directory> classic-comfort,unified-comfort positive`. The default remains `negative`, retaining the deliberately invalid-reference cases. Unified positive fixtures keep real integrated connections, scalar/DB tags and direct screen bindings, and omit intentional invalid bindings. Classic's accepted binding-template prerequisite remains explicit. Compile gates must still succeed natively. Invalid/duplicate profile selectors and unknown modes fail before creating an owned portal.

For the new documented bilingual resource input, select `qualify <fresh-directory> unified-comfort positive textlists`. Its paired fixtures are copied with the seeder build, both input hashes are retained, and independent native exports are recorded at every lifecycle stage. Import success is insufficient: the named nonempty list and both languages must survive native export. The bridge's selected-resource `includeSource` comparison requires both exact exported files and retains `nativeSourceCoverage.complete=false`; the vendor's formatted-text omissions are not waived. See the [fixture provenance](../../fixtures/hmi/unified-text-list/README.md).

The new native report records `compileBeforeTrial`, `compile`, `compileReopened` and `compileRecovered`; the harness reports `cleanBaselineCompile`, `compile`, `reopenedCompile` and `recoveredCompile` independently. Each result requires every expected exact HMI owner, the native HMI/owning-device compiler, a successful state with zero errors, and a complete message tree without any Error diagnostic. Summary/message contradictions fail even when the count is zero. Historical reports without these new lifecycle observations remain unverified for the corresponding gates. Existing native failures are retained.

The exact runtime target's available attribute names/access modes are retained without reading secret values. An absent Classic `StartScreen` setter or a failing Unified runtime-security default requires a natively configured positive template or an evidenced public provider route. CPU certificate prerequisites are addressed only for newly created disposable CPUs using generated DPAPI-protected configuration credentials. No existing protected CPU is altered, password protection is not disabled, and the project never runs or downloads.

After native run 01 exposed the new CPU's standard Full access user prerequisite, the seeder also provisions a generated local runtime user and exact device-scoped role for each newly created CPU. Its credential is separately protected with DPAPI and no anonymous right is granted. If a recovered positive HMI copy actually compiles successfully, the seeder tests a separate derived candidate through save, fresh reopen and fresh archive retrieval. `derived-native-report.json` records its own readbacks/compiles, the source report/archive hashes and the original authoring provenance. It never replaces or relabels failed source stages. Exact API process identities and OS observations are captured at native lifecycle boundaries; OS-only process observations do not grant ownership or termination authority.

Run `artifacts/p16h/native-positive-02` produced a separate Unified Comfort candidate whose native summaries report zero errors, but all stages retain Error-state password messages. The original summary-only assessment is retained; `artifacts/p16h/derived-positive-02-strict-compile-assessment.json` supersedes its compile conclusions and fails all four compile gates. Both independent scalar persistence comparisons remain observed. `bridge04-strict-compile-reassessment.json` similarly corrects only the inherited native conclusions while rechecking actual read-only comparisons and preserving original gates. Future seeder candidates and the derived-project fallback require clean complete diagnostics. The Classic editor prerequisite and unavailable template are recorded in [remaining work](../plans/hmi-remaining-work.md).



`scripts/probe_hmi_api.py` reads installed optional assembly/XML metadata without starting TIA. `scripts/native/HmiSeeder` is a separate native fixture author; it does not call the HMI adapter under test. It creates fresh projects and owned TIA processes, imports authored Classic SimaticML only through the native API, reads back accepted content, saves, closes/reopens, archives, and retrieves into independent fresh copies. It does not attach to existing user projects, deploy, start runtime, or execute project scripts.



`scripts/qualify_hmi.py` starts the exact supplied host/worker binaries, verifies the seeder archive hash, retrieves only those owned archives, compares inventories/scalars against independent readbacks, and exercises the actual project-model provider and HTML explorer export. Every required gate appears explicitly as passed, failed, blocked or unverified. A later explorer failure does not overwrite an earlier completed inspection gate. Failed native observations are retained before assertions. A faulted bridge or unknown startup outcome stops further requests through that worker. Native `AccessDenied` remains a per-launch failure: root-i subsequently launched both Unified profiles successfully. `--captured-report` performs supplemental offline reassessment of a completed report with matching native-report hash; it does not alter the original observations or dispatch native calls.

Current comparisons retain selected-target `hmi_capabilities`, including each operation's precise preservation/recovery requirements. For populated Unified fixtures the explorer also reads exact tag and alarm nodes and compares outgoing PLC/connection edges and incoming alarm edges to the independent HMI relationship observations. Native edge metadata alone cannot pass: the source must have its exact captured graph node, and HMI tag/connection/alarm endpoints must resolve to exact captured nodes. PLC member targets retain explicit capture limitations. Each partial explorer observation is retained before its assertions. Cleanup captures the native process handle immediately after owned startup, then requires its signaled exit, host exit zero and every worker-exit receipt zero. Merely disposing an Openness connection does not prove process exit.

`--allow-owned-derived-open` is an explicit diagnostic fallback for a known `ArchiveRetrieveFailed` with `retrieved=false`, followed by an exact same-process owned/empty session observation. It accepts only the derived report's recorded seeder-owned recovered `.ap21`, an unchanged source report and a successful native recovered compile. It opens that existing disposable project for read-only bridge comparison. The failed archive retrieval remains in `retrievalFailure` and `bridgeArchiveRetrieved=false`; this fallback never qualifies bridge archive recovery. Unknown outcomes cannot use it.

`scripts/qualify_hmi_explorer_browser.mjs` uses the installed project-local Playwright to render the exact native HTML/snapshot artifacts from a passed populated bridge capture. It verifies artifact hashes, scoped HMI detail names, visible partial coverage, actual source/destination graph paths for both HMI owners, the `hmiBinding` filter and 1440/1024 desktop layouts. Supply fresh `--output` and `--screenshots` directories along with `--bridge`. Optional `--repack-current` binds a separate report to current renderer source hashes and the same unchanged native snapshot; it does not rewrite or qualify the original host export. This browser assessment never opens TIA or claims native HMI screen rendering.



```powershell

dotnet build scripts/native/HmiSeeder/HmiSeeder.csproj -c Release

& scripts/native/HmiSeeder/bin/Release/net48/HmiSeeder.exe qualify D:\03_Entwicklung\TIA_MCP\artifacts\NEW-HMI-RUN

python scripts/qualify_hmi.py --native-report artifacts/NEW-HMI-RUN/native-report.json --host <exact-host.exe> --worker <exact-worker.exe> --report artifacts/NEW-BRIDGE-REPORT.json

python -B scripts/test_hmi_qualification.py

```



Use a fresh output directory. A launch from a restricted execution account failed native TIA connection; ordinary authorized user execution worked. That historical environment failure is retained and is not a license conclusion. The seeder never overwrites prior evidence. If the environment uses a restricted temporary directory, point `TEMP`/`TMP` to a new writable workspace directory for the pure Python tests.



## Evidence inventory



| Artifact | Meaning |

| --- | --- |

| `artifacts/hmi-api-v21-20261008.json` | Installed optional assembly/XML identities and discovered public member signatures |

| `artifacts/hmi-native-20261008-d/native-report.json` | Six exact profile attempts; five independently created targets; screens/duplicate item names; native compile, save/reopen and archive/retrieval evidence |

| `artifacts/hmi-native-20261008-d/<profile>/baseline.zap21` | Immutable native fixture archive with SHA-256 retained in the report |

| `artifacts/hmi-native-20261008-e/native-report.json` | Comfort family expansion: two PLC owners, duplicate `SharedSignal` declarations, valid integrated HMI connections, Unified direct/indirect/broken/formula references |

| `artifacts/hmi-bridge-20261008-root-e.json` | Actual host/worker qualification; all three Classic profiles passed inventory, stale/wrong-family controls and actual explorer provider; Unified comparison exposed the flat/table native-view alias in the harness, retained as a failed historical run |

| `artifacts/hmi-bridge-20261008-root-g.json` | Authoritative five-profile read-only run: discovery, section/item accuracy, valid duplicate scopes, wrong-family/stale controls, and actual explorer integration all passed; RT Professional remains explicitly blocked |

| `artifacts/hmi-native-20261008-f/native-report.json` | Classic Comfort and Unified Comfort native integrated endpoint evidence; Unified Comfort direct-item/scalar PLC and DB candidates; Unified PC missing-interface authoring failure and Classic IOField link rejection retained |

| `artifacts/hmi-native-20261008-g/native-report.json` | Both exact Unified profiles with nonempty discrete alarm and screen Loaded event/global script source; native direct creation probes for global modules/text/graphic lists expose no constructors |

| `artifacts/hmi-native-combined-20261008.json` | Unaltered aggregation of f Classic Comfort and g Unified profile rows, with original source report hashes; avoids repeating superseded fixture profiles during final bridge qualification |

| `artifacts/hmi-bridge-20261008-root-h.json` | Retained failed H3 attempt: `tia_start` timed out, subsequent host calls correctly returned `BridgeFaulted`; this run provides no native H3 pass |

| `artifacts/hmi-bridge-20261008-root-i.json` | Completed rich-fixture run: Classic launch failed native authorization; both Unified profiles retrieved and actual explorer passed. Original harness relationship-key/complete-coverage assertions are retained as failed |

| `artifacts/hmi-bridge-20261008-root-i-reassessment-b.json` | Supplemental offline assertions against immutable root-i observations: Comfort exact PLC/global/DB/full item chain and both profiles’ script/dynamic/broken references pass; exact item inventory/scalars pass with explicit native getter gaps. Historical PC network-interface authoring failure stays failed |

| `artifacts/hmi-visual-classic-comfort-20261008/visual-receipt.json` | Owned native UI fixture attempt; detailed Classic missing-start-screen compile result; visual verification remained false |

| `artifacts/hmi-visual-classic-comfort-20261008-b/visual-receipt.json` | New owned native UI copy opened; user stopped Computer Use with physical Escape before representative screen assessment; owned process closed and visual gate remains unverified |

| `artifacts/hmi-remote-prerequisite-20261008.json` | Actual configured SSH prerequisite attempt, port 22 timeout/exit 255; no current remote HMI qualification |

| `artifacts/hmi-seeder-cleanup-20261008.json` | OS process checks confirm owned d/e/f native portals exited; no user-owned process was terminated |

| `artifacts/hmi-native-f-recovery-audit-20261008.json` | Explains f Classic's failed ordered recovery comparison: only HMI_RT_1 screen enumeration order changed; all eight scoped native screen export comparisons match except exporter timestamp |

| `fixtures/hmi/evidence-manifest.json` | Versioned map to independent native archives/exports and their exact hashes |



Later numbered/native reports are additive. A failed earlier run is never edited to look successful. The root integration report identifies the final immutable binary manifest and the newest bridge report. Native evidence does not automatically enable an engineering operation.



## What the fixtures establish



Each d target has two HMI software owners, each with `Overview_A` and `Overview_B`. Every screen contains a native `RepeatedItem` rectangle; same member names in valid separate screen/device scopes remain distinct exact identities. Classic screen XML additionally contains a `Qualification` layer. The bridge comparison verifies item native type/name multisets and exposed scalar geometry against native exports/readbacks, not just the HMI device names.



The HMI inspect sections independently compare connections, tags/tables, screens/items, scripts, resources and Unified alarms. Classic alarms must return the documented unsupported result. Empty script/resource/alarm fixtures establish correct empty enumeration only. They do not establish preservation of nonempty translations, rich alarm variants, arbitrary scripts, or library-linked faceplates.



The e fixtures independently add CPU 1511-1 PN (`OrderNumber:6ES7 511-1AL03-0AB0/V4.1`) twice. Both PLC owners declare `SharedSignal : Int` at `%MW0` and `QualificationDB.Value : Int`. Native `CommunicationManagement.Connections.Create<HmiConnection>` configures each HMI owner to a different exact PLC. The bridge must retain native endpoint-equality proof and may resolve a scalar declaration only within that PLC owner. The e evidence initially recorded CLR proxy `==` as false; this is not engineering-object equality and is not used as proof. The adapter uses native `Equals`.



Unified `LinkedSignal` is configured with the native integrated connection and `PlcTag=SharedSignal`; native readback derives its `PlcName`, `DataType=Int` and `AccessMode=SymbolicAccess`. The subsequent attempted `DataType` assignment correctly fails as read-only; it is a fixture authoring error, not a missing product/license. The native tag still exists and is usable for declaration qualification. Later fixtures remove that redundant setter and create the DB tag as well.



The e Unified screen contains direct, indirect, broken and formula tag dynamics. Broken `MissingTag_Qualification` is retained even when other tag-dynamic getters throw; indirect/formula references keep a null resolved target. Native formula text is retained, never evaluated. In e the direct item targets internal `SharedValue`, so the complete item-to-PLC chain is a separate unverified gate until a fixture directly targets `LinkedSignal`.



The completed f/g fixtures change the direct item to `LinkedSignal`. Unified Comfort creates `LinkedDbValue` configured as `QualificationDB.Value` and records both native endpoint `Equals` checks true. The historical f/g Unified PC link authoring failed because the newly created PC station had no network node (`InvalidOperationException`, sequence contains no elements); its linked tags were consequently absent. This is a fixture setup error, not an external product/license block or qualified PC mapping. The g fixture additionally creates `QualificationAlarm` with native `RaisedStateTag=SharedValue` and bit 0, and a screen `Loaded` event with nonempty script and global definition source. Native readback retains those exact strings; no script is executed. Global script modules, text lists and graphic lists expose only paired-format import and return empty native creation-info lists; those richer resource fixtures remain an explicit vendor-template prerequisite, not an assumed unsupported inspector. The installed API exposes no generic nested item-group composition on the selected screen types; no nesting API is invented.



## Compilation, preservation and recovery



Native d Classic compilation failed because no runtime start screen was configured. Native d Unified compilation failed due a seeder service-provider cast error, not a native HMI compiler result. Native e and later Unified compilation actually runs through the owning `Device`; its software object itself is not an `IEngineeringServiceProvider`. The e errors include required runtime password defaults, the intentionally broken/invalid indirect references, and PLC certificate configuration requiring device compilation. These are retained real failures. A successful method call or a passing PLC software compile does not make the HMI compile pass.



Classic standalone screen `BackColor` was changed and restored by native import/export. Precise scalar readback and selected-screen unrelated XML equality passed after removing only the exporter timestamp. The native imported rectangle's geometry and layer survived. This is an independent native feasibility experiment, **not** qualification of the prepared production change pipeline. No supported production mutation has passed whole-HMI preservation, changed-state compile/save/reopen, archive recovery and visual acceptance together.



Save/reopen and archive retrieval gates compare independent native scalar snapshots; raw native export artifacts remain available for inspection. They do not assert full content preservation outside public exposure. Interrupted-operation recovery tests of the shared change machinery are separate from a fully qualified native HMI interrupted mutation. Since mutations are unavailable, no HMI interrupted mutation is reported passed.



The richer **f Classic Comfort fixture fails the strict ordered `saveReopen` and `archiveRecovery` gates**. All three basic Classic profiles also have failed strict gates in root-g; their causes have not been established by the f audit. For f, the only scalar-snapshot differences are `HMI_RT_1.ScreenFolder.Screens`: enumeration changes from `Overview_A, Overview_B` to `Overview_B, Overview_A`. Both screens still exist in their exact HMI scope. Each screen's native XML was paired by its unique name inside that specific owner, and all eight baseline-versus-reopened/recovered exports match after removing only `DocumentInfo.Created`; every source hash was verified. The audit isolates a composition-order difference, not a renamed or lost screen. The snapshots do not identify the precise native operation that reordered it. The original failed gates remain failed, and this narrower audit neither claims whole-HMI preservation nor qualifies any mutation.



## Explorer status and unfinished gates



Actual project-model capture, children, exact HMI details, relationships and HTML export passed for all five independently available exact profiles in root-g. The harness requires both the outer details status and the inner `hmi.v21` provider status to be available/partial. It precisely compares all twelve Classic screens against retained XML and Unified item geometry against independent native properties. Metadata-only capture does not claim screen-item completeness; selected-screen content is read separately. No approximate or native rendered HMI preview is claimed. All five profiles remain unaccepted overall because compile, production mutation and other independent acceptance gates have not passed.



Root-i's Classic `tia_start` returned `AccessDenied` with `Siemens.Engineering.EngineeringSecurityException` and “Security error. The operation has timed out.” That specific launch failed; the subsequent Unified Comfort and PC launches succeeded. The immutable captured Comfort observations prove native endpoint ownership, scoped duplicate-name PLC tags, exported scalar DB members and the direct screen-item chain. Both Unified profiles retain exact event/global code, indirect/formula expressions and broken references. Supplemental reassessment fixes the harness response-key error without rewriting original results. Item inventories and exposed scalar geometry match precisely, but native missing-tag getter diagnostics keep overall content coverage incomplete. The pure qualification harness currently passes 26 tests, including response-shape, getter-gap, exact geometry, script source/hash and fail-fast regressions.



Remaining acceptance work includes: final per-profile rich content fixtures; RT Professional prerequisites; successful valid baseline and changed-state HMI compile; native visual assessment of representative screens; protected/license-failure native target controls; full preserving mutation routes and operation-specific prepare/apply/status/restore qualification; actual interrupted native mutation recovery; and reachable remote validation of the current package. Arrays/UDTs/dynamic expressions/unknown Classic binding shapes remain unresolved until native evidence and parsers establish exact mappings.



See [capability matrix](../reference/hmi-capability-matrix.md), [engineering gates](../guides/hmi-engineering.md), [relationship evidence](../reference/hmi-relationship-evidence.md), [explorer/library handoff](../reference/hmi-explorer-handoff.md), and [optional startup controls](hmi-optional-startup.md). No physical HMI/PLC deployment or Siemens assembly redistribution occurred.


## Final checkpoint

A fresh PC-only fixture `artifacts/hmi-native-20261008-pc-h/native-report.json` corrects the historical PC network prerequisite. Native catalog discovery returned `IE General`; `CanPlugNew` approved the exact observed `OrderNumber:IE_CP/SW V6.2 SP1 ...` module in the PC rack at slot 2. Both owned PC stations then expose node X1, valid integrated connections with exact native endpoint equality, and both linked scalar tags. The native project completed save/reopen/archive retrieval and disposed its owned portal. This establishes fixture authoring, not a current-package bridge pass.

`artifacts/hmi-native-combined-final-20261008.json` contains unaltered f Classic Comfort, g Unified Comfort and corrected h Unified PC rows, with original report hashes. The harness now requests alarm relationships and checks exact same-HMI `RaisedStateTag`/bit identities, plus native catalog/profile provenance; absent direct runtime-version metadata remains explicit. These new assertions and the corrected PC mapping have pure regression coverage but await native execution with the final package. No further native or GUI runs were started after the user's checkpoint instruction. See [remaining work](../plans/hmi-remaining-work.md).
