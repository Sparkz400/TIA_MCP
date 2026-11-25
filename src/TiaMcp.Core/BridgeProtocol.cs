using System.Text;
using System.Text.Json;

namespace TiaMcp.Core;

public static class BridgeProtocol
{
    public const int Version = 1;
    public const int MaxRequestBytes = 4 * 1024 * 1024;
    public const int MaxResponseBytes = 16 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string EncodeRequest(string id, string operation, object? arguments)
    {
        string json;
        try { json = JsonSerializer.Serialize(new { protocolVersion = Version, id, operation, arguments = arguments ?? new { } }, Json); }
        catch (JsonException ex) { throw new BridgeException("InvalidRequest", "The request arguments cannot be written as JSON (nested too deep?); nothing was sent.", ex); }
        if (Encoding.UTF8.GetByteCount(json) > MaxRequestBytes)
            throw new BridgeException("RequestTooLarge", "The bridge request exceeds 4 MiB.");
        RequireWorkerReadable(json);
        return json;
    }

    // The workers load a request with duplicate names refused and at most 64 levels. A request they cannot load comes
    // back without its id, which the Host can only treat as a protocol fault that ends the worker session (attachment
    // and open projects included). Tool arguments pass through as JsonElement, so both are refused here, before
    // anything is sent.
    private const int MaxRequestDepth = 64;
    private static void RequireWorkerReadable(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = MaxRequestDepth });
        var names = new Stack<HashSet<string>?>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: names.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                    case JsonTokenType.StartArray: names.Push(null); break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray: names.Pop(); break;
                    case JsonTokenType.PropertyName:
                        string name = reader.GetString()!;
                        if (!names.Peek()!.Add(name))
                            throw new BridgeException("InvalidRequest", $"The request repeats the property name '{(name.Length > 64 ? name[..64] : name)}' in one object; nothing was sent.");
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new BridgeException("InvalidRequest", $"The request is nested deeper than {MaxRequestDepth - 2} levels or is not valid JSON; nothing was sent.", ex);
        }
    }

    public static JsonElement DecodeResponse(string json, string expectedId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("protocolVersion").GetInt32() != Version || root.GetProperty("id").GetString() != expectedId)
                throw new BridgeException("ProtocolMismatch", "The worker returned an unexpected protocol version or request ID.");
            if (!root.GetProperty("success").GetBoolean())
            {
                var error = root.GetProperty("error");
                throw new BridgeOperationException(error.GetProperty("code").GetString() ?? "WorkerError",
                    error.GetProperty("message").GetString() ?? "The worker reported an error.",
                    error.TryGetProperty("details", out var details) ? details.Clone() : null);
            }
            return root.GetProperty("result").Clone();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new BridgeException("InvalidResponse", "The worker returned a malformed response.", ex);
        }
    }
}

public class BridgeException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed class BridgeOperationException(string code, string message, JsonElement? details = null) : BridgeException(code, message)
{
    public JsonElement? Details { get; } = details;
}

/// <summary>Bounds allocation before parsing a line and retains buffered bytes between frames.</summary>
public sealed class BoundedLineReader(Stream stream, int maxBytes)
{
    private readonly byte[] _buffer = new byte[8192];
    private int _position;
    private int _length;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        using var frame = new MemoryStream();
        while (true)
        {
            if (_position == _length)
            {
                _length = await stream.ReadAsync(_buffer, cancellationToken);
                _position = 0;
                if (_length == 0)
                    throw new EndOfStreamException("Worker disconnected before completing a response.");
            }
            var newline = Array.IndexOf(_buffer, (byte)'\n', _position, _length - _position);
            var end = newline < 0 ? _length : newline;
            var count = end - _position;
            if (frame.Length + count > maxBytes)
                throw new BridgeException("ResponseTooLarge", $"Worker response exceeded {maxBytes} bytes.");
            frame.Write(_buffer, _position, count);
            _position = newline < 0 ? end : end + 1;
            if (newline >= 0)
            {
                var bytes = frame.GetBuffer();
                var length = checked((int)frame.Length);
                if (length > 0 && bytes[length - 1] == '\r') length--;
                return Utf8.GetString(bytes, 0, length);
            }
        }
    }
}
