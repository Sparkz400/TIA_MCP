# Library HMI preview observations — 23 October 2026

The explicit `hmi-plan` and `hmi-change` validator modes add retained target-preview
checks to the existing operation contracts. `library-hmi-preview.v1.schema.json`
covers Classic instantiation, Unified internal tags, faceplate creation and
faceplate binding. Existing envelope and operation modes keep their prior scope.

The validator checks exact selected HMI/compile-owner identity, action, dependency
closure, source version metadata and effect-to-request agreement. Incomplete before
observations refuse. Classic version copies bind the source class/name/version;
master copies check the six supported composition classes. Captured root-object
and tag collisions refuse. Unified creation checks the selected screen, rectangle,
contained type and captured container names. Binding checks the complete captured
interface, ordered tag observations, exact selected link and requested data types.

Returned selected-version metadata must match the preview. Creation readback binds
the contained type and requested rectangle. Binding readback separately reports
whether all requested properties were captured and whether retained layout fields
match. Changed layout remains a valid observation with a false comparison result;
it never becomes a preservation claim. An absent result has no readback scope.

## Evidence limits

The serialized preview omits evidence needed for several independent checks:

- Native master-copy identity/content and source folder/namespace resolution.
- Native interface property types and complete connection evidence for bindings.
- All possible collisions: Classic scripts/templates/popups, other Unified screen
  item names, and Unified tag-table existence are not fully retained.
- Complete screen/resource/event/script/translation preservation and native
  compiler, save/reopen, recovery, runtime and visual acceptance.

Reports keep `nativeSelectorVerified`, `nativeInterfaceTypesVerified`,
`collisionAbsenceVerified`, `fullPreservationVerified` and
`targetFingerprintRecomputed` false. The original target fingerprint remains a
recorded pin. No referenced paths are opened and no native operation is executed.
Classic master results lack created-object name/content fields; their reported
scope is `master-link-metadata-only`. Internal tag results retain their separate
name/type/default-connection checks. Compiler evidence uses the existing compiler
modes independently.

## Verification

Evidence is recorded under `artifacts/todo-hmi-preview-20261023/` and pinned in
`verification.json`. All 196 library tests pass, including 30 new HMI-preview
cases. Coverage includes all four routes and six Classic master compositions,
altered identities/classes/version numbers, missing/duplicate dependencies,
capture truncation, placement/collision checks, shared tag bindings, UTF-16 text
bounds, changed layout, incomplete result readback and a relocated CLI running
without the repository import path. The checked-in binding fixture is explicitly
synthetic, with authored provenance in `fixture-source.json`.

Compatibility replay passes 32 retained operation changes, four synchronization
changes and eight synchronization previews. These are PLC/library observations,
not native HMI previews. The original lifecycle report remains unchanged at SHA-256
`2b57f4e7be57321d0439068559b8dd53b47aa989c455d278a0aaaf5eb49b4880`.
The earlier draft's scan of ten original HMI probe reports found zero target
previews; this absence does not establish native preview compatibility.

Contract and source-inventory validation pass without promoting qualification.
The preceding [full aggregate and package](todo-integration-libraries-20261023.md)
passed against its frozen source before this increment. This increment has focused
validation and relocation checks; it is not included in that immutable package.
Component plans, other operation effects, native selector observations and native
HMI acceptance remain open.
