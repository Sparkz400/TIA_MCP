using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Explicit V21 representation contract observed in the retained xp01 packet.
    // Both complete exports remain hash-pinned; there is no generic ignore list.
    internal static class PlcComponentContentContract
    {
        internal const string PolicyId = "v21-fb-explicit-source-oracle-three-properties-v1";
        private static readonly string[] Names = { "AutoNumber", "IsIECCheckEnabled", "MemoryLayout" };
        private static readonly string[] Values = { "true", "false", "Optimized" };

        internal static bool HasOmittedFbProperties(string xml)
        {
            var document = Read(xml);
            var blocks = document.Descendants("SW.Blocks.FB").ToArray();
            return blocks.Length == 1 && blocks[0].Element("AttributeList") != null &&
                Names.All(name => !blocks[0].Element("AttributeList").Elements(name).Any());
        }

        internal static JObject CreatePolicy(JObject pin, JObject oracleArtifact, string sourceBaseXml, string sourceOracleXml, string sourceLibraryName)
        {
            var source = Read(sourceBaseXml); var oracle = Read(sourceOracleXml);
            var baseBlock = Fb(source, pin, false); var fullBlock = Fb(oracle, pin, true);
            BaseOracleIdentity(source, oracle);
            var baseAttributes = One(baseBlock, "AttributeList"); var fullAttributes = One(fullBlock, "AttributeList");
            if (Names.Any(name => baseAttributes.Elements(name).Any()))
                Fail("This explicit policy requires all three source base properties to be absent; existing values are never rewritten.");
            if (string.IsNullOrWhiteSpace(sourceLibraryName)) Fail("A reviewed source global-library name is required.");
            for (int i = 0; i < Names.Length; i++) Scalar(fullAttributes, Names[i], Values[i], true);
            var version = One(oracle.Root, "SW.Blocks.CodeBlockLibraryTypeVersion");
            string origin = Scalar(One(version, "AttributeList"), "OriginalLibrary", null, true);
            if (string.IsNullOrWhiteSpace(origin)) Fail("Source original-library identity is missing.");
            string created = Scalar(fullAttributes, "CreationDate", null, true); Utc(created);
            ValidateArtifactDescriptor(oracleArtifact);
            return new JObject { ["policyId"] = PolicyId, ["sourceLibraryName"] = sourceLibraryName,
                ["sourceOriginalLibrary"] = origin,
                ["sourceOracleSha256"] = oracleArtifact.Value<string>("sha256"), ["sourceOracleArtifact"] = oracleArtifact.DeepClone(),
                ["sourceBaseExportOptions"] = "WithDefaults", ["sourceOracleExportOptions"] = "WithDefaults, WithReadOnly", ["documentInfoOptions"] = "None",
                ["properties"] = new JObject { [Names[0]] = Values[0], [Names[1]] = Values[1], [Names[2]] = Values[2] },
                ["remainingContentRule"] = "Full base and full oracle trees preserve every other node, attribute, ID, comment, instruction and significant text. Only exact version OriginalLibrary and direct FB CreationDate are contextual in the oracle; no generic metadata removal.",
                ["creationDateRule"] = "Only direct content FB CreationDate is provenance: explicit UTC exported leaves retained independently. Native getter ticks/Kind are observations; no unspecified/local timezone inference or chronology acceptance.",
                ["qualification"] = "Explicit observed profile; production combined apply remains disabled." };
        }

        internal static void ValidatePin(JObject pin)
        {
            if (pin["contentPolicy"] == null) return;
            var policy = pin["contentPolicy"] as JObject;
            if (policy?.Value<string>("policyId") != PolicyId || pin.Value<string>("engineeringType") != "Siemens.Engineering.SW.Blocks.CodeBlockLibraryTypeVersion")
                Fail("Unknown policy or non-FB library version.");
            string source = Artifact((JObject)pin["artifact"], pin.Value<string>("contentSha256"));
            string oracle = Artifact((JObject)policy["sourceOracleArtifact"], policy.Value<string>("sourceOracleSha256"));
            var expected = CreatePolicy(pin, (JObject)policy["sourceOracleArtifact"], source, oracle, policy.Value<string>("sourceLibraryName"));
            if (!JToken.DeepEquals(expected, policy)) Fail("Reviewed content policy differs from its exact source artifacts.");
        }

        internal static void ValidateSourceObservation(JObject pin, string baseHash, string oracleHash, string sourceLibraryName)
        {
            if (baseHash != pin.Value<string>("contentSha256")) Fail("Native source base export changed before component import.");
            if (pin["contentPolicy"] is JObject policy && (policy.Value<string>("policyId") != PolicyId ||
                oracleHash != policy.Value<string>("sourceOracleSha256") || sourceLibraryName != policy.Value<string>("sourceLibraryName")))
                Fail("Native source full oracle/library identity changed before component import.");
        }

        internal static JObject Validate(JObject pin, JObject evidence)
        {
            ValidatePin(pin);
            var policy = (JObject)pin["contentPolicy"];
            if (policy == null || evidence.Value<string>("contentPolicyId") != PolicyId || evidence.Value<string>("sourceContentSha256") != pin.Value<string>("contentSha256") ||
                evidence.Value<string>("sourceOracleSha256") != policy.Value<string>("sourceOracleSha256"))
                Fail("Readback does not match both pinned source hashes and explicit content policy.");
            string sourceText = Artifact((JObject)evidence["sourceArtifact"], pin.Value<string>("contentSha256"));
            string sourceOracleText = Artifact((JObject)evidence["sourceOracleArtifact"], policy.Value<string>("sourceOracleSha256"));
            string installedText = Artifact((JObject)evidence["artifact"], evidence.Value<string>("contentSha256"));
            string installedOracleText = Artifact((JObject)evidence["installedOracleArtifact"], evidence.Value<string>("installedOracleSha256"));
            var source = Read(sourceText); var sourceOracle = Read(sourceOracleText); var installed = Read(installedText); var installedOracle = Read(installedOracleText);
            BaseOracleIdentity(source, sourceOracle); BaseOracleIdentity(installed, installedOracle);
            var baseAttributes = One(Fb(source, pin, false), "AttributeList");
            var installedAttributes = One(Fb(installed, pin, false), "AttributeList");
            var fullSourceAttributes = One(Fb(sourceOracle, pin, true), "AttributeList");
            var fullInstalledAttributes = One(Fb(installedOracle, pin, true), "AttributeList");
            for (int i = 0; i < Names.Length; i++)
            {
                if (baseAttributes.Elements(Names[i]).Any()) Fail("A source base field was already present; policy cannot change existing content.");
                Scalar(fullSourceAttributes, Names[i], Values[i], true);
                Scalar(installedAttributes, Names[i], Values[i], false);
                Scalar(fullInstalledAttributes, Names[i], Values[i], false);
            }
            // Exact additions only: removing those three proven direct fields
            // from the installed base must recover the complete source base.
            var baseComparison = new XDocument(installed);
            var compareAttributes = One(Fb(baseComparison, pin, false), "AttributeList");
            foreach (string name in Names) One(compareAttributes, name).Remove();
            Preserve(source, baseComparison);

            var sourceVersionAttributes = One(One(sourceOracle.Root, "SW.Blocks.CodeBlockLibraryTypeVersion"), "AttributeList");
            var installedVersionAttributes = One(One(installedOracle.Root, "SW.Blocks.CodeBlockLibraryTypeVersion"), "AttributeList");
            Scalar(sourceVersionAttributes, "OriginalLibrary", policy.Value<string>("sourceOriginalLibrary"), true);
            Scalar(installedVersionAttributes, "OriginalLibrary", policy.Value<string>("sourceLibraryName"), true);
            // Its lexical value is already bound by the complete source-oracle
            // raw hash; do not duplicate an ISO string in the policy because
            // standard Newtonsoft journal parsing materializes date tokens.
            string originalDate = Scalar(fullSourceAttributes, "CreationDate", null, true);
            string installedDate = Scalar(fullInstalledAttributes, "CreationDate", null, true);
            Utc(originalDate); Utc(installedDate);
            ValidateGetters(evidence["nativeFbSettings"] as JObject);

            // Full oracle comparison permits exactly five concrete differences:
            // three ReadOnly markers and two precisely located context values.
            var fullComparison = new XDocument(sourceOracle);
            var fullCompareAttributes = One(Fb(fullComparison, pin, true), "AttributeList");
            foreach (string name in Names) One(fullCompareAttributes, name).Attribute("ReadOnly").Remove();
            One(One(One(fullComparison.Root, "SW.Blocks.CodeBlockLibraryTypeVersion"), "AttributeList"), "OriginalLibrary").Value = policy.Value<string>("sourceLibraryName");
            One(fullCompareAttributes, "CreationDate").Value = installedDate;
            Preserve(fullComparison, installedOracle);
            return new JObject { ["policyId"] = PolicyId, ["sourceBaseSha256"] = pin.Value<string>("contentSha256"),
                ["installedBaseSha256"] = evidence.Value<string>("contentSha256"), ["sourceOracleSha256"] = policy.Value<string>("sourceOracleSha256"),
                ["installedOracleSha256"] = evidence.Value<string>("installedOracleSha256"), ["baseRemainingTreeMatches"] = true,
                ["oracleRemainingTreeMatches"] = true, ["nativeSettingsMatch"] = true,
                ["sourceCreationDate"] = originalDate, ["installedCreationDate"] = installedDate,
                ["creationDateRule"] = policy["creationDateRule"].DeepClone(), ["nativeCreationDate"] = evidence["nativeFbSettings"]["CreationDate"].DeepClone(),
                ["properties"] = policy["properties"].DeepClone(), ["rawBaseBytesEqual"] = false };
        }

        private static void ValidateGetters(JObject getters)
        {
            if (getters == null) Fail("Exact linked native FB settings are required.");
            foreach (var item in new[] { new { Name = "AutoNumber", Value = true }, new { Name = "IsIECCheckEnabled", Value = false } })
                if (getters[item.Name]?["value"]?.Type != JTokenType.Boolean || getters[item.Name].Value<bool>("value") != item.Value || getters[item.Name].Value<string>("type") != "System.Boolean")
                    Fail("Installed native Boolean settings differ or are unavailable.");
            if (getters["MemoryLayout"]?.Value<string>("value") != "Optimized" || getters["MemoryLayout"]?.Value<string>("type") != "Siemens.Engineering.SW.Blocks.MemoryLayout")
                Fail("Installed native memory layout differs or is unavailable.");
            var date = getters["CreationDate"];
            if (date?.Value<string>("type") != "System.DateTime" || !new[] { "Utc", "Local", "Unspecified" }.Contains(date.Value<string>("kind")) || date["ticks"]?.Type != JTokenType.Integer || (date["value"]?.Type != JTokenType.String && date["value"]?.Type != JTokenType.Date))
                Fail("Retain native creation-date ticks/Kind without guessing timezone semantics.");
        }

        private static XDocument Read(string text)
        {
            var document = EngineeringSources.ReadXml(text);
            if (document.Root?.Name != "Document" || document.Root.Elements("DocumentInfo").Any())
                Fail("DocumentInfo.None native envelopes are required; no provenance stripping is allowed.");
            var ids = document.Descendants().Attributes("ID").Select(a => a.Value).ToArray();
            if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) Fail("Duplicate or empty XML IDs are not accepted.");
            return document;
        }
        private static XElement Fb(XDocument document, JObject pin, bool full)
        {
            if (!Guid.TryParse(pin.Value<string>("typeGuid"), out var typeGuid) || typeGuid == Guid.Empty || !Guid.TryParse(pin.Value<string>("versionGuid"), out var versionGuid) || versionGuid == Guid.Empty || string.IsNullOrWhiteSpace(pin.Value<string>("typeName")))
                Fail("Exact nonempty native type/version/name pins are required.");
            var version = One(document.Root, "SW.Blocks.CodeBlockLibraryTypeVersion");
            if (document.Descendants("SW.Blocks.CodeBlockLibraryTypeVersion").Count() != 1 || document.Descendants("SW.Blocks.FB").Count() != 1)
                Fail("Exactly one direct code-block version and one direct FB content object are required.");
            var block = One(One(version, "ObjectList"), "SW.Blocks.FB");
            if ((string)block.Attribute("CompositionName") != "ContentObject") Fail("FB must be the exact native library content object.");
            Scalar(One(block, "AttributeList"), "Name", pin.Value<string>("typeName"), false);
            if (full)
            {
                var attributes = One(version, "AttributeList");
                Scalar(attributes, "Guid", pin.Value<string>("versionGuid"), true);
                Scalar(attributes, "LibraryTypeGuid", pin.Value<string>("typeGuid"), true);
                Scalar(attributes, "LibraryTypeName", pin.Value<string>("typeName"), true);
                Scalar(attributes, "State", "Committed", true);
            }
            return block;
        }
        private static void BaseOracleIdentity(XDocument basis, XDocument oracle)
        {
            // Both exports originate from the same native version object. Verify
            // all object IDs/kinds/attributes agree across the two formats too;
            // read-only scalar expansions do not define new engineering objects.
            var left = basis.Descendants().Where(e => e.Attribute("ID") != null).ToArray();
            var right = oracle.Descendants().Where(e => e.Attribute("ID") != null).ToArray();
            if (left.Length != right.Length) Fail("Base/oracle engineering-object identity inventory differs.");
            for (int i = 0; i < left.Length; i++)
                if (!XNode.DeepEquals(new XElement(left[i].Name, left[i].Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal)),
                                     new XElement(right[i].Name, right[i].Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))))
                    Fail("Base/oracle engineering-object IDs, kinds or attributes differ.");
        }
        private static XElement One(XElement parent, string name)
        {
            var elements = parent?.Elements(name).ToArray();
            if (elements == null || elements.Length != 1) Fail("Required exact XML path is absent or duplicated: " + name);
            return elements[0];
        }
        private static string Scalar(XElement parent, string name, string expected, bool readOnly)
        {
            var element = One(parent, name);
            if (element.Nodes().Any(n => !(n is XText)) || element.Attributes().Count() != (readOnly ? 1 : 0) ||
                (readOnly && element.Attribute("ReadOnly")?.Value != "true") || (expected != null && element.Value != expected))
                Fail("Exact native scalar value/attributes differ at " + name + ".");
            return element.Value;
        }
        private static DateTimeOffset Utc(string text)
        {
            if (text == null || !Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$") ||
                !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
                throw new PlcComponentException("ComponentReadbackMismatch", "An explicit valid UTC timestamp is required.");
            return value;
        }
        private static void Preserve(XDocument source, XDocument actual)
        {
            try { EngineeringSources.RequireNativePreservation(source, actual); }
            catch (BridgeException error) { Fail("Full native content preservation failed: " + error.Message); }
        }
        internal static string Artifact(JObject descriptor, string expectedHash)
        {
            long? expectedBytes = ValidateArtifactDescriptor(descriptor);
            if (descriptor.Value<string>("sha256") != expectedHash) Fail("Artifact descriptor differs from its pinned raw hash.");
            string path = descriptor.Value<string>("path");
            if (!File.Exists(path)) Fail("Content artifact is missing.");
            using (var file = File.OpenRead(path)) return ReadArtifactText(file, expectedHash, expectedBytes);
        }
        internal static string ReadArtifactText(Stream source, string expectedHash, long? expectedBytes = null)
        {
            const int limit = 4 * 1024 * 1024;
            if (source.CanSeek && source.Length > limit) Fail("Content artifact exceeds the XML bound.");
            byte[] bytes;
            using (var copy = new MemoryStream())
            {
                var buffer = new byte[65536];
                while (true)
                {
                    int remaining = limit - (int)copy.Length;
                    int count = source.Read(buffer, 0, Math.Min(buffer.Length, remaining + 1));
                    if (count == 0) break;
                    if (count > remaining) Fail("Content artifact exceeds the XML bound.");
                    copy.Write(buffer, 0, count);
                }
                bytes = copy.ToArray();
            }
            if (expectedBytes.HasValue && bytes.LongLength != expectedBytes.Value) Fail("Content artifact length differs from its original descriptor.");
            using (var sha = SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != expectedHash) Fail("Retained content artifact bytes changed.");
            // Decode only the bytes just hashed. A second path lookup could consume a
            // replacement file; a second stream pass could observe changed content.
            // Preserve the supported BOM/default encodings, but never replace
            // malformed sequences: different raw artifacts must not collapse to
            // identical replacement-character text before XML comparison.
            int offset = 0;
            Encoding encoding = new UTF8Encoding(false, true);
            if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
            { encoding = new UTF32Encoding(false, false, true); offset = 4; }
            else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)
            { encoding = new UTF32Encoding(true, false, true); offset = 4; }
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
            else if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            { offset = 3; }
            try { return encoding.GetString(bytes, offset, bytes.Length - offset); }
            catch (DecoderFallbackException) { Fail("Content artifact contains malformed text for its BOM or default UTF-8 encoding."); return null; }
        }
        private static long? ValidateArtifactDescriptor(JObject artifact)
        {
            if (artifact == null || !Path.IsPathRooted(artifact.Value<string>("path") ?? "") || !Regex.IsMatch(artifact.Value<string>("sha256") ?? "", "^[0-9a-f]{64}$"))
                Fail("An exact absolute artifact path and raw SHA-256 are required.");
            if (artifact["bytes"] == null) return null; // Retained older descriptors contain only path/hash.
            long count = 0;
            if (artifact["bytes"].Type != JTokenType.Integer || !long.TryParse(artifact["bytes"].ToString(Newtonsoft.Json.Formatting.None),
                NumberStyles.None, CultureInfo.InvariantCulture, out count) || count > 4 * 1024 * 1024)
                Fail("Artifact byte count must be an exact nonnegative integer within the XML bound.");
            return count;
        }
        private static void Fail(string message) { throw new PlcComponentException("ComponentReadbackMismatch", message); }
    }
}
