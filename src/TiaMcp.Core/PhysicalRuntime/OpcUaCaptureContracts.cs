using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core.Diagnostics;

namespace TiaMcp.Core.PhysicalRuntime;

public sealed record OpcUaCaptureRequest(string AttemptId, OpcUaRuntimeNode[] Nodes, int IntervalMilliseconds,
    int DurationSeconds, int MaximumSamples = 100000, long MaximumBytes = 64L << 20,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DiagnosticBindingId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int MaximumReconnectAttempts = 0);
public sealed record OpcUaCaptureContext(OpcUaCaptureRequest Request, OpcUaRuntimeStatus Session);

/// <summary>UA-specific admission and retained evidence validation; the durable capture journal is shared.</summary>
public static class OpcUaCaptureContracts
{
    public static string SelectionId(OpcUaRuntimeNode node) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(node, BridgeProtocol.Json)))[..32];
    public static RuntimeCaptureRequest StorageRequest(OpcUaCaptureRequest request)
    {
        Require(request is not null && request.Nodes is { Length: >= 1 and <= 64 });
        DiagnosticUaBindings.ValidateId(request.DiagnosticBindingId);
        Require(request.MaximumReconnectAttempts is >= 0 and <= 3);
        foreach (var node in request.Nodes) ValidateNode(node);
        Require(request.Nodes.Distinct().Count() == request.Nodes.Length);
        var result = new RuntimeCaptureRequest(request.AttemptId, request.Nodes.Select(SelectionId).ToArray(),
            request.IntervalMilliseconds, request.DurationSeconds, request.MaximumSamples, request.MaximumBytes,
            Representation: "raw");
        RuntimeCaptureStore.Validate(result);
        return result;
    }
    public static void ValidatePlan(RuntimeCapturePlan plan)
    {
        var context = plan.UaContext!;
        var request = StorageRequest(context.Request);
        Require(plan.Signals.Length == 0 && plan.Association is null && plan.Handoff is null &&
            JsonElement.DeepEquals(JsonSerializer.SerializeToElement(request, BridgeProtocol.Json),
                JsonSerializer.SerializeToElement(plan.Request, BridgeProtocol.Json)));
        var session = context.Session;
        Require(session.SchemaVersion == "opcua-runtime-session.v1" && session.AttemptId == request.AttemptId &&
            session.HostId == plan.HostId && session.State == "connected" && session.PeerVerified &&
            session.ErrorCode is null && session.OperationErrorCode is null);
    }
    public static void ValidateRead(RuntimeCapturePlan plan, RuntimeCaptureRow row)
    {
        if (row.ErrorCode is not null)
        {
            Require(ReadErrors.Contains(row.ErrorCode, StringComparer.Ordinal));
            return;
        }
        using var doc = JsonDocument.Parse(row.ReadResultJson!);
        var result = doc.RootElement.Deserialize<OpcUaRuntimeReadResult>(new JsonSerializerOptions(BridgeProtocol.Json) {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true })!;
        var index = Array.IndexOf(plan.Request.SelectionIds, row.SelectionId);
        Require(result is not null && result.AttemptId == plan.Request.AttemptId && result.Items is { Count: 1 } && index >= 0 &&
            result.Items[0].Node == plan.UaContext!.Request.Nodes[index] &&
            JsonElement.DeepEquals(doc.RootElement, JsonSerializer.SerializeToElement(result, BridgeProtocol.Json)));
        var item = result.Items[0];
        if (item.DataType is not null) ValidateNode(item.DataType);
        Require(item.ArrayDimensions is null || item.ArrayDimensions.Length <= 4096);
        Require(item.StatusCode is { Length: 10 } && item.StatusCode.StartsWith("0x", StringComparison.Ordinal) &&
            uint.TryParse(item.StatusCode.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _));
        uint status = uint.Parse(item.StatusCode.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        Require(item.StatusCode == $"0x{status:x8}" && item.Quality == ((status & 0x80000000) != 0 ? "Bad" : (status & 0x40000000) != 0 ? "Uncertain" : "Good"));
        Require(item.Outcome is "value" or "itemError" or "metadataError" or "unsupported");
        bool nullValue = item.Value is null || item.Value is JsonElement { ValueKind: JsonValueKind.Null };
        if (item.Outcome != "value") Require(nullValue && item.Encoding is null && item.ErrorCode is not null);
        else
        {
            Require(item.Quality != "Bad" && item.DataType is not null && item.ValueRank is not null && item.ErrorCode is null);
            if (item.Encoding == "array") ValidateArray(item);
            else
            {
                Require((item.ValueRank is < 0 || item.Encoding == "null") && item.Encoding is "null" or "boolean" or "decimalInteger" or "roundTripFloat" or "roundTripDouble" or "string" or "utcDateTime" or "guid" or "base64");
                ValidateScalar(item, nullValue);
            }
        }
        if (item.Quality == "Bad") Require(item.Outcome == "itemError");
    }
    public static void ValidateReconnectRows(RuntimeCapturePlan plan,IEnumerable<RuntimeCaptureRow> rows)
    {
        var history=new OpcUaReconnectHistory(plan.UaContext!.Request.MaximumReconnectAttempts);
        string? error=null;double elapsed=0;
        foreach(var row in rows)
        {
            Require(row.ElapsedMilliseconds>=elapsed);elapsed=row.ElapsedMilliseconds;
            if(row.Reconnect is { } e)
            {
                if(e.State=="starting")Require(e.ErrorCode==error && OpcUaReconnectHistory.Retryable(error));
                history.Apply(e);
                if(e.State!="starting")error=e.ErrorCode;
            }
            else if(row.Kind=="read")
            {
                Require(history.Healthy && !history.Pending && error is null);
                error=row.ErrorCode;
            }
        }
    }
    public static string? ScalarEncoding(string? type) => type switch {
        "Boolean" => "boolean",
        "SByte" or "Byte" or "Int16" or "UInt16" or "Int32" or "UInt32" or "Int64" or "UInt64" => "decimalInteger",
        "Float" => "roundTripFloat", "Double" => "roundTripDouble", "String" => "string",
        "DateTime" => "utcDateTime", "Guid" => "guid", "ByteString" => "base64", _ => null };
    public static bool ArrayShapeMatches(int? rank, uint[]? declared, int[]? dimensions, int? count)
    {
        if (rank is null or < -3 or -1 or > 32 || declared is { Length: > 32 }) return false;
        if (dimensions is null) return count is null;
        if (dimensions.Length is < 1 or > 32 || count is null or < 0 or > 4096 ||
            dimensions.Any(d => d is < 0 or > 4096) || rank > 0 && rank != dimensions.Length ||
            rank == -3 && dimensions.Length != 1) return false;
        if (declared is { Length: > 0 } && (declared.Length != dimensions.Length ||
            declared.Where((d, i) => d != 0 && dimensions[i] > d).Any())) return false;
        long product = dimensions.Contains(0) ? 0 : 1;
        if (product != 0) foreach (int d in dimensions) { product *= d; if (product > 4096) return false; }
        return product == count;
    }
    private static void ValidateArray(OpcUaRuntimeValue item)
    {
        Require(item.Value is JsonElement { ValueKind: JsonValueKind.Object });
        var json = (JsonElement)item.Value!;
        OpcUaRuntimeArray? array;
        try { array = json.Deserialize<OpcUaRuntimeArray>(new JsonSerializerOptions(BridgeProtocol.Json) {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true }); }
        catch (JsonException) { throw new BridgeException("UaCaptureInvalid", "Invalid retained UA array."); }
        Require(array is not null && JsonElement.DeepEquals(json, JsonSerializer.SerializeToElement(array, BridgeProtocol.Json)) &&
            ScalarEncoding(item.BuiltInType) is { } encoding && array.ElementEncoding == encoding &&
            ArrayShapeMatches(item.ValueRank, item.ArrayDimensions, array.Dimensions, array.Elements?.Length));
        foreach (var element in array.Elements ?? [])
        {
            bool isNull = element is null || element is JsonElement { ValueKind: JsonValueKind.Null };
            Require(!isNull || item.BuiltInType is "String" or "ByteString");
            ValidateScalar(item with { Encoding = isNull ? "null" : array.ElementEncoding, Value = element }, isNull);
        }
    }
    private static void ValidateScalar(OpcUaRuntimeValue item, bool nullValue)
    {
        // DataType is an original server declaration, possibly custom/abstract. Validate its
        // identity above; do not invent a subtype proof by equating it to decoded BuiltInType.
        if (item.Encoding == "null")
        {
            Require(nullValue && item.BuiltInType is null or "Null" or "Boolean" or "SByte" or "Byte" or "Int16" or "UInt16" or
                "Int32" or "UInt32" or "Int64" or "UInt64" or "Float" or "Double" or "String" or "DateTime" or "Guid" or "ByteString");
            return;
        }
        if (item.BuiltInType == "Boolean")
        {
            Require(item.Encoding == "boolean" && item.Value is JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False });
            return;
        }
        Require(item.Value is JsonElement { ValueKind: JsonValueKind.String });
        string text = ((JsonElement)item.Value!).GetString()!;
        bool valid = item.BuiltInType switch {
            "SByte" or "Byte" or "Int16" or "UInt16" or "Int32" or "UInt32" or "Int64" or "UInt64" =>
                item.Encoding == "decimalInteger" && Integer(text,item.BuiltInType),
            "Float" => item.Encoding == "roundTripFloat" && float.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var f) &&
                float.IsFinite(f) && f.ToString("R",CultureInfo.InvariantCulture) == text,
            "Double" => item.Encoding == "roundTripDouble" && double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var d) &&
                double.IsFinite(d) && d.ToString("R",CultureInfo.InvariantCulture) == text,
            "String" => item.Encoding == "string" && text.Length <= 16384,
            "DateTime" => item.Encoding == "utcDateTime" && DateTime.TryParseExact(text,"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var date) &&
                date.Kind == DateTimeKind.Utc && date.ToString("O",CultureInfo.InvariantCulture) == text,
            "Guid" => item.Encoding == "guid" && Guid.TryParseExact(text,"D",out var guid) && guid.ToString("D") == text,
            "ByteString" => item.Encoding == "base64" && ByteString(text),
            _ => false };
        Require(valid);
    }
    private static bool Integer(string text,string type)
    {
        if (text.Length > 20 || !BigInteger.TryParse(text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var n) ||
            n.ToString(CultureInfo.InvariantCulture) != text) return false;
        return type switch {
            "SByte" => n >= sbyte.MinValue && n <= sbyte.MaxValue, "Byte" => n >= byte.MinValue && n <= byte.MaxValue,
            "Int16" => n >= short.MinValue && n <= short.MaxValue, "UInt16" => n >= ushort.MinValue && n <= ushort.MaxValue,
            "Int32" => n >= int.MinValue && n <= int.MaxValue, "UInt32" => n >= uint.MinValue && n <= uint.MaxValue,
            "Int64" => n >= long.MinValue && n <= long.MaxValue, "UInt64" => n >= ulong.MinValue && n <= ulong.MaxValue, _ => false };
    }
    private static bool ByteString(string text)
    { try { var bytes=Convert.FromBase64String(text);return bytes.Length<=65536 && Convert.ToBase64String(bytes)==text; } catch { return false; } }
    public static readonly string[] ReadErrors = ["UaCancelled", "UaTimeout", "UaTransportInterrupted", "UaSessionUnavailable", "UaSessionExpired",
        "UaSessionRecordChanged", "UaSessionIdentityMismatch", "UaSessionIdentityChanged", "UaCredentialUnavailable",
        "UaNamespaceUnavailable", "UaAuthenticationDenied", "UaCertificateRejected", "UaProtocolFailure",
        "UaResponseInvalid", "UaResponseLimit", "UaSecretEchoRejected", "UaCaptureReadFailed"];
    public static string ReadError(Exception error) => error is OperationCanceledException ? "UaCancelled" :
        error is BridgeException e && ReadErrors.Contains(e.Code, StringComparer.Ordinal) ? e.Code : "UaCaptureReadFailed";
    public static void ValidateNode(OpcUaRuntimeNode node)
    {
        Require(node is not null && !string.IsNullOrEmpty(node.NamespaceUri) && node.NamespaceUri.Length <= 4096 &&
            Uri.TryCreate(node.NamespaceUri, UriKind.Absolute, out _) && node.Identifier is { Length: > 0 and <= 4096 });
        bool valid = node.IdentifierType switch {
            "numeric" => uint.TryParse(node.Identifier, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n.ToString(CultureInfo.InvariantCulture) == node.Identifier,
            "string" => !node.Identifier.Any(char.IsControl),
            "guid" => Guid.TryParseExact(node.Identifier, "D", out var g) && g.ToString("D") == node.Identifier,
            "opaque" => Opaque(node.Identifier), _ => false };
        Require(valid);
    }
    private static bool Opaque(string value) { try { return Convert.ToBase64String(Convert.FromBase64String(value)) == value; } catch { return false; } }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition) { if (!condition) throw new BridgeException("UaCaptureInvalid", "Invalid UA capture selection or retained evidence."); }
}
