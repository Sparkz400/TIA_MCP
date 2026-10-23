using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Translations;

/// <summary>Optional file-only verification of a retained genuine workbook against independent decoded cases.</summary>
internal static class NativeReadChecks
{
    internal static int Run(string casePath, string outputPath)
    {
        if (File.Exists(outputPath)) throw new Exception("Use a new native-read verification output path.");
        var cases = JsonNode.Parse(File.ReadAllText(casePath))!.AsArray();
        var results = new JsonArray();
        foreach (var item in cases)
        {
            var test = item!.AsObject();
            byte[] bytes = File.ReadAllBytes(test["workbookPath"]!.GetValue<string>());
            var profile = TranslationJson.Parse<WorkbookProfile>(File.ReadAllText(test["profilePath"]!.GetValue<string>()));
            var identity = TranslationJson.Parse<EngineeringObjectReference>(test["identity"]!.ToJsonString());
            var workbook = TranslationWorkbook.Open(bytes, profile, identity, test["workbookSha256"]!.GetValue<string>());
            int rows = 0;
            foreach (var row in test["rows"]!.AsArray())
            {
                var values = workbook.Read(row!["nativeKey"]!.GetValue<string>(), "en-GB");
                if (values.Source.Text != row["source"]!.GetValue<string>() || values.Target.Text != row["target"]!.GetValue<string>() ||
                    values.Source.RichText || values.Target.RichText)
                    throw new Exception("Managed decoded value differs from the independent ZIP/XML reader.");
                rows++;
            }
            if (rows != profile.Rows.Length || !bytes.AsSpan().SequenceEqual(workbook.OriginalBytes()))
                throw new Exception("Capture row coverage or immutable original bytes differ.");
            var first = profile.Rows[0]; var pair = workbook.Read(first.NativeKey, "en-GB");
            var probe = new TranslationEdit(first.NativeKey, "en-GB", TranslationEditKind.Set,
                "Read-only profile edit refusal probe", pair.Source.Sha256, pair.Target.Sha256, true, "test-only refusal");
            var refusal = TranslationReview.Review(workbook, workbook, [probe]);
            if (refusal["canWriteOfflineCandidate"]!.GetValue<bool>()) throw new Exception("Native read profile unexpectedly permits a changed candidate.");
            var inspection = TranslationInspection.Page(workbook, 1000);
            if (inspection["rows"]!.AsArray().Count != rows) throw new Exception("Paged native-read target count differs.");
            results.Add(new JsonObject { ["mode"] = test["mode"]!.GetValue<string>(), ["workbookSha256"] = workbook.WorkbookSha256,
                ["profileSha256"] = workbook.ProfileSha256, ["rowsVerifiedAgainstIndependentReader"] = rows,
                ["originalBytesUnchanged"] = true, ["changedCandidateBlocked"] = true,
                ["refusalCodes"] = new JsonArray(refusal["issues"]!.AsArray().Select(i => JsonValue.Create(i!["code"]!.GetValue<string>())).ToArray()),
                ["inspection"] = inspection, ["nativeInvokedByThisCheck"] = false, ["nativeImportExecuted"] = false,
                ["nativeWriteQualified"] = false, ["projectFreshnessVerified"] = false });
        }
        var report = new JsonObject { ["kind"] = "genuine-translation-capture-offline-read-verification", ["allPassed"] = true,
            ["checks"] = results, ["nativeInvokedByThisCheck"] = false, ["nativeImportExecuted"] = false };
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(output); writer.Write(report.ToJsonString());
        Console.WriteLine($"{results.Count} genuine retained workbook captures decoded and read-only profiles verified; no native invocation/import.");
        return 0;
    }
}
