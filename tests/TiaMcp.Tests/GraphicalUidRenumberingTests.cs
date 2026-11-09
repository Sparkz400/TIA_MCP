using System.Xml.Linq;
using TiaMcp.Openness.V21;
using EngineeringBridgeException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

internal static class GraphicalUidRenumberingTests
{
    private static readonly XNamespace Net = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";

    private static string CorpusDirectory()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
        {
            string path = Path.Combine(current.FullName, "fixtures", "typed-graphical", "native", "6es7-513-1am03-0ab0-v4-1");
            if (Directory.Exists(path)) return path;
        }
        throw new InvalidOperationException("Native graphical corpus is missing.");
    }

    private static HashSet<string> Definitions(XDocument document, string networkId) => new(document.Descendants("SW.Blocks.CompileUnit")
        .Where(e => (string?)e.Attribute("ID") == networkId).Descendants().Where(e => e.Name.LocalName is not ("IdentCon" or "NameCon")).Attributes("UId").Select(a => a.Value), StringComparer.Ordinal);

    // Every native V21 export in the corpus is already in the predicted order: the rule reproduces TIA's own numbering.
    internal static Task NativeExportsAreFixedPointsOfThePrediction()
    {
        int networks = 0;
        foreach (var file in Directory.GetFiles(CorpusDirectory(), "*.xml"))
        {
            var document = XDocument.Load(file);
            foreach (var unit in document.Descendants("SW.Blocks.CompileUnit").Where(u => u.Descendants(Net + "FlgNet").Any()).ToArray())
            {
                string id = (string)unit.Attribute("ID")!;
                var before = new XDocument(document);
                var result = GraphicalUidRenumbering.Predict(document, id, Definitions(document, id));
                if (((Newtonsoft.Json.Linq.JArray)result["changes"]!).Count != 0 || !XNode.DeepEquals(before, document))
                    throw new InvalidOperationException(Path.GetFileName(file) + " network " + id + " is not a fixed point: " + result["changes"]);
                networks++;
            }
        }
        if (networks < 50) throw new InvalidOperationException("Too few native networks checked: " + networks);
        return Task.CompletedTask;
    }

    internal static Task SeriesContactInsertionIsRenumberedInNativeOrder()
    {
        var document = XDocument.Load(Path.Combine(CorpusDirectory(), "Tgp_TON_LAD.xml"));
        string networkId = (string)document.Descendants("SW.Blocks.CompileUnit").Single().Attribute("ID")!;
        var original = Definitions(document, networkId);
        // Insert a contact on #Extra between the existing contact (25) and the TON input: rewire 31, add access 90, contact 91, wires 92/93.
        EngineeringSources.PatchGraphicalNetwork(document, networkId, new[]
        {
            "<Access xmlns='" + Net + "' Scope='LocalVariable' UId='90'><Symbol><Component Name='Extra' /></Symbol></Access>",
            "<Part xmlns='" + Net + "' Name='Contact' UId='91' />",
            "<Wire xmlns='" + Net + "' UId='31'><NameCon UId='25' Name='out' /><NameCon UId='91' Name='in' /></Wire>",
            "<Wire xmlns='" + Net + "' UId='92'><NameCon UId='91' Name='out' /><NameCon UId='26' Name='IN' /></Wire>",
            "<Wire xmlns='" + Net + "' UId='93'><IdentCon UId='90' /><NameCon UId='91' Name='operand' /></Wire>",
        }, Array.Empty<string>());
        var result = GraphicalUidRenumbering.Predict(document, networkId, original);
        var network = document.Descendants(Net + "FlgNet").Single();
        var parts = network.Element(Net + "Parts")!.Elements().Select(e => e.Name.LocalName + ":" + ((string?)e.Attribute("Name") ?? "") + ":" + (string)e.Attribute("UId")!).ToArray();
        Check.Equal("Access::21,Access::22,Access::23,Access::24,Access::25,Part:Contact:26,Part:Contact:27,Part:TON:28,Part:Coil:30", string.Join(",", parts));
        Check.Equal("29", (string)network.Descendants(Net + "Instance").Single().Attribute("UId")!);
        var wires = network.Element(Net + "Wires")!.Elements().Select(e => (string)e.Attribute("UId")!).ToArray();
        Check.Equal(string.Join(",", Enumerable.Range(31, 9)), string.Join(",", wires));
        Check.Equal(false, (bool)result["qualified"]!);
        // The renumbered network still means: TON input = #Enable AND #Extra.
        Check.Equal("TON<Time> #Storage(PT := #Preset, IN := #Enable AND #Extra, ET => #Elapsed)" + Environment.NewLine + "#Done := #Storage.Q",
            GraphicalNetworkText.Render(document)[0].Text);
        return Task.CompletedTask;
    }

    internal static async Task UnderivableOrdersAreRefused()
    {
        var document = XDocument.Load(Path.Combine(CorpusDirectory(), "Tgp_Boolean_LAD.xml"));
        string networkId = (string)document.Descendants("SW.Blocks.CompileUnit").Single().Attribute("ID")!;
        var original = Definitions(document, networkId);
        // A second, new coil fed from the same OR output creates a new terminal instruction beside the existing coil.
        EngineeringSources.PatchGraphicalNetwork(document, networkId, new[]
        {
            "<Part xmlns='" + Net + "' Name='Coil' UId='90' />",
            "<Wire xmlns='" + Net + "' UId='33'><NameCon UId='28' Name='out' /><NameCon UId='29' Name='in' /><NameCon UId='90' Name='in' /></Wire>",
            "<Wire xmlns='" + Net + "' UId='91'><IdentCon UId='21' /><NameCon UId='90' Name='operand' /></Wire>",
        }, Array.Empty<string>());
        var before = document.ToString();
        var error = await Check.ThrowsAsync<EngineeringBridgeException>(() => { GraphicalUidRenumbering.Predict(document, networkId, original); return Task.CompletedTask; });
        Check.Equal("UidRenumberingUnpredictable", error.Code);
        Check.Equal(before, document.ToString());
    }
}
