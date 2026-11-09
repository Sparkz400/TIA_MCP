using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "hmi_classic_screen_capture", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Capture all regular screens of one exact Classic RT Advanced HMI under exclusive access. Request object: projectPath, target identity, optional includeTags=false or tagReferences:[exact native HMI member paths] to select required tables. Optional area:{plcPath,blockFolderPath,typePaths?,prototypeScreens?,hmiTagRoots?} collects up to 64 PLC-root area blocks, selected types, HMI members and reusable native control metadata in one retained report; inconsistent blocks return CompileRequired with the plc_compile route. Returns contentHash, hashed XML/capture artifacts and optional areaPreparation. No compilation or project mutation; meanings and runtime behavior remain unverified.")]
    public Task<CallToolResult> HmiClassicScreenCapture(JsonElement request, CancellationToken cancellationToken)
        => Call("hmi_classic_screen_capture", HmiArguments(request), cancellationToken);

    [McpServerTool(Name = "hmi_classic_screen_compose", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compose a Classic screen from hashed capture artifacts, without changing TIA. Request object: capture {path,sha256}, screenPath, optional removeItems, layers:[{sourceScreenPath,sourceLayer}] and components:[{sourceScreenPath,sourceItem,name,layer?,attributes?,tagReplacements?,texts?,captions?,interfaceValues?,eventParameters?,visibility?}]. captions:[{compositionName,culture,text}] supports plain or single body/p text preserving formatting; interfaceValues maps existing property names to typed constants; eventParameters:[{eventName,functionName,parameterName,value}] edits existing constants only; visibility:{sourceScreenPath,sourceItem,tag,rangeStart,rangeEnd} clones a native condition. Retains events/permissions/unknown fields. Returns XML, inert approximate multilingual HTML preview and layout findings for change_prepare. No project scripts/runtime execution or inferred array indices. Unsupported native shapes refuse.")]
    public Task<CallToolResult> HmiClassicScreenCompose(JsonElement request, CancellationToken cancellationToken)
        => Call("hmi_classic_screen_compose", HmiArguments(request), cancellationToken);

    [McpServerTool(Name = "hmi_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect bounded native HMI sections: devices, connections, tags, screens, items, alarms, scripts or resources, or opt-in section=model for Unified typed whole-HMI attribute readback (optional modelRoots; script code hashed, secrets excluded). Select exact hmiPath/family and objectPath returned by discovery; repeated names are not identities. Classic screens include regular, template, popup, slide-in, global and overview roles with per-scope coverage; additional roles expose metadata without qualified item parsing. Per-section coverage distinguishes unavailable and unsupported data. Script inspection never executes project code. Request schema: contracts/hmi-inspection.v1.schema.json.")]
    public Task<CallToolResult> HmiInspect(JsonElement request, CancellationToken cancellationToken)
        => Call("hmi_inspect", HmiArguments(request), cancellationToken);

    [McpServerTool(Name = "hmi_references", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read bounded HMI relationships and native evidence for exact target identities. Dynamic references, ambiguous names and incomplete PLC mappings stay unresolved and cannot authorize rewiring. Does not execute scripts or connect online.")]
    public Task<CallToolResult> HmiReferences(JsonElement request, CancellationToken cancellationToken)
        => Call("hmi_references", HmiArguments(request), cancellationToken);

    [McpServerTool(Name = "hmi_capabilities", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Report optional Classic and Unified V21 installation/API evidence, bridge implementation, qualification gates and current target availability separately. Installation alone does not establish a license, access or native qualification.")]
    public Task<CallToolResult> HmiCapabilities(JsonElement request, CancellationToken cancellationToken)
        => Call("hmi_capabilities", ObjectArguments(request), cancellationToken);

    private System.Text.Json.Nodes.JsonObject HmiArguments(JsonElement request)
    {
        var result = System.Text.Json.Nodes.JsonNode.Parse(ObjectArguments(request).GetRawText())!.AsObject();
        result["artifactDirectory"] = Path.Combine(options.ArtifactsDirectory, "hmi");
        return result;
    }

    private System.Text.Json.Nodes.JsonObject ClassicScreenArtifactPayload(JsonElement payload)
    {
        var result = System.Text.Json.Nodes.JsonNode.Parse(payload.GetRawText())!.AsObject();
        var members = new List<System.Text.Json.Nodes.JsonObject> { result };
        if (result.ContainsKey("additionalScreens"))
        {
            if (result["additionalScreens"] is not System.Text.Json.Nodes.JsonArray extra || extra.Count is < 1 or > 7
                || extra.Any(x => x is not System.Text.Json.Nodes.JsonObject))
                throw new ArgumentException("additionalScreens must contain 1 to 7 screen objects.");
            members.AddRange(extra.Select(x => x!.AsObject()));
        }
        if (members.Count > 8) throw new ArgumentException("A screen batch is bounded to eight members.");
        var store = new ArtifactStore(options.ArtifactsDirectory);
        foreach (var member in members)
        {
            if (!member.ContainsKey("xmlArtifact")) continue;
            if (member.ContainsKey("xml") || member["xmlArtifact"] is not System.Text.Json.Nodes.JsonObject artifact
                || (artifact.Count != 2 && (artifact.Count != 3 || !artifact.ContainsKey("bytes")))
                || artifact["path"] is not System.Text.Json.Nodes.JsonValue pathValue || !pathValue.TryGetValue<string>(out var inputPath)
                || artifact["sha256"] is not System.Text.Json.Nodes.JsonValue hashValue || !hashValue.TryGetValue<string>(out var expectedHash)
                || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
                throw new ArgumentException("Use xml or xmlArtifact {path,sha256,bytes?}, never both; artifact paths and SHA-256 values must be strings.");
            if (!Path.IsPathFullyQualified(inputPath)) throw new ArgumentException("Screen artifact paths must be absolute.");
            string path = store.TryResolve(inputPath) ?? throw new ArgumentException("Screen artifacts must be retained under this Host's artifact root.");
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new ArgumentException("Screen XML artifact exceeds its byte bound.");
            store.Read(path, 0, 1); // Reuse the junction/symlink guard.
            using var stream = File.OpenRead(path);
            if (stream.Length > 8 * 1024 * 1024) throw new ArgumentException("Screen XML artifact exceeds its byte bound.");
            if (artifact.ContainsKey("bytes") && (artifact["bytes"] is not System.Text.Json.Nodes.JsonValue byteValue
                || !byteValue.TryGetValue<long>(out var expectedBytes) || expectedBytes != stream.Length))
                throw new ArgumentException("Screen XML artifact byte count differs from its retained reference.");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Screen XML artifact changed since review.");
            member["xml"] = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            member.Remove("xmlArtifact");
        }
        return result;
    }
}
