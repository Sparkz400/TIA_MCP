using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core.WatchTables;

internal static class NativeDocumentChecks
{
    internal static void Run()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "native-owned-v21");
        var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(directory, "manifest.json")))!;
        foreach (var file in manifest["files"]!.AsArray())
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(directory, file!["path"]!.GetValue<string>()));
            Require(WatchTableDocument.Hash(bytes) == file["sha256"]!.GetValue<string>() && bytes.Length == file["bytes"]!.GetValue<int>(), "Native capture provenance");
            var document = new WatchTableDocument(bytes);
            Require(bytes.SequenceEqual(document.ToBytes()), "Native no-op must retain exact bytes, metadata and IDs");
        }
        byte[] baseline = File.ReadAllBytes(Path.Combine(directory, "MilestoneWatch.before.xml"));
        var edited = new WatchTableDocument(baseline); edited.SetField(0, "ModifyValue", "TRUE");
        var expected = Parse(baseline);
        expected.Descendants("ModifyValue").Single().Value = "TRUE";
        Require(XNode.DeepEquals(expected, Parse(edited.ToBytes())), "Only the reviewed literal may change in memory");
        foreach (string phase in new[] { "applied", "reopened", "original" })
        {
            var native = Parse(File.ReadAllBytes(Path.Combine(directory, "MilestoneWatch." + phase + ".xml")));
            // Independent XML oracle: only the exporter timestamp is volatile.
            native.Root!.Element("DocumentInfo")!.Element("Created")!.Value = expected.Root!.Element("DocumentInfo")!.Element("Created")!.Value;
            Require(XNode.DeepEquals(expected, native), "Genuine " + phase + " must match all reviewed nodes, IDs and cultures");
        }
        var restored = Parse(File.ReadAllBytes(Path.Combine(directory, "MilestoneWatch.restored.xml")));
        var original = Parse(baseline);
        restored.Root!.Element("DocumentInfo")!.Element("Created")!.Value = original.Root!.Element("DocumentInfo")!.Element("Created")!.Value;
        Require(XNode.DeepEquals(original, restored), "Genuine checkpoint recovery restores the complete baseline");
    }
    private static XDocument Parse(byte[] bytes) => XDocument.Load(new MemoryStream(bytes), LoadOptions.PreserveWhitespace);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
