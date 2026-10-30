using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.TestSuite.V21
{
    // Evidence transport for the explicitly assisted qualification build. This is not an
    // automatic scope reader and is never registered as production qualification.
    internal static class StyleUiEvidence
    {
        internal static string Hash(byte[] bytes)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static JObject Read(string path)
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("UI evidence exceeds bound.");
            using (var reader = new JsonTextReader(new StreamReader(path)) { DateParseHandling = DateParseHandling.None, MaxDepth = 32 })
            {
                var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("One exact UI evidence document required.");
                return value;
            }
        }
        // A manifest read back from its file holds JTokenType.Null where the live capture holds a null string token;
        // DeepEquals tells them apart, so compare both as they serialize.
        internal static bool SameContent(JToken before, JToken after) =>
            before != null && after != null && JToken.DeepEquals(JToken.Parse(before.ToString(Formatting.None)), JToken.Parse(after.ToString(Formatting.None)));
        internal static void Save(string path, JObject value)
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        internal static JObject Validate(JObject challenge, JObject response, string directory, DateTime now)
        {
            void Require(bool value, string message) { if (!value) throw new InvalidDataException("TestSuiteScopeIncomplete: " + message); }
            Require(response.Value<string>("method") == "operator-read-native-scope-editor", "Independent visual review is required.");
            Require(JToken.DeepEquals(challenge, response["challenge"]), "UI evidence does not bind the exact current challenge.");
            DateTime issued = DateTime.Parse(challenge.Value<string>("issuedUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind);
            Require(issued <= now && now - issued <= TimeSpan.FromMinutes(15), "The UI observation challenge expired.");
            var expected = challenge["selectedProgramBlocks"] as JArray;
            var selected = response["selectedProgramBlocks"] as JArray;
            Require(expected != null && selected != null && JToken.DeepEquals(expected, selected), "Selected leaves differ or are incomplete.");
            Require(JToken.DeepEquals(challenge["excludedBranches"], response["excludedBranches"]), "Excluded branches differ or are incomplete.");
            Require(response.Value<bool?>("allLeavesVisible") == true && response.Value<bool?>("checkboxesChanged") == false,
                "The full unchanged scope tree must be visible.");
            Require(response.Value<string>("objectSelector") == "CodeBlock_FB_FC_OB" && response.Value<bool?>("ruleEnabled") == true,
                "The exact enabled rule selector must be observed.");
            var images = response["screenshots"] as JArray;
            Require(images != null && images.Count >= 1 && images.Count <= 8, "Original screenshots are required.");
            string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var item in images)
            {
                string file = Path.GetFullPath(Path.Combine(directory, item.Value<string>("file") ?? ""));
                Require(file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(file) &&
                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0, "Screenshot must be a retained local challenge artifact.");
                Require(new FileInfo(file).Length >= 1024 && new FileInfo(file).Length <= 16 * 1024 * 1024, "Screenshot size is invalid.");
                var bytes = File.ReadAllBytes(file);
                Require(Hash(bytes) == item.Value<string>("sha256"), "Screenshot bytes changed.");
                Require(bytes.Take(3).SequenceEqual(new byte[] { 255, 216, 255 }) ||
                    bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Original PNG/JPEG required.");
            }
            if (challenge.Value<bool>("requireLicenseCheck"))
            {
                var license = response["license"] as JObject;
                Require(license != null && license.Value<string>("product") == "TIA Portal Test Suite Advanced V21" &&
                    license.Value<string>("method") == "Automation License Manager Check" && license.Value<int?>("validKeys") >= 1 &&
                    license.Value<int?>("defectiveKeys") == 0 && images.Any(i => i.Value<string>("purpose") == "license"),
                    "A fresh independent valid-key check with its original screenshot is required.");
            }
            Require(images.Any(i => i.Value<string>("purpose") == "scope"), "A scope screenshot is required.");
            return (JObject)response.DeepClone();
        }
    }
}
