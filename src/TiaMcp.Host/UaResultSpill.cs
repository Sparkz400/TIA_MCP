using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Host;

/// <summary>Keeps a UA tool reply small: at most 128 KiB goes inline (the reply carries it twice); a larger result is written whole as
/// a content-addressed artifact under the artifact directory, for artifact_read. Nothing is ever deleted: the directory has no retention.</summary>
internal static class UaResultSpill
{
    internal const int InlineLimitBytes = 128 * 1024;

    internal static object Bounded<T>(string artifactsRoot, T result, string kind, int count, Func<OpcUaRuntimeSpill, T> spilled) where T : notnull
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json);
        if (bytes.Length <= InlineLimitBytes) return result;
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)), relative = $"runtime-ua-results/ua-{kind}-{hash[..32]}.json";
        string full = Path.Combine(artifactsRoot, relative);
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, bytes); File.Move(temp, full, true); }
            finally { File.Delete(temp); }
        }
        return spilled(new(relative, hash, bytes.Length, count));
    }
}
