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
Host-owned directory. Workspace creation, mapping edits and synchronization remain
unimplemented. The tool is compiled against the V21 Public API only and is not
natively qualified.

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

This delivery exposes the existing portable comparison model. Native mapping
lifecycle, export/import, directed synchronization, native companion/language
preservation, compilation, reopening and checkpoint recovery remain unexecuted.
Host branch operations, remote publishing and larger measured performance also
remain separate work; this increment does not complete all of D01 or the release.
See [verification](../verification/vci-package-workflow-20261016.md).
