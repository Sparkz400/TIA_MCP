# PLC library-instance observations — 18 October 2026

Status: full offline validation passed; exact pushed commit and hosted-CI results
are recorded separately in `artifacts/plc-library-association-01/integration.json`.
Base: `80d432528ca9cb30170f06a89feda8bbc646f72d`.

The existing project-model block/type capture omitted PLC library-version
associations. It now retains a `metadata.libraryInstance` observation at the exact
existing object path. No new tool, native mutation, library traversal or name-based
association was added. Existing model byte limits and metadata revision hashing
include these observations. A separate partial coverage entry distinguishes this
from complete hierarchy, library, dependency or consumer coverage.
The feature catalog now marks `project_snapshot_capture` unverified for this
changed native capture route; its historical evidence IDs remain intact. The
other affected native model entries were already unverified.

The documented route is the instance's `LibraryTypeInstanceInfo` service and its
`LibraryTypeVersion`. V21 documents this service for FBs, FCs and PLC user data
types; an unlinked instance has no service. The helper checks the service's
instance against the selected object using native `Equals` semantics, which can
accept different wrappers for the same native object. Each service, back-reference,
version, type and GUID is read once. See Siemens [instance access](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-on-libraries/accessing-instances)
and [object equality](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/verifying-object-equality).
Documentation establishes the intended route, not installed SDK compatibility.

The detached record has schema `plc-library-instance-observation.v1`, a source
label and `nativeQualified: false`. Its status is one of:

| Status | Meaning |
| --- | --- |
| `linked` | The observed service points back to the selected object and supplies nonempty type/version GUIDs. |
| `noService` | The eligible object returned no instance service. |
| `notApplicable` | The object is outside the FB/FC/PLC-type route; no service was queried. |
| `unavailable` | Required service state or identity was missing, inconsistent or raised an engineering exception. |

Only linked observations contain GUID fields. Native error text is not copied
into records. Unexpected non-engineering exceptions propagate to the existing
capture caller; they do not become absent links. This is sequential observation,
not an atomic native snapshot, proof of runtime instance DB identity, export
correspondence or dependency closure. The helper cannot bound a blocking native
getter. Old snapshots remain unchanged and acquire no invented association.

## Focused evidence

The linked production helper passes 22 cases covering all three eligible kinds,
one-read capture, wrapper/native equality, absent services, unsupported objects,
missing/mismatched owners, missing type/version, empty GUIDs, seven native failure
points, unexpected exceptions and subsequent version changes. The project-model
suite passes 13 groups, including snapshot detachment, saved-review reopening,
exact-byte import and C01 retained workflow reopening after upload release.
C01 retains the original metadata while library identity, runtime instance and
dependency-completeness verification stay false. The old representative snapshot
remains usable without the new field.

The same helper and 22 cases run on the actual x64 .NET Framework runtime using
synthetic SDK shapes, with no Siemens assembly loaded. Removing the instance
equality check or the empty-GUID guard is tested in isolated mutation controls.
Exact logs, result counts and source/binary hashes are retained under
`artifacts/plc-library-association-01/`. Full frozen validation passes within the scope below.

## Remaining work

Compile and exercise the native callers with the Siemens SDK, including real
linked/unlinked FBs, FCs and PLC data types, protected objects, failures, scope
bounds and metadata verification after version changes. Retained observations
are supplied evidence when imported; their source authenticity remains unverified.

Next, connect exact supported original library exports and pinned versions to
the saved component observations, preserving prior C01 record compatibility.
Complete dependency/consumer mapping and authored generation/preservation. A
matching GUID pair or display name alone is not sufficient evidence for those
steps. No genuine HMI library-version export was found in the inspected fixtures;
ordinary screen XML must not be treated as one. The [full offline queue](../plans/offline-execution-20261016.md)
and all original 44-workstream obligations remain open.

## Full frozen verification

Both snapshots contain 2380 identical source files. The aggregate
passes all 44 managed suites, including 13 project-model groups, 22 linked capture
cases, 242 general checks, 207 PLC integration checks and 25 HMI model checks.
All 1,510 Python tests pass with one existing skip, together with 25 Explorer
model/client tests and all Host workflows. All 38 package/relocation stages pass.
The 1706-file package manifest is
`4d3da6adfd30603aaa806159253229a301714733273e91e3ab15c92ec35de8e5`.

Both runs retain 384 HMI MCP checks, four independently verified expected-effect
records and 416 prior-version checks over nine original C01 records. Five packaged
Explorer assets match the prior browser-tested assets; no new browser execution
is claimed. The new native capture callers remain source-reviewed only. Framework
execution uses the exact helper with synthetic native types, not a Siemens SDK.

All 112 historical evidence entries and their original hashes are preserved;
106 inputs remain unavailable. Original scope-reconciliation files and catalog
evidence IDs are unchanged. The snapshot-capture qualification status is lowered
to unverified for the changed native route. Only final docs differ from the frozen
sources. Full C01 and the remaining 44-workstream requirements remain open.

## Preparation for original-export association

The original-export audit verifies all four retained xp01 hashes. Both full V21
FB exports expose the same type/version GUID pair but differ in original bytes;
the default exports omit those GUIDs. Thirty independent profile preflight checks
cover the original files and malformed/ambiguous variants. This establishes a
supported starting fixture, not an implemented association or preservation proof.

Four actual v4 records from this validated package are retained unchanged with
their 18 original dependency files. All 72 section comparisons pass across two
clean Host lifetimes. These records prepare independent compatibility checks for
the next request version. Association-only review must not require an unrelated
Classic screen edit, and existing v1-v4 semantics must remain unchanged. Results
are in `export-profile-preflight.json` and `retained-v4/fixture.json` under the same
artifact directory. Production association, complete dependency/consumer mapping
and authored generation remain open.
