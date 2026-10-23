# Unified retained library preview validation — 23 October 2026

`preview-plan` and `preview-change` provide one explicit entry point for validating
retained library previews. Both include the existing operation contracts, effect
validation and the appropriate HMI, component, update or synchronization analysis.
Change mode also validates compiler evidence and its scope. Earlier envelope,
operation and specialized modes retain their documented scope.

The new `library-preview-effects.v1.schema.json` and cross-field checks cover:

- Exactly the selected source/destination resources and one final operation
  effect with matching kind, library and project. Additional effects must belong
  to that operation and appear in the producer's order.
- Selected dependency roots and complete reachable captures, consistent repeated
  metadata, and committed usable consumption dependencies. Maintenance may retain
  explicit unavailable in-work dependencies; these remain incomplete observations.
  Copy/publication uses the existing all-release synchronization checks.
- Create/archive/upgrade destination shape, original archive pins versus directory
  manifest scope, and separate owned document snapshots with matching hashes.
- Master-copy source identity and supplied selector observations, including exact
  paths, project/PLC/software-unit ownership, structurally parsed namespace paths
  and supported recorded object kinds. A group named `plc` or `namespaces` is not
  mistaken for a structural marker.
- Partial-state save intent, its failed-operation ID and original library
  checkpoint descriptor. Component import pins bind every captured dependency,
  content descriptor and optional oracle policy; no referenced file is opened.

The component, HMI and update checks now run through this shared entry point;
callers do not need to choose a specialized analysis mode for each operation.

## Evidence boundaries

These are consistency checks on serialized observations. Native selector
resolution, absence/collision checks, artifact/snapshot/checkpoint bytes, original
master-copy eligibility and native content preservation remain unverified. A
recorded output path does not prove it is absent or establish ownership. An
inventory or directory hash is not recomputed without its original capture.

Only selectors retained in the payload can be compared. Outer request selectors,
native uniqueness searches and complete lookup inventories are not retained here.
ASCII names are compared without case; exact non-ASCII matches are reported
separately. Other non-ASCII case comparisons remain explicitly unverified because
Python full Unicode casing is not .NET OrdinalIgnoreCase. A missing historical
namespace field is reported, while namespace selectors still bind the canonical
path. Project operations require a nonempty recorded project path.

`preview-change` checks compiler observations, including failure/truncation and
legacy name-only scope; it does not qualify execution. No mode authorizes apply,
replays mutations, recomputes producer fingerprints or promotes native evidence.

## Verification

Evidence is pinned under `artifacts/todo-preview-effects-20261023/verification.json`.

- All 260 library tests pass, including 33 new cases and all 18 operation kinds.
  Controls cover wrong operation targets, missing/extra effects/resources,
  dependency gaps, source/document/recovery pins, canonical selector ownership,
  namespace structure, specialized dispatch, compiler scope and relocated CLI
  loading. Old operation-only mode accepts the altered-effect control; unified
  mode rejects it. Original fixture failures remain retained: missing synthetic
  project context and an expectation that a schema-invalid null target should pass.
- Compatibility checks pass 90 serialized change-response occurrences and 180
  embedded preview occurrences across four original reports and ten operation
  kinds. These include repeated representations within reports, not 270 separate
  native operations. Original bytes and original failed/qualified outcomes remain
  unchanged; this adds serialized compatibility evidence only.
- Six previously captured current artifact-bound planner outputs pass the unified
  mode, and 18 altered-output controls refuse. Their outer native envelope fields
  remain explicitly synthetic. The earlier 245-check managed producer run was
  not repeated because the production planner and its test exporter are unchanged.
- Contract and source bindings pass for 261 tools, 56 mutation kinds and 335
  features; no qualification label changes. Whitespace checks pass.

This increment has focused evidence and is outside the earlier immutable package.
Complete post-operation preservation/readback, native acceptance and the broader
release obligations remain open.
