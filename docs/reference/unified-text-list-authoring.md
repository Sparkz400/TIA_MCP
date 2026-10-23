# Offline Unified text-list authoring

`hmi_text_list_author` produces a downloadable pair of plain text-list candidate
files without Siemens products. Supply a committed `uploadRef` containing a
`unified-text-list-author-request.v1` request. To reopen the result, supply its
`recordId` instead. Exactly one selector is required. Reopening reconstructs both
candidates from the retained original request and checks every stored byte and
record field. Changed or invented results refuse.

Start with the [bilingual request](../../fixtures/hmi/unified-text-list/author-request.json)
and [strict contract](../../contracts/unified-text-list-author.v1.schema.json).
The existing [retained bilingual fixtures](../../fixtures/hmi/unified-text-list/README.md)
provide an independent structure reference; their historical native evidence does
not qualify newly authored files.

1. Upload the JSON request through `artifact_upload_begin/chunk/commit`.
2. Call `hmi_text_list_author` with its `uploadRef`.
3. Download each `files[].artifact` with `artifact_read`, saving under the returned
   `fileName`. Keep the original request and `recordArtifact` with the candidates.
4. For component work, upload the downloaded files as explicitly selected proposed
   files in [component/HMI review](hmi-component-review.md). Supply independent
   baseline/current files, exact destination, ownership, mappings and observations
   as appropriate. Authoring alone does not establish those associations.
5. Release transient uploads when retained results are confirmed. Reopen later by
   `recordId`; no upload or native worker is required.

The two output names are `<fileBase>.hmi.yml` and
`<fileBase>.TextLibrary.hmi.yml`. Output is deterministic UTF-8 without BOM, with LF
line endings and the `#Version: 2.0` marker. This authors new candidates; it never
parses, normalizes, merges or overwrites an existing supplied export.

## Component-bound generation and regeneration (v2)

The same `hmi_text_list_author` tool accepts a
`unified-text-list-author-request.v2` upload. Its single supported profile,
`unified-bool-binding-text-list.v1`, authors one plain Unified Bool text list from
an explicit binding in an existing v7 component review. The
[v2 contract](../../contracts/unified-text-list-author.v2.schema.json) is separate
from the unchanged v1 contract.

```json
{
  "schemaVersion": "unified-text-list-author-request.v2",
  "profile": "unified-bool-binding-text-list.v1",
  "componentReviewId": "<retained v7 component review ID>",
  "outputId": "states",
  "bindingId": "run",
  "previousRecordId": null,
  "template": {
    "fileBase": "States",
    "textLibrary": "StateLibrary",
    "languages": ["en-US", "de-DE"],
    "defaultLanguage": "en-US",
    "falseText": {"en-US": "Off", "de-DE": "Aus"},
    "trueText": {"en-US": "On", "de-DE": "Ein"}
  },
  "retainOutputIds": ["manual"]
}
```

The saved review must select a Unified `hmiResource` with action `create` or
`update`, declared generated ownership, and the exact selected binding. Its after
member must resolve to a captured literal `Bool` declaration and its declared HMI
type must be `Bool`. The output's selected name becomes the list name. Two fixed
entries, `False` and `True`, select values 0 and 1 and reference `FalseText` and
`TrueText`. Neither is a default entry. Language ordering and all translations
come from the explicit template. No PLC runtime address, enum value, implicit
conversion, project language or native property binding is inferred.

For initial generation, declare empty baseline/current file sets, complete current
coverage and no predecessor. Proposed files can remain absent while preparing the
generation review. For regeneration, download the prior generated pair and supply
independently captured baseline/current files in a new v7 review. Select the exact
previous author record using `previousRecordId`. Both complete sets must match its
two filenames, lengths and hashes. Changed, missing or additional current content,
manual/unknown ownership, changed type, removed member and relevant review
conflicts produce a retained `status: "conflict"` result with no candidate files.
An explicit before/after rename between captured Bool members is supported.

A v2 predecessor must match the exact saved before-component review and member
selection. The output ID, HMI path, family, list name, fixed entries, text-library
identity and filenames remain stable. A v1 predecessor with the same single Bool
list structure is accepted, but its missing component lineage is an explicit gap.
Multiple lists, broader replacement, merges, output/file renames and deletions
remain unsupported. Unrelated review conflicts do not become selected-output
conflicts. Original selected gaps and references to relevant dependency
associations remain visible; contradictory selected dependency impacts suppress
candidates.

`retainOutputIds` selects up to 256 separate `retain` outputs. Each requires a
complete nonempty current file set. `preservedFiles` references their exact
retained source bytes, including opaque manual scripts and resources. Candidate
and peer filenames cannot collide, including case variants. This is preservation
of selected offline files, not proof that a native import preserves project peers.
The returned `files` contains the generated pair only.

Every reopen recomputes the component review from its original request, saved
project observations and retained files, then revalidates the predecessor chain
and both candidate bytes. Rehashed invented intent, binding, lineage,
qualification and peer descriptors refuse. Upload release and Host restart do
not invalidate records. At most 16 predecessor records and 128 MiB of original
declared review-file/request bytes across the v2 lineage are examined; existing
per-review XML limits, candidate limits and the shared 512 MiB retention bound
also apply. These byte budgets do not represent complete project coverage.

After generation, supply the pair as proposed files to a subsequent component
review, including independent observations when available. The independent
Python oracle checks original PLC XML, binding selections, template intent,
complete YAML trees and retained peer bytes. All native execution, native
qualification, ownership, preservation and complete-consumer flags remain false.
See [verification](../verification/hmi-bound-generation-20261020.md).

## Explicit input

- `fileBase`: one safe extension-free filename, up to 120 characters.
- `textLibrary`: ASCII identifier; text and entry identifiers use the same
  letter/underscore followed by letters, digits or underscores rule (128 maximum).
  The narrower authoring grammar avoids ambiguous dotted text references; it is
  not a claim about every identifier Siemens accepts.
- `languages`: 1–32 distinct language tags, with explicit order. Case variants
  collide. `defaultLanguage` must exactly select one of them. Installed or active
  project languages are not inferred.
- `texts`: 1–4096 unique IDs, each with exactly one string translation per language.
  Empty strings are explicit. Quotes, backslashes, line breaks and Unicode remain
  data, including astral characters. Each translation permits at most 16384 UTF-16
  units. Every text must be referenced by at least one entry; sharing is allowed.
- `lists`: 1–128 distinct names, including case variants; 1–4096 entries per list,
  with at most 4096 entries across the request. Each list has at most one default
  entry. Entry IDs are unique within each list.

| Selection / entry | Required bounds |
| --- | --- |
| Decimal / SingleValue | Equal `fromValue` and `toValue` |
| Decimal / Range | Both bounds, with `fromValue <= toValue` |
| Decimal / From | `fromValue`; `toValue: null` |
| Decimal / To | `fromValue: null`; `toValue` |
| BitNumber / SingleValue | Equal nonnegative bounds |
| Bool / SingleValue | Equal bounds selecting 0 or 1 |

Bounds use exact signed 64-bit integer encoding; strings and fractions refuse.
This is the authoring representation, not certification of a particular native
data type or bit width. Overlapping intervals refuse instead of inventing a
precedence policy. Unsupported fields, formatted/system-list intent and unknown
entry/range types refuse. Output construction uses a conservative character budget
to keep each candidate below 8 MiB; requests are limited to 4 MiB and share the
existing 512 MiB HMI retention store, including incomplete files.

## Evidence and native boundary

The independent Python oracle uses PyYAML SafeLoader to compare complete generated
trees against original structured intent, including language order, exact integer
types and every text reference. Duplicate keys, aliases, tags and extra documents
are rejected. Mutation controls change content while updating hashes, ensuring
integrity checks alone cannot pass an unintended output.

All native execution, qualification, preservation and complete-consumer flags stay
false. No project language activation, component/instance binding, system-list
editing, rich-text export, native range acceptance, import, compile, reopen or HMI
runtime acceptance is supplied. Full C01/SiVArc authoring and regeneration remain
open.

The official [V21 export description](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/text-lists/export-of-text-lists)
documents the paired configuration/translation files and entry attributes. Its
examples and retained repository fixtures informed this authoring format. The
official [import description](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/text-lists/import-of-text-lists)
states that existing lists are deleted and recreated during import. Independent
content/consumer review and native preservation qualification are therefore still
required before importing a candidate. PyYAML is a test-only dependency; its
[SafeLoader documentation](https://pyyaml.org/wiki/PyYAMLDocumentation) describes the
non-executing parser used by the oracle. No parser dependency is added to the Host.
