namespace TiaMcp.Host;

/// <summary>The stage a long tool call is in, reported by the progress heartbeat. The call filter creates one holder per
/// call that carries a progressToken; a tool sets <see cref="Stage"/> as it goes. Without a token there is no holder and
/// setting the stage does nothing.</summary>
public static class ToolProgress
{
    private static readonly AsyncLocal<Holder?> CurrentHolder = new();

    public sealed class Holder { public volatile string? Stage; }

    internal static Holder Begin() { var holder = new Holder(); CurrentHolder.Value = holder; return holder; }

    /// <summary>Records the current stage for the next heartbeat, for example "exporting block 12 of 200".</summary>
    public static void Stage(string stage) { if (CurrentHolder.Value is { } holder) holder.Stage = stage; }
}
