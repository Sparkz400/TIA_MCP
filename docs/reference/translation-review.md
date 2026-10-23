# Offline translation review and candidate artifacts

The pure [translation library](../../src/TiaMcp.Translations/TiaMcp.Translations.csproj) implements a complete **offline candidate workflow**: captured XLSX plus explicit profile → reviewed cell proposals → exact approval digest → preserving candidate and manifest. The [durable Host workflow](translation-host-workflow.md) now exposes capture, paged inspection/review, explicit approval, preserving candidates, reconciliation and restart retrieval through dedicated MCP tools. Neither layer imports into TIA. Native grammar, import, compile, fresh reopen and recovery remain separate obligations in the [translation plan](../plans/expansion-plans/project-translations.md).

Read a captured key with `workbook.Read(nativeKey, culture)`. Each source/target `TextCellState` provides `State`, `Text`, `RichText` and a digest of its state and value. `missing`, `empty` and whitespace-only text have distinct meanings and hashes. Use those expected source/target hashes in `TranslationEdit`; do not replace them with a row number or text-based match.

```csharp
var pair = workbook.Read("captured-native-key", "de-DE");
var edit = new TranslationEdit("captured-native-key", "de-DE",
    TranslationEditKind.Set, "Kühlung aktiviert",
    pair.Source.Sha256, pair.Target.Sha256,
    Approved: true, Reviewer: "reviewer-id");
var edits = new[] { edit };
var review = TranslationReview.Review(workbook, currentCapture, edits);
// Display this exact review. Approval must bind its digest.
string approvedDigest = review["reviewSha256"]!.GetValue<string>();
var candidate = TranslationReview.BuildCandidate(workbook, currentCapture,
    edits, approvedDigest);
candidate.WriteNew(newWorkbookPath, newManifestPath);
```

An integration must obtain the reviewer's approval before supplying `Approved:true` and the digest. These pure APIs check explicit data and hashes; they are not an authentication or human-approval service. Suggestions remain unapproved by default. Project strings are untrusted content and no string becomes a tool instruction or an authorization to transmit data.

The [review contract](../../contracts/translation-review.v1.schema.json) reports the exact before/after cells, source state, categories, locations, per-cell approvals and errors, glossary version/options, change count and completeness for explicit mapped rows only. It does not claim whole-project completeness. Any changed workbook, profile, project, snapshot or content fingerprint requires a new review. The separate reconciliation API below produces fresh unapproved suggestions; it never reuses an earlier approval or automatically merges workbooks. Deleted/recreated keys are refused even if the visible text is the same.

- `Keep` carries no value and produces no text change. An omitted cell is also preserved.
- `Set` needs a nonempty value, matching source/target hashes and explicit reviewer approval when it changes content.
- `Clear` carries no value and requires the target's explicit offline `AllowClear` profile flag. This flag does not qualify native empty-cell semantics.
- A missing target may be materialized only with `AllowCreateMissing`; missing whole rows are not created.
- Source-language changes additionally require `UpdateSourceLanguage:true`. The report marks `sourceChangesRequireFreshExport` and invalidates downstream translation reviews. Changing the same key's source and target in one candidate is blocked: capture the corrected source before reviewing its translation. Other languages stay unchanged.
- Placeholder grammar, UTF-16 limits, optional versioned glossary terms and exact category/culture scope are checked before candidate creation. Glossary matching is explicit ordinal or ordinal-ignore-case substring matching; language morphology or machine translation is not inferred.

`BuildCandidate` captures mutable proposal collections, recomputes the review and rejects a differing approval digest or any error. It reopens its own candidate, checks every approved value, all unedited mapped values, unrelated part bytes and unselected worksheet XML. Tests also compare independent fixture values and XML, rather than relying only on that production verifier. The [candidate manifest](../../contracts/translation-candidate.v1.schema.json) includes input/output/profile/review hashes, exact identity, part hashes, preservation scope and the remaining native gate. Its `manifestSha256` hashes the canonical manifest before adding that digest field; the review uses the same convention. Hashes are integrity/provenance checks, not signatures.

`WriteNew` uses create-new file semantics and refuses existing workbook/manifest paths, identical paths and reparse output parents. It flushes the workbook first and writes the receipt last. If receipt publication fails, an unreceipted workbook can remain; it is never a native import success and is not overwritten or silently deleted. Use new artifact paths for retries. In-memory candidates and manifests are defensive copies.

Build and test without shared `bin/obj` outputs:

```powershell
dotnet build tests/TiaMcp.Translations.Tests/TiaMcp.Translations.Tests.csproj --artifacts-path artifacts/expansion-implementation-20261015/translations/build
dotnet artifacts/expansion-implementation-20261015/translations/build/bin/TiaMcp.Translations.Tests/debug/TiaMcp.Translations.Tests.dll
```

The test executable optionally accepts `--demo <new-directory>` to save its synthetic original, two approved German changes, preserving candidate and manifest after all checks pass. The fixture has eleven keys, one missing and one explicit empty German cell, shared-text peers, Unicode/multiline/control cases, rich text and a French preservation sentinel. The test binary executes no native or spreadsheet process.

`TranslationReconciliation.Reconcile(baseline, edited, current)` compares exact mapped target cells and returns the [reconciliation contract](../../contracts/translation-reconciliation.v1.schema.json). The edited workbook must retain baseline provenance and the same explicit profile. Current may be a new capture of the same exact project object and TIA version, with that same profile. Source changes, key/layout changes, removed cells, formatting changes, opaque selected-cell metadata and unrelated package changes are refused. A target changed concurrently produces `TargetTextConflict`; a changed source produces `SourceTextConflict`; a target already equal to the edited value is reported as `already-applied`. Unrelated current translations remain in the current capture. Clear requires an explicit present empty cell and the profile's clear permission.

Reconciliation returns `Approved:false` proposals bound to current source/target hashes, a fresh-review requirement, and baseline/edited/current provenance. Review the proposals using `Review(current, current, proposals)` and obtain new per-cell approvals and the new digest before calling `BuildCandidate(current, current, ...)`. The output is always materialized from current. A third-party spreadsheet editor that rewrites styles, relationships, shared-string parts or other unrelated content will be refused by this conservative ingestion path even if it displays the same text. Rich-run edits, structural/profile migrations and native project freshness are still separate obligations.

`TranslationInspection.Page(workbook, pageSize, cursor)` returns the [inspection-page contract](../../contracts/translation-inspection-page.v1.schema.json): at most 1,000 target cells ordered by exact key and culture, source/target state digests, and findings for missing, empty, whitespace-only, source-equal, rich-text, unknown-placeholder and explicit length cases. These findings are review aids; source-equal text is not automatically wrong. Resume cursors bind workbook, profile and project observation hashes. Persisting a cursor permits read-only replay of that exact capture; it never authorizes a write or native retry. Changed capture/profile/observation and invalid offsets are refused. Counts describe the explicit map, including targets on hidden sheets.

The expanded managed suite retains the original synthetic fixture and adds independently authored styles, comments, number/date cells, row/column properties, merged ranges, defined names, rich source runs and a second very-hidden owner sheet. An independent XML oracle verifies both edited worksheets and unchanged package-part bytes. The 100k-key receipt (`artifacts/expansion-foundation-20261015/translations/evidence-02/scale-100k.json`) covers ten synthetic owner worksheets, four categories, two target cultures, 200,000 targets over 200 pages, cursor serialization/replay, 1,000 approved changes and an independent full-value check of all 100,000 keys. The ordered key/culture digest and missing/empty/whitespace counts are independently seeded. `--evidence <new-directory>` writes the formatted fixture, candidate, review/reconciliation/page artifacts and measured scale receipt; it executes no native process.

TR-I01/I02 remain partial overall. Native layout/filtering, category-specific token/length rules, rich-run edits, tolerant third-party structural ingestion, advanced glossary analysis and production/native batching remain required. The scale receipt reports actual duration, allocations and process peak working set; this in-memory implementation is not a demonstrated production memory budget. Cursor serialization/read-only replay is exercised, while process-crash recovery, durable native jobs and interrupted native imports are not. No 1.0 obligation is removed by these synthetic results.

## Supplied PLC alarm instance-text inheritance

An explicit profile row may opt into `"alarmTextPolicy": "instance-inheritance-v1"`.
Omit it (or use null) for ordinary text. This is a supplied mapping of one alarm
text field across languages, not automatic recognition of a Siemens workbook.
Keep alarm text, information text and each additional text as separate keyed rows;
include that field's source and every available language cell. Native alarm IDs,
owner paths and the actual workbook layout still need independently captured data.
The existing unique cell/key checks and maximum 1,000 targets per row apply.

Inspection adds `alarmInheritance` for opted-in rows. Review exposes a bounded
`alarmInheritance` section through `translation_review_page`, with before/after
counts and `literal`, `inherited`, `mixed` or `missingFields` status. Every mapped
language, including unedited ones, participates. The exact marker is
`###Inherited from type###`; it is never trimmed, translated or case-folded.
Mixed marker/literal states and unobserved mapped cells block candidate approval,
even with no edits. A reviewed repair may make all mapped fields consistent.
All status values describe only explicit mapped fields, never complete native
language coverage or runtime text substitution.

The existing placeholder policy remains independent: `none` is an explicit
supplied assertion, `brace-literals-v1` checks that literal grammar only, and
`unknown` refuses edits. None of these establishes the Siemens alarm placeholder
grammar. The inheritance policy does not permit simultaneous changed source and
target text, bypass approval, create alarm instances/classes, or admit a native
import. Ordinary profiles retain their old serialized profile and report shape;
old reviews return an empty inheritance section.

Use the existing capture → inspect → prepare → review → approve → candidate →
reopen workflow. Candidate generation preserves all unselected worksheet content
and unrelated ZIP parts. Genuine alarm XLSX compatibility, all-language coverage,
source/target inheritance-mode transitions and native import remain open in the
[PLC alarm/ProDiag plan](../plans/expansion-plans/plc-alarms-prodiag.md).
The rule is grounded in Siemens' [alarm-instance-text documentation](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/export/import-of-alarm-instance-text).
