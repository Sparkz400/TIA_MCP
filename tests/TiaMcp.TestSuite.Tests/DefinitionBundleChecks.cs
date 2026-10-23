using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

internal sealed record DefinitionBundleReceipt(int Checks, int ScaleDefinitions, long ScaleOriginalBytes,
    int ScaleBundleBytes, long CaptureMilliseconds, long RestoreMilliseconds);

internal static class DefinitionBundleChecks
{
    public static DefinitionBundleReceipt Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Reject(Action action, string code = "InvalidDefinitionBundle")
        {
            try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; }
            throw new Exception("Expected bundle refusal: " + code);
        }
        var reference = new EngineeringObjectReference(new("Project/BundleSynthetic", "PLC/A", "Root", "Rules/Names", "ruleSet"), new("authored", new('a', 64)));
        var scope = reference with { Object = reference.Object with { ObjectPath = "Blocks/Motor", Kind = "block" } };
        var original = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n<Rule><!-- Ω reviewed original --></Rule>\r\n")).ToArray();
        TestSuiteDefinitionCapture Definition(string id, string[]? dependencies = null, byte[]? bytes = null, string profile = "authored-v1", string family = "style")
            => new(id, family, id, family == "style" ? ".xml" : ".tat", reference with { Object = reference.Object with { ObjectPath = "Rules/" + id } },
                bytes ?? original, profile, [scope], dependencies ?? [], true, true, []);
        var root = Definition("root", ["dependency"]);
        var dependency = Definition("dependency");
        var excluded = Definition("excluded");
        var run = TestSuiteRunPlanner.Prepare([root, dependency, excluded], ["root"], ["excluded"], "style");
        var bundle = TestSuiteDefinitionBundle.Capture(run, [excluded, dependency, root]);
        Check(bundle.DefinitionCount == 3 && bundle.OriginalByteCount == original.Length * 3, "Dependency and excluded definitions remain in the retained closure.");
        Check(bundle.Bytes.AsSpan().SequenceEqual(TestSuiteDefinitionBundle.Capture(run, [root, dependency, excluded]).Bytes), "Capture order cannot alter reviewed closure order or retained hash.");
        Check(bundle.Sha256 == Convert.ToHexStringLower(SHA256.HashData(bundle.Bytes)), "Bundle hash identifies the actual retained bytes.");
        var restored = TestSuiteDefinitionBundle.Restore(run, bundle.Bytes);
        Check(restored.OriginalBytes("dependency").AsSpan().SequenceEqual(original), "UTF-16 BOM, CRLF, comments and Unicode remain byte-exact on reopen.");
        var returned = restored.OriginalBytes("root"); returned[0] = 0;
        Check(restored.OriginalBytes("root")[0] == 0xff, "Returned original bytes cannot mutate the immutable bundle.");
        var returnedBundle = bundle.Bytes; returnedBundle[0] = 0;
        Check(bundle.Bytes[0] == '{', "Returned bundle bytes cannot mutate the immutable bundle.");
        Reject(() => bundle.OriginalBytes("unselected"));
        Reject(() => TestSuiteDefinitionBundle.Capture(run, [root, dependency]));
        Reject(() => TestSuiteDefinitionBundle.Capture(run, [root, dependency, excluded, Definition("extra")]));
        Reject(() => TestSuiteDefinitionBundle.Capture(run, [root, root, excluded]));
        Reject(() => TestSuiteDefinitionBundle.Capture(run, [root, dependency, Definition("extra")]));
        Reject(() => TestSuiteDefinitionBundle.Capture(run, [root, dependency, null!]));
        Reject(() => TestSuiteDefinitionBundle.Capture(run, null!));
        Reject(() => TestSuiteDefinitionBundle.Capture(run, [root, Definition("dependency", profile: "changed-review-profile"), excluded]));
        Reject(() => TestSuiteDefinitionBundle.Capture(run, [root, Definition("dependency", bytes: Encoding.UTF8.GetBytes("<Changed/>")), excluded]));
        var anotherReview = TestSuiteRunPlanner.Prepare([root, dependency, excluded], ["root", "excluded"], [], "style");
        Reject(() => bundle.EnsureMatches(anotherReview));
        Reject(() => TestSuiteDefinitionBundle.Restore(anotherReview, bundle.Bytes));

        byte[] Change(Action<JsonNode> edit)
        {
            var node = JsonNode.Parse(bundle.Bytes)!; edit(node);
            return Encoding.UTF8.GetBytes(CanonicalJson.Serialize(node));
        }
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["schemaVersion"] = 2)));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["planSha256"] = new string('f', 64))));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]!.AsArray().RemoveAt(2))));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]!.AsArray().Add(n["definitions"]![0]!.DeepClone()))));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![1] = n["definitions"]![0]!.DeepClone())));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n =>
        {
            var first = n["definitions"]![0]!.DeepClone();
            n["definitions"]![0] = n["definitions"]![1]!.DeepClone(); n["definitions"]![1] = first;
        })));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["originalBytesBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("<Other/>")))));
        var sameLengthCorruption = original.ToArray(); sameLengthCorruption[^2] ^= 1;
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["originalBytesBase64"] = Convert.ToBase64String(sameLengthCorruption))));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["originalBytesBase64"] = "!invalid base64!")));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["originalBytesBase64"] = " " + Convert.ToBase64String(original))));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["manifest"]!["originalBytes"] = original.Length + 1)));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["manifest"]!["profileId"] = "other-profile")));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["manifest"]!["scopeComplete"] = false)));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["manifest"]!["requiredScope"]![0]!["object"]!["objectPath"] = "Blocks/Other")));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["manifest"]!["reference"]!["snapshot"]!["observationId"] = "other-observation")));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["unknownReviewField"] = true)));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["manifest"]!["unknownGrammarClaim"] = true)));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0] = null)));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["manifest"] = null)));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Change(n => n["definitions"]![0]!["originalBytesBase64"] = null)));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Encoding.UTF8.GetBytes("{\"schemaVersion\":1," + Encoding.UTF8.GetString(bundle.Bytes)[1..])));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(bundle.Bytes))));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Encoding.UTF8.GetBytes("null")));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, Encoding.UTF8.GetBytes("{malformed")));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, []));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, null!));
        Reject(() => TestSuiteDefinitionBundle.Restore(run, new byte[TestSuiteDefinitionBundle.MaxBundleBytes + 1]), "DefinitionBundleLimit");
        Reject(() => TestSuiteDefinitionBundle.Restore(run with { NativeExecutionAuthorized = true }, bundle.Bytes), "StaleRunPlan");

        var opaque = Definition("opaque", bytes: [0, 0xff, 0x0d, 0x0a, 0x2b], family: "application");
        var opaqueRun = TestSuiteRunPlanner.Prepare([opaque], ["opaque"], [], "systemManaged");
        var opaqueBundle = TestSuiteDefinitionBundle.Capture(opaqueRun, [opaque]);
        Check(TestSuiteDefinitionBundle.Restore(opaqueRun, opaqueBundle.Bytes).OriginalBytes("opaque").SequenceEqual(opaque.OriginalBytes), "Opaque non-XML definitions retain every byte without interpreting native grammar.");

        var scale = Enumerable.Range(0, 1000).Select(i => Definition("scale-" + i.ToString("D4"))).ToArray();
        var scaleRun = TestSuiteRunPlanner.Prepare(scale, scale.Select(c => c.Manifest.Id).ToArray(), [], "style");
        var stopwatch = Stopwatch.StartNew();
        var scaleBundle = TestSuiteDefinitionBundle.Capture(scaleRun, scale.Reverse().ToArray());
        long captureMs = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();
        var scaleRestored = TestSuiteDefinitionBundle.Restore(scaleRun, scaleBundle.Bytes);
        long restoreMs = stopwatch.ElapsedMilliseconds;
        Check(scaleRestored.DefinitionCount == 1000 && scaleRestored.Sha256 == scaleBundle.Sha256, "All 1,000 reviewed originals survive canonical persistence and reopen.");
        Check(scaleRestored.OriginalBytes("scale-0999").SequenceEqual(original), "The final required original is retained without truncation.");

        // A single large opaque allocation is shared only by this authored fixture; captures own their bytes.
        var large = new byte[12 * 1024 * 1024];
        var largeDefinitions = Enumerable.Range(0, 3).Select(i => Definition("large-" + i, bytes: large, family: "application")).ToArray();
        var largeRun = TestSuiteRunPlanner.Prepare(largeDefinitions, largeDefinitions.Select(c => c.Manifest.Id).ToArray(), [], "systemManaged");
        Reject(() => TestSuiteDefinitionBundle.Capture(largeRun, largeDefinitions), "DefinitionBundleLimit");
        // This pattern becomes '+' in base64, which the canonical JSON encoder escapes. Account for
        // the encoded size rather than assuming every base64 character consumes one JSON byte.
        for (int i = 0; i < large.Length; i += 3) { large[i] = 0xfb; large[i + 1] = 0xef; large[i + 2] = 0xbe; }
        var escaped = Definition("escaped", bytes: large, family: "application");
        var escapedRun = TestSuiteRunPlanner.Prepare([escaped], ["escaped"], [], "systemManaged");
        Reject(() => TestSuiteDefinitionBundle.Capture(escapedRun, [escaped]), "DefinitionBundleLimit");
        return new(checks, scaleRestored.DefinitionCount, scaleRestored.OriginalByteCount, scaleRestored.Bytes.Length, captureMs, restoreMs);
    }
}
