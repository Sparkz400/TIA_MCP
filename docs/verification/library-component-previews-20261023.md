# Library component preview observations — 23 October 2026

Explicit `component-plan` and `component-change` modes validate retained PLC
integration plans alongside the existing operation checks. Combined apply remains
disabled. `library-component-preview.v1.schema.json` describes the selected version
exports, content policy, observed interface/tag mappings, proposed steps, caller,
instance DB and required recovery/qualification boundaries.

The validator now binds the following recorded relationships:

- Selected and dependency pins exactly cover a closed, root-reachable graph of
  committed usable PLC versions. The outer dependency capture matches every
  version's metadata. Root version class and name are suitable for the recorded
  FB call; this does not inspect the actual FB XML.
- Every content hash equals its artifact descriptor hash. Oracle hashes, policy
  identity, fixed property values and export options agree. Descriptor paths are
  absolute and supplied byte counts are exact integers within 4 MiB. Older
  descriptors without byte counts remain readable.
- Interface and requested mappings agree in name, order, direction and type.
  Recorded tag paths, owners, types and repeated observations agree. Safety tags,
  writable physical-input addresses and multiple writers to one captured tag
  refuse. Multiple input parameters may share a tag.
- The entire proposed typed graph is reconstructed from those observations:
  exact callee and instance, operands, connections, LAD rail/FBD enable, creation
  or insertion mode and selected caller. Import arguments, step order, fresh
  preparation requirements and the repeated component effect must agree.
- Caller mode/absence, instance name/path and selected PLC/compile target agree.
  Plan-only, no-replay and initial recovery-anchor boundaries remain explicit.

## What the observation cannot prove

Referenced XML files are never opened. `componentContentPinsBound` means repeated
descriptor/hash agreement; `componentArtifactBytesVerified` remains false.
Interface exports, native selectors and complete preservation remain unverified.
Content-policy strings and property values do not independently establish the
underlying source/oracle tree relationship.

Only the full inventory's hash is retained. The validator cannot recompute that
hash, prove complete collision absence, independently choose the first available
DB number, or resolve all native tag names. The provisional number remains an
observation requiring fresh native rechecking. Captured name comparisons check
ASCII case equivalence and exact non-ASCII characters; they do not claim full
.NET native name resolution. Python Unicode expansion must not merge distinct
names such as `Straße` and `STRASSE`.

Existing callers in current producer output retain `callerArtifact`. Historical
plans without it remain readable with `componentCallerArtifactRetained: false`.
Retention of that descriptor is distinct from verification of its bytes. An
insertion blocker remains valid planning evidence, not an executable installation.
Other operation effects, complete content/readback contracts, native installation,
compilation, save/reopen, recovery and runtime qualification remain separate.

## Verification

The immutable receipt is `artifacts/todo-component-preview-20261023/verification.json`.

- All 227 library tests pass, including 31 new component cases. The altered-call
  positive control still passes operation-only mode and refuses in component
  mode. Negative cases cover pin/descriptor/policy drift, dependency coverage,
  target ownership, incomplete mappings, wiring, aliases, caller modes, step
  ordering and execution claims. Relocated CLI checks use copied local resources
  without the repository Python import path. The original first run's test-fixture
  session mismatch remains retained; correcting that fixture resolves the error.
- All 245 managed component checks pass. An explicit test-only exporter runs the
  actual `BuildFromArtifacts` planner for six authored profiles: existing/standalone
  LAD/FBD, InOut and blocked insertion. Their emitted plans pass the Python
  validator, and 18 altered-output controls refuse. The outer preview and native
  coordinator fields are synthetic scaffolding, identified in the receipt.
- Eight retained previews from two of three inspected component-run reports pass with
  original source hashes unchanged. This is serialized compatibility evidence;
  it does not change the failed outcomes or qualification limits of those runs.
  The checked-in `component-plan.json` is reserialized from
  `artifacts/p17n/component-native-05/result.json`, pointer `/plan/journal/preview`.
  Original and derived hashes are recorded in `fixture-source.json`.
- Contract and source-binding validation pass for 261 tools, 56 mutation kinds
  and 335 features, without promoting qualification. Whitespace checks pass.

This increment has focused validation. Earlier full aggregate/package results
describe their frozen sources and do not include these later changes. No native
engineering dispatch, installation or release publication was performed.
