namespace TiaMcp.Core.PhysicalRuntime;

/// <summary>Local recovery boundaries; the interval can contain unknown remote data loss.</summary>
public sealed record OpcUaReconnectEvent(int Attempt, string State, string? ErrorCode);

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
