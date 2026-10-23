using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class PublicCertificateTools(HostOptions options)
{
    [McpServerTool(Name = "public_certificate_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Compare supplied public certificates and declared store associations against one exact saved owner, at an explicit UTC time. Direct public-certificate-review-request.v1 JSON only: each phase is one DER/PEM certificate in base64 or explicitly unavailable. Rejects key containers, extra payloads and duplicate extensions before this operation retains input. Reports public fingerprints, validity, SAN/KU/EKU/basic constraints and opaque extensions. No trust-store access, network, signature/chain/revocation verification, private-key access, native mutation or deployment. Send only public metadata; client/request logs are outside this operation's control.")]
    public CallToolResult Review(JsonElement request) => Run(s => s.Review(System.Text.Encoding.UTF8.GetBytes(request.GetRawText())));

    [McpServerTool(Name = "public_certificate_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen a retained public-certificate review and revalidate original public input, exact saved owner and deterministic evaluation time. section: summary/observations; 1..200 rows within 128 KiB. artifact_read downloads complete public originals/reports. Missing certificate bytes, supplied associations and matching expected fingerprints do not establish native identity or endpoint trust.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<PublicCertificateWorkflowStore, JsonObject> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "PublicCertificateReviewFailed", message = e is BridgeException ? e.Message : "Public certificate review failed.", nativeAccessAttempted = false } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
