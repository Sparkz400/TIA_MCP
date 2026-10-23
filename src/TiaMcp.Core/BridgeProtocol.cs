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
        var json = JsonSerializer.Serialize(new { protocolVersion = Version, id, operation, arguments = arguments ?? new { } }, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxRequestBytes)
            throw new BridgeException("RequestTooLarge", "The bridge request exceeds 4 MiB.");
        return json;
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
