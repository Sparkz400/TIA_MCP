using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.TestSuite;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "project_snapshot_capture", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Save a persistent offline review: native hierarchy plus up to maxBlocks (0..200, default 25) exact standard block XML exports. request accepts projectName, plcPath, maxVisited, maxBlocks, blockPaths, maxRelationshipRoots (0..25, default 5), requireAllContent (refuse instead of saving when any block content is missing), hashProjectFolder (also hash every byte of the saved project folder; slower on large projects). capture.projectFolder reports the saved folder's file count, bytes and a path/size/time hash: the .ap21 file alone does not change on save. Check degraded and degradedReasons: they name failed exports, protected or unsupported blocks and blocks omitted by maxBlocks. A capture whose every export is refused for a read-only project is refused (ReadOnlyProject) instead of saved; pass fromTemporaryCopy:true to archive the open project, close it and capture a read-write copy instead (the original stays unchanged and the copy stays open). blockOffset pages through blocks beyond maxBlocks; capture.content.nextBlockOffset gives the next page. Captures bounded direct relationships for the first selected blocks. Native metadata is rechecked after exports; sequential content is not atomic. Protected/safety/unavailable content and omitted blocks stay explicit. Returns a content-hash reviewId usable after host restart. No project changes.")]
    public async Task<CallToolResult> ProjectSnapshotCapture(JsonElement request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonNode.Parse(request.GetRawText())?.AsObject() ?? throw new BridgeException("InvalidRequest", "request must be an object.");
            var permitted = new HashSet<string>(["projectName", "plcPath", "maxVisited", "maxBlocks", "blockPaths", "maxRelationshipRoots", "requireAllContent", "hashProjectFolder", "fromTemporaryCopy", "blockOffset"], StringComparer.Ordinal);
            if (input.Any(p => !permitted.Contains(p.Key))) throw new BridgeException("InvalidRequest", "Unknown snapshot capture field.");
            int maximum = input["maxBlocks"]?.GetValue<int>() ?? 25;
            if (maximum is < 0 or > 200) throw new BridgeException("InvalidRequest", "maxBlocks must be 0..200.");
            int relationshipRoots = input["maxRelationshipRoots"]?.GetValue<int>() ?? 5;
            if (relationshipRoots is < 0 or > 25) throw new BridgeException("InvalidRequest", "maxRelationshipRoots must be 0..25.");
            JsonObject? temporaryCopy = null;
            if (input["fromTemporaryCopy"]?.GetValue<bool>() == true)
                temporaryCopy = JsonNode.Parse((await bridge.CallAsync("project_open_copy", new { projectName = input["projectName"]?.GetValue<string>(),
                    artifactDirectory = Path.Combine(options.ArtifactsDirectory, "copies") }, cancellationToken)).GetRawText())!.AsObject();
            int blockOffset = input["blockOffset"]?.GetValue<int>() ?? 0;
            if (blockOffset < 0) throw new BridgeException("InvalidRequest", "blockOffset must be 0 or more.");
            var captureArgs = new JsonObject { ["action"] = "capture", ["lifetimeSeconds"] = 900,
                ["artifactDirectory"] = Path.Combine(options.ArtifactsDirectory, "project-explorer") };
            foreach (string name in new[] { "projectName", "plcPath", "maxVisited" })
                if (input[name] is not null) captureArgs[name] = input[name]!.DeepClone();
            var captured = JsonNode.Parse((await bridge.CallAsync("project_model", captureArgs, cancellationToken)).GetRawText())!.AsObject();
            string snapshotId = captured["snapshotId"]?.GetValue<string>() ?? throw new BridgeException("InvalidSnapshot", "Native capture did not return a snapshot identity.");
            var export = JsonNode.Parse((await bridge.CallAsync("project_model", new { action = "export", snapshotId,
                artifactDirectory = Path.Combine(options.ArtifactsDirectory, "project-explorer") }, cancellationToken)).GetRawText())!.AsObject();
            var model = JsonNode.Parse(ProjectReviewStore.ReadArtifact(options.ArtifactsDirectory, export["artifact"]!.AsObject()))!.AsObject();
            var allBlocks = model["nodes"]!.AsArray().Where(n => n?["kind"]?.GetValue<string>() == "block")
                .OrderBy(n => n!["objectPath"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
            HashSet<string>? requested = null;
            if (input["blockPaths"] is not null)
            {
                var paths = input["blockPaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                requested = paths.ToHashSet(StringComparer.Ordinal);
                if (paths.Length != requested.Count || paths.Length > maximum || requested.Any(p => !allBlocks.Any(n => n!["objectPath"]!.GetValue<string>() == p)))
                    throw new BridgeException("InvalidRequest", "blockPaths must be unique exact block paths within this capture and within maxBlocks.");
            }
            var selected = allBlocks.Where(n => requested is null || requested.Contains(n!["objectPath"]!.GetValue<string>())).Skip(requested is null ? blockOffset : 0).Take(maximum).ToArray();
            var relationships = new JsonArray();
            foreach (var node in selected.Take(relationshipRoots))
            {
                var reference = new JsonObject { ["schemaVersion"] = "project-model.v1", ["snapshotId"] = snapshotId,
                    ["sessionGeneration"] = model["sessionGeneration"]!.DeepClone(),
                    ["projectPath"] = model["project"]!["projectPath"]!.DeepClone(),
                    ["nodeId"] = node!["nodeId"]!.DeepClone(), ["objectPath"] = node["objectPath"]!.DeepClone() };
                var observed = JsonNode.Parse((await bridge.CallAsync("project_model", new JsonObject {
                    ["action"] = "relationships", ["reference"] = reference, ["direction"] = "both",
                    ["depth"] = 1, ["maxNodes"] = 500, ["maxEdges"] = 1000 }, cancellationToken)).GetRawText())!.AsObject();
                var observation = new JsonObject { ["objectPath"] = node["objectPath"]!.DeepClone() };
                foreach (string key in new[] { "status", "retainedForExport", "coverage", "nativeCoverage", "warnings", "truncated" })
                    if (observed[key] is not null) observation[key] = observed[key]!.DeepClone();
                relationships.Add(observation);
            }
            if (relationships.Count > 0)
            {
                export = JsonNode.Parse((await bridge.CallAsync("project_model", new { action = "export", snapshotId,
                    artifactDirectory = Path.Combine(options.ArtifactsDirectory, "project-explorer") }, cancellationToken)).GetRawText())!.AsObject();
                model = JsonNode.Parse(ProjectReviewStore.ReadArtifact(options.ArtifactsDirectory, export["artifact"]!.AsObject()))!.AsObject();
            }
            var documents = new JsonArray(); long totalBytes = 0;
            foreach (var node in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = node!["objectPath"]!.GetValue<string>();
                var row = new JsonObject { ["objectPath"] = path, ["status"] = "unavailable" };
                documents.Add(row);
                string language = node["metadata"]?["programmingLanguage"]?.GetValue<string>() ?? "";
                if (node["metadata"]?["isKnowHowProtected"]?.GetValue<bool>() == true || !new[] { "SCL", "STL", "LAD", "FBD", "DB" }.Contains(language, StringComparer.OrdinalIgnoreCase))
                { row["reason"] = "Protected/safety block content is outside this review export route."; row["skipped"] = true; continue; }
                try
                {
                    var block = JsonNode.Parse((await bridge.CallAsync("block_read", new { blockPath = path,
                        artifactDirectory = Path.Combine(options.ArtifactsDirectory, "exports") }, cancellationToken)).GetRawText())!.AsObject();
                    if (block["block"]?["path"]?.GetValue<string>() != path)
                        throw new BridgeException("SnapshotIdentityMismatch", "Export resolved a different native block.");
                    var artifact = block["artifact"]!.AsObject();
                    byte[] bytes = ProjectReviewStore.ReadArtifact(options.ArtifactsDirectory, artifact, 16 * 1024 * 1024);
                    totalBytes += bytes.Length;
                    if (totalBytes > 40L * 1024 * 1024) throw new BridgeException("ArtifactCapacity", "Selected block exports exceed 40 MiB.");
                    string xml;
                    using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true)) xml = reader.ReadToEnd();
                    row["xml"] = xml; row["status"] = "exported"; row["artifact"] = artifact.DeepClone();
                    row["utf8Sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));
                }
                catch (BridgeOperationException error)
                {
                    // A completed read error is retained. Lost transport remains terminal and aborts capture.
                    row["reason"] = error.Message; row["errorCode"] = error.Code;
                }
            }
            var content = ProjectReviewStore.ContentCoverage(documents, allBlocks.Length, selected.Length);
            if (requested is null)
            {
                content["blockOffset"] = blockOffset;
                content["nextBlockOffset"] = blockOffset + selected.Length < allBlocks.Length ? blockOffset + selected.Length : null;
            }
            // A capture whose every attempted export was refused for a read-only project holds no block content at all;
            // saving it would let a later compare look clean without comparing any block. Refuse instead.
            if (content["attemptedBlocks"]!.GetValue<int>() > 0 && content["exportedBlocks"]!.GetValue<int>() == 0
                && documents.All(row => row?["skipped"] is not null || row?["errorCode"]?.GetValue<string>() == "ReadOnlyProject"))
                throw new BridgeException("ReadOnlyProject", "Every block export was refused because the project is open read-only, so no block content would be captured. "
                    + "Remediation: repeat the capture with fromTemporaryCopy:true; it captures a read-write copy and leaves the original unchanged.");
            if (input["requireAllContent"]?.GetValue<bool>() == true && content["degraded"]!.GetValue<bool>())
                throw new BridgeException("SnapshotContentIncomplete", "requireAllContent is set and block content is incomplete: "
                    + string.Join(", ", content["degradedReasons"]!.AsArray().Select(r => r!["code"]!.GetValue<string>())) + ". No review was saved.");
            var verification = await bridge.CallAsync("project_model", new { action = "verify", snapshotId }, cancellationToken);
            if (!verification.TryGetProperty("matchesCurrentMetadata", out var matched) || matched.ValueKind != JsonValueKind.True)
                throw new BridgeException("SnapshotInvalidated", "Native metadata changed during review capture; no persistent snapshot was accepted.");
            var coverage = new JsonObject { ["selectedBlocks"] = selected.Length, ["observedBlocks"] = allBlocks.Length,
                ["omittedBlocks"] = allBlocks.Length - selected.Length, ["originalSnapshotId"] = snapshotId,
                ["metadataVerification"] = JsonNode.Parse(verification.GetRawText()), ["nativeContentAtomic"] = false,
                ["relationshipObservations"] = relationships, ["relationshipCoverageComplete"] = false, ["content"] = content,
                ["temporaryCopy"] = temporaryCopy is null ? null : new JsonObject { ["source"] = temporaryCopy["source"]?.DeepClone(),
                    ["copy"] = temporaryCopy["project"]?.DeepClone(), ["archive"] = temporaryCopy["archive"]?.DeepClone(),
                    ["note"] = "Captured from a temporary read-write copy; the original project was closed unchanged and the copy stays open." },
                ["projectFolder"] = ProjectReviewStore.ProjectFolderManifest(model["project"]!["projectPath"]!.GetValue<string>(), input["hashProjectFolder"]?.GetValue<bool>() == true) };
            return ReviewResult(new ProjectReviewStore(options.ArtifactsDirectory).Save(model, documents, coverage));
        }
        catch (Exception error) when (ReviewError(error)) { return ReviewFailure(error); }
    }

    [McpServerTool(Name = "project_snapshot_import", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Import a committed upload containing a UTF-8 project-review.v1 saved review, at most 64 MiB, 50000 nodes/edges and 200 block/type XML documents combined. Documents require unique exact nonvirtual block/type paths. Preserves exact bytes and content-hash reviewId; verifies identities, XML hashes and derived signatures. Returns a persistent reviewId for offline compare/search/health/diagnosis after restart or upload release. Embedded paths are data and are never opened. Supplied native observations are not authenticated, live-verified or qualified. No TIA/PLC connection or project import.")]
    public CallToolResult ProjectSnapshotImport(string uploadRef)
    { try { return ReviewResult(new ProjectReviewStore(options.ArtifactsDirectory).Import(uploadRef)); } catch (Exception e) when (ReviewError(e)) { return ReviewFailure(e); } }

    [McpServerTool(Name = "project_snapshot_compare", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compare two persistent reviewIds: exact relative paths, metadata and captured native XML interfaces/networks; retain opaque changes and unknown content. Reports bounded static dependency paths with exact edge evidence separately for before and after snapshots. impactDepth 1..5 (default 3), maxImpactPaths 1..200 (default 100), maxImpactEdgeVisits 1..20000 (default 5000). Paths identify review candidates, not runtime effects; incomplete coverage and truncation stay explicit. Different project copies require explicit opt-in. Absence in partial capture never proves deletion. Does not start TIA.")]
    public CallToolResult ProjectSnapshotCompare(string beforeId, string afterId, bool allowDifferentProjectCopy = false, int offset = 0, int maxItems = 100,
        int impactDepth = 3, int maxImpactPaths = 100, int maxImpactEdgeVisits = 5000)
    { try { return ReviewResult(new ProjectReviewStore(options.ArtifactsDirectory).Compare(beforeId, afterId, allowDifferentProjectCopy, offset, maxItems, impactDepth, maxImpactPaths, maxImpactEdgeVisits)); } catch (Exception e) when (ReviewError(e)) { return ReviewFailure(e); } }

    [McpServerTool(Name = "project_snapshot_search", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Search saved review metadata and text inside exported native XML, including captured code/comments. Case-insensitive literal query; bounded pages. Saved snapshots survive host restart but do not track live edits. No TIA startup or script execution.")]
    public CallToolResult ProjectSnapshotSearch(string reviewId, string query, int offset = 0, int maxItems = 50)
    { try { return ReviewResult(new ProjectReviewStore(options.ArtifactsDirectory).Search(reviewId, query, offset, maxItems)); } catch (Exception e) when (ReviewError(e)) { return ReviewFailure(e); } }

    [McpServerTool(Name = "project_health_report", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Review a saved reviewId for native inconsistency, protected/unavailable source and exact relationship evidence. Optional testSuiteResults:[{relativePath,sha256}] selects up to 32 distinct retained result-review receipts for this exact review. Revalidates originals/report/JUnit; returns per-packet outcome/coverage summaries and download references in a separate <=128 KiB testSuite section. Counts are execution units/messages, not assertions or unique defects. offset/maxItems page metadata findings only; testsuite_result_review pages supplied findings. Empty or passing selections never establish whole-project health. No compile, physical/safety validation or native qualification.")]
    public CallToolResult ProjectHealthReport(string reviewId, int offset = 0, int maxItems = 100, JsonElement? testSuiteResults = null)
    { try { return ReviewResult(new ProjectReviewStore(options.ArtifactsDirectory).Health(reviewId, offset, maxItems,
        testSuiteResults is null ? null : TestSuiteStyleJobs.Parse<EngineeringArtifactReference[]>(Encoding.UTF8.GetBytes(testSuiteResults.Value.GetRawText())))); } catch (Exception e) when (ReviewError(e)) { return ReviewFailure(e); } }

    private static bool ReviewError(Exception e) => e is BridgeException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.Xml.XmlException;
    private static CallToolResult ReviewResult(JsonObject result)
    {
        result["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy;
        result["sourceAuthenticityVerified"] = false;
        return Result(result.ToJsonString(), false);
    }
    private static CallToolResult ReviewFailure(Exception e) => Result(JsonSerializer.Serialize(new { error = new {
        code = (e as BridgeException)?.Code ?? "ProjectReviewFailed", message = e.Message } }, BridgeProtocol.Json), true);
}
