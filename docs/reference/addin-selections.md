# Review detached Add-In selections without TIA

`addin_selection_review(reviewId, uploadRef)` imports a committed `addin-selection.v1` JSON upload and binds each selected reference to the exact saved project review. `addin_selection_page(recordId, section, offset, count)` reopens the immutable result; `section` is `summary` or `selections`. Download original bytes and the complete report using the returned `artifact_read` references.

This works without Siemens installed. It reviews supplied data; it does not run an Add-In, launch TIA, authenticate the producer or authorize engineering changes. The [companion plan](../plans/expansion-plans/tia-addin-companion.md) retains native delivery, workflow hooks, permissions, signing, UI lifetime and authenticated handoff requirements.

## Input and identity

The [schema](../../contracts/addin-selection.v1.schema.json) defines the request, report, retained record, receipt and page. Required request fields:

| Field | Meaning |
| --- | --- |
| `schemaVersion`, `reviewId` | `addin-selection.v1` and exact saved-review SHA-256. Import a saved review first with `project_snapshot_import`. |
| `selectionId`, `selectedAtUtc` | Sender-declared 32 lowercase hex ID and UTC round-trip timestamp, for example `2026-10-18T00:00:00.0000000+00:00`. Neither grants freshness or replay authority. |
| `surface`, `projectHost` | `projectTree` or `devicesAndNetworks`, and exact host label from the saved review. Library and workflow-hook selections are not yet supported. Surface is supplied context, not proof that a native UI produced it. |
| `producer` | Declared `productId`, `version`, `tiaVersion:21`, `assemblySha256` and `configurationSha256`. These hashes are not a signature or producer authentication. |
| `references` | 1..64 ordered `project-model.v1` references: `schemaVersion`, `snapshotId`, `sessionGeneration`, `projectPath`, `nodeId`, `objectPath`. Copy exact saved values; do not manufacture identifiers or join by names. |

The parser rejects duplicate JSON fields, unknown fields, invalid UTF-8, control characters, invalid hashes/version/time, duplicate objects, ambiguous paths, virtual nodes and mismatched context. Maximum input is one MiB; text/reference fields have explicit schema bounds. The original input and derived report are retained with content hashes. Report retention is bounded to 512 MiB including incomplete files; no automatic eviction occurs.

## Reopening and limitations

Repeated identical intake returns the same receipt. Upload release does not remove the retained input. A fresh Host revalidates the input, saved review and derived report before serving pages; changed or missing required bytes refuse. Keep the saved review available. An expired saved snapshot remains historical evidence and never becomes a current native selection.

Pages contain at most 200 rows within 128 KiB. Oversized rows are explicitly omitted with the complete report available for download. All producer-authentication, live-selection, content-verification and native-execution claims remain false. This handoff cannot dispatch operations; use the separately admitted native preparation/approval flow when that integration is implemented and qualified.

Implementation evidence: [18 October checks](../verification/addin-selection-20261018.md).

## Existing-worker owner context

The private worker operation `addin_owner_context` accepts an empty object and returns [addin-owner-context.v1](../../contracts/addin-owner-context.v1.schema.json). It is an internal receiver prerequisite, not a public MCP tool or a way to attach/start TIA. Ordinary `session_status` output stays unchanged.

Attach captures an OS process handle before attaching. Start captures the created process after resolving its native PID, refusing a process whose birth time is later than the beginning of that PID observation. The handle remains with that attachment; failed capture, process exit, changed native PID or disposal cannot be repaired by reopening the numeric PID. Detachment closes only this OS query handle in addition to the existing attachment cleanup. The new owner read is explicitly non-invalidating for project snapshots and remains subject to existing exclusive-operation guards.

`available` means that the retained OS process is still alive and the current native PID matches it. The context includes the captured start time, image path, Windows session, worker instance and current model session generation. `nativeProcessIdMatched` is a numeric observation, not proof of installed companion identity, current project content or native selection. Capture/clock/native-proxy behavior still needs genuine TIA qualification. `producerAuthenticated`, `liveSelectionVerified` and `nativeQualified` stay false. A new handoff receiver must not treat this context alone as authorization.

[Owner-context verification and remaining integration](../verification/addin-owner-20261018.md).

The [owner-discovery correction](../verification/addin-owner-discovery-20261018.md) also prevents the bridge from lazily starting a worker for `addin_owner_context`. An unused Host returns `AddInOwnerUnavailable`; later explicit work may establish its ordinary connection. Observation never starts or attaches an owner.

## Internal handoff lifecycle

The [portable lifecycle component](../verification/addin-handoff-lifecycle-20261018.md) now retains request identity, issues a one-use thirty-second challenge and publishes a hash-linked receipt before acknowledgment. It reuses the detached selection store, binds supplied authority context and refuses replay after cancellation, owner change, publication failure or restart. Completed receipt lookup is read-only and rechecks the original saved evidence.

The lifecycle itself is internal source with synthetic-authority tests. Production enrollment is the explicit opt-in [described below](#native-companion-build-publish-and-enroll); an OS process observation or declared package hash still does not authenticate the producer. The existing upload/review/page workflow is unchanged. The receiver integration is described below.

## Opt-in Host receiver

The [bounded receiver](../verification/addin-receiver-20261018.md) now runs inside the existing Host when launched with `--addin-handoff`. It is off by default and can be combined with `--no-native-workers`. It publishes `addin-handoffs/receiver.json` under the selected artifacts directory. Discovery may be stale after a crash and is not authentication; clients must verify the connected server process. The [wire contract](../../contracts/addin-handoff-wire.v1.schema.json) permits only status, retained receipt lookup and the fixed selection handoff exchange.

Without `--addin-enrollment`, begin, complete and cancel return `AddInEnrollmentUnavailable`, and all returned evidence remains unqualified. No new MCP tool or native worker is launched by the receiver. The existing supplied upload/review/page workflow remains usable. Positive transport/receipt tests use a separate test executable with explicitly synthetic authority, never a production option.

## Native companion: build, publish and enroll

Added 27 October 2026. `src/TiaMcp.AddIn.V21` is a .NET Framework 4.8 Add-In for TIA Portal V21. Its project-tree context menu entry **TIA MCP › Send selection to TIA MCP** maps each selected object to the worker's project-model object path, copies the exact references from the newest saved review of the same project copy, and hands an `addin-selection.v1` document to the receiver through `begin` and `complete`. It only reads: no file writes, no network, no credentials.

Build and package on an engineering workstation (PowerShell 7):

```powershell
./scripts/Publish-AddIn.ps1 -TiaApiDirectory 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48'
```

The script builds the project, validates `AddIn.Publisher.xml` against the installed `Siemens.Engineering.AddIn.Publisher.xsd` and runs the Siemens Publisher with its engineering-member check enabled. It writes `TiaMcp.AddIn.V21.addin`, `publisher.log` and `addin-enrollment.json` into a new `artifacts/addin-<timestamp>/` folder. It never installs the package and never starts TIA. A plain build needs `-p:TiaApiDirectory=...` when TIA is not in the default location; hosted CI does not build this project.

To use it:

1. Install the `.addin` in TIA Portal yourself and grant its declared permissions: `TIA.ReadOnly`, environment and file read, and unmanaged code for the pipe client and its server check (TIA's sandbox refuses the .NET pipe client and serializer, so the companion uses kernel32 handles and its own JSON).
2. Set the user environment variable `TIAMCP_ARTIFACTS` to the Host's absolute `--artifacts` directory, then start TIA.
3. Start the Host with `--addin-handoff --addin-enrollment <addin-enrollment.json>`. The enrollment option is refused without `--addin-handoff`, and the file must contain exactly `schemaVersion`, `assemblySha256` and `configurationSha256`.
4. Through MCP, attach the worker to that TIA instance and save a review of the project (`project_snapshot_import`).
5. In TIA, select 1 to 64 objects and choose *Send selection to TIA MCP*. The notification names the retained record; open it with `addin_selection_page`.

With enrollment, the Host asks the already attached worker for `addin_owner_context`; it never starts or attaches one. It then admits a request only when all of these hold:

- The pipe peer is `Siemens.Engineering.AddIn.Loader.x64.exe` under that TIA's `Bin\PublicAPI\AddIn\Loader`. It must be a direct child of the owned TIA process, in the same Windows session, and started after it.
- The binding carries the live worker session generation and this Host's machine name, so a review captured in an earlier attachment or on another machine is refused.
- The declared `assemblySha256` and `configurationSha256` equal the enrolled values. The first is the SHA-256 of the deterministic build's module version ID, because TIA may load Add-Ins from bytes. The second is the SHA-256 of the embedded Publisher configuration.

The 30-second challenge stays one-use and is rechecked against the same owner tuple at `begin` and twice during `complete`.

Native run (29 October 2026, `local.addin.companion.20261029`): installed in TIA Portal V21 Update 3, the companion sent one project-tree selection (a device item) from inside TIA's Add-In sandbox; the Host admitted the loader peer, bound the selection to the saved review, and the stored record carries the enrolled hashes. [`scripts/qualify_addin_companion.py`](../../scripts/qualify_addin_companion.py) runs this with an operator at the desktop, and [`scripts/Test-AddInSandbox.ps1`](../../scripts/Test-AddInSandbox.ps1) replays the companion's pipe and JSON code offline under the declared permissions.

Limits: only one device-item selection has run natively; the other mapped kinds and multi-object selections share the same path code but have not. Enrollment is an OS-lineage and declared-hash binding, not attestation of the loaded package, so `producerAuthenticated` and `liveSelectionVerified` stay false. The selection binds saved references, not live content. Only project-tree projects, device groups, devices, device items, PLC software, software units, block/type/tag-table groups, blocks, types, tag tables and tags are mapped. Other objects and the devices-and-networks, library and workflow-hook surfaces are refused. Package signing is not configured.
