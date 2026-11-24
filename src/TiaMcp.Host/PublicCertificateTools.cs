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
    [Description("Compare supplied public certificates and declared store associations against one exact saved owner at an explicit UTC time. Direct public-certificate-review-request.v1 JSON; each phase is one base64 DER/PEM certificate or explicitly unavailable. Rejects key containers and extra payloads. Reports fingerprints, validity, SAN/KU/EKU. No trust-store access, network, signature/chain/revocation check, private-key access, mutation or deployment. Send only public metadata.")]
    public CallToolResult Review(JsonElement request) => Run(s => s.Review(System.Text.Encoding.UTF8.GetBytes(request.GetRawText())));

    [McpServerTool(Name = "public_certificate_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen a retained public-certificate review and revalidate original public input, exact saved owner and deterministic evaluation time. section: summary/observations; 1..200 rows within 128 KiB. artifact_read downloads complete public originals/reports. Missing certificate bytes, supplied associations and matching expected fingerprints do not establish native identity or endpoint trust.")]
    public CallToolResult Page(string recordRef, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordRef, section, offset, count));
    private CallToolResult Run(Func<PublicCertificateWorkflowStore, JsonObject> action) =>
        ToolResult.Guard(() => action(new(options.ArtifactsDirectory)), "PublicCertificateReviewFailed", "Public certificate review failed.", false);
}
