using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TiaMcp.Core.Inventory;

public sealed record InventoryBinding(string RowId, uint HardwareId);

public interface IInventoryIdentityFactory
{
    SiemensWebIdentity Create(string endpoint, string? token);
}

public sealed class SiemensWebIdentityFactory : IInventoryIdentityFactory
{
    public SiemensWebIdentity Create(string endpoint, string? token) => new(endpoint, token);
}

/// <summary>Read-only subset of Siemens Web API. No engineering settings or PLC program changes.</summary>
public sealed class SiemensWebIdentity : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string? token;
    public string Endpoint => endpoint.GetLeftPart(UriPartial.Authority);
    public const string Provider = "siemensWebApiIm0";

    public SiemensWebIdentity(string endpoint, string? token = null, HttpMessageHandler? testHandler = null)
    {
        this.endpoint = ValidateEndpoint(endpoint); this.token = token;
        // Never forward a bearer token through a redirect or workstation proxy.
        client = new HttpClient(testHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    }

    public static Uri ValidateEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath is not ("/" or "/api/jsonrpc"))
            throw new BridgeException("InvalidRequest", "endpoint must be an HTTPS controller origin or /api/jsonrpc URL without credentials, query or fragment.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/api/jsonrpc");
    }

    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        var methods = await RpcAsync("Api.Browse", null, cancellationToken);
        if (methods.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        var available = methods.EnumerateArray().Select(m => Text(m, "name")).ToHashSet(StringComparer.Ordinal);
        if (!available.Contains("Modules.Browse") || !available.Contains("Modules.ReadIdentificationMaintenance"))
            throw new BridgeException("UnsupportedInterface", "The controller does not advertise Modules.Browse and Modules.ReadIdentificationMaintenance. Use a qualified device-family adapter; no configured-value fallback is allowed.");
    }

    public async Task<JsonElement> BrowseAsync(uint? hardwareId, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object> { ["mode"] = "children" };
        if (hardwareId.HasValue) parameters["hwid"] = hardwareId.Value;
        var result = await RpcAsync("Modules.Browse", parameters, cancellationToken);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();
        return result;
    }

    public async Task<JsonElement> NodeAsync(uint hardwareId, CancellationToken cancellationToken)
    {
        var result = await RpcAsync("Modules.Browse", new { hwid = hardwareId, mode = "node" }, cancellationToken);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        var matches = nodes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object && n.TryGetProperty("hwid", out var id) &&
            id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out var value) && value == hardwareId).ToArray();
        if (matches.Length != 1) throw new BridgeException("IdentityMismatch", "The controller did not return exactly the selected hardware ID.");
        return matches[0].Clone();
    }

    public async Task<InventoryRow> IdentifyAsync(InventoryRow row, uint hardwareId, CancellationToken cancellationToken)
    {
        var node = await NodeAsync(hardwareId, cancellationToken);
        if (!string.Equals(Text(node, "name"), row.Name, StringComparison.Ordinal))
            throw new BridgeException("IdentityMismatch", "Online module name differs from the selected snapshot row. Review the mapping and downloaded configuration.");
        var result = await RpcAsync("Modules.ReadIdentificationMaintenance", new { hwid = hardwareId, number = 0, type = "actual" }, cancellationToken);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var source = Endpoint + "/api/jsonrpc#Modules.ReadIdentificationMaintenance/actual/0/" + hardwareId;
        var actual = new Dictionary<string, InventoryField>
        {
            ["mlfb"] = OnlineField(Text(data, "order_number"), source),
            ["serialNumber"] = OnlineField(Text(data, "serial_number"), source),
            ["hardwareRevision"] = OnlineField(Number(data, "hardware_revision"), source),
            ["firmware"] = OnlineField(ReadFirmware(data), source)
        };
        return (row with { Actual = actual, Online = new(actual.Values.All(f => f.State == "available") ? "read" : "partial",
            Endpoint, hardwareId, DateTimeOffset.UtcNow, null,
            "Explicit controller/row/hardware-ID binding plus exact online name; downloaded configuration equivalence is not proven.") }).Compare();
    }

    public static InventoryRow Failure(InventoryRow row, string endpoint, uint hwid, BridgeException error)
    {
        string state = error.Code switch
        {
            "UnsupportedInterface" or "ImNotSupported" => "unsupported", "PermissionDenied" => "permissionDenied",
            "IdentityMismatch" => "identityMismatch", "OnlineTimeout" or "OnlineUnreachable" => "unreachable", _ => "readError"
        };
        return (row with { Actual = InventoryRow.ActualUnavailable(state, error.Message),
            Online = new(state, endpoint, hwid, DateTimeOffset.UtcNow, error.Message) }).Compare();
    }

    private async Task<JsonElement> RpcAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString("N");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var requestCancellation = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrEmpty(token)) request.Headers.Add("X-Auth-Token", token);
        var body = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters != null) body["params"] = parameters;
        request.Content = JsonContent.Create(body);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new BridgeException("PermissionDenied", "This controller requires an authorized Web API session with read_diagnostics permission.");
            if (!response.IsSuccessStatusCode) throw new BridgeException("OnlineHttpError", "Controller returned HTTP " + (int)response.StatusCode + ". No redirect or authentication retry was attempted.");
            const int maximum = 256 * 1024;
            if (response.Content.Headers.ContentLength > maximum) throw new BridgeException("OnlineResponseLimit", "Response exceeds 256 KiB; select a smaller hardware subtree.");
            await using var input = await response.Content.ReadAsStreamAsync(requestCancellation);
            using var output = new MemoryStream(); var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, requestCancellation)) > 0)
            {
                if (output.Length + count > maximum) throw new BridgeException("OnlineResponseLimit", "Response exceeds 256 KiB; select a smaller hardware subtree.");
                output.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "jsonrpc") != "2.0" || Text(root, "id") != id ||
                root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
                root.TryGetProperty("result", out _) == root.TryGetProperty("error", out _)) throw InvalidResponse();
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("code", out var codeNode) ||
                    codeNode.ValueKind != JsonValueKind.Number || !codeNode.TryGetInt32(out int code)) throw InvalidResponse();
                // Do not echo remote messages or request bodies: they can contain credentials or untrusted text.
                throw code switch
                {
                    2 => new BridgeException("PermissionDenied", "The Web API session lacks read_diagnostics permission."),
                    -32601 => new BridgeException("UnsupportedInterface", "This controller does not support the requested identification method."),
                    1100 => new BridgeException("IdentityMismatch", "The hardware ID is invalid for this controller."),
                    1102 => new BridgeException("OnlineUnreachable", "The controller cannot read I&M data from this module."),
                    1103 => new BridgeException("ImNotSupported", "The selected module does not support I&M0."),
                    _ => new BridgeException("OnlineApiError", "Siemens Web API error code " + code + ".")
                };
            }
            if (!root.TryGetProperty("result", out var result)) throw InvalidResponse();
            return result.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new BridgeException("OnlineTimeout", "The read exceeded the 10-second request timeout."); }
        catch (HttpRequestException) { throw new BridgeException("OnlineUnreachable", "HTTPS connection failed. Check routing, Web API availability and certificate trust; TLS verification remains enabled."); }
        catch (IOException) { throw new BridgeException("OnlineUnreachable", "The HTTPS response could not be read completely. Retry identification after checking the connection."); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private static InventoryField OnlineField(string? value, string source) => string.IsNullOrWhiteSpace(value)
        ? InventoryField.Missing("notExposed", "The actual I&M0 response did not supply this value.", source)
        : value.Length > 4096 ? InventoryField.Missing("readError", "Actual value exceeds the field bound.", source)
        : InventoryField.Read(value.Trim(), source);
    private static string? Text(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? Number(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    private static string? ReadFirmware(JsonElement data)
    {
        if (!data.TryGetProperty("software_revision", out var revision) || revision.ValueKind != JsonValueKind.Object) return null;
        string? type = Text(revision, "type"), major = Number(revision, "major"), minor = Number(revision, "minor"), patch = Number(revision, "patch");
        return type == null || major == null || minor == null || patch == null ? null : type + major + "." + minor + "." + patch;
    }
    private static BridgeException InvalidResponse() => new("InvalidOnlineResponse", "The response does not match the Siemens Web API response contract.");
    public void Dispose() => client.Dispose();
}
