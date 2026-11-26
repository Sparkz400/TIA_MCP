#nullable disable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Argument checks and hashes of the change pipeline. They read only the request and local files, never the project,
    // so tests/TiaMcp.HardwareEngineering.Tests compiles this file; OpennessSession's partial files import it with
    // `using static`.
    internal static class ChangeArguments
    {
        internal static JObject ChangeObject(JObject args, string name) => args[name] as JObject ?? throw new BridgeException("InvalidRequest", name + " must be an object.");

        internal static string[] ChangeStringArray(JObject args, string name, int count, int length, bool optional = false)
        {
            if (args[name] == null && optional) return Array.Empty<string>();
            var array = args[name] as JArray;
            if (array == null || array.Count > count || (!optional && array.Count == 0) || array.Any(item => item.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)item) || ((string)item).Length > length))
                throw new BridgeException("InvalidRequest", name + " must be a bounded array of nonempty strings.");
            return array.Values<string>().ToArray();
        }

        internal static string[] ChangeNames(JObject args, string name)
        {
            var array = args[name] as JArray ?? throw new BridgeException("InvalidRequest", name + " must be an array.");
            if (array.Count < 1 || array.Count > 100 || array.Any(v => v.Type != JTokenType.String || string.IsNullOrWhiteSpace(v.Value<string>())))
                throw new BridgeException("InvalidRequest", name + " must contain 1–100 block names.");
            var names = array.Values<string>().ToArray();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new BridgeException("InvalidRequest", "Duplicate expected block names.");
            return names;
        }

        internal static bool ExplicitChangeBool(JObject args, string name)
        {
            if (args[name]?.Type != JTokenType.Boolean) throw new BridgeException("InvalidRequest", name + " must be explicitly true or false.");
            return args.Value<bool>(name);
        }

        internal static void RequireChangeBool(JObject args, string name)
        {
            if (!ExplicitChangeBool(args, name)) throw new BridgeException("InvalidRequest", name + " must be true for this operation.");
        }

        internal static string ChangeHash(string text)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        internal static string ChangeFileHash(string path)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static JObject ChangeArtifact(string path) => new JObject { ["path"] = path, ["bytes"] = new FileInfo(path).Length, ["sha256"] = ChangeFileHash(path) };
    }
}
