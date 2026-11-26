using System.Security.Cryptography;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

internal static class ContentContractTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("explicit oracle profile accepts retained xp01 full/base pair while preserving raw hashes", () => {
            using var f = new OracleFixture(); var result = f.Validate();
            Equal(true, result.Value<bool>("baseRemainingTreeMatches")); Equal(true, result.Value<bool>("oracleRemainingTreeMatches"));
            Equal(false, result.Value<bool>("rawBaseBytesEqual")); Equal("Unspecified", result["nativeCreationDate"].Value<string>("kind"));
            Equal("878c05c2c41a9b402f4647a16875ffa68e600745eae493d162598a6be0448341", result.Value<string>("sourceBaseSha256"));
        });
        foreach (string name in new[] { "AutoNumber", "IsIECCheckEnabled", "MemoryLayout" })
        {
            test("source oracle missing " + name + " never infers a default", () => { using var f = new OracleFixture(); f.Rewrite("sourceOracle", d => Attr(d).Element(name).Remove(), updatePin: true); Reject(() => f.RecreatePolicy()); });
            test("source oracle nondefault " + name + " rejects limited profile", () => { using var f = new OracleFixture(); f.Rewrite("sourceOracle", d => Attr(d).Element(name).Value = "changed", updatePin: true); Reject(() => f.RecreatePolicy()); });
            test("installed base missing " + name + " rejects", () => { using var f = new OracleFixture(); f.Rewrite("installed", d => Attr(d).Element(name).Remove()); Reject(() => f.Validate()); });
            test("installed base duplicate " + name + " rejects", () => { using var f = new OracleFixture(); f.Rewrite("installed", d => Attr(d).Add(new XElement(Attr(d).Element(name)))); Reject(() => f.Validate()); });
            test("installed base changed " + name + " rejects", () => { using var f = new OracleFixture(); f.Rewrite("installed", d => Attr(d).Element(name).Value = "changed"); Reject(() => f.Validate()); });
            test("installed oracle readonly marker on " + name + " rejects", () => { using var f = new OracleFixture(); f.Rewrite("installedOracle", d => Attr(d).Element(name).SetAttributeValue("ReadOnly", "true")); Reject(() => f.Validate()); });
            test("source oracle duplicate " + name + " rejects", () => { using var f = new OracleFixture(); f.Rewrite("sourceOracle", d => Attr(d).Add(new XElement(Attr(d).Element(name))), updatePin: true); Reject(() => f.RecreatePolicy()); });
        }
        test("planned source oracle hash mutation rejects", () => { using var f = new OracleFixture(); f.Pin["contentPolicy"]["sourceOracleSha256"] = new string('0', 64); Reject(() => f.Validate()); });
        test("native source oracle reread changed bytes rejects", () => { using var f = new OracleFixture(); f.Rewrite("sourceOracle", d => d.Root.Add(new XElement("Unknown"))); Reject(() => f.Validate()); });
        test("planned oracle wrong version identity rejects", () => { using var f = new OracleFixture(); f.Rewrite("sourceOracle", d => VersionAttr(d).Element("Guid").Value = Guid.NewGuid().ToString(), updatePin: true); Reject(() => f.RecreatePolicy()); });
        test("planned oracle wrong type identity rejects", () => { using var f = new OracleFixture(); f.Rewrite("sourceOracle", d => VersionAttr(d).Element("LibraryTypeGuid").Value = Guid.NewGuid().ToString(), updatePin: true); Reject(() => f.RecreatePolicy()); });
        test("planned source global name mismatch rejects oracle provenance", () => { using var f = new OracleFixture(); f.Pin["contentPolicy"]["sourceLibraryName"] = "OtherGlobal"; Reject(() => f.Validate()); });
        test("unknown policy cannot normalize content", () => { using var f = new OracleFixture(); f.Pin["contentPolicy"]["policyId"] = "ignore-anything"; Reject(() => f.Validate()); });
        test("UDT engineering kind cannot adopt FB materialization profile", () => { using var f = new OracleFixture(); f.Pin["engineeringType"] = "Siemens.Engineering.SW.Types.PlcTypeLibraryTypeVersion"; Reject(() => f.Validate()); });
        test("source existing property cannot be rewritten through policy", () => { using var f = new OracleFixture(); f.Rewrite("source", d => Attr(d).Add(new XElement("MemoryLayout", "Optimized")), updatePin: true); Reject(() => f.RecreatePolicy()); });
        var changes = new Dictionary<string, Action<XDocument>> {
            ["unknown element"] = d => Attr(d).Add(new XElement("UnknownNativeSetting", "anything")),
            ["unknown attribute"] = d => Attr(d).SetAttributeValue("UnknownNativeAttribute", "anything"),
            ["comment"] = d => Attr(d).Add(new XComment("different comment")),
            ["processing instruction"] = d => Attr(d).Add(new XProcessingInstruction("data", "different")),
            ["object ID"] = d => d.Descendants("SW.Blocks.FB").Single().SetAttributeValue("ID", "FFFF"),
            ["interface datatype"] = d => d.Descendants().First(e => e.Name.LocalName == "Member").SetAttributeValue("Datatype", "Int"),
            ["source text"] = d => d.Descendants().First(e => e.Name.LocalName == "Text" && e.Value.Contains("Independent component")).Value += " changed",
            ["duplicate context"] = d => VersionAttr(d).Add(new XElement(VersionAttr(d).Element("OriginalLibrary"))),
            ["namespaced context substitute"] = d => VersionAttr(d).Element("OriginalLibrary").Name = XName.Get("OriginalLibrary", "urn:other"),
            ["creationdate extra attribute"] = d => Attr(d).Element("CreationDate").SetAttributeValue("unknown", "true"),
            ["other timestamp"] = d => Attr(d).Element("CodeModifiedDate").Value = "2026-10-12T00:00:00Z"
        };
        foreach (var change in changes)
            test("full oracle rejects changed " + change.Key, () => { using var f = new OracleFixture(); f.Rewrite("installedOracle", change.Value); Reject(() => f.Validate()); });
        foreach (string key in new[] { "unknown element", "unknown attribute", "comment", "object ID", "interface datatype", "source text" })
            test("base comparison rejects changed " + key, () => { using var f = new OracleFixture(); f.Rewrite("installed", changes[key]); Reject(() => f.Validate()); });
        test("nested lookalike cannot replace direct setting", () => { using var f = new OracleFixture(); f.Rewrite("installed", d => { Attr(d).Element("AutoNumber").Remove(); Attr(d).Add(new XElement("Nested", new XElement("AutoNumber", "true"))); }); Reject(() => f.Validate()); });
        test("invalid exported provenance date rejects", () => { using var f = new OracleFixture(); f.Rewrite("installedOracle", d => Attr(d).Element("CreationDate").Value = "unknown-local-time"); Reject(() => f.Validate()); });
        test("native setting mismatch rejects even when XML agrees", () => { using var f = new OracleFixture(); f.Evidence["nativeFbSettings"]["IsIECCheckEnabled"]["value"] = true; Reject(() => f.Validate()); });
        test("native date ticks and Kind retained without timezone inference", () => { using var f = new OracleFixture(); f.Evidence["nativeFbSettings"]["CreationDate"]["kind"] = "Local"; Equal("Local", f.Validate()["nativeCreationDate"].Value<string>("kind")); });
        test("DocumentInfo cannot invoke generic native envelope exclusion", () => { using var f = new OracleFixture(); f.Rewrite("installed", d => d.Root.Add(new XElement("DocumentInfo", "ignored?"))); Reject(() => f.Validate()); });
        test("significant preserved whitespace cannot change in either full tree", () => {
            using var f = new OracleFixture();
            foreach (var key in new[] { "source", "installed", "sourceOracle", "installedOracle" })
                f.Rewrite(key, d => Attr(d).Add(new XElement("UnknownPayload", new XAttribute(XNamespace.Xml + "space", "preserve"), " a ")), updatePin: key.StartsWith("source"));
            f.RecreatePolicy(); f.Validate();
            f.Rewrite("installedOracle", d => Attr(d).Element("UnknownPayload").Value = "a"); Reject(() => f.Validate());
        });
        test("unknown identical content is preserved in full comparisons", () => {
            using var f = new OracleFixture();
            foreach (var key in new[] { "source", "installed", "sourceOracle", "installedOracle" })
                f.Rewrite(key, d => Attr(d).Add(new XElement("UnknownPayload", new XAttribute("future", "yes"), new XComment("keep"), " content ")), updatePin: key.StartsWith("source"));
            f.RecreatePolicy(); Equal(true, f.Validate().Value<bool>("oracleRemainingTreeMatches"));
        });
    }
    static XElement Attr(XDocument d) => d.Descendants("SW.Blocks.FB").Single().Element("AttributeList");
    static XElement VersionAttr(XDocument d) => d.Root.Element("SW.Blocks.CodeBlockLibraryTypeVersion").Element("AttributeList");
    static void Equal<T>(T a, T b) { if (!Equals(a, b)) throw new Exception("Expected " + a + "; got " + b); }
    static void Reject(Action body) { try { body(); } catch (PlcComponentException e) when (e.Code == "ComponentReadbackMismatch") { return; } throw new Exception("Expected fail-closed content contract rejection."); }

    internal sealed class OracleFixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "tia-component-oracle-" + Guid.NewGuid().ToString("N"));
        readonly Dictionary<string, string> paths = new();
        internal JObject Pin, Evidence;
        internal OracleFixture()
        {
            Directory.CreateDirectory(root);
            string fixtures = Path.Combine(RepositoryFiles.Root(), "fixtures/libraries/component-qualification/oracle-xp01");
            string guid = "43b5c338-b589-4ab5-8c82-cae3e08e9d06";
            foreach (var pair in new[] { ("source", "source", 1), ("installed", "installed", 1), ("sourceOracle", "source", 3), ("installedOracle", "installed", 3) })
            {
                paths[pair.Item1] = Path.Combine(root, pair.Item1 + ".xml");
                File.Copy(Path.Combine(fixtures, pair.Item2 + "-" + guid + "-options" + pair.Item3 + ".xml"), paths[pair.Item1]);
            }
            Pin = new JObject { ["typeGuid"] = "146cafdb-e1d5-4111-9b23-1078b6557d53", ["versionGuid"] = guid,
                ["typeName"] = "TiaMcpComponentGate", ["engineeringType"] = "Siemens.Engineering.SW.Blocks.CodeBlockLibraryTypeVersion",
                ["artifact"] = Artifact("source"), ["contentSha256"] = Artifact("source")["sha256"].DeepClone() };
            Evidence = new JObject { ["contentPolicyId"] = PlcComponentContentContract.PolicyId,
                ["nativeFbSettings"] = new JObject {
                    ["AutoNumber"] = new JObject { ["type"] = "System.Boolean", ["value"] = true },
                    ["IsIECCheckEnabled"] = new JObject { ["type"] = "System.Boolean", ["value"] = false },
                    ["MemoryLayout"] = new JObject { ["type"] = "Siemens.Engineering.SW.Blocks.MemoryLayout", ["value"] = "Optimized" },
                    ["CreationDate"] = new JObject { ["type"] = "System.DateTime", ["value"] = "2026-10-12T06:25:52.2063076", ["ticks"] = 639116583522063076L, ["kind"] = "Unspecified" } } };
            RefreshEvidence(); RecreatePolicy();
        }
        JObject Artifact(string key) => new() { ["path"] = paths[key], ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(paths[key]))) };
        void RefreshEvidence()
        {
            foreach (var item in new[] { ("source", "sourceArtifact", "sourceContentSha256"), ("installed", "artifact", "contentSha256"), ("sourceOracle", "sourceOracleArtifact", "sourceOracleSha256"), ("installedOracle", "installedOracleArtifact", "installedOracleSha256") })
            { Evidence[item.Item2] = Artifact(item.Item1); Evidence[item.Item3] = Artifact(item.Item1)["sha256"].DeepClone(); }
        }
        internal void RecreatePolicy() => Pin["contentPolicy"] = PlcComponentContentContract.CreatePolicy(Pin, Artifact("sourceOracle"), File.ReadAllText(paths["source"]), File.ReadAllText(paths["sourceOracle"]), "ComponentQualification");
        internal JObject Validate() => PlcComponentContentContract.Validate(Pin, Evidence);
        internal void Bind(string type, string version, string name)
        {
            string oldType = Pin.Value<string>("typeGuid"), oldVersion = Pin.Value<string>("versionGuid"), oldName = Pin.Value<string>("typeName");
            foreach (string key in paths.Keys)
                File.WriteAllText(paths[key], File.ReadAllText(paths[key]).Replace(oldType, type).Replace(oldVersion, version).Replace(oldName, name));
            Pin["typeGuid"] = type; Pin["versionGuid"] = version; Pin["typeName"] = name;
            Pin["artifact"] = Artifact("source"); Pin["contentSha256"] = Artifact("source")["sha256"].DeepClone();
            RefreshEvidence(); RecreatePolicy();
        }
        internal void Rewrite(string key, Action<XDocument> action, bool updatePin = false)
        {
            var document = EngineeringSources.ReadXml(File.ReadAllText(paths[key])); action(document); File.WriteAllText(paths[key], document.ToString(SaveOptions.DisableFormatting)); RefreshEvidence();
            if (updatePin && key == "source") { Pin["artifact"] = Artifact("source"); Pin["contentSha256"] = Artifact("source")["sha256"].DeepClone(); }
        }
        public void Dispose() { Directory.Delete(root, true); }
    }
}
