# Explicit Unified text-list set evolution

`hmi_text_list_generate_set` accepts `unified-text-list-set-request.v2` through
the existing committed-upload route. Supply exactly one `uploadRef` or retained
`recordId`. v1 requests and records keep their original fixed-identity behavior;
v2 starts from an exact successful retained v1 or v2 set.

The [v2 contract](../../contracts/unified-text-list-set.v2.schema.json) supports
the established Bool and integer-state paired YAML formats. It generates an
offline candidate set and an explicit effect preview. It does not execute a
rename, deletion, import, binding update, or other project operation.

## Explicit transition request

```json
{
  "schemaVersion": "unified-text-list-set-request.v2",
  "componentReviewId": "<revalidated-v7-Unified-review>",
  "previousRecordId": "<exact-prior-v1-or-v2-set>",
  "requireObservedDependencies": false,
  "retainOutputIds": ["manual"],
  "removeOutputIds": ["state"],
  "outputs": [
    {
      "outputId": "running",
      "previousOutputId": "run",
      "operation": "rename",
      "bindingId": "running-binding",
      "bindingAction": "rebind",
      "profile": "unified-bool-binding-text-list.v1",
      "listName": "RunningList",
      "template": {
        "fileBase": "Running",
        "textLibrary": "RunningTexts",
        "languages": ["en-US", "de-DE"],
        "defaultLanguage": "en-US",
        "falseText": {"en-US": "Stopped", "de-DE": "Gestoppt"},
        "trueText": {"en-US": "Running", "de-DE": "Läuft"}
      }
    }
  ]
}
```

Every predecessor generated output must be selected exactly once through
`previousOutputId` or `removeOutputIds`. Every output and binding declared in
the supplied current review must also be accounted for. Additional unselected
project content remains outside this scope.

| Operation | Exact review selection | Required interpretation |
|---|---|---|
| `add` | Generated `hmiResource`, `action: create` | `previousOutputId: null`, a new output ID, complete empty baseline/current sets, no before member, `bindingAction: preserve`, and `listName` equal to the review name |
| `update` | Generated `hmiResource`, `action: update` | Same output ID, file base, list name and text-library identity as the selected predecessor |
| `rename` | Generated `hmiResource`, `action: update` | At least one output ID, file base, list name or text-library identity explicitly changes |
| Removal | Generated `hmiResource`, `action: remove` | The review output ID equals the selected `removeOutputIds` entry; exactly one before-only binding with the predecessor binding ID/property; complete empty proposed set |
| Retained peer | `action: retain` | Complete nonempty current files; ordered peer membership, names and bytes unchanged from the predecessor |

For existing outputs the review `name` and `objectPath` describe the independently
captured **current** object. The separate `listName` describes the proposed
candidate. The captured current name must agree with the predecessor list name.
The exact predecessor after-component selection must equal the current review's
before-component selection; the HMI path and family remain fixed. These checks
also apply to a predecessor with zero generated outputs.

The v2 set may have **0–16 surviving generated outputs**. A remove-all result is
an explicit empty candidate set with removal previews and unchanged retained
peers. It does not remove any file or project object.

## Binding and type rules

`bindingAction: preserve` requires unchanged binding ID, property and exact
before/after member selection. `rebind` requires an explicit change to one or
more of those selections. The before member must still match the predecessor's
captured after member. Both before and after declarations must be captured and
must retain the same exact data type and generation profile.

The Bool profile requires `Bool`. The integer profile supports
`SInt`, `USInt`, `Int`, `UInt`, `DInt`, `UDInt` and `LInt` through the existing
[integer-state template](unified-integer-state-generation.md). Rebinding an
integer list to another member of the same exact type is supported; aliases,
implicit casts, width changes and Bool/integer conversion are not.

Numeric state additions, removals and changes remain explicit in `stateChanges`.
Mathematical value coverage stays separate from native enum or runtime behavior.
All proposed outputs share the same explicit language order and default.

## Baseline, current and proposed comparisons

Each existing generated output and each removal requires a complete baseline and
complete current pair whose filenames, byte counts and SHA-256 hashes match the
exact retained predecessor candidates. Manual changes, missing companions, stale
baselines and export variation conflict. The tool performs no automatic merge.

Each output records its independent baseline/current files, any supplied proposed
files, and the derived proposed pair. A caller may omit an independent proposed
pair with `proposed: []` and `proposedComplete: false`; this remains explicitly
`notSupplied`. If proposed files are supplied or declared complete, they must be
the exact complete generated pair. They are never silently ignored.

All proposed file, list and text-library identities must be distinct, including
case variants. Proposed filenames cannot collide with retained peer filenames.
Proposed list names cannot collide with a captured destination object outside
the explicitly evolved/removed outputs. A case-only rename of one output or a
whole-set swap is supported when final identities are unique and the exact
prior/current pairs still match. Cross-output filename reuse is enumerated in
`filenameTransfers`; it does not imply an executable temporary-rename sequence.

`effectsPreview` shows every add/update/rename/removal, its exact old file pair
and proposed pair, and any binding action. Existing-output effects are marked
`destructiveEffectPreviewOnly`. No old file is rewritten or deleted. Any
relevant conflict makes top-level `files` empty and publishes no candidates;
comparison descriptors and effect previews remain reviewable.

Retained peers remain fixed in v2. Adding/removing/renaming a manual peer or
declaring bindings on a retained peer is outside this profile. These boundaries
prevent generated-output evolution from implying ownership of unrelated files.

## Original evidence, lineage and publication

v2 owns its candidate pairs and transition record. It does not fabricate an
earlier single-output author record to represent a transition that those older
formats could not express. `previousRecordArtifact` links to the exact immutable
prior set. Mixed v1/v2 chains retain all original requests, component reviews,
saved project XML/model evidence, child authors from v1, candidates and peer
bytes.

Reopening by `recordId` reconstructs the result from those originals. Missing or
changed inputs, sources, candidate files, predecessor records or v1 child
authors prevent reopen. Rehashing a fabricated derived record or review does not
replace original evidence. All linked artifact bytes are downloadable through
`artifact_read`; independent exported-lineage checks cover mixed chains.

The final immutable v2 set record is the completion marker. Candidate publication
or capacity failure can leave unreferenced immutable artifacts, but cannot
complete a partial set or alter prior evidence. A later explicit resubmission of
the unchanged request can reuse those bytes. There is no automatic retry.

Both profiles, including removals, retain exact selected dependency observations
and same-scope bounded consumer paths. `requireObservedDependencies` refuses
unavailable or unmatched selected indices; it does not certify dependency
closure. Original consumer/dependency truncation and omissions remain visible.
Member enumeration retains unselected declarations and its existing
4,096-observation / depth-16 bounds.

Limits remain 4 MiB request, 32 proposed/preserved files, 64 MiB total bytes,
16 MiB retained record, 16 predecessor-set levels, a shared 128 MiB declared
lineage-input budget and 512 MiB store retention. Native execution, qualification,
ownership, post-import preservation, complete component-member coverage and
complete consumer/dependency coverage remain false.

Reproducible evidence:
[set evolution verification](../verification/hmi-set-evolution-20261020.md).
