using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    private static readonly Lazy<FeatureCatalog> CatalogContracts = new(FeatureCatalog.LoadBundled);

    [McpServerTool(Name = "feature_catalog", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Query versioned feature contracts, implementation gaps, acceptance criteria and recorded fixture evidence without starting TIA. Evidence applies only to its recorded profiles; use target_capabilities for current-object availability. Supports bounded pagination and exact feature/domain/status filters.")]
    public CallToolResult FeatureCatalogQuery(string? featureId = null, string? domain = null, string? implementationStatus = null,
        int offset = 0, int maxItems = 100)
    {
        try { return Result(JsonSerializer.Serialize(CatalogContracts.Value.Query(featureId, domain, implementationStatus, offset, maxItems), BridgeProtocol.Json), false); }
        catch (BridgeException error) { return Result(JsonSerializer.Serialize(new { error = new { code = error.Code, message = error.Message } }, BridgeProtocol.Json), true); }
    }

    [McpServerTool(Name = "object_resolve", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Resolve a canonical native object identity using selectors such as kind, objectPath, name, scopePath, plcPath and projectName. Names are not globally unique; exact paths distinguish software units and groups. No mutation or export occurs.")]
    public Task<CallToolResult> ObjectResolve(JsonElement selectors, CancellationToken cancellationToken)
        => Call("object_resolve", ObjectArguments(selectors), cancellationToken);

    [McpServerTool(Name = "target_capabilities", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Probe the selected native target's services and typed metadata without mutation, export, compilation or login. selectors accepts kind/objectPath/name/scopePath/plcPath/projectName and maxItems. Separates API availability, bridge implementation, target blockers and unverified qualification. Read/write metadata never bypasses prepared-change validation.")]
    public Task<CallToolResult> TargetCapabilities(JsonElement selectors, CancellationToken cancellationToken)
        => Call("target_capabilities", ObjectArguments(selectors), cancellationToken);
}
