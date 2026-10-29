# Unified integer-state generation

`hmi_text_list_author` accepts `unified-text-list-author-request.v3` with profile
`unified-integer-state-text-list.v1`. This extends the existing retained offline
authoring workflow with one explicitly mapped numeric text list. Its files use
the existing paired `TextLists` / `TextLibrary` YAML authoring format; this is
not a new native export format or a native import qualification.

The strict request, record and receipt contract is
[`unified-text-list-author.v3.schema.json`](../../contracts/unified-text-list-author.v3.schema.json).
The earlier [Bool generation and v1 authoring](unified-text-list-authoring.md)
contracts remain supported.

## Exact supported scope

Select one generated `hmiResource` create/update output and one explicit binding
from a retained `hmi-component-review-request.v7` review with a Unified destination.
The captured original after-member declaration, binding `declaredHmiType`, and
template `dataType` must agree exactly.

| `dataType` | Minimum | Maximum |
| --- | ---: | ---: |
| `SInt` | -128 | 127 |
| `USInt` | 0 | 255 |
| `Int` | -32768 | 32767 |
| `UInt` | 0 | 65535 |
| `DInt` | -2147483648 | 2147483647 |
| `UDInt` | 0 | 4294967295 |
| `LInt` | -9223372036854775808 | 9223372036854775807 |

Aliases, enum inference, `ULInt`, bit strings, floating point, implicit
conversions and native numeric/runtime representation are unsupported.
`LInt` bounds are parsed as exact signed 64-bit JSON integers; fractions, strings,
nulls and values outside that domain refuse. New candidates also need every state
bound within signed 32-bit: V21 imports larger values as 0, so `UDInt` states above
2147483647 and `LInt` states outside signed 32-bit refuse with
`HmiTextListValueOutOfRange` before anything is published. A full `UDInt` or `LInt`
domain therefore cannot be listed. Records published earlier still reopen.

Each template contains 1–256 states. A state has a unique ASCII identifier,
`SingleValue` with equal bounds or an explicit inclusive `Range`, a default flag,
and one translation for each declared language. Bounds must fit the selected
type and intervals must not overlap. Open ranges, ambiguous identifiers,
missing translations and multiple defaults refuse. The state identifier also
serves as its text identifier. The authored list's name comes from the selected
original output; its range type is `Decimal`.

## Request

Upload UTF-8 JSON through `artifact_upload_begin/chunk/commit`, then call
`hmi_text_list_author` with the resulting `uploadRef`. Replace the review ID and
selection IDs below with the exact retained selections.

```json
{
  "schemaVersion": "unified-text-list-author-request.v3",
  "profile": "unified-integer-state-text-list.v1",
  "componentReviewId": "<retained-v7-record-sha256>",
  "outputId": "states",
  "bindingId": "operating-state",
  "previousRecordId": null,
  "retainOutputIds": ["manual-script"],
  "template": {
    "fileBase": "OperatingStates",
    "textLibrary": "StateLibrary",
    "languages": ["en-US", "de-DE"],
    "defaultLanguage": "en-US",
    "dataType": "Int",
    "requireFullTypeCoverage": false,
    "states": [
      {
        "id": "Stopped",
        "type": "SingleValue",
        "fromValue": 0,
        "toValue": 0,
        "isDefault": false,
        "translations": {"en-US": "Stopped", "de-DE": "Gestoppt"}
      },
      {
        "id": "Running",
        "type": "Range",
        "fromValue": 1,
        "toValue": 10,
        "isDefault": false,
        "translations": {"en-US": "Running", "de-DE": "Läuft"}
      }
    ]
  }
}
```

`valueCoverage` records the exact type bounds, state count and every unlisted
inclusive interval. `requireFullTypeCoverage: true` causes a retained conflict
when any interval is unlisted. With `false`, the same omissions remain explicit
gaps. A default entry never proves the native handling of unlisted values.
Full mathematical coverage also does not verify native numeric semantics.

## Regeneration and preserved peers

Initial generation requires a complete empty current file set, empty baseline
and no predecessor. Regeneration requires the exact predecessor `recordId`,
complete current and baseline pairs with the predecessor's exact filenames,
hashes and byte counts, and matching before-component/member lineage.
The new after member may have an explicitly selected new name.

The supported update can add, remove, translate or change the intervals of states.
`stateChanges` lists each identifier with its `added`, `removed`, `changed` or
`unchanged` status and exact before/after entries and translations. Native consumer
compatibility stays false. File, single-list and text-library identities must
stay fixed; integer type conversions are refused.

A v1 Decimal author record can be a predecessor when the exact candidate pair
and single-list identity match. The retained gap states that v1 proves candidate
bytes but has no component lineage. A Bool list cannot silently become a Decimal
list. Existing v1 and v2 requests and retained results keep their old behavior.

Changed current/baseline bytes, missing companion files, manual or unknown
selected ownership, mismatched member/type/lineage and filename collisions
produce a retained `conflict` with no generated files. There is no automatic
merge, output deletion or output rename.

Every selected `retainOutputIds` peer must be an explicit `retain` output.
Its exact supplied current bytes are included in `preservedFiles`. Peer filenames
cannot collide with either generated file, including case variants. This proves
only retained supplied bytes, not project-wide or post-import preservation.

## Dependency coverage

The numeric record selects both exact before/after member scopes, plus original
same-review incoming consumer roots reaching those members. It examines every
original dependency association touching those roots, even when an association
was omitted from the review's bounded dependency-impact page. The report includes
each original association index/row and coverage for relevant roots and associated
owners/targets.

Definite `different` or `conflicting` associations suppress both candidates.
Unavailable observations, missing associations and repeated unassociated indices
stay visible as gaps. Unrelated contradictions do not automatically block this
bounded selected output. `allObservedSelectedIndicesMatched` describes only
the selected supplied indices. An observed empty list differs from an unavailable
observation; neither establishes complete native dependency closure.

`sourceImpactCoverage` and `sourceConsumerCoverage` retain the original limits
and omissions. No transitive dependency closure, foreign-snapshot identity join,
complete consumer coverage or native identity is inferred.

## Retention and limits

After upload release or Host restart, call the same tool with `recordId`.
The store revalidates original request bytes, saved PLC/model evidence, the
component review, predecessor chain, source/peer bytes and independently
regenerated candidates before returning the exact retained result. Rehashing
a changed derived record or changing a source chain cannot bypass that comparison.

Inherited limits include 4 MiB author request input, 32 languages, 16,384 characters
per translated string, 8 MiB per candidate, 16 MiB retained report, 16 predecessor
steps, a 128 MiB cumulative declared source-file/request budget and a 512 MiB
store bound. The v3 state count adds its tighter 256-state limit.

`nativeExecutable`, `nativeQualified`, `preservationVerified`,
`consumerCoverageComplete` and `ownershipVerified` stay false. This route performs
no TIA import, project mutation, compilation, runtime action or physical operation.

Focused evidence and reproducible commands:
[numeric generation verification](../verification/hmi-numeric-generation-20261020.md).
