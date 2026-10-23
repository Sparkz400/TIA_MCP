using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class CheckpointEvidence
{
    internal static JObject Capture(string archivePath, string authorizationPath, string projectPath, int processId)
    {
        if (processId <= 0 || !Path.IsPathRooted(projectPath)) throw new InvalidOperationException("Exact owned checkpoint context required.");
        string archive = Guard(archivePath), authorization = Guard(authorizationPath);
        if (Path.GetDirectoryName(archive) != Path.GetDirectoryName(authorization) || Path.GetExtension(archive) != ".zap21")
            throw new InvalidOperationException("Checkpoint must be beside its exact authorization.");
        var receipt = new JObject { ["schemaVersion"] = 1, ["phase"] = "beforeEncoderMutation",
            ["projectPath"] = projectPath, ["processId"] = processId,
            ["authorization"] = Artifact(authorization), ["archive"] = Artifact(archive) };
        if (receipt["archive"].Value<long>("bytes") == 0) throw new InvalidOperationException("Empty checkpoint archive.");
        byte[] bytes = new UTF8Encoding(false).GetBytes(receipt.ToString(Formatting.Indented));
        string destination = Guard(authorization + ".checkpoint.json");
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
        using (var sha = SHA256.Create())
            return new JObject { ["path"] = destination, ["bytes"] = bytes.Length,
                ["sha256"] = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() };
    }

    private static JObject Artifact(string path)
    {
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var sha = SHA256.Create())
            return new JObject { ["path"] = path, ["bytes"] = file.Length,
                ["sha256"] = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant() };
    }

    private static string Guard(string path)
    {
        if (!Path.IsPathRooted(path)) throw new InvalidOperationException("Absolute checkpoint paths required.");
        string full = Path.GetFullPath(path);
        for (string cursor = full; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Checkpoint paths cannot traverse links or reparse points.");
        return full;
    }
}
