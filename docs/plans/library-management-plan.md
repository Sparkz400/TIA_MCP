# TIA library management implementation plan

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

Status: implementation in progress; full release acceptance remains open. Date: 8 October 2026. Initial native platform: TIA Portal V21. This document retains the complete intended scope and acceptance gates. See [the implementation guide](../guides/library-management.md) and recorded verification (`artifacts/library-implementation-verification.json`) for delivered operations and the exact profiles tested; an item appearing in this plan does not establish implementation or qualification.

The user requested a stop at the latest checkpoint. Continue from the prioritized [remaining-work TODO](library-management-todo.md); retain this document as the complete acceptance specification.

The feature will let a user supply a Siemens or company library package, inspect its contents, select exact components and versions, and add or update them in an existing TIA project through a previewed, recoverable workflow. It also covers maintaining reusable libraries: creating types and master copies, developing and releasing versions, comparing uses, publishing library archives, and validating PLC and HMI integration.

The complete outcome includes both consuming libraries and authoring them. ZIP extraction alone, opening a library, or copying a PLC block is an intermediate milestone. General HMI support requires separate Classic WinCC and WinCC Unified adapters and qualification.

## Product outcome and terminology

A representative request is: “Use this Siemens ZIP to add MotorControl version 1.2.0 and its required data types to PLC_1, create an instance and call, then place and connect its supported HMI faceplate on the selected screen.” The assistant resolves the package and exact targets, obtains only missing choices, prepares a reviewable plan, and executes within the user's authorized scope. An import request does not authorize unrelated replacements, a project-wide update, or changes to a shared source library.

The feature must distinguish these operations:

| Operation | Meaning |
| --- | --- |
| Open a global library | Make its contents available for inspection without adding program objects to the project. |
| Copy types into a project library | Add the selected type versions and necessary dependencies to the project's reusable component store. |
| Instantiate a library type | Create an eligible engineering object in the chosen PLC or HMI target from a specific type version. A PLC library type instance and an FB instance DB are different objects. |
| Use a master copy | Copy a supported root object into an eligible composition. The root may be a supported group containing children. Master copies do not provide the same native version lifecycle as types. |
| Integrate a component | Create or adapt calls, instance DBs, tags, connections, screen placement, and bindings according to an explicit mapping. |
| Update a use | Change existing type instances through the native library update mechanism and verify the resulting versions and affected scope. |
| Author a library | Create reusable elements, edit supported type documents, test and release versions, and publish an archive. |
| Pin a version | Record an exact desired native version and verify it in this bridge's operations. A bridge lock manifest does not prevent someone changing the project in TIA directly. |

TIA project libraries and global libraries support reuse, with types and master copies serving different purposes. Native type versions are separate from Git history of source exports. Git integration is not required to deliver this feature. [Siemens library basics](https://docs.tia.siemens.cloud/r/en-us/v20/using-libraries/library-basics)

## Scope and delivery boundaries

The full planned scope is:

- Project libraries; user global libraries; inspection and consumption of accessible corporate and Siemens system libraries; management of explicitly selected writable user libraries.
- Native global library files with their complete directories, native library archives, and ZIP wrappers containing one or more candidates and accompanying documentation.
- PLC code-block types, PLC data types, dependencies, exact version identities, standard program and eligible software-unit destinations, and eligible master-copy compositions.
- Classic WinCC and WinCC Unified faceplate/type workflows, supported HMI master copies, screens and resources, tags and PLC bindings, and native compile/persistence verification.
- Library folder organization, metadata, type/version comparison, usage search, version selection, update previews, authoring, release, explicit default changes, archive export, and guarded cleanup.
- Local and SSH/VM execution with explicit transfer into the TIA host, durable operation status, recoverable failures, and an evidence-based support catalog.
- Reusable integration recipes for PLC-only and combined PLC/HMI components. Recipes are declarative data validated by the bridge, not executable scripts from a package.

The following boundaries remain explicit:

- V21 is the first native adapter. Opening an older library through a supported upgrade is different from providing a V17/V19 server. A newer library is never silently down-converted.
- Objects that the vendor cannot expose or instantiate through a documented route receive a specific unsupported result. They are not transformed by editing private TIA databases or stripping protection.
- Safety elements and drive/hardware packages are classified and inspected where accessible. Mutation needs its own eligible typed adapter and qualification; this project does not establish safety acceptance or physical commissioning.
- Example projects and loose SCL/XML sources found inside ZIPs are identified as distinct candidates. A compatible example project can be retrieved/opened as a separate owned copy and its project library inspected through this subsystem. Importing an unlinked block or source requires an explicitly selected source-import or type-authoring subplan. Older example-project upgrades require a separately qualified project route. Preserve those distinctions in the user interface and reports.
- Physical PLC/HMI download, physical runtime control, unrestricted HMI design, arbitrary vendor executable installation, and unattended downloads from authenticated websites are separate capabilities. The package workflow begins with a supplied file or already completed transfer.

No release may advertise “all TIA library objects.” Publish the exact object families, target profiles, operations, and limitations. A missing optional installation can block a test run; it cannot turn an untested promised adapter into a completed milestone.

## Existing code to reuse and gaps to close

| Current component | Reuse | Required extension |
| --- | --- | --- |
| TiaMcp.Host and BridgeClient | MCP stdio, versioned IPC, serialization, terminal handling of uncertain worker outcomes | Library tools, upload contracts, operation progress and cancellation at safe boundaries. |
| ArtifactTools and ArtifactStore | Bounded outbound chunks, hashes, configured artifact root | Inbound package transfer, immutable staging, quotas, source manifests, and export of library packages. Current artifact_read only downloads from the host. |
| change_prepare/apply/status/restore | Preview hashes, project checkpoints, journals, compile/readback and explicit recovery | Changes involving libraries without an open project, multiple resources, multiple input files, HMI compilation, library snapshots, and durable restart reconciliation. |
| ObjectIdentity and object_resolve | Exact project, PLC, software scope and object paths | Separate library/type/version identities, HMI target identities and ownership/session generations. A native library type is not the existing PLC “type” selector. |
| DependencyRules and analysis tools | Existing block/interface/call evidence | Native type/version dependency graphs, library use information, HMI references and explicit unknown dependencies. Text mentions alone do not establish a library dependency closure. |
| ProjectIndexStore | Bounded metadata pages and authenticated cursors | Library/HMI snapshots, deterministic ordering, revision/invalidation rules, and usage indexing. |
| FeatureCatalog and contracts | API, implementation, qualification and target availability separation | Granular library/HMI feature IDs, schemas, profiles and evidence. Existing library.types and hmi.engineering entries are backlog. |
| Packaging and SSH relay | Exact binary manifests and tested remote transport | Package upload/download, library operation recovery, HMI prerequisites, and new schemas/fixtures in bundles. |

The current engineering worker references Base, Step7 and Safety assemblies. HMI support needs separately resolved installed WinCC and WinCCUnified components. Vendor assemblies must remain installation-resolved and excluded from release bundles. The existing single-project change record and process-local change dictionaries do not already provide a durable transaction across project and global-library writes.

## User workflows

### Supply and inspect a package

1. Accept an uploaded artifact or an explicitly supplied path on the TIA machine. Explain which machine owns the path in every transfer response.
2. Copy or receive bytes into a new managed staging location, calculate SHA-256, and finalize an immutable package identity.
3. Inspect the archive within extraction limits; return candidates, relative paths, inferred formats and versions, documentation, and reasons for any rejection.
4. If several libraries or examples exist, present the candidates. Select one from the user's named request or ask a single focused question. Never pick the first archive by directory order.
5. Open the selected native library in a managed read-only session. Retrieve archives into a new directory. A supported upgrade uses a separate working copy and produces a distinct manifest and report.
6. List types, versions, state/default metadata, master copies, folders, dependencies, and available target operations with completeness information.

A PDF, README, source comment or filename inside the package is reference material. It does not authorize running programs, deleting files, changing target scope, or submitting credentials. Bundled executables and scripts are inventoried, never automatically executed.

### Add components to an existing project

1. Resolve the exact project copy, PLC/software unit or HMI device, destination groups and selected source identities.
2. Specify whether the operation only copies to the project library, also instantiates engineering objects, or additionally integrates calls and bindings.
3. Resolve the dependency closure and intended versions. Preview every included dependency and project object affected by native synchronization.
4. Resolve conflicts and required mappings. An unresolved required connection or incompatible object stops preparation before target mutation.
5. Prepare an immutable plan with source hashes, target revisions, intended names/numbers, native operation order, compilation scope, and validation requirements.
6. Validate on an owned scratch copy when the operation is new, includes complex dependency updates, or requests stronger pre-apply verification. Scratch validation is an explicit mutating operation on the copy; it is never disguised as a read-only inspection.
7. Revalidate, checkpoint, apply once, compile every affected device, verify native readback and library links, save, and write durable results.
8. Report added/reused/updated/skipped objects, resolved versions, remaining integration work, compiler findings, test evidence and recovery locations.

Reusing an existing correctly linked version should be a reported no-op. Merely having an object with the same display name is insufficient to reuse or overwrite it.

### Update installed components

1. Compare the requested source release against the lock manifest and current native project state.
2. Enumerate uses and dependent types; classify additions, interface changes, implementation changes, version conflicts, and unresolved coverage.
3. Distinguish updating the project library from updating its instances in selected devices. Preview both scopes explicitly.
4. Compute the native update scope. If Siemens can update only a broader scope than requested, expose that scope before apply; do not silently update peer devices or other instances.
5. Resolve caller and HMI binding changes through explicit mappings. Report DB layout/start-value changes separately from runtime-retained data, which this offline operation does not read or preserve by implication.
6. Checkpoint all writable resources, execute, inspect actual type/version GUIDs and use links, compile, and run the selected regressions.
7. Update the lock manifest only after the corresponding native outcome is verified and saved.

An exact requested downgrade or older-version assignment is enabled only after its native strategy passes the version-selection fixtures. If it cannot be supported, return VersionSelectionUnsupported with the available choices. Never substitute a newer version while reporting that the requested version was installed.

### Develop and publish reusable components

1. Create or choose a writable working library/project copy and exact source objects. Capture their dependencies and native eligibility.
2. Create types or master copies using native APIs; keep type creation separate from copying an existing type identity.
3. Create/edit the native working version, modify supported PLC or HMI documents, maintain required test instances, and run compile and validation.
4. Compare the new version to its released predecessor, including dependencies, interfaces, metadata, languages, and expected behavior.
5. Release with an explicit native version number and release note, honoring dependency release rules. Do not infer semantic compatibility from the number alone.
6. Set the default version only when part of the request. Releasing, setting default, synchronizing libraries and updating project instances are separate effects.
7. Publish into the selected writable global library or a new archive. Record source and resulting native identities and byte hashes.
8. Reopen the published archive in a fresh owned session and instantiate representative components into a new consumer project before declaring the package reusable.

Project-library authoring may be required for particular native document APIs; publish its verified results to a global library afterward. Library text, supported metadata and translations travel with the component. An external shared library is not overwritten as an incidental effect of editing a project.

### Organize and retire library content

Support folder creation/rename/move and eligible metadata edits with the same prepare/apply workflow. Before deleting a type, version, folder or master copy, inventory dependencies and uses, including default-version status. Default cleanup proposes only items proved unused within the declared inspected scope. Unknown use is not proof of no use. Never enable native options that delete older versions or force conflicting replacements as an implicit import default. Preserve a recoverable library snapshot before every destructive library change.

Expose native DoNotUse and SetForUpdate metadata where applicable. Changing these flags is an explicit version/type-maintenance action, not an import side effect. A type marked DoNotUse should remain inspectable; a new use requires the declared product policy and an explicit resolution. Harmonizing project paths/names with library structure also requires a separate diff because it can move or rename existing objects.

## Package intake and transfer

Implement intake in the .NET host/core layer without starting Siemens software. A native .alXX file is part of a library directory; uploading only that file must not be accepted as a complete library unless its required companion files are present. Prefer a native archive or a ZIP containing the complete directory for transfer. Native .zalXX retrieval belongs to the Siemens adapter, not an assumed generic rewrite of its internal format.

Use server-issued upload IDs, a fixed expected byte count and SHA-256, bounded chunks, explicit offsets, and a commit operation. A duplicate chunk at the same offset is accepted only when its bytes match. Gaps, changed chunks, incomplete uploads and final hash mismatches cannot create a usable package. Upload status may resume byte transfer; it never resumes a native mutation. The host chooses all storage paths under its configured intake root.

Proposed initial configurable limits are 1 GiB compressed input, 4 GiB total expanded bytes, 20,000 entries, nesting depth 3 for ordinary wrapper archives, and 200 returned candidates per page. Validate these defaults against real Siemens samples in the intake milestone. Count expanded bytes while streaming; do not trust archive headers. A high compression ratio is diagnostic, while absolute byte/entry/depth limits are enforced. Limit increases must be explicit configuration and recorded in the intake result.

Reject traversal and rooted paths, Windows drive/UNC escapes, alternate data streams, device names, reparse points/symlinks, case-insensitive path collisions, conflicting Unicode/path normalization, overwritten entries, and corrupt or encrypted ZIP content that the intake adapter cannot read. The resolved output of every entry must remain within the new extraction root. Extraction never follows links and never overwrites existing data. Check available disk space, bound elapsed work, and remove only incomplete directories demonstrably owned by that upload.

Keep the original package, sorted per-file hash manifest, extraction manifest, candidate list, source provenance, and supported readme/license references. A hash establishes byte identity, not publisher authenticity. Record a supplied source URL as provenance without treating it as a verified signature. Preserve redistributable test packages separately from third-party packages that must be obtained by the operator.

Explicit release/garbage-collection operations may remove unreferenced staging data. Retain packages referenced by plans, lock manifests, active sessions or recovery journals. Never evict the only recovery copy. Storage retention, quotas and expiry must be visible through status queries.

## Native library access and compatibility

The API discovery milestone must establish an operation matrix against the installed V21 assemblies and documentation before implementing each route. Record exact assembly/version, object family, native signature, eligibility, side effects, and a minimal passing/blocked fixture.

Required native areas include global library open/retrieve/upgrade/save/archive, folder access, type/version and master-copy traversal, native dependencies and uses, comparison, selected type synchronization, object creation, project updates, version editing/release/discard, default selection, supported document access and cleanup.

Compatibility evaluation combines declared package information with actual native observations:

- TIA major version/update and supported upgrade path.
- STEP 7 and WinCC product/runtime family, installed engineering options and native license failures.
- CPU family/firmware, instruction eligibility, language, optimized access, software-unit scope and namespace.
- HMI family, device/runtime version, faceplate schema, screen/container support and supported resources.
- Protection, read-only library state, project access and required dependencies.

An API method's existence is not target qualification. Native service absence, missing software, a protection restriction, a failed fixture and a feature not yet implemented must remain separate states. Never upgrade the user's existing project to make a library import fit without a separately scoped project-upgrade operation.

Open global libraries read-only by default; upgrade only managed copies. Keep ownership records for each handle. Closing the bridge releases only handles it owns and does not close a user-owned TIA process or silently save an already-open shared library. library_close closes clean handles only. A dirty library requires a prepared library_save or native library_discard_version action as appropriate. An explicitly requested discard of a wholly owned scratch working copy may use a narrower recorded cleanup path, but it must retain failed/unknown-operation evidence and must not discard another user's work.

Read-only archive retrieval still creates a managed directory: record an intake/lifecycle receipt with the source hash, unique output claim and ownership before the native call. An interrupted retrieval leaves an incomplete candidate that cannot be used until verified. This staging receipt does not imply that a target project was changed.

### Verified V21 API foundation

The following API families were checked against the installed V21 PublicAPI XML. They are implementation entry points, not completed native qualification. Compile adapters against the actual installed assemblies; documentation examples can contain older names or signatures.

| Area | Native entry points and constraints |
| --- | --- |
| Library access | GlobalLibraryComposition.Open, OpenWithUpgrade, Retrieve and RetrieveWithUpgrade; read-only access through OpenMode.ReadOnly. Open library information objects for eligible system/corporate libraries. V13 and earlier require an intermediate supported upgrade rather than an assumed direct conversion. [Opening libraries](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/opening-libraries) |
| Persistence | GlobalLibraryComposition.Create and UserGlobalLibrary.Save, SaveAs, Archive and Close. Archive saved state; preserve and account for unsaved work before checkpointing. [Archive and retrieve](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/archiving-and-retrieving-a-library) |
| Identity and versions | FindType(Guid), FindVersion(Guid), Versions.Find(Version), version Guid/VersionNumber/IsDefault, Dependencies, Dependents and FindInstances. Native LibraryTypeVersionState values are InWork and Committed; released maps to Committed. Dependency access on an in-work version needs error handling. [Type versions](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/accessing-type-versions) |
| New PLC objects | PlcBlockComposition.CreateFrom(CodeBlockLibraryTypeVersion) and PlcTypeComposition.CreateFrom(PlcTypeLibraryTypeVersion), with documented path options. Select and verify a concrete released native version. Existing-instance collisions are not an overwrite strategy. [Type version creation](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/accessing-type-versions) |
| Master copies | Eligible target composition.CreateFrom(MasterCopy), and MasterCopyComposition.Create/CreateFrom for creation/copying. The documented project route accepts one root object, including an eligible root group, not arbitrary multi-root bundles. [Create from master copy](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/create-an-object-from-a-master-copy) |
| Comparison | CompareToLibrary and supported type/version/master-copy CompareTo methods. Retain native comparison details and coverage alongside supported semantic diffs. [Comparing libraries](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/comparing-libraries) |
| Synchronization and updates | UpdateLibrary, UpdateProject and UpdateCheck. Documented update scopes include PlcSoftware and HmiTarget; arbitrary per-instance selection is not established. Simple updates use the last released version; advanced default/update options need explicit selection. [Project update](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/updating-the-project) |
| Type authoring | Type/version compositions CreateFromDocuments; use the project library. Do not invent a generic create-type-from-existing-FB method. Establish a supported export/document/test-instance route for each family. [Create from documents](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/creating-new-type-from-document?contentId=oHYkC7VEW2IysI6BcOIjEw) |
| Working versions | Edit operates in the project library; STEP 7 uses Edit(LibraryTypeInstanceInfo), while eligible HMI versions use Edit(). Discard follows native rules. [Editing a version](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/editing-library-type-version?contentId=GCPl6M~rBqBsVv6hhNtmhQ) |
| Release | Release takes the native dependency mode, System.Version and release metadata. Validate eligible working state and dependency effects before changing to a released version. [Releasing a version](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/releasing-library-type-version?contentId=i2gNw3vLIjn~lnYEsMtN8A) |
| Document export | GetSupportedExportFormats and ExportAsDocuments; document export is project-library-only. Preserve library version information files where supported. [Export as documents](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/exporting-library-type-version-as-document?contentId=wOh1SGzxD5Zbqx8K7ZTVzw) |

Installed reference files are Siemens.Engineering.Base.xml, Siemens.Engineering.Step7.xml, Siemens.Engineering.WinCC.xml and Siemens.Engineering.WinCCUnified.xml under the configured V21 PublicAPI directory. Record their assembly/document identities in the M0 evidence rather than embedding this workstation's path in runtime logic.

For exact updates of existing uses, M0 must test a managed source copy with the desired released version set as default, then the documented ForceUpdateMode options. ForceSetAnyUpdatedVersionAsDefault, SetOnlyHigherUpdatedVersionAsDefault and NoDefaultVersionChange have distinct effects. Combine them deliberately with DoNotDelete and an explicit structure-conflict policy. Verify all resulting dependency defaults and target use/version GUIDs; do not assume this strategy establishes downgrade support merely because the enum exists. [Advanced library update options](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/updating-structure-during-library-update?contentId=0E1t9ZSCYMxS_VIlA3L1LQ)

## Identity and reproducibility model

Add versioned plain-data contracts with these records:

| Record | Required content |
| --- | --- |
| PackageManifest | packageId, schema version, original SHA-256/length, host identity, original label, sorted member hashes, extraction decisions, candidates and provenance. |
| LibraryRef | bridge library ID, source package/candidate, canonical working path, observed native identity when available, project/global/system kind, open mode, owner session generation, original and upgraded revisions. |
| LibraryElementRef | library identity/revision, native type GUID when applicable, version GUID and native version value, element family, display name, folder path, state/default status, and content evidence. |
| MasterCopyRef | exact owning library/revision and native identity where exposed; otherwise qualified folder/name plus an observed revision. Do not fabricate a persistent Siemens GUID. |
| TargetRef | exact project copy and session generation, PLC/software-unit or HMI device/screen/composition, object identity, native family and observed revision. |
| DependencyGraph | typed nodes and edges, required/version relationships, actual uses, source evidence, traversal completeness, opaque/protected nodes and conflicts. |
| LibraryChangePlan | all source refs/hashes, writable resources/base revisions, explicit selection and mappings, dependency closure, ordered effects, expected native scope, checkpoint strategy, validation and immutable plan hash. |
| LibraryLockManifest | desired and observed versions/GUIDs, imported object paths, package lineage, dependency versions, binding/recipe revision, last verified project revision and report links. |
| LibraryOperationReport | durable stage events, actual native actions and outcomes, affected resources, compiler results, validation flags, outputs/hashes and recovery state. |

Use native type/version GUIDs for lineage and native version values for selection. A display name and a version string are not identity. Do not assume SemVer ordering or compatibility rules; retain Siemens' observed version representation. Same name/different GUID, same version label/different GUID or content, and same native identity with divergent observed content are separate conflicts.

The bridge manifest is reproducibility evidence, not a replacement for the native project library. Re-read native state before applying critical operations. A reopened or externally modified project can drift from its manifest. Return drift explicitly and require a new plan when relevant state changes.

## Dependency resolution and conflict policy

Construct the graph from native type dependencies/uses and supported PLC/HMI reference inspection. Classify edges as native-confirmed, parsed-supported, inferred or unresolved. Only confirmed and validated supported edges authorize automatic dependency inclusion. Unknown dependencies may permit inspection, but must block a mutation whose scope or integrity cannot be established.

Resolve versions before modifying the target. Topologically order dependencies where appropriate and handle native dependency cycles as explicit groups if the vendor supports them. Native instantiation can copy dependencies or affect defaults automatically; inspect before/after project-library state and include these effects in the plan. No mutation may claim an exact diff if required dependency closure was truncated.

| Conflict | Default behavior | Explicit resolution possibilities |
| --- | --- | --- |
| Exact identity/version already present and matching | Reuse and report no-op | Revalidate current content and links. |
| Same name, different native type identity | Block | Choose another destination/name when native semantics permit, or intentionally replace with a separately previewed operation. |
| Same type, requested new version | Preview dependency and instance changes | Copy version only, update selected supported scope, or retain current use. |
| Same version value with different version GUID/content | Block | Select the authoritative source or perform a separately qualified force replacement; no silent merge. |
| Target has a newer version/default | Preserve it until policy is selected | Keep current, import side-by-side if native semantics permit, or use a qualified exact-version assignment strategy. |
| Local object detached from its library type | Show independent local changes | Keep local object, compare and explicitly replace, or create a separate use. Do not claim it can automatically merge into the type. |
| Block number/name or HMI resource collision | Block unresolved collision | Explicit rename/free-number allocation or exact reuse backed by equivalence evidence. |
| Missing/protected dependency or missing option | Block affected operation | Supply dependency, install the required option through an independent workflow, or omit the selected component. |
| Update affects more instances/devices than requested | Block current scope | Widen the reviewed plan or use a supported narrower route. |

Force-update, delete-unused-version and default-changing options are explicit plan fields. The ordinary import path disables them unless necessary effects have been explained and requested. Never use a broad native “update all” to satisfy a request for one component without exposing the actual affected scope.

## PLC import and integration

Support code-block and PLC data-type library versions through typed native compositions. Preserve native library links, programming language, namespace, comments/translations, relevant attributes, number policy and instance relationships. An import must not flatten a versioned type into unlinked XML and still report it as a native library instance.

Required PLC operations are copy-to-project-library, instantiate selected version, master-copy creation in eligible block/type/tag/group compositions, version update, use inspection, and explicit integration. FB integration can create an instance DB, bind typed parameters and insert a call in an exact caller/network using the existing qualified editing routes. FC return/output bindings and FB single/multi-instance choices need separate validation. New multi-instance declarations require caller/interface and dependent instance-DB impact checks.

Input/output mapping must validate direction, data type, dimensions, string capacity and referenced object scope. No guessed absolute I/O address or invented hardware connection is allowed. Where an insertion language/instruction form is unsupported, import may complete as “component added; integration pending” only when that reduced scope was requested. A request to fully integrate cannot be declared successful with missing required bindings.

Test both standard groups and supported software units, exact namespaces, duplicate short names, pre-existing UDT dependencies and nested FB dependencies. Treat protection and safety metadata as native constraints. Opaque implementation may still support qualified copying if the vendor allows it, but behavioral/semantic coverage remains explicitly limited.

## HMI adapters and combined PLC HMI integration

Implement Classic WinCC and WinCC Unified as separate typed adapters with shared plain-data contracts. Resolve the optional vendor modules lazily so a PLC-only installation can still start and use library inspection/PLC functions. Emit an actionable prerequisite result for an unavailable HMI family.

For every proposed HMI family, establish which operations are available as library types, native master copies, supported export/import, or no public route. A format fallback is a different operation with separate preservation and qualification requirements; it cannot silently replace native type semantics.

| Area | Required planning and verification |
| --- | --- |
| Faceplate types and versions | Native identity/state/dependencies, interface parameters, nested faceplates where exposed, events/scripts and actual supported release/update route. |
| Faceplate instances | Exact target screen/container, selected type/version, dimensions/position, parameter and event mappings, readback of the actual referenced version. |
| Screens and templates | Target resolution, intended sizing/adaptation, child objects, navigation, template references, object names and preservation of unrelated screen content. |
| HMI tags and connections | Exact PLC connection, tag/data type, symbolic access where supported, update cycle and exposed attributes; no connection guessed from a matching name. |
| Resources | Text/graphic lists, images, translations, scripts/events and other dependencies that the specific native object requires. Preserve unsupported payloads only through a qualified native route. |
| PLC UDT to HMI binding | Required PLC data type version, HMI representation, field mapping, direction/type compatibility, and changed-interface diagnostics. |
| HMI owned data types | Native HMI UDT identity/version, nested members, dependent faceplate interfaces, library lifecycle and actual runtime representation, independently of PLC UDT mapping. |

Classic screen/faceplate imports can require pre-existing faceplate types and valid event/interface names. Screen resizing and omitted attributes can be native behavior; qualify and report any normalization rather than assuming byte equality means nothing or accepting arbitrary visual differences. [Classic faceplate screen import](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-an-hmi-device/screens/importing-a-screen-with-a-faceplate-instance)

Unified HmiFaceplateContainer, ContainedType, Interface and event bindings have their own API. Its documented UDT assignment is an integration entry point; exact version/path assignment still requires native verification. Do not assume Classic master-copy compositions exist in Unified. [Unified UDT binding](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/screenitems/controls/faceplate-container/assigning-udt-to-faceplate-container)

Qualify Comfort Panel and RT Advanced separately for Classic; inspect Basic and RT Professional as distinct capability profiles rather than inheriting support from a documentation title. Qualify Unified Comfort Panel and Unified PC separately. Use at least one compatible and one deliberately incompatible device/runtime version per advertised family. A Classic faceplate does not become a Unified faceplate through ordinary import; the target-family compatibility check must reject that mismatch. [Unified instance requirements](https://docs.tia.siemens.cloud/r/en-us/v21/configuring-screens-rt-unified/configuring-faceplates-rt-unified/using-faceplates-rt-unified/creating-a-faceplate-instance)

The inventory matrix also includes HMI UDTs, nested faceplates, script modules, styles/stylesheets, graphics or dynamic SVGs, palettes and text lists when exposed by the installed product. A recognized family does not imply available create/edit/export APIs. Test the intended operation for each advertised cell; keep dynamically constructed script references unresolved when their actual target cannot be proved.

A combined integration recipe declares selected PLC/HMI components, supported target profiles, dependency versions, caller and instance placement, PLC/HMI tag mappings, connection selection, screen placement, expected compiler outcomes, and test cases. Recipe validation runs before native work. Binding changes resulting from a type update require a fresh preview.

Native HMI qualification requires compile, native readback, save/reopen, resource/reference checks and visual inspection of representative screens. Runtime qualification separately checks events, scripts, navigation, dynamic properties and PLC communication on the matching HMI runtime. A successful engineering import is never labeled runtime-tested. HMI runtime testing is a release gate for advertised working integrated examples, not a new unrestricted production runtime-control tool.

## Change execution and recovery

Introduce a common resource-aware change record behind the existing change tools. Keep existing PLC change behavior backward compatible. Library-only plans must not require SelectProject; project-library and PLC/HMI changes bind to the exact project resource, while writable global libraries are separate resources with separate snapshots.

Use an immutable plan plus an append-only or atomically replaced durable journal. Persist and flush each “about to invoke native mutation” event before dispatch, then persist the observed completion. A process crash can then leave outcome_unknown at a specific stage rather than an empty report. Write the initial journal before opening a mutating native workflow. Hash and validate every input file in the plan, not just the existing single InputPath.

Native transaction restrictions exclude operations needed by this workflow, including global-library open/close, project lifecycle/save and compilation. Use short permitted transactions only where verified; the surrounding workflow relies on checkpoints and durable reconciliation. [Transaction restrictions](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/transaction-handling)

The execution sequence is:

1. Resolve all handles anew from exact identities; verify session generation, ownership, permissions, source manifests and target revisions.
2. Acquire the appropriate native exclusive access and recheck critical state inside it. Establish deterministic resource ordering to prevent bridge-side deadlocks; shared-library contention must be reported.
3. Save the user's current project state only under the already explicit checkpointSave contract, create and verify its recovery archive, and archive/snapshot every pre-existing writable global library before the first mutation. Record unsaved-state disposition for each resource. A plan with several resources cannot proceed with only some required checkpoints.
4. Apply the recorded native steps once. Track actual dependencies/default changes and verify each expected target rather than trusting a native return alone.
5. Compile all affected PLC/HMI targets and validate library consistency. Keep save, compile and library lifecycle calls outside native transaction scopes where the vendor forbids them.
6. Verify object/version links, interfaces, bindings and preservation, then save the intended resources and record persisted outcome.
7. Reopen in a fresh owned instance for qualification and optionally for high-assurance user operations; retain exact hashes and native identity evidence.

Proposed durable states are prepared, validating, validation_failed, checkpointing, applying, compiling, verifying, saving, applied, outcome_unknown, recovery_required, recovering and recovered. Reports distinguish requested work completed from validation levels reached. A compile failure after import is a changed project requiring recovery or an explicitly planned corrective operation, not an automatic rollback claim.

Persist a unique idempotency claim for the engineering host identity, authenticated Windows user, operation namespace and user-supplied key before native mutation. Store operation ID and immutable plan hash as claim values, not as additional key components that would permit conflicting reuse. Coordinate claims and operation ownership across host/worker processes sharing the same artifact root with a durable lock/claim mechanism, including owner-crash reconciliation. A matching repeat returns the durable result or unresolved status; the same key with different plan content is rejected even after restart or through another concurrent host. After restart, query/reconcile with native readback and recorded resources. Never replay uncertain mutations. Rebind newly prepared operations to the new session generation; do not reuse saved native proxies or authorize work solely because a TIA PID matches.

Cancellation before dispatch is clean. After native dispatch it is a request to stop at the next safe boundary; a timeout/disconnection remains uncertain until reconciled. Progress reports cannot claim cancellation of a Siemens call that does not support cancellation.

New resources require a recorded previouslyAbsent baseline instead of an impossible pre-change archive. This covers library_create, managed upgrade outputs, archive files and publication to a new path. Reserve a unique owned output, verify absence immediately before creation, and reject any intervening occupant. Recovery retains or quarantines created outputs with their receipts; it restores archives only for resources that existed before the operation. Cleanup can remove newly created content only through an explicitly owned cleanup operation after recovery evidence is retained.

Recovery retrieves each recorded project/library checkpoint into a new owned location, verifies hashes, native identities and representative content, and retains the changed copies. Cross-resource restoration is not atomic; report the status of each resource and any required relinking. Recovering a global-library file does not automatically reverse project updates or offline DB layout changes. No recovery closes or replaces an unrelated user-owned project/library.

## Proposed MCP surface

The names below are proposed contracts, not existing tools. Keep reads distinct from operations that write staging data or native objects, and use conservative MCP annotations. Return bounded structured data plus artifact references for large reports.

| Tool | Purpose and important arguments |
| --- | --- |
| artifact_upload_begin | Create a managed upload for expected size/hash and a display label; return uploadId, chunk limit and expiry. |
| artifact_upload_chunk | Write bounded base64 bytes at an exact offset; report accepted range/hash. |
| artifact_upload_status | Read received ranges, remaining bytes and finalized artifact identity. |
| artifact_upload_commit | Verify full contents and publish an immutable artifactRef. |
| artifact_upload_abort | Abandon only the specified bridge-owned incomplete upload. |
| library_package_stage | Copy a finalized artifact or explicit host-local package into managed storage, extract/classify, return packageId and report. No TIA project mutation. |
| library_package_inspect | Page package candidates, manifests, compatibility hints and documents without executing native operations. |
| library_open | Open/retrieve one selected compatible candidate or exact existing library; default managed/read-only ownership. Upgrade requires a prepared library_upgrade change. |
| library_query | Discriminated read actions for libraries, folders, elements, versions, dependencies, uses, capabilities and status. Exact identity selectors and authenticated cursors. |
| library_compare | Compare exact source/target elements or native versions; return supported semantic differences and explicit opaque coverage. |
| library_validate | Run a prepared plan against an owned scratch target; report scratch mutations and independent validation levels. |
| library_close | Release a clean owned handle; dirty state requires the prepared lifecycle action or the explicitly bounded scratch-discard route. |
| change_prepare | Accept new library mutation kinds and a versioned typed payload; return plan/preview and hashes. |
| change_apply/status/restore | Apply once, inspect durable outcome and explicitly recover the full resource set. Preserve current callers. |
| artifact_read | Transfer generated archives, lock manifests and reports using the existing outbound mechanism. |

Proposed change kinds are library_create, library_upgrade, library_copy_elements, library_instantiate, library_update_project, library_integrate, library_create_type, library_create_master_copy, library_edit_version, library_release_version, library_discard_version, library_set_default, library_type_policy, library_organize, library_delete, library_publish, library_save and library_archive. Use tagged payload schemas for each kind; do not create a generic reflection-based native write endpoint. library_type_policy covers eligible DoNotUse/SetForUpdate changes. A coordinated import/integration request can compile these typed actions into one reviewed resource-aware plan.

Example preparation payload, with illustrative returned IDs and paths:

    {
      "kind": "library_instantiate",
      "payload": {
        "schemaVersion": 1,
        "source": { "libraryId": "returned-library-id", "revision": "observed-revision" },
        "selection": [{ "typeGuid": "observed-type-guid", "versionGuid": "observed-version-guid" }],
        "target": { "projectPath": "D:\\Projects\\Machine\\Machine.ap21", "objectPath": "exact-returned-destination" },
        "dependencyPolicy": "includeRequiredExact",
        "conflictPolicy": "rejectUnresolved",
        "integration": { "mode": "instantiateOnly" },
        "validation": { "compile": true, "verifyLinks": true }
      }
    }

Return error codes for PackageInvalid, PackageLimitExceeded, PackageAmbiguous, UploadIncomplete, PackageChanged, LibraryVersionUnsupported, LibraryUpgradeRequired, LibraryReadOnly, LibraryIdentityMismatch, LibraryConflict, DependencyUnresolved, DependencyConflict, TargetIncompatible, VersionSelectionUnsupported, IntegrationMappingRequired, PreviewStale, NativeScopeTooBroad, CompileFailed, PreservationMismatch and OutcomeUnknown. Include actual resource/stage, retry safety, completed effects and recovery evidence where applicable; do not discard structured native failure details.

## Implementation work breakdown

Each work item should be a reviewable change with contract tests and a specific exit condition. Shared contract decisions precede parallel implementation; native work is serialized per TIA session.

| Milestone | Work items | Exit condition |
| --- | --- | --- |
| M0 Native feasibility | LIB-001 API and object-family matrix; LIB-002 exact version/update and scope experiments; LIB-003 Classic/Unified probes; LIB-004 upgrade/lifecycle/recovery probes | Checked-in evidence-backed capability design with native signatures, minimal fixtures and unresolved blockers. No broad capability claim from reflection alone. |
| M1 Contracts and intake | LIB-010 manifest/identity/plan schemas; LIB-011 bounded upload; LIB-012 extraction/classification; LIB-013 package status/retention; LIB-014 deterministic fixtures | Package intake works without TIA; complete-directory and native-archive cases recognized; malformed and adversarial inputs rejected; local/remote hashes match. |
| M2 Read access | LIB-020 session-owned library registry; LIB-021 retrieve/open and upgrade preflight; LIB-022 paged element/version/dependency/use queries; LIB-023 comparison; LIB-024 capability/index integration | Native read scenarios pass without modifying source libraries or target projects; missing scope remains explicit. Upgrade execution waits for M3. |
| M3 Durable changes | LIB-030 resource-aware plans/checkpoints; LIB-031 durable idempotency/stage events; LIB-032 session/revision guards; LIB-033 crash reconciliation; LIB-034 multi-resource recovery and managed upgrade execution | Injected failures at every stage retain useful outcomes; no unknown write replay; original content recoverable in new locations and newly created outputs accounted for. Existing engineering suites remain green. |
| M4 PLC consumption | LIB-040 exact type/UDT instantiation; LIB-041 qualified master-copy families; LIB-042 native dependency/conflict planner; LIB-043 call/instance/tag integration; LIB-044 lock manifest | ZIP to an existing scratch project passes import, precise diff, compile, use/version verification, save/reopen and recovery. Source package unchanged. |
| M5 Version lifecycle | LIB-050 update impact and scope; LIB-051 exact/default update strategy; LIB-052 create/edit/test/release; LIB-053 default/discard/delete rules; LIB-054 library synchronization and publish/archive | Two release generations pass consumer update, incompatible interface rejection, drift/conflict cases, archive reopen and new-consumer roundtrip. |
| M6 Classic HMI | LIB-060 optional module/prerequisites; LIB-061 type/master-copy matrix; LIB-062 faceplate/screen/tag/resource and authoring adapters; LIB-063 PLC/HMI binding recipes; LIB-064 visual/runtime and publish/reconsume fixtures | Selected Classic profiles pass engineering, authoring/release/publication, bindings, preservation, recovery and advertised runtime examples. |
| M7 Unified HMI | LIB-070 optional module/prerequisites; LIB-071 native faceplate/type access; LIB-072 container/binding/resource and authoring adapters; LIB-073 supported transfer-route probes; LIB-074 visual/runtime and publish/reconsume fixtures | Equivalent Unified requirements pass using Unified-native semantics. Missing Classic parity remains explicitly listed, not emulated without evidence. |
| M8 Product qualification | LIB-080 combined package scenarios; LIB-081 scale/cancellation; LIB-082 VM/SSH upload/import/recovery; LIB-083 immutable release and diagnostics; LIB-084 docs/support matrix; LIB-085 complete regression gate | Full release acceptance below passes on exact recorded builds, with operator instructions and retained evidence. |

Dependencies: M0 and M1 establish contracts; M2 and M3 can then progress in parallel; M4 requires both; M5 builds on M4. HMI feasibility starts in M0, and M6/M7 adapter work can proceed in parallel after shared contracts stabilize. Combined integration and final release depend on both HMI tracks. Existing simulator provisioning is a separate dependency for PLC runtime acceptance, not a reason to stop intake/read-only/engineering work.

Recommended work ownership is one core/transport engineer, one PLC/library engineer and one HMI engineer, with a shared native qualification owner. Roles can be sequential on a smaller team. Give calendar estimates only after M0 establishes HMI and exact-version feasibility; track effort and blockers per LIB item instead of promising a date based on unverified native APIs.

## Planned files and modules

- Host: add LibraryTools and upload tools beside existing ArtifactTools; preserve EngineeringTools signatures and expose new typed kinds through the existing change workflow.
- Core: add package/transfer storage, archive policy, manifest hashing, library plain-data models, dependency/plan validation and durable operation records. Pure modules must not reference Siemens assemblies.
- Worker: add focused library session/access/query/change/authoring partials or services; separate PLC, Classic and Unified adapters; integrate handle ownership, session generations, capability discovery, fingerprints and index invalidation.
- Worker project/bootstrap: lazily resolve optional installed WinCC modules, probe prerequisites and keep all Siemens references non-copying.
- Contracts: add package-manifest, library-identity, dependency-graph, library-plan, lock-manifest, operation-result and integration-recipe schemas with explicit versions and bounded arrays/strings.
- Tests: add pure intake/planner/journal suites, fake native adapter fault tests, native PLC/library fixtures, distinct Classic/Unified fixtures and packaging/remote cases.
- Fixtures: add authored source and native fixture-generation manifests under fixtures/libraries, including version pairs, master-copy roots, combined PLC/HMI recipes and invalid packages. Record native generation prerequisites and do not require redistribution of restricted Siemens packages.
- Scripts: add qualify_library_access, qualify_library_import, qualify_library_versions, qualify_library_authoring, qualify_library_hmi_classic, qualify_library_hmi_unified and qualify_library_remote runners, with explicit binary paths and new artifact directories.
- Documentation: maintain this plan, add the eventual library workflow/support reference, link it from PLAN/README, and generate concise current support summaries from the feature/evidence registry.

## Test strategy and fixture matrix

Use independent expected manifests and authored behavior vectors. Tests must detect a wrong native result rather than simply reproduce the implementation's own selection logic. Pure checks never mark native functionality qualified.

| Test level | Required coverage |
| --- | --- |
| Pure package and transfer | Chunk retries/resume, wrong offsets/hashes, quota/depth limits, traversal/ADS/reparse escapes, collisions, malformed archives, full library directories, multiple candidates, interrupted extraction and retention ownership. |
| Pure identity and planning | Exact/native GUID matching, copied libraries, duplicate names, version conflicts, dependency cycles, unresolved/protected references, deterministic plans, bounded queries and stable hashes. |
| Fake native execution | Error before/after every checkpoint/mutation/compile/save; partial returned objects; implicit dependencies; cancellation; disconnect; crash between dispatch and journal completion; concurrent-host idempotency collisions; previously absent output recovery; all recovery states and no replay. |
| Native library reads | Project/global/system/user libraries, read-only/shared/locked states, versions/defaults, dependency/use discovery, comparison, retrieval/upgrades and source preservation. |
| Native PLC consumption | FB/FC/UDT, nested UDT/array/string types, dependent FBs and instances, SCL/LAD/FBD/STL where eligible, master-copy supported roots, standard/software-unit targets, namespace/name/number conflicts. |
| Native version and authoring | Create/edit/discard/release, dependency release, explicit defaults, exact selected versions, existing newer defaults, intended downgrade behavior, same-version divergent identities, publish/retrieve and update scope. |
| Classic HMI | Eligible faceplates/screens/templates/tags/scripts/resources, missing type/interface/event dependencies, resizing effects, binding preservation, multiple device profiles, compile/reopen and visual/runtime checks. |
| Unified HMI | Faceplate type/container versions, interface/tag/UDT binding, scripts/events/resources, nested structures where eligible, target-version compatibility, supported transfer semantics and runtime checks. |
| Combined integration | PLC type+UDT+HMI faceplate, required connection and tag mappings, update changing a field, stale binding rejection, intended changes only, all affected compilers and behavior. |
| Remote and release | Interrupted/resumed upload, immutable package hashes on host/guest, Unicode/space paths, missing optional products, worker crash/reconnect/journal reads, recovery, source locking and exact bundle identities. |

Critical fixture cases are exhaustive: identity ambiguity, unresolved overwrites, exact version outcomes, protected/safety routing, archive escapes, partial mutations and replay prevention. Use representative/pairwise combinations for CPU/firmware/language/device variants and publish each actual combination. Do not multiply every axis into an unmaintainable claim of universal coverage.

Every supported mutation family must pass: baseline export/inventory; exact expected effect; compile where applicable; preservation of unrelated objects and languages; native library link/version readback; save and fresh reopen; checkpoint recovery into a new location; fresh reopen of the recovery; and matching recorded hashes. Behavior tests are additional for executable PLC/HMI examples.

Initial scale targets, subject to M0/M2 profiling, are a 10,000-element library metadata fixture, a 1,000-node dependency graph and a 100-component planned import. Record native capture time, cached paging time, peak memory, serialized bytes and checkpoint/storage cost. Use at most 200 items/page and configurable native traversal budgets. Truncation must be visible and must block a plan that requires complete closure. Define measured performance budgets before release; no latency guarantee is inferred from pure helper timings.

Vendor acceptance includes at least two independently structured Siemens distributions, one PLC-focused and one containing supported PLC/HMI components, plus a compatible older library requiring upgrade. Record their actual source pages, prerequisites and hashes when selected; no particular package is assumed compatible in advance. Add negative fixtures for newer/incompatible libraries and unrelated ZIP content. Existing-project fixtures include unsaved user work and pre-existing compiler diagnostics; record those before a change so reports can distinguish inherited problems from newly introduced errors. Saving an imported result still requires the declared compiler acceptance policy, not merely “no additional errors.”

Library metadata snapshots remain bounded and session-local like the existing index. Persist package manifests, plans, lock manifests and evidence separately; do not turn short-lived ProjectIndexStore IDs into a permanent catalog. Reopening a library rebuilds native observations and invalidates old cursors. Every tool/change kind gets feature-catalog coverage, separate API/implementation/qualification/availability fields, and links to exact fixture results. Aggregate library.types and hmi.engineering statuses must not hide partial object-family coverage.

Create a single offline gate including current core, deployment/reliability, catalog/contract, index and new library suites. Native gates run on an ordinary authorized engineering user and exact binaries. Named-pipe sandbox failures are environment-blocked results, not native failures or passes. Keep runtime and HMI visual tests distinct from compile-only results.

## Full release acceptance

The full feature is accepted only when the following end-to-end scenarios pass for the declared release profiles:

1. Actual independently sourced Siemens PLC and supported PLC/HMI packages are staged, identified, opened and inspected without changing the original sources or existing project. Authored fixtures supplement these vendor acceptance runs; they do not replace them.
2. An exact released PLC FB and UDT dependency are imported into an existing scratch project; native type links and versions match the selection and all unrelated content is preserved.
3. A qualified master-copy root is instantiated in its correct destination; unsupported composite content is identified before project mutation.
4. A PLC component is fully integrated through an explicit call/instance/signal mapping and passes independently authored runtime tests.
5. An installed type is updated to the intended version; the preview identifies affected dependencies/uses, changed interfaces and actual native update scope; stale plans and unresolved conflicts are rejected.
6. The exact-version policy passes older/newer/default conflict fixtures. If the native API cannot support a requested assignment, the operation is consistently blocked and the published feature contract does not promise it.
7. A new reusable PLC type is created, edited, tested, released, published and used successfully in another project from the exported library archive. Every advertised Classic and Unified HMI authoring category passes its own create/edit/test/release/publish/fresh-consumer roundtrip; an unsupported authoring operation must be excluded explicitly from that category's contract rather than inferred from import success.
8. A supported Classic HMI faceplate and a supported Unified faceplate each pass their own import, placement, binding, version update, compile, persistence, visual and advertised runtime scenarios.
9. A combined PLC/HMI package is integrated and updated with explicit UDT/tag/connection mappings; a broken binding produces a failure with actionable impact evidence.
10. Library folder/metadata and retirement operations preserve dependencies/defaults, reject unsafe scope, and have verified recovery.
11. A forced worker termination or SSH loss during a mutation yields a durable unknown/completed outcome that can be reconciled without replay; all affected project/library resources can be recovered to verified new copies.
12. A fresh local and VM installation can run the documented ZIP-to-project workflow from an immutable release, with actionable diagnostics for missing components and no machine-specific development paths.

Acceptance evidence records exact TIA/update, CPU/firmware or HMI device/runtime, adapter and binary hashes, package/source hashes, native identities, operation inputs, checkpoint hashes, compiler messages, preservation checks, actual versions/bindings, runtime/visual evidence and cleanup state. Report packageVerified, engineeringVerified, preservationVerified, persistenceVerified, recoveryVerified and runtimeVerified separately.

A PLC-only intermediate release is useful and should be labeled that way. It does not close the full scope in this document. An unresolved required Classic or Unified adapter remains an open delivery item; a vendor-impossible operation requires an explicit documented product boundary rather than a passing test marked skipped.

## Decisions and technical risks to resolve first

| Question | Planned decision or experiment | Consequence |
| --- | --- | --- |
| Can existing instances be assigned the exact selected release? | Probe documented update/default options on isolated library copies and verify actual version GUIDs and collateral scope. | Enable only strategies with evidence; never implement private GUID rewriting or pretend latest equals selected. |
| Which HMI objects have native transfer/authoring routes? | Build separate Classic and Unified operation matrices from installed assemblies and native fixtures. | Determines adapter contracts, optional prerequisites and honest support boundaries. |
| How complete is native dependency/use discovery? | Compare native graphs against hand-authored cross-library, cross-device and nested fixtures. | Unknown required closure blocks automatic plans. |
| How can writable global libraries be checkpointed without collateral saves? | Probe ownership, lock, archive, save and reopen behavior on managed copies and explicit writable targets. | Defines resource-aware checkpoint and publication policy. |
| Which normalization is legitimate? | Retain raw native exports; establish narrow per-family comparison rules with negative tests. | No generic removal of GUIDs, bindings, comments or HMI attributes to make preservation tests pass. |
| What is required to use a downloaded component beyond import? | Model required hardware/options and author explicit integration recipes from component documentation. | Import and full functional integration remain distinct outcomes. |

Implement M0 and M1 first. Their deliverables make the subsequent work concrete: a verified native operation matrix, stable contracts, deterministic package fixtures, and a usable package inspector. They also prevent designing the rest of the feature around an API behavior that TIA does not provide.
