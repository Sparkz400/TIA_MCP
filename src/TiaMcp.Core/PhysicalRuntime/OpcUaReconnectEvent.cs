using System.Text.Json.Serialization;

namespace TiaMcp.Core.PhysicalRuntime;

/// <summary>Local recovery boundaries; the interval can contain unknown remote data loss.
/// Transfer is set only when the server reported the original session gone and a subscription moved (or was refused) to a replacement session.</summary>
public sealed record OpcUaReconnectEvent(int Attempt, string State, string? ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Transfer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TransferStatusCode = null);

public sealed class OpcUaReconnectHistory(int maximumAttempts)
{
    public int Attempts { get; private set; }
    public bool Pending { get; private set; }
    public bool Healthy { get; private set; } = true;
    private string? previousError;
    public static bool Retryable(string? code) => code is "UaTransportInterrupted" or "UaTimeout";
    public static void Validate(OpcUaReconnectEvent e, int maximumAttempts)
    {
        Require(e.Attempt >= 1 && e.Attempt <= maximumAttempts && maximumAttempts <= 3);
        Require(e.State switch {
            "starting" => Retryable(e.ErrorCode),
            "succeeded" => e.ErrorCode is null,
            "failed" => e.ErrorCode is { Length: >= 1 and <= 80 } && e.ErrorCode.StartsWith("Ua", StringComparison.Ordinal) && !e.ErrorCode.Any(char.IsControl),
            _ => false });
        Require(e.Transfer switch {
            null => e.TransferStatusCode is null,
            "transferred" => e.State == "succeeded" && e.TransferStatusCode is null,
            "refused" => e.State == "succeeded" && e.TransferStatusCode is { Length: 10 } s && s.StartsWith("0x", StringComparison.Ordinal) &&
                uint.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint code) &&
                s == $"0x{code:x8}" && (code & 0xC0000000) == 0x80000000,
            _ => false });
    }
    public void Apply(OpcUaReconnectEvent e)
    {
        Validate(e, maximumAttempts);
        if (e.State == "starting")
        {
            Require(!Pending && e.Attempt == Attempts + 1 && (Healthy || Retryable(previousError)));
            Attempts++; Pending = true; Healthy = false;
        }
        else
        {
            Require(Pending && e.Attempt == Attempts);
            Pending = false; Healthy = e.State == "succeeded"; previousError = e.ErrorCode;
        }
    }
    private static void Require(bool condition)
    { if (!condition) throw new BridgeException("UaReconnectEvidenceInvalid", "Invalid retained reconnect sequence."); }
}
