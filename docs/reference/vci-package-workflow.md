# Offline package manifests and three-way review

The normal Host adds `vci_package_capture` and `vci_package_compare` to the
[local Git document workflow](vci-host-workflow.md). They use retained raw files
and explicit uploaded mapping evidence. They neither connect to TIA nor create,
change or synchronize native VCI mappings. A manifest describes supplied evidence;
it does not establish that an engineering observation is authentic or current.

## Capture and review

1. Prepare or commit each raw-file set through `vci_prepare` / `vci_commit`.
   Baseline, project and workspace are explicit review roles, not live connections.
   A preview is permitted, so a workspace edit can be reviewed before committing.
2. Upload mapping JSON matching
   [vci-mapping-evidence.v1](../../contracts/vci-mapping-evidence.v1.schema.json).
   Supply `workspaceId`, the exact historical `snapshot`, an explicit
   `enumerationComplete` boolean and the package inventory. Each package identifies
   its exact object/snapshot, observed format, expected file paths and roles,
   evidence hash/origin and explicit profile `complete` boolean. Preserve library
   type/version GUIDs when using a library profile. Snapshot versions must be
   explicit; omission does not silently select V21. Only V21 is currently modeled.
3. Call `vci_package_capture` with `sourceRecordRef` and `mappingUploadRef`.
   The result returns a `vci:manifest:...` handle, `manifestSha256`, package/file
   counts, incomplete-package and unmanaged-file counts, and artifact names.
   Download `mapping.json` and `manifest.json` through `vci_artifact_read`.
   The first preserves the exact supplied bytes; the second hashes the actual
   retained raw files and reports missing companions and ownership problems.
4. Capture all three sides and call `vci_package_compare` with `baselineRef`,
   `projectRef`, `workspaceRef`, and `expectedHashes` containing the three reviewed
   `manifestSha256` values. Inputs must share the same explicit workspace lineage.
   Hash mismatch produces a retained **stale conflict**, not an accepted comparison.
5. Read the bounded summary and download `comparison.json` for every package row,
   reason and source content hash. `inputs.json` records the selected handles and
   reviewed hashes. The comparison handle is immutable; `receipt.json` provides
   the exact receipt bytes bound by that handle.

The mappings are supplied evidence. Setting `evidenceOrigin` to `nativeExport`,
claiming complete enumeration or supplying an opaque `Equal` native-status label
does not establish a current target, native completeness or engineering equality.
The caller must review those assertions against its original evidence.

## Native workspace inspection

`vci_workspace_inspect` (optional `projectName`, `maxItems` 1..2000, default 500)
reads the open project's native VCI tree without changing it: system/user workspace
groups, each workspace's root path, language and library policy (global library
path, delete-unused-type-versions flag), and every mapped object with its native
ancestor identity, directory, file name, format, `CompareState`,
`IndividualObjectCompareDetails`, child `MappingState` and the formats
`GetSupportedFileFormats` offers for it. Groups, workspaces, mappings and formats
share the `maxItems` budget; `completeness.truncated` and per-item `warnings` are
explicit. Use the result to write reviewed mapping evidence; it is an observation,
not a manifest, and its states do not establish preservation or import support.

There is no native export tool. V21 `Workspace.ExportObject` creates a mapping
in the project and writes below the workspace root, and `MappedObject.Synchronize`
writes the user's workspace; neither is a project-neutral export into a
Host-owned directory. Those writes are the prepared change kinds below. The tool
is verified (`local.read_contracts.20261028`).

## Native workspace changes

Five kinds run through `change_prepare` / `change_apply` with the usual
checkpoint, idempotency key and journal; payloads are in the
[kind table](../guides/engineering-workflows.md):

| Kind | Native call |
| --- | --- |
| `vci_workspace_create` | `WorkspaceComposition.Create(string, DirectoryInfo, CultureInfo)` |
| `vci_workspace_delete` | `Workspace.Delete()` |
| `vci_mapping_add` | `Workspace.ExportObject` or `Workspace.ConnectObject` |
| `vci_mapping_remove` | `MappedObject.Delete()` |
| `vci_synchronize` | `MappedObject.Synchronize(SynchronizationMode)` |

Workspace and group paths are the ones `vci_workspace_inspect` returns; objects
are one exact block, PLC data type, tag table or technology object by
`objectPath`. Guards and verification:

- The workspace root must not overlap the project directory, the Host artifact
  store or (for a new workspace) another workspace root, must not be a drive or
  share root, and a new root must be absent or empty. Symbolic links and
  junctions in the root, its parents or below it are refused, never followed.
- Prepare hashes every file below the root (at most 20,000 entries and 1 GiB).
  Apply refuses with `StaleRevision` before the checkpoint if any file changed.
  After the native call only the previewed packages and `.vci` configuration may
  differ; anything else fails with `MutationVerificationFailed`. Create and
  delete only report the file effect.
- Files a change may overwrite or remove are copied into the change directory
  first. The project checkpoint restores project state only, never workspace files.
- `GetStatus`/`GetChildStatus` are reported before and after. Export and every
  synchronized mapping must read back `Equal`. A mapping whose project object and
  workspace files both changed is refused as `VciConflict` in either direction.
- Project-ward synchronization is one exact `objectPath` per change, keeps the
  direct-mutation guards (safety units, F-blocks, know-how protection, generated
  objects) and requires `compile=true`; the workspace files must stay identical.
  Workspace-ward synchronization can cover all 1..200 mappings of a workspace as
  a journaled sequence, not a transaction.
- The engineering fingerprint covers workspace groups, roots, languages, library
  policy and mappings. Files are outside it, so an interrupted apply is held for
  manual reconciliation.

Not implemented: workspace rename, root relocation and library policy
(`Name`, `RootPath`, `GlobalLibraryPath`, `DeleteUnusedTypeVersionFromLibrary`
setters), user group create/delete, the language setting for imports, and the
undocumented status initialization. Package completeness (for example SIMATIC
SD `.s7res` companions) is not checked. The directory is passed to V21 as the
absolute path below the root, because the
[15 October probe](vci-native-profiles-20261015.md) saw the native route reject
a relative `DirectoryInfo`. All five kinds are verified: workspace create/delete by
`local.access.qualify.rerun.20261028`, mapping add/remove and synchronize by
`local.vci_mappings.20261028` (export mode, one workspace in the root group).

## Conflict and preservation rules

- Package identity includes exact object selectors and relevant library/native
  identities. Equal names cannot silently substitute different objects.
- All required companion paths and roles remain explicit. Missing companions are
  incomplete packages; incomplete enumeration cannot prove deletion.
- Case/Unicode/path aliasing and multiply owned files cannot become safe imports.
  Unmanaged files remain in the raw revision and are counted separately.
  Package rows do not compare unmanaged-file changes; use the raw Git diff and
  file manifests for those. An empty package report is not whole-project equality.
- One-sided changes, identical two-sided changes, divergent edits, divergent
  additions and delete-versus-change cases remain distinct. No automatic merge
  or conflict resolution is performed.
- Original upload release does not invalidate retained mapping bytes. Reopening
  or downloading a report verifies its artifacts and reconstructs the full input
  chain from the pinned mappings and retained source bytes. A changed input fails
  closed. Discovery reports incomplete or damaged publications without replay.

Every report retains `nativeQualified=false`. Comparison also retains
`canSynchronize=false` and `resolutionApplied=false`, even with zero conflicts.
Neither Git equality nor a clean text comparison establishes compilability or
valid engineering behavior.

## Bounds and remaining work

Mapping JSON is limited to 1 MiB, 1,000 packages and 1,000 total expected files.
Raw revisions retain the existing 1,000-file / 64 MiB bounds. Complete reports are
limited to 16 MiB and downloaded in chunks of at most 64 KiB. Package publications
share the 128-record / 2 GiB store limits with Git records. No partial/truncated
report is published as complete. Summaries contain counts; exact large rows remain
available in the complete report artifact.

This delivery exposes the existing portable comparison model. The native mapping
lifecycle is verified as described under [native workspace changes](#native-workspace-changes);
connect mode, native companion/language preservation and library policies remain unexecuted.
Host branch operations, remote publishing and larger measured performance also
remain separate work; this increment does not complete all of D01 or the release.
See [verification](../verification/vci-package-workflow-20261016.md).
