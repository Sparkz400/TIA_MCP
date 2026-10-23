# Library synchronization observations — 23 October 2026

`library-synchronization.v1.schema.json` covers the copy/publication preview's
type-synchronization rows and effects. Explicit `sync-plan` and `sync-change`
modes include the existing operation checks and validate the following retained
relationships without opening referenced paths or invoking native operations:

- Every source release is captured as a dependency root. Each closure contains
  exactly its reachable dependency versions, with consistent repeated metadata.
  Every copied type is reachable from a selected type through dependencies of
  any of its releases, including older releases. Source versions are committed
  and usable, with exactly one highest-release default per type.
- Selected versions match their source defaults. The expected version set
  contains every source release and prior destination default; GUIDs and version
  numbers are unambiguous. A new destination type contains exactly the captured
  source versions. Total expected pins and the known minimum source/destination
  capture stay within the producer's 2,000-version bound.
- Default choice follows `SetOnlyHigherUpdatedVersionAsDefault`: initialize
  absent types, promote a newer source default, otherwise preserve the destination
  default and its observed metadata. Version comparisons use numeric System.Version
  component ordering. Force/delete flags, effect names and change flags must agree.
- Selected copy effects retain all source versions; the operation effect matches
  its kind, destination library and project. Returned type sets and exact
  GUID/version-number sets must match the preview, with the planned default.
  Returned metadata such as author/modified time is not assumed immutable.

This closes a specific local-validation gap: operation-only mode could accept a
result with an internally consistent default different from the approved plan.
The new explicit synchronization mode rejects that mismatch while preserving the
scope of older modes. Production native verification already checks this policy
and is unchanged by this increment.

## Evidence boundaries

The preview retains only the prior destination default, not its complete version
inventory. It cannot independently prove that every old destination release was
included in the approved union. `destinationBeforeInventoryVerified` therefore
remains false. The validator checks returned evidence against the recorded plan;
it does not independently resolve native identities or authorize execution.

Master-copy dependency captures come from a separate native scan and do not
populate `CopyTypes`. A master-only selection legitimately has an empty type
synchronization plan/result. Embedded master dependency observations are checked
for closure consistency but do not establish copied type sets or master content
preservation. `masterCopyContentVerified` remains false.

Results report `synchronizationPlanValidated` and `synchronizationResultBound`;
the latter is false when no result was retained. Native synchronization, native
execution, referenced-file reads, preview-hash recomputation and qualification
remain false. Compiler, save/reopen, recovery and broader preservation gates are
separate. No native engineering code changes in this batch.

## Verification

Evidence is under `artifacts/todo-sync-20261023/`, pinned in `verification.json`.

- All 166 library tests pass, including 150 observation tests and 22 new
  synchronization cases. Coverage includes initialization, promotion, preserving
  equal/newer defaults, altered effects, release collisions, old-release-only
  dependencies, missing/extra closure members, unavailable results, master-only
  copies, exact result binding and the known capture bound.
- The relocated CLI validates the new change fixture using only copied local
  schema/helper resources, without relying on the repository import path.
- Native replay validates 32 retained operation changes, four synchronization
  changes and eight embedded previews. These cover initial publication and a
  later version promotion. Structures are extracted and reserialized. The original
  lifecycle report remains unchanged at SHA-256
  `2b57f4e7be57321d0439068559b8dd53b47aa989c455d278a0aaaf5eb49b4880`.
- `fixture-source.json` pins the original response pointer and the distinct bytes
  of the derived checked-in fixture.
- Contract coverage and source bindings pass for 261 tools, 56 mutations and 335
  features. Only source inventory changes; no qualification is promoted.

HMI target previews, component plans/content pins, other operation effects,
post-update readbacks and native qualification remain open. Earlier full
aggregate/package reports describe their frozen inputs; this increment has focused
verification and does not claim a new deployment package.
