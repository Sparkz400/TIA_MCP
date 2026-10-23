using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

public sealed class TranslationCandidate
{
    private readonly byte[] bytes;
    private readonly JsonObject manifest;
    internal TranslationCandidate(byte[] bytes, JsonObject manifest) { this.bytes = bytes.ToArray(); this.manifest = (JsonObject)manifest.DeepClone(); }
    public byte[] Bytes => bytes.ToArray();
    public JsonObject Manifest => (JsonObject)manifest.DeepClone();

    /// <summary>Create-new artifacts. An existing output is never replaced. Manifest is written last.</summary>
    public void WriteNew(string workbookPath, string manifestPath)
    {
        string workbook = Path.GetFullPath(workbookPath), receipt = Path.GetFullPath(manifestPath);
        Require(!string.Equals(workbook, receipt, StringComparison.OrdinalIgnoreCase) && !File.Exists(workbook) && !File.Exists(receipt),
            "OutputAlreadyExists", "Use distinct new artifact paths; outputs are immutable.");
        foreach (string path in new[] { workbook, receipt })
        {
            var parent = new DirectoryInfo(Path.GetDirectoryName(path)!);
            for (var cursor = parent; cursor is not null; cursor = cursor.Parent)
                if (cursor.Exists) Require((cursor.Attributes & FileAttributes.ReparsePoint) == 0, "UnsafeArtifactPath", "Reparse output parents are not accepted.");
            parent.Create();
        }
        using (var stream = new FileStream(workbook, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        // If receipt creation fails, the workbook stays as an unreceipted candidate, never an accepted native import.
        using var receiptStream = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] json = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(manifest) + "\n");
        receiptStream.Write(json); receiptStream.Flush(true);
    }
}

public static class TranslationReview
{
    public static JsonObject Review(TranslationWorkbook baseline, TranslationWorkbook current,
        IReadOnlyList<TranslationEdit> edits, TranslationReviewOptions? options = null)
    {
        options ??= new();
        Require((options.Glossary?.Length ?? 0) <= 10000, "TranslationContractLimit", "Glossary exceeds entry budget.");
        foreach (var term in options.Glossary ?? [])
            Require(term is not null && !string.IsNullOrWhiteSpace(options.GlossaryId) &&
                !string.IsNullOrEmpty(term.SourceTerm) && !string.IsNullOrEmpty(term.RequiredTargetTerm) &&
                !string.IsNullOrWhiteSpace(term.TargetCulture), "InvalidGlossary", "Glossary version, culture and nonempty terms are required even when no cells change.");
        EngineeringIdentityGuard.EnsureFresh(baseline.Project, current.Project);
        Require(baseline.WorkbookSha256 == current.WorkbookSha256 && baseline.ProfileSha256 == current.ProfileSha256,
            "StaleWorkbook", "Any changed workbook/profile requires fresh export review; automatic rebasing is not implemented.");
        Require(edits is not null && edits.Count <= current.Package.Limits.MaximumCells,
            "WorkbookLimit", "Bounded edit list required.");
        Require(edits.All(e => e is not null && !string.IsNullOrEmpty(e.NativeKey) && !string.IsNullOrEmpty(e.Culture)),
            "InvalidTranslationEdit", "Every proposal requires an exact key and culture.");
        var rows = new JsonArray();
        var issues = new JsonArray();
        var seen = new HashSet<(string Key, string Culture)>();
        var proposedTexts = new Dictionary<(string Key, string Culture), string?>();
        int changes = 0;
        foreach (var edit in edits!.OrderBy(e => e.NativeKey, StringComparer.Ordinal).ThenBy(e => e.Culture, StringComparer.Ordinal))
        {
            var rowReport = new JsonObject { ["nativeKey"] = edit.NativeKey, ["culture"] = edit.Culture,
                ["edit"] = Node(edit), ["changed"] = false };
            rows.Add(rowReport);
            try
            {
                Require(seen.Add((edit.NativeKey, edit.Culture)), "DuplicateTranslationEdit", "Only one edit per exact key/culture is accepted.");
                var mapping = current.Row(edit.NativeKey);
                var pair = current.Read(edit.NativeKey, edit.Culture);
                Require(pair.Source.Sha256 == edit.ExpectedSourceSha256, "SourceTextConflict", "Source text/state differs from reviewed value.");
                Require(pair.Target.Sha256 == edit.ExpectedTargetSha256, "TargetTextConflict", "Target text/state differs from reviewed value.");
                Require(Enum.IsDefined(edit.Kind), "InvalidTranslationEdit", "Unknown edit kind.");
                Require(edit.Kind == TranslationEditKind.Set ? edit.Value is not null && edit.Value.Length != 0 : edit.Value is null,
                    "InvalidTranslationEdit", "Set needs a nonempty value; keep/clear carry no value. Use explicit clear for empty text.");
                rowReport["source"] = Node(pair.Source);
                rowReport["before"] = Node(pair.Target);
                string? proposed = edit.Kind switch { TranslationEditKind.Set => edit.Value, TranslationEditKind.Clear => "", _ => pair.Target.Text };
                bool changed = edit.Kind != TranslationEditKind.Keep && (pair.Target.State == "missing" || pair.Target.Text != proposed);
                rowReport["after"] = proposed;
                proposedTexts[(edit.NativeKey, edit.Culture)] = proposed;
                rowReport["changed"] = changed;
                rowReport["invalidatesTranslationReviews"] = changed && edit.Culture == current.Mapping.SourceCulture;
                rowReport["category"] = mapping.Category;
                rowReport["location"] = Node(current.Location(mapping, edit.Culture));
                if (!changed) continue;
                changes++;
                Require(edit.Culture != current.Mapping.SourceCulture || options.UpdateSourceLanguage,
                    "SourceEditNotApproved", "Source-language writes require explicit source mode as well as cell approval.");
                current.CheckWritable(mapping, edit.Culture, edit.Kind, proposed!);
                Require(mapping.TokenGrammar != "unknown", "PlaceholderGrammarUnqualified", "Unknown token grammar blocks editing.");
                if (mapping.TokenGrammar == "brace-literals-v1")
                    Require(Tokens(pair.Source.Text ?? "").SequenceEqual(Tokens(proposed!)),
                        "PlaceholderMismatch", "Exact literal placeholder names, formats and multiplicity must match source.");
                foreach (var term in options.Glossary ?? [])
                {
                    Require(!string.IsNullOrEmpty(options.GlossaryId) && !string.IsNullOrEmpty(term.SourceTerm) && !string.IsNullOrEmpty(term.RequiredTargetTerm),
                        "InvalidGlossary", "Glossary version and nonempty source/target terms are required.");
                    var comparison = term.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    if (edit.Culture == term.TargetCulture && (term.Category is null || term.Category == mapping.Category) &&
                        (pair.Source.Text ?? "").Contains(term.SourceTerm, comparison))
                        Require(proposed!.Contains(term.RequiredTargetTerm, comparison), "GlossaryMismatch", "Required reviewed terminology is absent.");
                }
                // Validate suggestions before checking approval, so an unapproved review
                // exposes real profile/preservation failures without inventing consent.
                Require(edit.Approved && !string.IsNullOrWhiteSpace(edit.Reviewer) && edit.Reviewer.Length <= 256,
                    "UnapprovedTranslationEdit", "Changed text needs an explicit reviewer approval.");
            }
            catch (BridgeException ex)
            {
                issues.Add(new JsonObject { ["nativeKey"] = edit.NativeKey, ["culture"] = edit.Culture,
                    ["code"] = ex.Code, ["severity"] = "error", ["message"] = ex.Message });
            }
        }
        var sourceChanges = rows.Where(r => r?["invalidatesTranslationReviews"]?.GetValue<bool>() == true)
            .Select(r => r!["nativeKey"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r?["changed"]?.GetValue<bool>() == true &&
            r!["culture"]!.GetValue<string>() != current.Mapping.SourceCulture && sourceChanges.Contains(r["nativeKey"]!.GetValue<string>())))
            issues.Add(new JsonObject { ["nativeKey"] = row!["nativeKey"]!.GetValue<string>(), ["culture"] = row["culture"]!.GetValue<string>(),
                ["code"] = "SourceAndTargetEditConflict", ["severity"] = "error",
                ["message"] = "Changed source text requires a fresh capture and target review before changing its translations." });
        var completeness = new JsonArray();
        var alarmInheritance = new JsonArray();
        foreach (var mapping in current.Mapping.Rows.Where(r => r.AlarmTextPolicy is not null).OrderBy(r => r.NativeKey, StringComparer.Ordinal))
        {
            var after = AlarmInheritance(current, mapping, proposedTexts);
            alarmInheritance.Add(new JsonObject { ["nativeKey"] = mapping.NativeKey,
                ["before"] = AlarmInheritance(current, mapping), ["after"] = after });
            string status = after["status"]!.GetValue<string>();
            if (status is "mixed" or "missingFields")
                issues.Add(new JsonObject { ["nativeKey"] = mapping.NativeKey, ["culture"] = "*",
                    ["code"] = status == "mixed" ? "AlarmInheritanceMixed" : "AlarmLanguageMissing", ["severity"] = "error",
                    ["message"] = "All mapped language fields of an alarm text must be observed and either all inherit from type or none inherit. Unmapped languages remain unverified." });
        }
        foreach (string culture in current.Mapping.Rows.SelectMany(r => r.Targets).Select(t => t.Culture).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var states = current.Mapping.Rows.SelectMany(r => r.Targets.Where(t => t.Culture == culture).Select(t => current.ReadText(t.Location))).ToArray();
            completeness.Add(new JsonObject { ["culture"] = culture, ["mapped"] = states.Length,
                ["missing"] = states.Count(s => s.State == "missing"), ["empty"] = states.Count(s => s.State == "empty"),
                ["nonempty"] = states.Count(s => s.State == "text"),
                ["whitespaceOnly"] = states.Count(s => s.State == "text" && string.IsNullOrWhiteSpace(s.Text)),
                ["scope"] = "explicit-profile-rows-only" });
        }
        var report = new JsonObject { ["schemaVersion"] = 1, ["kind"] = "translation-offline-review",
            ["project"] = Node(current.Project), ["workbookSha256"] = current.WorkbookSha256, ["profileSha256"] = current.ProfileSha256,
            ["profileKind"] = current.Mapping.Kind, ["sourceCulture"] = current.Mapping.SourceCulture,
            ["options"] = Node(options), ["edits"] = rows, ["issues"] = issues, ["completeness"] = completeness,
            ["changedCells"] = changes, ["canWriteOfflineCandidate"] = issues.Count == 0,
            ["sourceChangesRequireFreshExport"] = rows.Any(r => r?["invalidatesTranslationReviews"]?.GetValue<bool>() == true),
            ["nativeQualified"] = false, ["nativeExecuted"] = false, ["nativeImportAllowed"] = false };
        // Omit the new section for old profiles so retained review digests remain valid.
        if (alarmInheritance.Count > 0) report["alarmInheritance"] = alarmInheritance;
        report["reviewSha256"] = CanonicalJson.Sha256(report);
        return report;
    }

    public static TranslationCandidate BuildCandidate(TranslationWorkbook baseline, TranslationWorkbook current,
        IReadOnlyList<TranslationEdit> edits, string approvedReviewSha256, TranslationReviewOptions? options = null)
    {
        // Snapshot mutable caller collections before computing and consuming the approval.
        Require(edits is not null && edits.Count <= current.Package.Limits.MaximumCells, "WorkbookLimit", "Bounded edit list required.");
        // Edit and glossary records contain immutable scalar values. Copy only the
        // mutable collections, avoiding the external JSON size cap for in-memory batches.
        var frozenEdits = edits.ToArray();
        options ??= new();
        var frozenOptions = options with { Glossary = options.Glossary?.ToArray() };
        var review = Review(baseline, current, frozenEdits, frozenOptions);
        Require(review["reviewSha256"]!.GetValue<string>() == approvedReviewSha256, "ReviewApprovalMismatch", "Approval digest differs from current exact review.");
        Require(review["canWriteOfflineCandidate"]!.GetValue<bool>(), "TranslationReviewBlocked", "Review contains unresolved errors.");
        var changed = review["edits"]!.AsArray().Where(r => r!["changed"]!.GetValue<bool>()).Select(r =>
        {
            var mapping = current.Row(r!["nativeKey"]!.GetValue<string>());
            return (Location: current.Location(mapping, r["culture"]!.GetValue<string>()), Value: r["after"]!.GetValue<string>());
        }).ToArray();
        byte[] bytes = current.Overlay(changed);
        var after = TranslationWorkbook.Open(bytes, current.Profile, current.Project, Sha256(bytes), current.Package.Limits);
        VerifyPreservation(current, after, changed);
        var beforeHashes = current.PartHashes;
        var afterHashes = after.PartHashes;
        var manifest = new JsonObject { ["schemaVersion"] = 1, ["kind"] = "translation-offline-candidate",
            ["project"] = Node(current.Project), ["profileId"] = current.Mapping.Id, ["profileKind"] = current.Mapping.Kind,
            ["profileSha256"] = current.ProfileSha256, ["inputSha256"] = current.WorkbookSha256,
            ["outputSha256"] = after.WorkbookSha256, ["reviewSha256"] = approvedReviewSha256,
            ["review"] = review, ["changedCells"] = changed.Length,
            ["parts"] = new JsonArray(beforeHashes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject {
                ["path"] = p.Key, ["beforeSha256"] = p.Value, ["afterSha256"] = afterHashes[p.Key], ["changed"] = p.Value != afterHashes[p.Key] }).ToArray()),
            ["preservationVerified"] = true, ["preservationScope"] = "OPC-parts-and-unedited-worksheet-XML; explicit offline profile only",
            ["nativeQualified"] = false, ["nativeExecuted"] = false, ["nativeImportAllowed"] = false,
            ["remainingGate"] = "Genuine vendor XLSX grammar and native import/preservation/reopen/recovery acceptance are unavailable." };
        manifest["manifestSha256"] = CanonicalJson.Sha256(manifest);
        return new TranslationCandidate(bytes, manifest);
    }

    private static string[] Tokens(string text) => Regex.Matches(text, "\\{[^{}\\r\\n]*\\}", RegexOptions.CultureInvariant)
        .Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    internal static JsonObject AlarmInheritance(TranslationWorkbook workbook, TranslationRow row,
        IReadOnlyDictionary<(string Key, string Culture), string?>? proposed = null)
    {
        var fields = new[] { (workbook.Mapping.SourceCulture, row.SourceCell) }
            .Concat(row.Targets.Select(t => (t.Culture, t.Location)));
        int total = 0, inherited = 0, missing = 0;
        foreach (var (culture, location) in fields)
        {
            string? text = proposed is not null && proposed.TryGetValue((row.NativeKey, culture), out var value)
                ? value : workbook.ReadText(location).Text;
            total++; if (text is null) missing++;
            if (text == "###Inherited from type###") inherited++;
        }
        return new JsonObject { ["policy"] = "instance-inheritance-v1", ["mappedFields"] = total,
            ["inheritedFields"] = inherited, ["missingFields"] = missing,
            ["status"] = missing > 0 ? "missingFields" : inherited == 0 ? "literal" : inherited == total ? "inherited" : "mixed",
            ["scope"] = "explicit-profile-language-fields-only" };
    }

    internal static void VerifyPreservation(TranslationWorkbook before, TranslationWorkbook after,
        IReadOnlyList<(CellLocation Location, string Value)> changes)
    {
        XElement NonTextNodes(XElement? cell) => new("cell", cell?.Nodes().Where(n => n is not XElement element ||
            element.Name != BoundedOpcPackage.Sheet + "is" && element.Name != BoundedOpcPackage.Sheet + "v"));
        var changedLocations = changes.ToDictionary(c => c.Location, c => c.Value);
        Require(before.Package.Parts.Keys.Order(StringComparer.Ordinal).SequenceEqual(after.Package.Parts.Keys.Order(StringComparer.Ordinal)),
            "TranslationPreservationFailure", "Package part set changed.");
        foreach (var part in before.Package.Parts)
        {
            var selected = changes.Where(c => c.Location.Part == part.Key).ToArray();
            if (selected.Length == 0)
            {
                Require(part.Value.AsSpan().SequenceEqual(after.Package.Parts[part.Key]), "TranslationPreservationFailure", "Unrelated OPC part changed.");
                continue;
            }
            var oldDocument = new XDocument(before.Package.Xml[part.Key]);
            var newDocument = new XDocument(after.Package.Xml[part.Key]);
            var oldCells = oldDocument.Descendants(BoundedOpcPackage.Sheet + "c").ToDictionary(c => (string)c.Attribute("r")!, StringComparer.Ordinal);
            var newCells = newDocument.Descendants(BoundedOpcPackage.Sheet + "c").ToDictionary(c => (string)c.Attribute("r")!, StringComparer.Ordinal);
            foreach (var change in selected)
            {
                Require(after.ReadText(change.Location).Text == change.Value, "TranslationPreservationFailure", "Output cell does not equal approved text.");
                oldCells.TryGetValue(change.Location.Cell, out XElement? oldCell);
                XElement newCell = newCells[change.Location.Cell];
                if (oldCell is not null)
                    Require(oldCell.Attributes().Where(a => a.Name != "t").Select(a => a.ToString()).SequenceEqual(newCell.Attributes().Where(a => a.Name != "t").Select(a => a.ToString())),
                        "TranslationPreservationFailure", "Non-text cell attributes changed.");
                else Require(newCell.Attributes().All(a => a.Name == "r" || a.Name == "t"),
                    "TranslationPreservationFailure", "New cell has unreviewed attributes.");
                Require(XNode.DeepEquals(NonTextNodes(oldCell), NonTextNodes(newCell)),
                    "TranslationPreservationFailure", "Non-text cell content changed.");
                oldCell?.Remove(); newCell.Remove();
            }
            Require(XNode.DeepEquals(oldDocument.Root, newDocument.Root), "TranslationPreservationFailure", "Unapproved worksheet XML changed.");
        }
        foreach (var row in before.Mapping.Rows)
            foreach (var location in new[] { row.KeyCell, row.SourceCell }.Concat(row.Targets.Select(t => t.Location)))
                if (!changedLocations.ContainsKey(location))
                    Require(before.ReadText(location) == after.ReadText(location), "TranslationPreservationFailure", "Unapproved mapped text changed.");
    }
}
