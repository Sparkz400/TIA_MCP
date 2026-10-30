using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

/// <summary>Read-only resources over what the base tools already expose: retained artifacts, saved reviews and the feature catalog.</summary>
[McpServerResourceType]
public sealed class TiaResources(HostOptions options)
{
    public const string ArtifactTemplate = "tia://artifacts/{+path}";
    public const string ReviewTemplate = "tia://project-reviews/{reviewId}";
    public const string FeatureTemplate = "tia://feature-catalog/{featureId}";
    internal const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxCompletions = 100;
    private static readonly Lazy<FeatureCatalog> Catalog = new(FeatureCatalog.LoadBundled);
    private static readonly Dictionary<string, string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "application/json", [".xml"] = "application/xml", [".aml"] = "application/xml", [".csv"] = "text/csv",
        [".md"] = "text/markdown", [".html"] = "text/html", [".txt"] = "text/plain", [".log"] = "text/plain", [".scl"] = "text/plain",
    };

    [McpServerResource(UriTemplate = ArtifactTemplate, Name = "artifact", Title = "Retained artifact")]
    [Description("A file under this Host's artifact directory, by path relative to it, at most 4 MiB. The same root and link checks as artifact_read apply; use artifact_read for larger files.")]
    public ResourceContents Artifact(string path)
    {
        var (bytes, relative) = Read(path, null);
        var uri = "tia://artifacts/" + relative.Replace('\\', '/');
        var type = TextTypes.GetValueOrDefault(Path.GetExtension(relative));
        if (type is not null)
            try { return new TextResourceContents { Uri = uri, MimeType = type, Text = new UTF8Encoding(false, true).GetString(bytes) }; }
            catch (DecoderFallbackException) { }
        return BlobResourceContents.FromBytes(bytes, uri, type ?? "application/octet-stream");
    }

    [McpServerResource(UriTemplate = ReviewTemplate, Name = "project-review", Title = "Saved project review", MimeType = "application/json")]
    [Description("A saved project-review.v1 document by its content-hash reviewId, at most 4 MiB; its bytes are verified against the ID. Larger reviews are read with artifact_read or the project_snapshot_* tools.")]
    public string Review(string reviewId)
    {
        if (!Regex.IsMatch(reviewId, "\\A[a-f0-9]{64}\\z")) throw new McpException("reviewId must be the exact 64-character content hash.");
        return Encoding.UTF8.GetString(Read("project-reviews/" + reviewId + ".json", reviewId).Bytes);
    }

    [McpServerResource(UriTemplate = "tia://feature-catalog", Name = "feature-catalog", Title = "Feature catalog summary", MimeType = "application/json")]
    [Description("Every bundled catalog feature with its tools, implementation and qualification status. feature_catalog or the feature template returns a feature's full contract and evidence.")]
    public static string CatalogSummary() => JsonSerializer.Serialize(new
    {
        catalogVersion = Catalog.Value.Version,
        features = Catalog.Value.Features.Select(feature => new
        {
            featureId = feature.GetProperty("featureId").GetString(), domain = feature.GetProperty("domain").GetString(),
            title = feature.GetProperty("title").GetString(), mcpTools = feature.GetProperty("mcpTools"),
            implementation = feature.GetProperty("implementation").GetProperty("status").GetString(),
            qualification = feature.GetProperty("qualification").GetProperty("status").GetString()
        })
    }, BridgeProtocol.Json);

    [McpServerResource(UriTemplate = FeatureTemplate, Name = "feature", Title = "Feature contract", MimeType = "application/json")]
    [Description("One catalog feature with its acceptance criteria and recorded evidence, as feature_catalog returns it for featureId.")]
    public static string Feature(string featureId)
    {
        try { return JsonSerializer.Serialize(Catalog.Value.Query(featureId), BridgeProtocol.Json); }
        catch (BridgeException error) { throw Refusal(error); }
    }

    private (byte[] Bytes, string Relative) Read(string path, string? sha256)
    {
        try
        {
            // ArtifactStore applies the root and reparse-point checks; ReadArtifact then verifies the bytes it returns.
            var artifact = new JsonObject { ["path"] = path };
            if (sha256 is not null) artifact["sha256"] = sha256;
            var probe = new ArtifactStore(options.ArtifactsDirectory).Read(path, 0, 1);
            if (probe.Bytes > MaxBytes) throw new McpException("The artifact exceeds 4 MiB; read it in chunks with artifact_read.");
            return (ProjectReviewStore.ReadArtifact(options.ArtifactsDirectory, artifact, MaxBytes), probe.RelativePath);
        }
        catch (BridgeException error) { throw Refusal(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new McpException("ArtifactReadFailed: the artifact could not be read."); }
    }

    private static McpProtocolException Refusal(BridgeException error) => new(error.Code + ": " + error.Message,
        error.Code is "ArtifactUnavailable" or "FeatureNotFound" ? McpErrorCode.ResourceNotFound : McpErrorCode.InvalidParams);

    /// <summary>completion/complete for the resource templates; prompt arguments are free text and get no suggestions.</summary>
    internal static ValueTask<CompleteResult> Complete(RequestContext<CompleteRequestParams> context, CancellationToken cancellationToken)
    {
        var request = context.Params!;
        var prefix = request.Argument.Value ?? "";
        var root = context.Services!.GetRequiredService<HostOptions>().ArtifactsDirectory;
        var values = (request.Ref as ResourceTemplateReference)?.Uri switch
        {
            FeatureTemplate when request.Argument.Name == "featureId" => Catalog.Value.Features.Select(f => f.GetProperty("featureId").GetString()!),
            ArtifactTemplate when request.Argument.Name == "path" => ArtifactPaths(root),
            ReviewTemplate when request.Argument.Name == "reviewId" => ArtifactPaths(Path.Combine(root, "project-reviews"))
                .Where(name => Regex.IsMatch(name, "\\A[a-f0-9]{64}\\.json\\z")).Select(name => name[..64]),
            _ => []
        };
        var matches = values.Where(value => value.StartsWith(prefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).Take(MaxCompletions + 1).ToList();
        return ValueTask.FromResult(new CompleteResult { Completion = new Completion
            { Values = matches.Take(MaxCompletions).ToList(), HasMore = matches.Count > MaxCompletions } });
    }

    // Names only, never contents; links are not followed and the walk is bounded, so completion stays cheap and inside the root.
    internal static IEnumerable<string> ArtifactPaths(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        return Directory.EnumerateFiles(directory, "*", options).Take(20000)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/')).Order(StringComparer.Ordinal);
    }
}
