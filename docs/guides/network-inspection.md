# Configured network inspection

`hardware_inspect` retains its bounded hardware/address/channel inspection and adds native offline network associations:

- `network.nodes`: native node name/type, exposed attributes and connected subnet reference. Global subnet paths require native equality with the observed project collection. Internal or unobserved subnets remain unresolved.
- `network.ioControllers`: native I/O system name/number/subnet, resolved controller owner hardware path and assigned connector owners.
- `network.ioConnectors`: native connected I/O-system reference and exposed connector attributes. `notAssigned` and `notConnected` are distinct from unavailable getters.
- `network.ports`: each port of the interface (`portIndex`, the port's own hardware path) with the hardware paths of its configured partner ports (`NetworkPort.ConnectedPorts`).
- `gsd` / `gsdDevice`: `GsdId`, `GsdName`, `GsdType` and the PROFINET/PROFIBUS flags of a GSD device item, and of its GSD device on root items. `gsdExportAvailable` tells whether the item exposes the I-device `GsdExportProvider`.

Owner paths come from native engineering-object equality and bounded native ancestry. Names and address overlaps are never ownership proof. Individual getter failures retain earlier metadata and appear in warnings. `network.scopeComplete` becomes false on truncation, failed getters or unresolved association owners. Hardware lookup is capped at 50,000 items; global subnet resolution examines at most 5,000 subnets. The public `maxItems` budget still applies to emitted rows, attributes and associations.

The output is an offline configured view. It does not establish physical cables, live connectivity or electrical channel mappings. Inspection itself does not create distributed I/O. Explicit prepared engineering changes remain separate from inspection.

Native API basis: Siemens V21 [I/O systems of an interface](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-networks/get-master-system-or-io-system-of-an-interface) and the installed `Siemens.Engineering.Base.xml` members for `NetworkInterface.IoControllers`, `IoConnectors`, `IoSystem.ConnectedIoDevices` and `IoConnector.ConnectedToIoSystem`. The implementation preserves the native associations; empty fixture enumeration does not qualify populated distributed-I/O topology.

`scripts/qualify_project_reviews.py --check-network` uses a fresh owned scratch CPU, creates an explicit subnet through the existing prepared workflow and checks its exact readback.

The 9 October native run (`artifacts/continuation-20261009/reviews-network-native-03.json`) passed the explicit `ReviewSubnet` readback with complete selected-interface coverage on build `b2a680e2694d441384ccb963fe5298d0`. The native controller correctly reported no assigned I/O system and no connectors. Review capture, comparison, comment search, offline restart and exact owned process exit also passed.

The later populated network run (`artifacts/continuation-20261009/network-populated-native-01.json`) passed on build `0fa1d22835e9487d88b8c48a32da154c`. An independent native seeder created a CPU 1511-1 PN V4.1 and an IM 155-6 PN ST V6.4 from exact installed catalog entries. It assigned connector 0 to `ProofIoSystem` on `ProofSubnet`, verified native object equality and persisted the association through save/reopen. The device exposes four connectors; the MCP correctly retained one assigned and three unassigned connectors, the exact controller owner, reverse assigned-device reference and global subnet identity. Both fresh MCP worker sessions passed and all three owned TIA process exits were observed through captured handles. Independent native fixture (`artifacts/n14/native.json`).

This is configured-association inspection evidence for those exact devices. The fixture does not qualify complete remote-I/O module engineering, compilation, multi-controller/shared-device assignments, runtime or physical wiring. The first seeder attempt incorrectly assumed exactly one connector and remains recorded as a failed fixture attempt in `artifacts/n13/native.json`.

## Reviewed remote-I/O engineering

The 12 October implementation adds three prepared change kinds alongside the existing catalog-driven `hardware_add`, `hardware_plug`, `hardware_address_set` and `network_configure` routes:

| Kind | Payload and boundary |
|---|---|
| `hardware_move` | `hardwarePath`, `positionNumber`. Moves an eligible pluggable module within its existing native rack, after `CanPlugMove` succeeds. Built-in and software-bearing items are excluded. Cross-rack movement is not implemented. |
| `hardware_channel_attributes` | `hardwarePath`, `channelType`, `channelIoType`, `channelNumber`, `attributes`. All four selectors must identify one native channel. Every field must be in the reviewed allowlist and expose native read/write access. |
| `network_io_assign` | `hardwarePath`, `connectorIndex`, `controllerHardwarePath`, `controllerIndex`, and `createIoSystemName` only when the selected controller has no system. Both interfaces must already expose one node on the same native subnet. To disconnect, supply `hardwarePath`, `connectorIndex`, `disconnect: true`, without controller/create selectors. |

`hardware_inspect` reports native `controllerIndex` and `connectorIndex` values. These indexes identify the observed collection, not a guessed controller by name. Preparation binds them to the project fingerprint. Channel inspection reports the bridge allowlist separately from native access and exposes native enum values when available; a symbolic enum's existence does not establish that every member is accepted by every module.

Controller assignments require the selected PLC's native ownership. A second assigned connector anywhere on the remote station, shared/foreign address ownership, unresolved ownership and same-system no-ops are rejected. The bridge verifies the selected forward association, the new system's reverse association and removal from the previous system's reverse association. Shared-device and multi-controller changes require separate implementation/qualification.

All five module/slot/channel/address/controller operations require compilation and a whole-project checkpoint. Their preservation manifest captures all observed hardware paths/type identifiers/slots, reviewed hardware and channel fields, configured address ranges/owners, node settings and exact I/O-system assignments. An operation-specific comparison permits only the reviewed change and its explicitly bounded dependent effects. Unrelated changes fail with `MutationVerificationFailed` and retain the archive. Archive retrieval must match the exact prechange configured manifest before recovery reports success. Unknown vendor payloads and parameters outside the reviewed allowlist are outside this metadata contract.

`scripts/qualify_remote_io.py` creates its own scratch CPU and selects an IM 155-6 PN ST and DI module from the installed catalog. It exercises prepared assignment/module/slot/address/channel changes, compilation, a fresh owned TIA/worker reopen, disconnect and archive recovery. It records each gate separately and never treats API availability or a passing pure test as native qualification. The three new feature-catalog entries remain unverified until corresponding native evidence is recorded. The focused `TiaMcp.HardwareEngineering.Tests` suite covers permitted changes and rejection of collateral module, channel, address, controller and second-interface changes.

The first 12 October native attempt (`artifacts/p16n/native-01.json`) found that a newly created IM 155-6 PN ST V6.4 cannot compile before its submodules are assigned. It stopped at the existing `hardware_add` prerequisite with five native unassigned-submodule errors; none of the new operation gates ran. The checkpoint was retrieved successfully and the exact owned TIA process, worker and host exited cleanly. The runner now uses the existing explicit `compile=false, save=false` option only for head/subnet prerequisites and requires full compilation at the first controller assignment. The failed attempt remains preserved.

The second native attempt (`artifacts/p16n/native-02.json`), on isolated worker `b3`, reached a zero-error controller-assignment compile. TIA automatically added a server module and its diagnostic child. The strict preservation comparison rejected that undeclared addition, retained the failure and restored the exact prechange hardware manifest; the restore reported `configured_hardware_recovery_verified`. All owned TIA/worker/host handles exited. This is evidence of the rejection and recovery boundary, not a passing controller-engineering qualification.

`network_io_assign` now accepts an optional explicit `expectedServerModule` contract for that captured initial IM `OrderNumber:6ES7 155-6AU02-0BN0/V6.4` profile. Its required fields are `name`, exact `typeIdentifier: "OrderNumber:6ES7 193-6PA00-0AA0/V1.2"`, `containerHardwarePath`, `positionNumber: 1`, and exactly six string `attributes`: `AdditionalInformation`, `Author`, `Comment`, `LocationIdentifier`, `Name`, `PlantDesignation`. Preparation proves that the native container is the head's own rack, slot 1 is empty and neither the expected subtree nor another server of that type exists. The preview lists the expected server and its exact captured diagnostic child, including the unowned zero-length diagnosis address at -1. The expectation comes entirely from the reviewed request and fixed captured child metadata. Missing, extra or changed generated metadata still fails preservation. Omitting this contract preserves the earlier strict behavior; it is not a general exemption for generated hardware or automatic slot shifts.

The third attempt (`artifacts/p16n/native-03.json`), on isolated worker `b5`, stopped at `tia_start` with a native `EngineeringSecurityException` security timeout. No project or mutation request was dispatched. Its host and worker exited cleanly; separate reconciliation (`artifacts/p16n/native-03-startup-reconciliation.json`) retains the later observed Portal absence without claiming a captured-handle exit. A single empty startup diagnostic (`artifacts/p16n/startup-b5-01.json`) using the identical binaries then started successfully, but its host/worker shutdown and exact Portal-handle wait did not complete within their bounds after successful detach. Further startup attempts were stopped. These are per-launch startup/cleanup observations, not license conclusions or positive module/slot/channel/address/controller qualification. The explicit companion implementation remains supported by 39 offline preservation checks, including the fixed native-02 manifest, while a fresh positive engineering run remains required.

A later read-only observation (`artifacts/p16n/startup-b5-01-late-reconciliation.json`) found both diagnostic process IDs absent. The original cleanup failure remains unchanged, and later absence is not presented as an exit observed through the original captured handle.

The separately admitted fourth attempt (`artifacts/p16n/native-04.json`) started successfully and again reached a zero-error assignment compile, then rejected preservation and restored its checkpoint. Its exact owned Portal, worker and host exited cleanly. The serialized delta audit (`artifacts/p16n/native-04-delta-audit.json`) verifies that the only changes were the two requested controller/connector associations and the two independently previewed companion rows; all other serialized fields and the recovered manifest match exactly. This does not change the failed native result.

The focused in-memory regression reproduced a Newtonsoft distinction hidden by serialization: converting a nullable native string directly produces a `String` token with a null value, while the explicit companion contract contains a `Null` token. Capture now creates canonical JSON null for nullable `typeIdentifier` and `containerPath` fields. The preservation comparator remains unchanged and continues to reject omitted fields, empty strings and wrong types. The isolated b6 receipt (`artifacts/p16n/implementation-manifest-03.json`) records a clean worker build and 46 passing hardware checks, including the original mismatch. Native positive module/slot/channel/address/controller qualification remains required on the corrected build.

On 13 October, the separately admitted fifth attempt (`artifacts/p16n/native-05.json`)
used the unchanged combined b7 worker and passed controller assignment, full two-target
compilation, configured preservation and save. The independent assignment audit (`artifacts/p17n/native05-assignment-audit.json`)
checks all 15 existing rows and the two exact pre-reviewed generated companion rows,
the two requested associations, complete zero-error compiler diagnostics and exact
owned Portal/worker/host exits. This establishes the scoped assignment result only.

The full attempt remains failed: its next fixture step tried to plug a DI module
into slot 1, already occupied by the generated server. All three installed DI
candidates returned `IncompatibleHardware` during preparation; no module mutation
was dispatched. Module/channel/address gates, fresh reopen and recovery were not
reached. The old failures remain unchanged.

The corrected qualification fixture explicitly moves that exact server from slot 1
to vacant slot 4 using `hardware_move`, with its own checkpoint, full compile and
strict preservation. It then requests DI creation at slot 1 and DI movement to slot 3.
Seven offline fixture checks reject changed companions, duplicate identities,
missing racks and occupied destination slots. The native06 readiness receipt (`artifacts/p17n/native06-readiness.json`)
records the expected single-field server delta and unchanged b7 binary. No generic
native slot-shift exemption was added.

The sixth attempt (`artifacts/p17n/native-06.json`) passed controller assignment,
explicit server relocation, DI creation, DI slot movement and input address change,
each with full two-device compile, strict configured preservation and save. The
independent audit (`artifacts/p17n/native06-engineering-audit.json`) binds all ten
before/after manifests, checks each exact delta and complete diagnostic tree, and
verifies owned Portal/worker/host exits. The full attempt remains failed: every DI
channel exposes the reviewed `InputDelay` as native read-only. The writable native
`ParameterSettings` field is outside the reviewed bridge route. No channel mutation,
reopen or recovery was dispatched, and this result does not qualify those gates.

The qualifier defaults to the full route and retains that failure. An explicit
`--scope configured-module-lifecycle` candidate can test the remaining configured
module persistence and recovery gates while retaining complete unsupported-channel
observations. Missing or erroneous channel metadata cannot establish that scope.
Its completion requires fresh-process readback, exact compiled-manifest comparison,
checkpoint recovery, disconnect outcome/recovery and recovered fresh-process reopen.
Both successful and expected-negative disconnect compilation require complete
independent diagnostics; a negative result must contain actual native errors.
Even a completed limited run leaves `fullRemoteIoRouteQualified` and
`channelWriteQualified` false.

The separately admitted seventh attempt (`artifacts/p17n/native-07.json`)
passed all nine gates of that limited scope on the unchanged b7/fa0067 binaries.
Its independent audit (`artifacts/p17n/native07-independent-audit.json`)
verifies the initial engineering deltas, complete two-device diagnostics, exact
fresh-process manifests, address checkpoint recovery, and restored fresh reopen.
Disconnect produced an actual native compile error with complete diagnostics; its
exact checkpoint recovery then restored the compiled configuration. All three
owned Portal/worker/host lifetimes exited cleanly. The eight DI channels still
expose `InputDelay` as native read-only, and no channel or `ParameterSettings`
mutation occurred. The full sixth attempt remains failed and unchanged; the
seventh result qualifies only the explicit configured-module lifecycle profile.

## 24 October requalification on current main

Two scenarios ran once each on the current bridge (owned scratch projects, English TIA UI):

| Evidence | Profile | Result |
| --- | --- | --- |
| [`local.remote-io.lifecycle.20261024`](../../evidence/local.remote-io.lifecycle.20261024.json) | `--scope configured-module-lifecycle --module di`, IM 155-6 PN ST V6.4 + DI 8x24VDC ST | All nine lifecycle gates passed. The DI's `InputDelay` stays native read-only; no channel write. |
| [`local.remote-io.full-route.20261024`](../../evidence/local.remote-io.full-route.20261024.json) | `--scope full --module ai`, IM 155-6 PN ST V6.4 + AI 4xU/I 2-wire ST V2.0 | All ten gates passed, including `Smoothing` None→Weak on analog input channel 0; `channelWriteQualified` and `fullRemoteIoRouteQualified` are true. |

Each step uses a full two-device compile, complete diagnostics and strict configured
preservation. The disconnect produced an actual native `CompileFailed`, and its
checkpoint was restored in the same worker session. This is also the first native
exercise of the shared admission rule that a structured worker failure is a returned
outcome.

Two findings preceded these runs:

- **Generated server-module name.** TIA names the generated server module in its UI
  language (`Server module_1` in English, `Servermodul_1` in German). The qualifier now
  declares the localized name, chosen from the observed interface name, before the
  assignment. The bridge contract was already name-agnostic.
- **Writable channel settings on the AI module.** The ET 200SP AI channels expose
  `Smoothing`, `OperatingType`, `OperatingRange` and `InterferenceFrequencySuppression`
  as native ReadWrite. The original guessed names `MeasuringType` and `MeasuringRange`
  do not exist on this module. Only `Smoothing` was added to the reviewed channel
  allowlist: it is a documented V21 enum (`Siemens.Engineering.HW.Smoothing`) with no
  address or wiring effect. The qualifier restricts it to the ET 200SP levels
  None/Weak/Medium/Strong rather than every member of the shared enum.

**Qualified topology.** This is one CPU 1511-1 PN controller assigning connector 0 of
one ET 200SP IM 155-6 PN ST V6.4 with one DI or AI module, plus the explicit
generated-server companion. The following stay outside the advertised route, each
needing its own scope and evidence:

- other module families and other companion layouts;
- shared devices and multi-controller assignments;
- cross-rack moves;
- channel settings other than `Smoothing`.

## Port topology, module replacement and GSD export

Three prepared kinds and one read-only tool extend the hardware routes (ledger X10). All
are compiled against the V21 PublicAPI only and are not natively qualified.

| Kind / tool | Payload and boundary |
|---|---|
| `hardware_port_connect` | `hardwarePath`, `partnerHardwarePath`: two exact port device items from `network.ports`. Both ports must be free; `NetworkPort.ConnectToPort`, then forward and reverse `ConnectedPorts` must show the link. Alternative partners (tool changers) are not supported. |
| `hardware_port_disconnect` | Same payload; the two ports must be linked. `NetworkPort.DisconnectFromPort`, then neither side may list the other. |
| `hardware_replace` | `hardwarePath`, exact installed catalog `typeIdentifier`, optional `acceptLoss: true`. `DeviceItem.ChangeType` on one pluggable module in a rack; built-in and software-bearing items are refused. |
| `hardware_gsd_export` | `hardwarePath`, `designation`, optional `description`. Calls `GsdExportProvider.Export` into a fresh directory under the export artifacts and returns every produced file (up to 32) with its SHA-256. The GSD content is not validated. |

The three kinds use the configured-hardware preservation described above, with
the port links of every port added to the manifest. A port change may alter only the
two selected link entries. Compilation and the whole-project checkpoint are mandatory.

V21 offers no compatibility check before `ChangeType`. The `hardware_replace` preview
records `inventoryBefore`: the addresses (with owners) and child items of the module's
subtree. After the write, every address must keep its I/O type, start, owners and at
least its length, and every child path must remain; otherwise the change fails with
`MutationVerificationFailed` and the checkpoint must be restored, unless the payload set
`acceptLoss: true`. Name, slot and container must stay. The subtree's channels, attributes
and children may follow the new type; everything outside it, port links included, must
stay exact even with `acceptLoss`. The readback records the before/after inventory.

`ConnectedPorts` is documented in V21 as "internal use only"; the bridge relies on it for
readback. Which item exposes `GsdExportProvider` and whether Siemens requires a compiled
I-device first are not observed yet.

## Preservation manifest identity checks

Configured hardware preservation comparison now refuses absent manifests/payloads,
non-object rows, missing/non-string/empty/control-bearing paths, duplicate exact
paths and missing selected identities. It never filters malformed rows out of the
comparison. Legitimate ordering differences and case-distinct exact paths remain
preserved. The shared guard covers plug/move/channel/address, I/O assignment, port link and replacement
verification; recovery still uses the existing exact manifest comparison.

[Portable regression evidence](../verification/hardware-manifest-20261016.md)
includes the retained native V21 server fixture, but does not qualify a newly built
native worker. Broader communication connection engineering is tracked separately
in the [D08 plan](../plans/expansion-plans/communication-connections.md).

## Supplied connection comparison without Siemens

Use [connection review](../reference/connection-review.md) to retain supplied
before/after connections against a saved project review, compare explicit mappings
and navigate declared consumers. Captured hardware ancestry is distinguished from
native endpoint ownership; resource rules and communication are not assessed.
