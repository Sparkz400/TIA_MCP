using System.Text.Json;
using System.Xml.Linq;
using TiaMcp.Core.WatchTables;

if (args.Length != 3) throw new ArgumentException("Usage: <new-output> <reorder-run> <delete-run>. Retained native evidence roots are required; this test never starts TIA.");
string destination = Path.GetFullPath(args[0]);
if (Directory.Exists(destination)) throw new Exception("Preserve previous evidence.");
Directory.CreateDirectory(destination);
var results = new List<object>(); int failed = 0;
void Check(string name, Action action) { try { action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.Message }); Console.WriteLine("FAIL " + name + ": " + e.Message); } }
void Require(bool value) { if (!value) throw new Exception("Independent fixture assertion failed"); }
byte[] NativeInput(string run)
{
    var review = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "review.json")));
    var input = review.RootElement.GetProperty("review").GetProperty("input");
    var bytes = File.ReadAllBytes(Path.Combine(run, input.GetProperty("relativePath").GetString()!));
    Require(WatchTableDocument.Hash(bytes) == input.GetProperty("sha256").GetString());
    return bytes;
}
var reorder = NativeInput(Path.GetFullPath(args[1]));
foreach (var test in new[] { (Name: "reorder", Bytes: reorder, Count: 16), (Name: "delete", Bytes: NativeInput(Path.GetFullPath(args[2])), Count: 11) })
    Check(test.Name + " explicit preorder candidate and immutable source", () => {
        var document = new WatchTableDocument(test.Bytes); var original = document.ToBytes();
        var mapping = document.ReindexReferenceFreeDocumentIds();
        Require(mapping.Count == test.Count && mapping.Values.SequenceEqual(Enumerable.Range(0, test.Count).Select(i => i.ToString("X"))));
        Require(test.Bytes.SequenceEqual(original));
        File.WriteAllBytes(Path.Combine(destination, test.Name + ".xml"), document.ToBytes());
        File.WriteAllText(Path.Combine(destination, test.Name + "-mapping.json"), JsonSerializer.Serialize(mapping));
        Require(new WatchTableDocument(document.ToBytes()).ReindexReferenceFreeDocumentIds().All(p => p.Key == p.Value));
    });
var mutations = new Dictionary<string, Action<XDocument>> {
    ["unknown scalar"] = d => d.Descendants("AttributeList").Last().Add(new XElement("Unknown", "retain")),
    ["unknown scalar attribute"] = d => d.Descendants("Text").First().SetAttributeValue("opaque", "value"),
    ["unknown nested object"] = d => d.Descendants("ObjectList").Skip(1).First().Add(new XElement("Unknown", new XAttribute("ID", "FA"))),
    ["reference attribute"] = d => d.Descendants("Text").First().SetAttributeValue("TargetID", "1"),
    ["reference element in metadata"] = d => d.Root!.Element("DocumentInfo")!.Add(new XElement("ObjectRef", "1")),
    ["external document id"] = d => d.Root!.Element("DocumentInfo")!.SetAttributeValue("ID", "FA"),
    ["wrong table id"] = d => d.Root!.Elements().Last().SetAttributeValue("ID", "FA"),
    ["duplicate cultures"] = d => d.Descendants("Culture").Take(2).Last().Value = d.Descendants("Culture").First().Value,
    ["wrong trigger"] = d => d.Descendants("MonitorTrigger").First().Value = "Once",
    ["wrong datatype"] = d => d.Descendants("DisplayFormat").First().Value = "Hex",
    ["reference-like attribute casing"] = d => d.Descendants("Text").First().SetAttributeValue("targetId", "1"),
    ["opaque language markup"] = d => d.Descendants("Text").First().Add(new XElement("Span", "opaque"))
};
foreach (var item in mutations)
    Check("refuse " + item.Key + " atomically", () => {
        var tree = XDocument.Load(new MemoryStream(reorder), LoadOptions.PreserveWhitespace); item.Value(tree);
        using var stream = new MemoryStream(); tree.Save(stream); var document = new WatchTableDocument(stream.ToArray());
        var before = document.ToBytes(); bool rejected = false;
        try { document.ReindexReferenceFreeDocumentIds(); } catch (InvalidDataException) { rejected = true; }
        Require(rejected && before.SequenceEqual(document.ToBytes()));
    });
File.WriteAllText(Path.Combine(destination,"result.json"), JsonSerializer.Serialize(new { completed = failed == 0, checks = results.Count, failed, nativeInvoked = false, profileEnabled = false, results }, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;
