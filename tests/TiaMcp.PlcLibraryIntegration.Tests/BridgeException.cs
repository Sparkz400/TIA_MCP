namespace TiaMcp.Openness.V21;
internal sealed class BridgeException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
