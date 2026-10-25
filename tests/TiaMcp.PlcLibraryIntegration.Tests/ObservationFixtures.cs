using System.Security.Cryptography;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

internal static class ObservationFixtures
{
    // Explicit test-only export for independent serialized-plan consumers. These
    // use the actual artifact-bound planner, with authored XML and no native SDK.
    internal static void Write(string directory)
    {
        string output = Path.GetFullPath(directory);
        if (Directory.Exists(output)) throw new InvalidOperationException("Choose a fresh fixture output directory.");
        Directory.CreateDirectory(output);
        foreach (string profile in new[] { "existing-fbd", "existing-lad", "standalone-fbd", "standalone-lad", "existing-inout", "existing-blocked" })
        {
            string folder = Path.Combine(output, profile); Directory.CreateDirectory(folder);
            var f = new Fixture();
            string language = profile.EndsWith("lad", StringComparison.Ordinal) ? "LAD" : "FBD";
            f.Caller.Descendants("ProgrammingLanguage").ToList().ForEach(e => e.Value = language);
            f.Inventory["objects"][0]["language"] = language;
            if (profile.StartsWith("standalone", StringComparison.Ordinal)) f.Standalone(language);
            if (profile == "existing-inout")
            {
                f.Fb.Descendants().Single(e => e.Name.LocalName == "Section" && (string)e.Attribute("Name") == "Output").SetAttributeValue("Name", "InOut");
                f.Payload["integration"]["mappings"][1]["direction"] = "InOut";
            }
            if (profile == "existing-blocked")
                f.Caller.Root.Element("SW.Blocks.FC").Element("ObjectList").Add(new XElement("MultilingualText", new XAttribute("ID", "9")));
            f.Payload["source"] = new JObject { ["libraryId"] = "library-" + new string('a', 32), ["revision"] = new string('b', 64) };
            var root = (JObject)f.Versions[0];
            root["kind"] = "version"; root["version"] = "1.0.0"; root["isDefault"] = true; root["setForUpdate"] = true;
            root["author"] = "Synthetic observation fixture"; root["modifiedDate"] = "2026-10-23T00:00:00Z";
            root["engineeringType"] = "Siemens.Engineering.SW.Blocks.CodeBlockLibraryTypeVersion";
            string rootPath = Path.Combine(folder, "root.xml"); File.WriteAllText(rootPath, f.Fb.ToString());
            root["artifact"] = Descriptor(rootPath); root["contentSha256"] = root["artifact"]["sha256"].DeepClone();
            JObject caller = null;
            if (f.Caller != null)
            {
                string callerPath = Path.Combine(folder, "caller.xml"); File.WriteAllText(callerPath, f.Caller.ToString());
                caller = Descriptor(callerPath);
            }
            var plan = PlcLibraryIntegrationPlanner.BuildFromArtifacts(f.Payload, f.Versions, caller, f.Inventory);
            var result = new JObject { ["synthetic"] = true, ["nativeExecuted"] = false, ["profile"] = profile,
                ["payload"] = f.Payload, ["plan"] = plan, ["inventory"] = f.Inventory };
            File.WriteAllText(Path.Combine(folder, "observation.json"), result.ToString());
        }
        Console.WriteLine("PASS exported six actual planner observation profiles; no native execution.");
    }

    private static JObject Descriptor(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new JObject { ["path"] = path, ["bytes"] = bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    }
}
