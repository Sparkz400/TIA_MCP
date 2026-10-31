# Library management in TIA MCP V21

The bridge accepts a ZIP or native library from the caller, stages an immutable copy, lists its contents, and opens an explicitly selected native library through Siemens Openness. Imports and updates use prepared changes with checkpoints and durable journals. The original supplied library is preserved. A package being accepted does **not** mean that every component can be imported into every PLC or HMI.

This is the implementation guide for [the full feature plan](../plans/library-management-plan.md). Native qualification remains specific to the recorded device, TIA version, library and operation. The feature catalog distinguishes implementation from qualification; `library_capabilities` and the HMI capabilities query describe current boundaries.

Work stopped at the 8 October 2026 checkpoint at the user's request. The [remaining-work TODO](../plans/library-management-todo.md) separates unfinished implementation from additional release qualification.

## End-to-end workflow

1. Transfer the file using `artifact_upload_begin`, sequential `artifact_upload_chunk` calls, and `artifact_upload_commit`. These run on the MCP host without starting Siemens. `artifact_upload_status` survives host restarts and gives the exact next byte offset. Repeating identical previously acknowledged bytes is allowed; different bytes, gaps and partial overlaps are rejected.
2. Call `library_package_stage` with the committed `artifactRef`. If the file already exists on the TIA machine, supply its absolute `hostPath` instead. A `hostPath` must name, by its full (not 8.3 short) name, a `.zip`, a native `.alXX`/`.zalXX`/`.apXX`/`.zapXX` file, or a complete native library or project directory with an `.alXX`/`.apXX` marker at its top level (an archive inside a folder does not qualify the folder; stage the archive itself). Any other host file or directory is refused before it is read, because staged originals become readable through `artifact_read`, and a directory whose staging fails keeps no partial copy. Upload a loose source file instead. A browser/client path on another computer is not a host path.
3. Page `library_package_inspect` to review `candidates`, `files`, `documents` and `diagnostics`. A Siemens download may contain several libraries, examples and source files. No candidate is selected automatically. `sourceUrl` is recorded provenance and is never fetched or considered proof of authenticity.
4. Call `library_open` with a `request` containing the returned `packageId` and exact `candidateId`. V21 global libraries are opened from independent managed source and working copies. Older compatible native libraries need an explicit prepared upgrade; newer TIA formats are rejected. Example projects use the separate project retrieval workflow, and loose sources use explicit source import.
5. Use `library_list`, `library_query` and `library_compare` to inspect native types, exact versions, folders, master copies, dependencies and uses. Preserve the returned native `typeGuid` and `versionGuid`; names and version labels alone are insufficient selectors. Read pagination includes a revision token.
6. Call `change_prepare` with a library change kind, `payload.schemaVersion: 1`, exact source/destination and target scope. Inspect the effects, dependency closure, fingerprints, conflicts and resources. Apply that preview using `change_apply` with its `changeId`, `previewHash`, unique `idempotencyKey`, `checkpointSave: true`, `compile: true`, and `save: true`.
   The preview's `unsavedTargetEdits` reports whether the target project has unsaved edits. When `present` is true, `checkpointSave` saves them before archiving (`savedIntoCheckpointBaseline`), so they become part of the recovery baseline and of the saved result. A library change refuses a target with an inconsistent block or PLC data type before any checkpoint or write (`InheritedDiagnostics`, or `FingerprintUnavailable` for blocks); compile or fix it first. A compile failure after the native mutation (for example a library type that uses data types the target CPU family lacks) returns `CompileFailed` without saving; detaching discards the change.
7. Inspect diagnostics and native readback. Save/reopen and runtime tests remain separate evidence. An imported FB does not automatically have an instance DB, a call site, connected signals or a configured HMI connection.
8. Close the library handle with `library_close`. Explicitly release unused staged packages with `library_package_release` and committed upload storage with `artifact_upload_release`. Working copies, checkpoints and journals are retained for diagnosis. An active retention reference prevents package release.

## Tool examples

Tool argument objects below are sent as MCP `tools/call.arguments`.

```json
{
  "artifactRef": "<returned committed artifactRef>",
  "sourceUrl": "<optional vendor download page>"
}
```

`library_open`:

```json
{
  "request": {
    "packageId": "<returned packageId>",
    "candidateId": "<returned candidateId>",
    "writableWorkingCopy": false
  }
}
```

`library_query`:

```json
{
  "request": {
    "libraryId": "<session libraryId>",
    "query": "elements",
    "maxItems": 100
  }
}
```

`change_prepare` for one native PLC type version:

```json
{
  "kind": "library_instantiate",
  "payload": {
    "schemaVersion": 1,
    "source": {
      "libraryId": "<session libraryId>",
      "revision": "<current library revision>"
    },
    "selection": [{
      "typeGuid": "<native type GUID>",
      "versionGuid": "<native version GUID>"
    }],
    "target": {
      "projectName": "<exact project>",
      "plcPath": "<returned PLC path>"
    }
  }
}
```

For a master copy, use `selection: [{"masterCopyPath": "<returned exact path>"}]` and explicitly specify `destinationComposition: "blocks"` or `"types"`. Master copies are native copied content; they do not acquire a version link merely because they came from a library.

## Implemented operation groups

| Operation | Change kinds / tools | Important restriction |
| --- | --- | --- |
| Global/project library discovery | `library_open/list/query/compare/close/capabilities` | Session IDs expire on detach. External open global libraries are read-only to this bridge. Sources are copied before native open/upgrade. |
| Update check | `library_update_check` | Native `UpdateCheck` of `libraryId` against an open project (`projectName`); `mode` `outOfDateOnly` (default) or `outOfDateAndUpToDate`. Returns the Siemens log messages depth-first, bounded by `maxItems` (1–1000) with explicit truncation. No update is performed. |
| Create, persist, archive, upgrade | `library_create`, `library_save`, `library_archive`, `library_upgrade` | New managed paths; no overwrite of the original source. Upgrades are V14–V20 to installed V21, subject to Siemens validation. V13 and older native libraries are refused before any native call (`LibraryVersionUnsupported`). No downgrade. When V21 saves `<name>_V21` and its conversion log records success but returning the library fails, that saved library is opened and the native error is recorded. Elements whose product is not installed stay listed as `unavailable`. Read-only sources get a writable working copy; a source locked by another process is refused. |
| Native type/version consumption | `library_instantiate` | Exact native GUIDs, committed usable dependency closure, typed PLC destination and native link readback. An already installed dependency is reused only with one native link to the identical type and version GUID and identical native version metadata; any other same-name object is a collision and is rejected. Unpreviewed new objects fail the change. TIA does not apply a software unit's namespace preset to library instances; an explicit `namespace` (dot-separated identifiers, software-unit `scopePath` only) is shown in the preview as `instanceNamespace` and set on the selected roots, with the namespace and the exact link read back. Created dependencies keep their type namespace. |
| Master copies | `library_create_master_copy`, `library_instantiate` | Eligible native source and explicit destination (`destinationComposition` `blocks`, `types` or `tagTables`); one root. Same-name destinations are refused. A PLC user group is a composite source with `object: { groupKind: "blocks" \| "types" \| "tags", groupPath, scopePath }`; several unrelated objects in one master copy have no V21 API. Instantiating group/composite master copies requires its own qualified adapter. |
| Native copy/publication | `library_copy_elements`, `library_publish` | Native synchronization copies all versions of each selected type and its dependency types. The preview identifies new defaults or promotion to a numerically newer source default. Exact existing versions are retained; no forced downgrade or GUID rewrite. |
| Version-control export | `library_export` | Exact project-library version, Siemens-supported document format, retained file hashes. No global-library document export or fabricated GUID metadata. |
| Project updates | `library_update_project` | Explicit `updateScope: "entireSelectedPlc"`, selected source default, preservation of old versions and a complete proof for retaining existing library-linked objects. Without `forceDefault` the source default must be its highest release and must not be below the project default. `forceDefault: true` assigns the selected source default to the PLC anyway (a forced downgrade or an older release made source default with `library_set_default`) and makes it the project default; the preview states `defaultEffect`, and apply reads back the new default and unchanged uses in every other PLC. V21 updates whole PLC software only: there is no instance, block-group or software-unit update scope, and a PLC holds one version of a type ([29 October](../verification/library-update-scopes-20261029.md)). |
| Authoring and lifecycle | `library_create_type`, `library_edit_version`, `library_release_version`, `library_discard_version`, `library_set_default`, `library_type_policy` | Documented SCL/UDT sources or eligible native exported documents; project-library restrictions apply. PLC editing needs a native test-instance selection, or `documentDirectory`/`documentName` to create the next version from exported documents with authored content (project library only; TIA V21 refuses replacing a test instance by source generation). Type-policy flags the native type declares read-only (for example `SetForUpdate` on PLC data types) are refused before mutation. Dependency release is never automatic. |
| Folders/deletion | `library_organize`, `library_delete` | Exact folders; no root rename/deletion, only empty folder deletion. Moving types or folders has no V21 API. Live-use/dependency checks must be complete before version deletion. Global external consumers cannot be proven absent. |
| Cleanup and harmonize | `library_cleanup`, `library_harmonize` | Exact `types: [typeGuid]` and/or `folderPaths`. Cleanup works on project libraries only; its preview lists every version and type it may delete (no project use, no master copy, no retained dependent) and apply fails if any other version disappears. Harmonize renames and regroups instances of PLC block and data types in one selected PLC (`harmonizeNames`, `harmonizePaths`); the preview lists each instance's expected name and group path and apply reads them back. See the [kind table](engineering-workflows.md#supported-payloads). |
| HMI | `library_instantiate`, `library_integrate`; `library_query` HMI actions | Separate Classic and Unified typed adapters; see [family-specific support](../reference/library-hmi-support.md). |

Project updates are intentionally bounded to the native scope that can be previewed and checked. A generic overwrite, forced arbitrary version assignment, same-name unrelated type adoption, hidden conversion of a copied master into a library type, or cross-family HMI import is not offered as a shortcut.

Native project updates can remove unreferenced library objects from the PLC, including objects unrelated to the selected type. Keeping old library versions does not disable that cleanup. Preparation therefore requires native reference paths from organization blocks to **every** existing library-linked object in the selected PLC. Outgoing references of objects being updated cannot prove preservation because the new release might remove those references. Missing proof rejects preparation with `LibraryUpdateCleanupRisk`; there is no deletion override. The preview records the positive reference evidence and explicitly does not claim a complete reference graph. Application rechecks this evidence and verifies existing object paths plus exact affected/unaffected version links after checkpointing, the update, compilation and saving. These checks are distinct from comparing all program content or testing runtime behavior. The cleanup behavior is also described in [Siemens' project-update guidance](https://docs.industrial-operations-x.siemens.cloud/r/en-us/ax/faq/2504.1.62/known-issues/best-practices-for-updating-a-tia-portal-project-using-a-tia-portal-global-library-generated-in-a-tiax-library-development-workflow/updating-a-tia-portal-project-when-having-types-with-duplicate-versions-in-the-global-library).

`library_save` is a recovery operation for an owned library left dirty by a recorded failed change in the same session. It requires that change's `recoveryChangeId` and a verified prior checkpoint; it does not adopt arbitrary unsaved external edits. For HMI, `library_integrate` currently accepts Unified `faceplate_bind` only. Classic placement uses `library_instantiate` and does not claim completed signal or resource integration.

Use the returned committed version GUID after releasing an InWork version. Siemens can rotate the version GUID during release while preserving the type GUID; re-query the committed version and refresh library revisions before the next operation.

## Storage policy and errors

Default host limits are 1 GiB input, 4 GiB total expanded data, 20,000 entries, three nested ZIP levels, 64 KiB decoded upload chunks, 200 items per page, 16 GiB intake storage, 128 uploads, 128 packages, ten minutes extraction work and 24 hours for receiving uploads. Effective limits are returned with upload/package metadata. Upload reservations count against storage capacity.

Supply `--library-limits-file C:\path\limits.json` when launching the host to override known limits. The file is at most 64 KiB, uses camelCase integer properties from `PackageIntakeLimits`, rejects duplicates/unknown keys, and validates hard upper bounds. For example:

```json
{"maxInputBytes": 268435456, "maxExpandedBytes": 1073741824, "maxEntries": 10000}
```

ZIP paths are interpreted under Windows rules even in pure tests. Traversal, absolute paths, alternate streams, device names, links/reparse points, ambiguous normalization, duplicate/case-colliding destinations, encrypted ZIP members, bad CRCs, excessive expansion and excessive nesting are rejected. Native `.zalXX` and `.zapXX` files remain opaque; private Siemens databases are never rewritten by the extractor. Documentation and license files are retained; executables/scripts are never run. CRC and SHA-256 establish integrity, not publisher identity or redistribution permission.

Useful structured failures include `PackageInvalid`, `PackageLimitExceeded`, `PackageChanged`, `PackageIncomplete`, `UploadOffsetMismatch`, `UploadChunkMismatch`, `UploadIncomplete`, `LibraryUpgradeRequired`, `LibraryIdentityMismatch`, `LibraryConflict`, `LibraryVersionUnsupported`, `InheritedDiagnostics`, `CompileFailed`, `DependencyUnresolved`, `PreviewStale`, `NativeScopeTooBroad`, `LibraryUpdateCleanupRisk`, `VersionSelectionUnsupported`, `HmiPrerequisiteMissing` and `OutcomeUnknown`. Do not infer that a failed mutating call left the project unchanged; inspect its journal.

## Recovery and concurrency

Each prepared library operation has a `lib-…` identity, preview hash, resource fingerprints and a durable journal under the configured `changes` directory. A durable claim reserves its idempotency key before native writes. Save, checkpoint archive, mutation, compilation and final persistence are recorded as separate native stages. Library-wide metadata revisions are explicitly not whole-content proofs; write preflight also uses applicable native content and saved-resource checks.

If a transport/worker failure loses a response, call `change_status`. The last dispatch may have completed. Live native handles do not survive a worker restart and a prepared mutation is never reconstructed and replayed automatically. A new preview is needed if no mutation was dispatched. A dispatched uncertain mutation must be reconciled or recovered.

`change_restore` verifies checkpoint hashes and retrieves each resource into new unique locations in an owned TIA instance. A project checkpoint requires that instance to have no open project. Original projects, source libraries, archives and new outputs remain available. Recovery itself has persisted intent/outcome records; a partially completed recovery does not delete earlier recovered resources. Checkpoint compilation alone is not runtime proof.

Same-process mutations are serialized by the worker. Native exclusive access is acquired during application, and fingerprints are rechecked after acquiring it. File upload/staging/release uses a cross-process gate. External TIA edits can invalidate previews and require preparation again.

Type-authoring documents are copied into the change's owned snapshot during preparation and verified before native import. InWork PLC versions cannot be exported by Siemens; release/discard previews instead fingerprint their exact native test-instance links and exported PLC content. Missing or incomplete test-instance coverage blocks the operation. This does not imply an equivalent authoring route for every HMI type.

Successful library applications also write `library-lock.v1.json`, binding the approved selection and dependency versions to source hashes, target identities and observed results. Keep this JSON and native document exports in version control when needed; the bridge does not automatically create Git commits. The lock is a reproducibility receipt, not proof that TIA can recreate every native object on another host.

## Tests and release qualification

The 8 October 2026 PLC qualification (`artifacts/lq/nl_9d539676/qualification.json`) passed the single-FC master-copy lifecycle through the actual MCP host: create, author, archive, wrap the native archive in a ZIP, chunk-upload, stage/open, release intake storage before native consumption, instantiate into a fresh consumer, compile, save/reopen, journal read after host restart, and checkpoint recovery into a separate project. Its exact executable hashes are retained. This is an authored native archive inside a synthetic wrapper, not a downloaded vendor distribution. This profile does not qualify released FB/UDT version updates or arbitrary existing-project preservation. Classic empty-screen native foundation checks passed; the Unified foundation fixture remains failed on required encrypted-transfer runtime password settings, as detailed in the [HMI support report](../reference/library-hmi-support.md).

The consolidated verification record (`artifacts/library-implementation-verification.json`) records the tested build scope, executable hashes, complete offline regression gate, individual native profiles and remaining acceptance gaps. It retains failed investigations rather than promoting an incomplete profile to success.

The linked type lifecycle run (`artifacts/lq/nt_a702508e/qualification.json`) recorded authoring-baseline verification, two publications, exact-version FB/UDT consumption and rejection of an unsafe unreferenced update without mutation. After a fixture selector correction, the referenced update continuation (`artifacts/lq/nt_a702508e/referenced-update-e7d98126/report.json`) passed FB 1.0 → 1.1, exact new/old-version use checks, unaffected UDT use checks and fresh reopen. Independent preservation verification (`artifacts/lq/nt_a702508e/referenced-update-e7d98126/native-preservation-verified.json`) verified all 18 raw exports and identical semantic payloads for five unrelated blocks plus the UDT before, after and after reopening. This is linked evidence, not one uninterrupted fresh-authoring run. The second release changes metadata only, and runtime behavior remains unverified.

The test entry point `scripts/Test.ps1` includes library and HMI pure checks. `scripts/Test-Libraries.ps1 -Mcp` additionally exercises an actual stdio host with deliberately absent Siemens workers, so package testing cannot accidentally start the default worker. An explicit `-BuildRoot` supports immutable build snapshots.

- `TiaMcp.Library.Tests`: uploads, restart receipts, retry mismatches, storage limits, strict policy configuration, path attacks, ZIP corruption, nested limits, classification, manifest drift and lifecycle cleanup.
- `TiaMcp.LibraryHmi.Tests`: family selection, names, placement, interfaces, exact versions and type/capacity mapping rules.
- `TiaMcp.Tests`: durable library journal atomicity, claims, tamper rejection and restart recovery records, alongside existing bridge/engineering regressions.
- `scripts/probe_library_api.py`: installed XML/DLL signature observation without executing Siemens code.
- `scripts/qualify_library_intake.py`: real MCP upload/stage/restart/release/negative-path profile.
- `scripts/qualify_native_libraries.py --run-native`: newly owned scratch PLCs only; create source, author master copy, archive, stage/open, consume, compile/read, restart, reopen and restore a checkpoint into a separate project. Host/worker/artifact paths must be explicit.
- `scripts/qualify_native_library_types.py --run-native`: separately records native FB/UDT creation, release, export, publication, exact-version consumption and update observations. Its second FB generation changes release metadata only; it cannot prove compatibility of changed interfaces or changed runtime behavior.
- `scripts/qualify_library_hmi.ps1`: independent owned Classic/Unified foundation probes, with exact catalog device IDs and retained native outputs.

PLC library qualification is recorded in the [lane C record](../verification/library-lane-c-20261024.md) (releases, dependencies, lifecycle, vendor upgrades, interruption), the [native scale record](../verification/library-native-scale-20261027.md) and the [preservation run](../verification/library-preservation-20261028.md), which checks exact whole-resource snapshots, a fresh reopen and per-kind checkpoint recovery for every PLC library change kind. Release acceptance still requires installed optional-product absence tests, Classic faceplate authoring/binding, Unified faceplate/UDT authoring and PLC communication, visual/runtime assertions and real vendor byte volumes. The [full plan](../plans/library-management-plan.md) retains these acceptance gates; presence of a tool does not mark them passed.


The [PLC component recipe planner](plc-library-integration-recipes.md) prepares explicit
FB/dependency, global instance DB, LAD/FBD call and existing scalar-tag mappings through
`library_integrate` with `action: plc_component_plan`. It returns separately prepared
steps and qualification requirements. Combined PLC application remains unavailable.

## Upload manifest publication

The Host retries a temporary Windows file-rename denial only while the prepared
manifest and prior destination remain unchanged. This never retries a native
operation. If an upload chunk/commit/abort still fails, its prior durable state
and prepared file remain available. Read `artifact_upload_status` and retry the
same known upload request and identical chunk bytes; do not infer commitment
from a data file alone. Retained temporary files count toward storage use until
explicit release. [Publication and recovery evidence](../verification/manifest-publication-20261017.md)
describes bounds, uncertain outcomes and platform coverage.

## Validate a complete retained preview

Use `preview-plan` for a retained preview or `preview-change` for its change
response. These modes combine operation/effect checks with the relevant HMI,
component, update or synchronization analysis. Change mode also checks recorded
compiler evidence and scope:

```powershell
python -B scripts/validate_library_observations.py --kind preview-change --path fixtures/libraries/observation-contracts/synchronization-change.json
```

The report distinguishes complete captures from unavailable maintenance
dependencies and unverified selector/file evidence. Referenced paths are not
opened, and validation does not authorize apply or establish preservation.
Earlier specialized modes remain available for their narrower documented checks.
See [verification and boundaries](../verification/library-preview-effects-20261023.md).

## Validate PLC component previews

Use `--kind component-plan` for a retained PLC integration preview, or
`--kind component-change` for its prepared change response. These modes bind the
exact version/dependency set, repeated content pins, interface/tag mappings and
complete proposed import/typed-call graph. For example:

```powershell
python -B scripts/validate_library_observations.py --kind component-plan --path fixtures/libraries/observation-contracts/component-plan.json
```

Referenced XML is not read. Hash agreement does not verify artifact bytes or
content preservation, and the retained inventory hash does not prove collision
absence or the first available DB number. Current and historical caller-artifact
coverage is reported separately. Existing blockers and disabled combined apply
remain in force. See [evidence and limits](../verification/library-component-previews-20261023.md).

## Validate HMI target previews

Use `--kind hmi-plan` for a retained HMI library preview and `--kind hmi-change`
for its change response. Both include operation checks and add exact target,
dependency, version, effect and captured-before consistency checks for Classic
copies, Unified internal tags and Unified faceplate creation/binding. For example:

```powershell
python -B scripts/validate_library_observations.py --kind hmi-change --path fixtures/libraries/observation-contracts/hmi-binding-change.json
```

That example is synthetic. A valid observation does not authorize native dispatch.
Binding results separately report complete requested-property readback and retained
layout agreement; a false layout comparison is preserved. Native selectors,
interface types, complete collision absence and full preservation remain unverified.
Use compiler modes separately for compiler evidence. See the [verified scope and
capture limits](../verification/library-hmi-previews-20261023.md).

## Validate retained identity and lock observations

[Library identity](../../contracts/library-identity.v1.schema.json) and
[lock receipt](../../contracts/library-lock.v1.schema.json) schemas describe the
serialized observations. Validate explicit original local bytes with:

```powershell
python -B scripts/validate_library_observations.py --kind lock --path C:/Evidence/library-lock.v1.json --expected-sha256 <original-sha256>
```

Use `--kind identity` for a standalone library view. The optional hash must be the
original lowercase SHA-256. The validator reads at most 4 MiB and never follows
embedded source/journal/artifact paths or starts native software. Its timestamp
checks do not depend on optional jsonschema format packages. It preserves the
input bytes and reports `qualificationGranted: false`.

This validates envelope structure, exact pins and explicit qualification limits.
Operation-specific result and compiler payloads remain opaque observations;
metadata revisions are not complete content fingerprints. Native authenticity,
preservation, complete compilation, reopen and runtime still need their separate
evidence. [Verification](../verification/library-observation-contracts-20261023.md).

Preview and live/recovered change envelopes can also be checked with
`python scripts/validate_library_observations.py --kind plan --path <preview.json>`
and `--kind change --path <change-response.json>`, optionally supplying
`--expected-sha256 <original-file-hash>`. The validator checks repeated identity,
preview and conservative recovered-state consistency, with the same 4 MiB local
input bound. It never follows embedded paths or replays work. Repeated preview
hashes are compared, not recomputed using a different serializer. Detailed native
plan/result payloads remain opaque; see the [verification and limits](../verification/library-change-observations-20261023.md).

Use `--kind inspection` for paged `elements`, `versions`, `dependencies` and
`uses` responses, `--kind export` for a `library_export` response, and
`--kind export-manifest` for its separately retained manifest. Inspection checks
query-specific rows, page bounds and completeness flags. Export checks include
file metadata, unique paths, relative document paths, media types, directory
aliases and the 200-file response prefix. A manifest may retain more than 200
files, within the same 4 MiB input limit. Its `documentsSha256` covers the export
before the manifest is added; the response directory hash has a different scope.
Neither hash is recomputed, and the validator never opens the listed files.
Unknown or failed transfer states remain observations, not successful exports.

Repeated preview comparisons preserve exact JSON decimal values, including
fractions that binary floating point would round or underflow. Integer, decimal
and Boolean values remain distinct. [Verification](../verification/library-export-observations-20261023.md)
records focused tests and compatibility with original retained native observations.

For operation payloads and returned results, use `--kind operation-plan` or
`--kind operation-change` with the same original-file and optional SHA-256
arguments. These explicit modes validate all 18 kinds, the PLC integration recipe
and the separate Classic/Unified routes. They check selected pins, resource
revisions, native text bounds, result identity and applicable HMI link/binding
readback. Applied journals require a result; an uncertain or failed journal with
a returned result remains uncertain or failed. PLC combined integration remains
plan-only. Release results may have a new version GUID, but must retain the type
and requested version number.

The original `plan`/`change` modes above remain envelope-only. The new modes add
`operationPayloadValidated` and `operationResultValidated` to their receipt;
neither grants native qualification. Unused supplied fields remain opaque, and
native selector resolution, detailed preview analyses, complete compiler evidence
and HMI library-query responses require separate validation. The validator does
not open embedded paths or recalculate native preview/content hashes. Schema
resources are cached for the process lifetime. See [operation verification](../verification/library-operation-observations-20261023.md).

Unified internal-tag creation now retains the native `defaultConnection` before
assigning the data type, then checks the returned name/type, unchanged connection,
empty PLC tag and absence of a configured native connection. Its result includes
`defaultConnection`, `plcTag` and `nativeConnectionResolved: false`; localized
internal labels are preserved verbatim. Operation validation requires these
three fields together and compares them without translating the label. Legacy
results without these fields remain valid only with an empty/null connection.
This fixes the adapter's former rejection of nonempty internal labels; it does
not establish native creation/compile/reopen acceptance. [Repair evidence](../verification/unified-internal-tag-20261023.md).

HMI queries have separate local validation modes:

- `hmi-request`: the inner `library_query` request object, with explicit
  `action: hmi_capabilities`, `hmi_targets` or `hmi_inventory`. Inventory accepts
  an explicit `target` or root-level `family`/`hmiPath`, and an optional 1–200
  `limit` (null/omitted uses the producer default). Project selection is still
  resolved by the worker, not the validator.
- `hmi-capabilities`: module presence, accepted/rejected/unreadable assembly
  identity, per-family operations and explicit false qualification flags.
- `hmi-targets`: exact returned target identities and compilation scopes.
  Classic compiles HMI software; Unified compiles the owning device's hardware
  and software.
- `hmi-inventory`: bounded Classic screen/tag-table identities and connections,
  or Unified screens/faceplate observations, tags and connections.

These modes use the same original-file SHA-256 option and 4 MiB input limit.
Classic nested tags/connections and Unified faceplate interfaces have fixed
200-row bounds. Top-level inventory limits are not returned, so a truncated
response can legitimately contain fewer than 200 rows. Unified
`screenItemsTruncated` counts all screen items; it can be true with no faceplates
and does not imply top-level truncation. Neither untruncated inventory nor an
accepted assembly identity establishes complete references, licensing, engineering
or runtime qualification. [Verification](../verification/library-hmi-query-20261023.md).

Library compiler observations can be validated with `--kind compiler` for a
standalone aggregate, `--kind compiler-change` for a full change response, and
`--kind compiler-lock` for a lock receipt. The change mode includes operation
validation and requires an applied project's compiler result plus its explicit
Boolean engineering-verification observation. A global operation can have no
compilation and must not claim engineering verification.

The validator checks target counts, flattened message depths, message/depth
truncation bounds, aggregate error/warning sums and compiler scope. Current
device paths are bound to the prepared path/name pairs; older receipts retain
their weaker name-only binding. Classic HMI compilation binds the selected HMI
path, while Unified binds its owning device. A lock lacks the original prepared
device inventory, so its declared compiler scope is not independently verified.

Standalone failed, truncated or unknown-state compiler observations remain valid
data and report `compilerReportsSuccess: false`. Warnings alone do not imply
failure. A zero summary cannot conceal an Error/unknown message state. Applied
project changes and issued project locks cannot pass with failed/incomplete
diagnostics. A successful compiler inside an uncertain journal does not change
the journal state or authorize retry. `compilerMessagesComplete` describes the
retained capture flags, and `compilerScopeBinding` describes the compared scope;
neither proves preservation, successful save/reopen or native qualification.
Original envelope/operation modes retain their previous behavior. [Verification](../verification/library-compilation-observations-20261023.md).

For a project-update preview, use `--kind update-plan`; for its retained change
response, use `--kind update-change`. These modes include operation validation
and check the detailed impact, baseline and cleanup guard. Requested types must
match the selected dependency closures, affected uses must exactly cover baseline
uses of those types, and comparison rows must cover every changed prior version.
Unavailable comparisons remain explicit and report `updateComparisonsComplete:
false`; a recorded no-op requires every affected version to already match.

Cleanup validation traverses the recorded graph from the reported OB roots. It
rejects unknown endpoints, disconnected required uses and outgoing edges from
objects being updated. Every baseline library use, including unrelated types,
must have a remaining positive path. Missing cross-reference services cannot
supply evidence but do not invalidate an independently recorded incoming path.
`recordedCleanupReachabilityValidated` describes this local calculation;
`nativeReferenceGraphVerified` and `engineeringContentPreservationVerified`
remain false. Embedded object descriptions and native root identities are
observations, not independently resolved selectors. Post-update readbacks and
compiler evidence require separate validation. [Verification](../verification/library-update-analyses-20261023.md).

Use `--kind sync-plan` for copy/publication previews or `--kind sync-change` for
their retained change responses. These modes include operation validation and
bind synchronization rows to every captured source release, including types
required only by older releases. The source default must be its highest committed
release. A newer source default is promoted; an equal/newer destination default
is preserved. The validator checks the declared effect, expected version pins,
copy effects and exact returned type/version/default coverage.

`synchronizationResultBound` means a retained result matches the plan, not that
TIA was executed again. The preview retains only the prior destination default,
so `destinationBeforeInventoryVerified` remains false: additional old destination
releases cannot be independently reconstructed. `nativeSynchronizationVerified`
and `masterCopyContentVerified` also remain false. Master-only copies can have
empty type-synchronization arrays; their embedded dependency observations do not
grant type-copy or content-preservation claims. Compiler and broader native
qualification checks remain separate. [Verification](../verification/library-synchronization-20261023.md).
