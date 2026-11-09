using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Tests;

internal static class ChangeInputAndCompactionTests
{
    private const string Scl = "FUNCTION \"Gate\" : Void\nBEGIN\nEND_FUNCTION\n";

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);

    internal static Task SourceRefInlinesArtifactFileWithHash()
    {
        var root = Directory.CreateTempSubdirectory("tiamcp-ref-").FullName;
        try
        {
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Scl)).ToArray();
            Directory.CreateDirectory(Path.Combine(root, "exports"));
            File.WriteAllBytes(Path.Combine(root, "exports", "gate.scl"), bytes);
            var resolved = JsonNode.Parse(ChangeInputReference.Resolve("source_upsert",
                Payload(new { language = "SCL", sourceRef = "exports/gate.scl", expectedBlockNames = new[] { "Gate" } }), root, NoUploads).GetRawText())!;
            Check.Equal(Scl, resolved["source"]!.GetValue<string>());
            Check.Equal(null, resolved["sourceRef"]);
            Check.Equal("artifactDirectory", resolved["inputReference"]!["origin"]!.GetValue<string>());
            Check.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), resolved["inputReference"]!["sha256"]!.GetValue<string>());
            Check.Equal(bytes.Length, resolved["inputReference"]!["bytes"]!.GetValue<int>());
            // Unrelated fields pass through unchanged.
            Check.Equal("Gate", resolved["expectedBlockNames"]![0]!.GetValue<string>());
            return Task.CompletedTask;
        }
        finally { Directory.Delete(root, true); }
    }

    internal static Task XmlRefInlinesCommittedUpload()
    {
        var root = Directory.CreateTempSubdirectory("tiamcp-ref-").FullName;
        try
        {
            var store = new ArtifactUploadStore(root);
            const string xml = "<Document><SW.Blocks.FC ID=\"0\" /></Document>";
            string reference = store.Upload(Encoding.UTF8.GetBytes(xml), "block.xml");
            var resolved = JsonNode.Parse(ChangeInputReference.Resolve("block_import_xml",
                Payload(new { xmlRef = reference, expectedBlockNames = new[] { "B" } }), root, store.ResolveCommitted).GetRawText())!;
            Check.Equal(xml, resolved["xml"]!.GetValue<string>());
            Check.Equal("upload", resolved["inputReference"]!["origin"]!.GetValue<string>());
            Check.Equal(reference, resolved["inputReference"]!["reference"]!.GetValue<string>());
            return Task.CompletedTask;
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task InputReferencesAreBoundedAndExclusive()
    {
        var root = Directory.CreateTempSubdirectory("tiamcp-ref-").FullName;
        var outside = Path.Combine(Path.GetTempPath(), "tiamcp-outside-" + Guid.NewGuid().ToString("N") + ".scl");
        try
        {
            File.WriteAllText(Path.Combine(root, "a.scl"), Scl);
            File.WriteAllText(outside, Scl);
            // Inline payloads are not touched.
            var inline = Payload(new { language = "SCL", source = Scl });
            Check.Equal(inline.GetRawText(), ChangeInputReference.Resolve("source_upsert", inline, root, NoUploads).GetRawText());
            await Check.BridgeError("InvalidRequest", () => Run(() => ChangeInputReference.Resolve("source_upsert", Payload(new { source = Scl, sourceRef = "a.scl" }), root, NoUploads)));
            await Check.BridgeError("InvalidRequest", () => Run(() => ChangeInputReference.Resolve("block_import_xml", Payload(new { sourceRef = "a.scl" }), root, NoUploads)));
            await Check.BridgeError("InvalidRequest", () => Run(() => ChangeInputReference.Resolve("plc_compile", Payload(new { xmlRef = "a.scl" }), root, NoUploads)));
            await Check.BridgeError("InvalidRequest", () => Run(() => ChangeInputReference.Resolve("source_upsert", Payload(new { sourceRef = "a.scl", inputReference = new { } }), root, NoUploads)));
            await Check.BridgeError("InvalidRequest", () => Run(() => ChangeInputReference.Resolve("source_upsert", Payload(new { sourceRef = 5 }), root, NoUploads)));
            await Check.BridgeError("ArtifactPathDenied", () => Run(() => ChangeInputReference.Resolve("source_upsert", Payload(new { sourceRef = outside }), root, NoUploads)));
            await Check.BridgeError("ArtifactPathDenied", () => Run(() => ChangeInputReference.Resolve("source_upsert", Payload(new { sourceRef = "../" + Path.GetFileName(outside) }), root, NoUploads)));
            File.WriteAllBytes(Path.Combine(root, "big.scl"), new byte[ChangeInputReference.MaxBytes + 1]);
            await Check.BridgeError("InputTooLarge", () => Run(() => ChangeInputReference.Resolve("source_upsert", Payload(new { sourceRef = "big.scl" }), root, NoUploads)));
            File.WriteAllBytes(Path.Combine(root, "latin1.scl"), [0x46, 0xFC, 0x72]);
            await Check.BridgeError("InvalidRequest", () => Run(() => ChangeInputReference.Resolve("source_upsert", Payload(new { sourceRef = "latin1.scl" }), root, NoUploads)));
        }
        finally { Directory.Delete(root, true); File.Delete(outside); }
    }

    internal static Task ChangeViewsAreSummarized()
    {
        string source = new('x', 30_000);
        var messages = new JsonArray(Enumerable.Range(0, 400).Select(i => (JsonNode)new JsonObject
        {
            ["path"] = "FC" + i, ["state"] = i % 2 == 0 ? "Error" : "Warning", ["description"] = "Finding " + i, ["depth"] = 1,
        }).ToArray());
        var view = new JsonObject
        {
            ["origin"] = "TIA Portal V21 Openness/offline", ["changeId"] = "c1", ["state"] = "applied", ["previewHash"] = "h1",
            ["journalPath"] = "C:/a/journal.json", ["stages"] = new JsonArray("checkpoint_created", "imported", "compiled"),
            ["readbacks"] = new JsonArray(new JsonObject { ["xml"] = source }),
            ["compileBaseline"] = new JsonObject { ["messages"] = messages.DeepClone() },
            ["preview"] = new JsonObject
            {
                ["kind"] = "source_upsert", ["warnings"] = new JsonArray("w1"), ["effects"] = new JsonArray(new JsonObject { ["action"] = "create", ["blockName"] = "Gate" }),
                ["payload"] = new JsonObject { ["language"] = "SCL", ["source"] = source, ["inputReference"] = new JsonObject { ["sha256"] = "abc" } },
                ["backups"] = new JsonArray(new JsonObject { ["role"] = "block", ["blockName"] = "Gate", ["artifact"] = new JsonObject { ["path"] = "C:/a/b.xml", ["sha256"] = "s", ["xml"] = source } }),
                ["intent"] = new JsonObject { ["decisions"] = new JsonArray("d"), ["conflicts"] = new JsonArray() },
            },
            ["compile"] = new JsonObject
            {
                ["errorCount"] = 200, ["warningCount"] = 200,
                ["targets"] = new JsonArray(new JsonObject { ["target"] = "PLC_1", ["errorCount"] = 200, ["warningCount"] = 200, ["messages"] = messages }),
                ["baselineComparison"] = new JsonObject { ["verified"] = false, ["introducedCount"] = 1, ["existing"] = new JsonArray("old"), ["introduced"] = new JsonArray("new") },
            },
        };
        var full = view.ToJsonString();
        var summary = ChangeResultCompaction.Compact(view)!.AsObject();
        Check.Equal(full, view.ToJsonString()); // The input is not modified.
        Check.Equal("summary", summary["responseDetail"]!.GetValue<string>());
        Check.Equal("c1", summary["changeId"]!.GetValue<string>());
        Check.Equal("h1", summary["previewHash"]!.GetValue<string>());
        Check.Equal(3, summary["stages"]!.AsArray().Count);
        Check.Equal(true, summary["preview"]!["payload"]!["source"]!["omitted"]!.GetValue<bool>());
        Check.Equal(30_000, summary["preview"]!["payload"]!["source"]!["characters"]!.GetValue<int>());
        Check.Equal("abc", summary["preview"]!["payload"]!["inputReference"]!["sha256"]!.GetValue<string>());
        Check.Equal("C:/a/b.xml", summary["preview"]!["backups"]![0]!["path"]!.GetValue<string>());
        Check.Equal(null, summary["preview"]!["intent"]!["decisions"]);
        Check.Equal(200, summary["compile"]!["errorCount"]!.GetValue<int>());
        var target = summary["compile"]!["targets"]![0]!;
        Check.Equal(null, target["messages"]);
        Check.Equal(ChangeResultCompaction.MaxCompileErrors, target["errors"]!.AsArray().Count);
        Check.Equal(true, target["errorsTruncated"]!.GetValue<bool>());
        Check.Equal(null, summary["compile"]!["baselineComparison"]!["existing"]);
        Check.Equal("new", summary["compile"]!["baselineComparison"]!["introduced"]![0]!.GetValue<string>());
        Check.Equal(1, summary["readbackCount"]!.GetValue<int>());
        Check.Equal(true, summary["omitted"]!.AsArray().Any(n => n!.GetValue<string>() == "compileBaseline"));
        if (summary.ToJsonString().Length > 8 * 1024) throw new InvalidOperationException("Summary is not bounded: " + summary.ToJsonString().Length);
        // Summaries and non-change results are returned unchanged.
        Check.Equal(summary.ToJsonString(), ChangeResultCompaction.Compact(summary)!.ToJsonString());
        var other = new JsonObject { ["items"] = new JsonArray(1, 2) };
        Check.Equal(other.ToJsonString(), ChangeResultCompaction.Compact(other)!.ToJsonString());
        return Task.CompletedTask;
    }

    internal static Task ChangeStatusJournalsAreSummarized()
    {
        var status = new JsonObject
        {
            ["changeId"] = "c2", ["journalPath"] = "j", ["recoveredFromDisk"] = true,
            ["journal"] = new JsonObject
            {
                ["kind"] = "block_import_xml", ["state"] = "recovery_required", ["previewHash"] = "h2", ["failure"] = "NativePreservationMismatch",
                ["preview"] = new JsonObject { ["kind"] = "block_import_xml", ["payload"] = new JsonObject { ["xml"] = new string('y', 5000) } },
                ["compile"] = new JsonObject { ["errorCount"] = 0, ["warningCount"] = 1 },
            },
        };
        var summary = ChangeResultCompaction.Compact(status)!;
        Check.Equal("recovery_required", summary["state"]!.GetValue<string>());
        Check.Equal("h2", summary["previewHash"]!.GetValue<string>());
        Check.Equal("NativePreservationMismatch", summary["failure"]!.GetValue<string>());
        Check.Equal(5000, summary["preview"]!["payload"]!["xml"]!["characters"]!.GetValue<int>());
        Check.Equal(1, summary["compile"]!["warningCount"]!.GetValue<int>());
        return Task.CompletedTask;
    }

    private static (string, string, string) NoUploads(string reference) => throw new BridgeException("InvalidRequest", "No uploads in this test.");

    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static Task Run<T>(Func<T> action) { action(); return Task.CompletedTask; }
}
