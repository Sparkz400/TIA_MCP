# HMI engineering and preservation

Classic ordinary screen inspection now uses the six public screen scopes and retains optional `screenRole` metadata (`regular`, `template`, `popup`, `slideIn`, `globalElements`, `overview`). `coverage.screenScopes` distinguishes complete, absent, unavailable and truncated scopes. Explorer nodes remain `hmiScreen`; their exact paths separate roles and folders. Additional Classic roles route details to screen metadata, while regular screens retain the existing item route. Added-role item parsing and ordinary source export remain unsupported/unqualified; these observations confer no mutation authority. See [offline verification](../verification/classic-screen-inventory-20261023.md).

The `hmi_engineering` change kind uses the existing `change_prepare`, `change_apply`, `change_status` and `change_restore` lifecycle. Payloads follow [hmi-engineering.v1.schema.json](../../contracts/hmi-engineering.v1.schema.json). This implementation contains family-specific operation bodies and fail-closed preservation gates. An implemented body is not native qualification or current target availability.

## Implemented candidates

| Family | Operation | Native implementation and boundary |
| --- | --- | --- |
| Classic | `scalar_property_set` | Exact standalone screen `BackColor`, canonical decimal RGB such as `224, 240, 255`. Export the whole screen, patch its exact export-local root/property, import through its original typed screen composition, then re-export and compare all content. Native library type instances are rejected: Classic screen XML alone cannot preserve their library identity. Rename is deliberately outside this scalar allowlist. |
| Classic | Other six operation kinds | Explicit unsupported outcome. Typed screen-item and primitive tag creation APIs are absent from the inspected Classic V21 surface; separate preserving XML adapters and dependency qualification remain necessary. |
| Unified | `scalar_property_set`, `item_properties_set` | Exact native item `Enabled`, `Visible`, and bounded `TabIndex`, using native current types and precise readback. |
| Unified | `tag_create` | Exact native tag table; primitive internal tags only; reject duplicate HMI namespace names and verify name/type and absent external connection/PLC tag. |
| Unified | `external_tag_create` | Exact native tag table, exact same-HMI `hmiConnection` and exact root PLC `tag` identity. Prepare proves the PLC owner from native `CommunicationManagement` endpoints, plus the exact scalar type. Apply sets `Connection` then `PlcTag`, and never writes the natively derived DataType/PlcName. Readback must show `SymbolicAccess`. DB members, arrays, UDTs, software-unit tags and connection creation are not mapped. [Verification](../verification/hmi-external-tag-create-20261026.md). |
| Unified | `item_place` | Existing top-level faceplate container, explicit integer rectangle wholly within its native screen; exact rectangle readback. Nested coordinate systems and faceplate type lifecycle require separate qualification. |
| Unified | `existing_binding_set`, `explicit_binding_set` | Existing faceplate interface and exact same-HMI tag identity; native interface existence, existing-value guard, tag type and ownership checks. External PLC tags are blocked unless exact connection/declaration/type evidence is complete. Caller assertions and matching names never authorize binding. |

No request field can override native qualification or preservation availability. Unknown fields, mixed operation payloads, client qualification flags, stale identities, metadata hashes and incomplete required scope are rejected. Basic, Comfort, RT Advanced, RT Professional, Unified Comfort and Unified PC remain separate profiles; no support is inherited merely from shared API types. Diagnostics retain observed profile, catalog identifier, runtime version and native assembly identity where available.

## Preservation

Classic capture exports every exposed screen, template, popup, slide-in, global element, overview, tag table, connection, cycle, script and text/graphic list through public APIs. Folder traversal, file count and bytes are bounded. Capture spans all discovered HMI targets, so another HMI with repeated member names is a separate preservation identity. Native failures retain partial artifacts, exact failure information and an incomplete/failed manifest; they never become a complete content fingerprint. Scripts are exported as content and never executed.

Raw SHA-256 is retained for every export. The sole current Classic content normalization changes `/Document/DocumentInfo/Created`, with its observed scalar timestamp shape, to a fixed export-time marker. Unchanged V21 Update 2 native exports before repeated read and fresh reopen established that this field is export metadata. No GUIDs, IDs, unknown nodes, events, bindings, translations, library version references, resource content, whitespace or product metadata are removed. Nested `Created` fields remain content. Pure negative tests enforce these boundaries. An unexpected native normalization fails preservation.

Unified tag tables use documented native composition export. After `tag_create` or `external_tag_create`, only files under the target table's export may change or appear; every other artifact must be identical. Installed V21 metadata exposes no `HmiScreen.Export`; item/property readback is insufficient to certify all events, dynamics, translations, resources and unknown screen payloads. Those screen mutations therefore remain unavailable until a complete preserving readback mechanism and exact-profile native fixtures establish the required coverage.

The Classic HmiTarget surface has no public alarm composition. Full resource and dependency coverage and operation-specific stable exports must still be established before claiming a complete affected preservation scope. An archive is a recovery artifact, not proof that all unrelated native content was compared.

## Change lifecycle and recovery

Preparation validates the exact project/session/HMI/object identity, immutable expected native-content fingerprint, operation shape, dependencies and typed destination, then retains the before exports and intended full-document/scalar effect. It does not obtain write eligibility from explorer metadata. Apply requires `checkpointSave=true`, `compile=true` and `save=true`; the existing lifecycle revalidates target fingerprints and prepared file hashes, durably records the idempotency key, saves current edits and creates a hashed recovery archive before dispatching one mutation.

After native mutation, precise readback, the affected HMI compiler, unrelated-content comparison and save are required. State and artifacts flow through the existing journal. Native failures retain the changed project and archive in `recovery_required`; a worker interruption has an unknown outcome and is never automatically replayed. Recovery retrieves the archive into a new owned location and compares complete recovered HMI content with its recorded baseline before reporting HMI equality. Native qualification must separately prove save/fresh-reopen, recovery/fresh-reopen and visual behavior.

## Library handoff

[hmi-library-handoff.v1.schema.json](../../contracts/hmi-library-handoff.v1.schema.json) is the explicit boundary. The library subsystem supplies an already-resolved source type/version, family, library revision and complete dependency closure. The HMI adapter validates the exact destination, placement, interface/tag mapping and preservation requirements. When a resolved native version is supplied by the library executor, the guard compares its actual type/version GUIDs with the handoff. A handoff remains a prerequisite and cannot override unavailable preservation. Type lifecycle, transfer, release, publication and updates remain in the library subsystem and execute in its existing resource-aware change.

## Validation and acceptance

Operation-specific `requiredGates` also include a clean baseline HMI compile, stale target/dependency rejection, idempotent apply and interrupted-write recovery. Scalar and item updates require exact property deltas; tag creation requires namespace uniqueness and internal-tag isolation; placement requires screen bounds and preserved faceplate identity; bindings require exact typed ownership and either the existing-value guard or dependency closure. Reporting these prerequisites does not qualify or enable any operation.

The scalar patch and sole timestamp normalization accept a single plain XML text node with no attributes. Comments, processing instructions and CDATA inside those fields are rejected because replacing the value would erase unqualified native payload. Such nodes elsewhere remain significant content.

Run `dotnet run --project tests/TiaMcp.Hmi.Engineering.Tests/TiaMcp.Hmi.Engineering.Tests.csproj` for the Siemens-independent request, ownership, strict XML patch/preservation and negative normalization suite. These tests do not qualify native engineering. Native acceptance records belong to `artifacts/hmi-native-*` and the HMI qualification report, with exact binary, catalog/runtime and input/output hashes.

Open engineering gates remain operation-specific: complete preservation/dependency scope, exact scalar or binding effect, all unrelated content, native compilation, save/reopen, archive retrieval equality, representative visual inspection, stale-target behavior and interrupted-write recovery. The capability result continues to publish `engineeringVerified`, `preservationVerified`, `persistenceVerified`, `recoveryVerified` and `runtimeVerified` separately. Missing or blocked evidence is never a passing gate.
