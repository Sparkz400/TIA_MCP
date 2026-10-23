# VCI manifest and conflict planning

The pure VCI package implements the offline parts of VCI-00 and VCI-01 in the [implementation plan](../plans/expansion-plans/vci-and-git.md). It captures caller-supplied observations and compares complete packages. It does not inspect a directory, call Siemens, invoke Git, create objects, synchronize, resolve conflicts, or establish native support. All remaining feasible routes remain release obligations.

Implementation: [models](../../src/TiaMcp.Core/VersionControl/VciModels.cs), [comparison](../../src/TiaMcp.Core/VersionControl/VciComparison.cs). Contracts: [manifest](../../contracts/vci-manifest.v1.schema.json), [conflict report](../../contracts/vci-conflict-report.v1.schema.json). The C# guards enforce additional semantic constraints beyond JSON Schema.

VCI-03 now also has a separate [controlled local Git runner](vci-git-runner.md), with explicit process-fixture opt-in. Its local commit/revision receipts do not promote these supplied observations to native qualification, and shared durable-job/Host integration remains required.

The subsequent [native profile audit](vci-native-profiles-20261015.md) verifies 12 genuine scratch exports and records the first preservation limits: native `Equal` accompanies missing multilingual comments in selected text profiles, and no `.s7res` was emitted. Those observations do not relax this model's complete-package checks or admit synchronization.

## Inputs and identity

`VciManifest.Capture(workspaceId, snapshot, packages, files, enumerationComplete)` consumes supplied bytes in memory and hashes them with SHA-256. It copies the expected-file lists and retains immutable file evidence; returned JSON is a deep copy. Callers must supply a stable inventory during capture, identify links/reparse entries truthfully, and observe directory containment and concurrent changes in a future filesystem adapter. This pure layer cannot detect false assertions about the source inventory.

Every package references the exact captured project, PLC, scope, object path and kind through `EngineeringObjectReference`. Its observation must match the manifest snapshot. One manifest cannot mix projects. Snapshot source hashes describe captured evidence, not a Siemens revision or a guarantee that the live project is still unchanged. Historical comparisons bind three separate snapshots and compare exact object identity across them. They do not require historical snapshot IDs to be equal.

A profile records the explicit native format token, object kind, evidence hash, origin (`authoredFixture` or `nativeExport`), completeness and complete expected-file list. Extension recognition validates a declared package family; it never discovers native format support. Supported structural families are:

| Family | Required observed roles |
| --- | --- |
| `plcSimaticSd` | `source` `.s7dcl` and `resources` `.s7res` |
| `unifiedScript` | `script` `.js` and `metadata` `.yml` |
| `nativeDocument` | One or more `document` files |
| `libraryDocument` | Document/source, exactly one `libraryVersion`, nonzero type/version GUIDs; an SD source also requires its resource companion |

Additional observed files use `auxiliary` and participate in hashing. Missing expected companions remain explicit package issues. A profile cannot silently claim an unmanaged file. Multiple packages claiming the same destination or exact object produce ownership conflicts. Library version changes are content changes; library type changes are identity conflicts.

Relative paths use forward slashes. Rooted paths, traversal, streams, Windows device names, control characters, invalid Unicode, trailing periods/spaces and links are rejected. Case/Unicode-equivalent inventory paths and file/directory collisions are rejected. Required paths whose spelling differs from an equivalent observed destination become ownership issues. Hard bounds are 20,000 files/expected entries, 1,000 packages, 1 GiB supplied bytes and 255 characters per relative path. Callers may lower bounds.

## Comparing observations

```csharp
var expected = new VciExpectedHashes(baseline.Sha256, project.Sha256, workspace.Sha256);
var report = VciComparison.Compare(baseline, project, workspace, expected);
var json = report.ToJson();
```

The three manifests must identify the same explicit workspace lineage. `expected` must come from the reviewed observation binding: recomputing it after an unintended change defeats the stale-binding check. A hash mismatch is `stale`; an incomplete enumeration or profile is `unknown`. Unknown versus observed object content/native identity cannot silently become equality.

Complete packages classify as unchanged; changes only in the project or workspace; identical changes on both sides; divergent changes; additions on either/both sides; deletions on either/both sides; or delete-versus-change. Identical changes are not conflicts. Divergent additions and delete-versus-change are conflicts. An absent package only has deletion/addition meaning when enumeration is complete. Priority is stale binding, identity conflict, ownership conflict, unknown observation, missing companion, then content classification. Even an empty result retains global incomplete-enumeration/stale issues.

Package content hashes include exact object identity, observed native object identity, native format, library GUID/version, optional observed object content hash, and every expected path/role/raw byte hash/size. Opaque native status and observation IDs remain separate from content equality. A green native status never overrides a byte difference. No state grants permission to overwrite: `canSynchronize`, `resolutionApplied`, `nativeQualified` and manifest `targetProbed` remain false.

## Validation and remaining admission

The [standalone checks](../../tests/TiaMcp.VersionControl.Tests/Program.cs) use an [independent conflict matrix](../../fixtures/vci/conflict-matrix.json) and exercise companions, identity, unmanaged ownership, traversal/link/case collisions, stale bindings, immutable results, budgets and 1,000 packages. Run with an isolated output directory:

```powershell
dotnet run --project tests/TiaMcp.VersionControl.Tests/TiaMcp.VersionControl.Tests.csproj --artifacts-path artifacts/expansion-implementation-20261015/vci/build-03
```

The [native request packet](../../artifacts/expansion-implementation-20261015/vci/native-request.json) is non-executable until a disposable fixture, exact profiles, native owner/admission and recovery evidence exist. Installed public metadata establishes route declarations only. Initialization and culture-enum documentation mismatches remain unresolved; no native synchronization was attempted. External Git fetch/push/credentials/remotes remain outside the native bridge. The [validation receipt](../../artifacts/expansion-implementation-20261015/vci/validation.json) records the tested source and emitted schemas without promoting native qualification.
