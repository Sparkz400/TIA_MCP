# Coordinated Unified text-list generation

This page describes the unchanged v1 contract. For explicit output additions,
removals, renames and same-type member rebinding from a retained v1/v2 set, see
[v2 set evolution](unified-text-list-set-evolution.md).

`hmi_text_list_generate_set` creates a retained set of **2–16 paired text lists**
from one supplied `hmi-component-review-request.v7` review with a Unified
destination. It combines the existing Bool and integer-state templates into one
preflight, one complete downloadable file set and one retained completion record.
It does not introduce another native file format.

The request, record and receipt are defined in
[`unified-text-list-set.v1.schema.json`](../../contracts/unified-text-list-set.v1.schema.json).
Individual successful outputs retain the existing v2/v3 author records, which
remain accessible through `hmi_text_list_author`.

## Select the complete declared output scope

Upload UTF-8 JSON using `artifact_upload_begin/chunk/commit`, then call
`hmi_text_list_generate_set` with `uploadRef`. Supply exactly one of `uploadRef`
and `recordId`.

```json
{
  "schemaVersion": "unified-text-list-set-request.v1",
  "componentReviewId": "<retained-v7-review-sha256>",
  "previousRecordId": null,
  "requireObservedDependencies": false,
  "retainOutputIds": ["manual-script"],
  "outputs": [
    {
      "outputId": "running-list",
      "bindingId": "running-binding",
      "profile": "unified-bool-binding-text-list.v1",
      "previousAuthorRecordId": null,
      "template": {
        "fileBase": "Running",
        "textLibrary": "RunningTexts",
        "languages": ["en-US", "de-DE"],
        "defaultLanguage": "en-US",
        "falseText": {"en-US": "Stopped", "de-DE": "Gestoppt"},
        "trueText": {"en-US": "Running", "de-DE": "Läuft"}
      }
    },
    {
      "outputId": "operating-state-list",
      "bindingId": "operating-state-binding",
      "profile": "unified-integer-state-text-list.v1",
      "previousAuthorRecordId": null,
      "template": {
        "fileBase": "OperatingState",
        "textLibrary": "OperatingStateTexts",
        "languages": ["en-US", "de-DE"],
        "defaultLanguage": "en-US",
        "dataType": "Int",
        "requireFullTypeCoverage": false,
        "states": [
          {
            "id": "Idle",
            "type": "SingleValue",
            "fromValue": 0,
            "toValue": 0,
            "isDefault": false,
            "translations": {"en-US": "Idle", "de-DE": "Bereit"}
          },
          {
            "id": "Operating",
            "type": "Range",
            "fromValue": 1,
            "toValue": 10,
            "isDefault": false,
            "translations": {"en-US": "Operating", "de-DE": "Betrieb"}
          }
        ]
      }
    }
  ]
}
```

Every output declared in that review must appear exactly once in `outputs` or
`retainOutputIds`. Every declared binding must be selected exactly once by a
generated output. Each generated output is one `hmiResource` create/update and
one exact member binding; the captured declaration must agree with the template.
Multiple outputs can explicitly reference the same source member. A member is
never selected merely because its name resembles an output.

This v1 profile excludes extra bindings on retained outputs. Retained outputs
are explicit byte-preservation peers, with `action: retain`, a complete nonempty
supplied current file set and no selected bindings. Unsupported output kinds,
output removal and additional unused bindings require a different review scope.
An omitted declared output or binding is a retained conflict, rather than
silently narrowing the set.

The templates retain their exact existing limits:
[Bool authoring](unified-text-list-authoring.md) and
[integer-state authoring](unified-integer-state-generation.md).
All outputs must use identical language order and default language. File, list
and text-library identities must be distinct across outputs, including case
variants. Generated and retained filenames also cannot collide. Translation
dictionary values remain exact even when language order changes consistently.

## Whole-set conflict handling

The tool evaluates all selections before publishing child authors or candidates.
A relevant conflict in any output suppresses **every** candidate and every child
author reference. The retained set still records its original request, exact
coverage, preserved peer references, gaps and conflicts.

Examples include:

- Manual edits to a generated current file, a mismatched baseline, or a missing
  companion file.
- Manual/unknown generated ownership, changed source type, mismatched source
  lineage, or a relevant original dependency contradiction.
- An unaccounted output or binding, differing language order/default, or
  conflicting file/library/list identities.
- Missing mathematical integer coverage when the numeric template explicitly
  requires full type coverage.
- Unavailable or unmatched selected dependency indices when
  `requireObservedDependencies` is true.

The tool performs no automatic merge, rename, deletion or native import.
`files` is empty and every `outputs[].authorRecordId` is null on conflict.

## Exact bounded coverage

`coverage.allDeclaredOutputsCovered` and `allDeclaredBindingsCovered` refer only
to the explicit supplied review. They never mean that every project object,
interface member or native consumer was discovered.

For both Bool and integer outputs, dependency selection includes the exact
before/after member scopes and nodes on their retained, same-scope consumer
paths. Every original dependency association touching those nodes is examined,
including associations omitted from the bounded dependency-impact projection.
Contradictions block the set. `unobservedScopes` identifies relevant path nodes
that have no selected dependency observation. Unavailable observations and
unmatched repeated indices stay visible.

An observed empty dependency list is distinct from unavailable evidence.
`requireObservedDependencies` requires agreement of the selected observed
indices; it cannot certify native or transitive dependency closure. Original
consumer and dependency-impact coverage records retain their limits, truncation
and omissions. The tool does not reconstruct omitted graph paths.

The record also enumerates up to 4,096 captured declarations across the selected
before/after component objects, to a maximum member depth of 16. Each observation
retains its original XML evidence and exact selected binding IDs. Unselected
members, unavailable objects and traversal truncation are explicit. This bounded
enumeration does not establish complete component-member coverage.

## Coordinated regeneration and legacy adoption

For regeneration, set `previousRecordId` to the exact prior set record and leave
every `previousAuthorRecordId` null. The prior set supplies all child predecessor
records. Output membership, binding IDs, profile, file/list/library identities
and integer data types remain fixed. The current and baseline pair for each
output must equal its original child candidates exactly. Before-component and
member selections must match the predecessor's after selections.

Supported updates include coordinated translations and explicit numeric state
additions/removals/range changes. Output additions/removals/renames, rebinding to a
different lineage, implicit type conversion and partial-set regeneration are
outside this v1 increment. Selected retained peers must keep the same ordered
membership, filenames and exact bytes as the previous set.

For the first coordinated set only, a selection may name an exact existing
v1/v2/v3 `previousAuthorRecordId`. Its complete current/baseline pair must match,
and the existing authoring lineage rules still apply. A v1 predecessor proves
candidate bytes but has no component lineage; that gap remains explicit. A
successful adopted set can subsequently use normal coordinated regeneration.

## Retention and publication boundary

Successful preflight publishes immutable child candidates, existing-format
child author inputs/records, then the final set record. The final set record is
the completion marker. A filesystem or capacity failure during publication can
leave unreferenced immutable artifacts; it cannot return a completed partial set
or rewrite earlier evidence. An explicit resubmission of the unchanged request
can reuse already published content-addressed bytes.

After upload release or a fresh Host restart, reopen using `recordId`. Reopen
revalidates original requests, saved project XML/model evidence, component
reviews, all predecessor sets/authors, exact peer/source bytes and regenerated
child candidates. An altered or missing child file prevents reopening the whole
set. Rehashing a fabricated derived summary, member map or review chain does not
make it valid.

Limits are 4 MiB request input, 2–16 generated outputs, 32 total
generated/preserved files, 64 MiB total candidate/peer bytes, 16 MiB retained set
record, 16 predecessor sets, a shared 128 MiB declared lineage-input budget and
512 MiB store retention. Existing author depth and per-file limits also apply.

Native execution, ownership verification, post-import preservation, complete
consumer/member coverage and dependency closure remain false. No TIA process,
native project operation, runtime connection or physical operation is invoked.

Evidence and reproducible commands:
[coordinated generation verification](../verification/hmi-coordinated-generation-20261020.md).
