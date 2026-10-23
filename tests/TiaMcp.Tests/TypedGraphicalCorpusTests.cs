using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class TypedGraphicalCorpusTests
{
    internal static Task StructuralFbdReplacementMatchesObservedNativeOrder()
    {
        string root = RepositoryRoot();
        var profiles = JObject.Parse(File.ReadAllText(Path.Combine(root, "fixtures", "typed-graphical", "profiles.v1.json")))["profiles"]!.ToObject<TypedGraphicalProfile[]>()!;
        var source = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(root, "fixtures", "typed-graphical", "native", "Tgp_Boolean_FBD.xml")));
        var context = new TypedGraphicalContext { CpuFamily = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1", Profiles = profiles };
        foreach (string key in new[] { "A", "B", "Inhibit", "Active" }) context.Operands[key] = new TypedGraphicalOperand {
            Identity = "/fixture/" + key, Scope = "LocalVariable", Components = [key], DataType = "Bool", Writable = key == "Active", DeclarationHash = "captured" };
        var model = new TypedGraphicalModel { SchemaVersion = 1, Language = "FBD", Operation = "replaceNetwork", NetworkId = "3",
            Nodes = [new() { Key = "join", Kind = "or", InputCount = 2 }, new() { Key = "invert", Kind = "not" },
                new() { Key = "gate", Kind = "and", InputCount = 2 }, new() { Key = "active", Kind = "assign", Operand = "Active" }],
            Connections = [Wire("$A", "value", "join", "in1"), Wire("$B", "value", "join", "in2"), Wire("$Inhibit", "value", "invert", "in1"),
                Wire("join", "out", "gate", "in1"), Wire("invert", "out", "gate", "in2"), Wire("gate", "out", "active", "in")] };
        var built = TypedGraphical.Build(source, model, context);
        // Transcribed from actual tq03 native post-import/export, change-659e070315ef45ea9d065919922877e0.
        // This asserts native reserialization; it makes no runtime-behavior claim.
        var expected = new XDocument(source);
        var net = expected.Descendants(TypedGraphical.Net + "FlgNet").Single();
        net.ReplaceWith(XElement.Parse("""
            <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"><Parts>
            <Access Scope="LocalVariable" UId="21"><Symbol><Component Name="A" /></Symbol></Access>
            <Access Scope="LocalVariable" UId="22"><Symbol><Component Name="B" /></Symbol></Access>
            <Access Scope="LocalVariable" UId="23"><Symbol><Component Name="Inhibit" /></Symbol></Access>
            <Access Scope="LocalVariable" UId="24"><Symbol><Component Name="Active" /></Symbol></Access>
            <Part Name="O" UId="25"><TemplateValue Name="Card" Type="Cardinality">2</TemplateValue></Part>
            <Part Name="A" UId="26"><TemplateValue Name="Card" Type="Cardinality">1</TemplateValue><Negated Name="in1" /></Part>
            <Part Name="A" UId="27"><TemplateValue Name="Card" Type="Cardinality">2</TemplateValue></Part><Part Name="Coil" UId="28" />
            </Parts><Wires>
            <Wire UId="29"><IdentCon UId="21" /><NameCon UId="25" Name="in1" /></Wire>
            <Wire UId="30"><IdentCon UId="22" /><NameCon UId="25" Name="in2" /></Wire>
            <Wire UId="31"><IdentCon UId="24" /><NameCon UId="28" Name="operand" /></Wire>
            <Wire UId="32"><IdentCon UId="23" /><NameCon UId="26" Name="in1" /></Wire>
            <Wire UId="33"><NameCon UId="27" Name="out" /><NameCon UId="28" Name="in" /></Wire>
            <Wire UId="34"><NameCon UId="26" Name="out" /><NameCon UId="27" Name="in2" /></Wire>
            <Wire UId="35"><NameCon UId="25" Name="out" /><NameCon UId="27" Name="in1" /></Wire>
            </Wires></FlgNet>
            """));
        EngineeringSources.RequireNativePreservation(expected, built.Document);
        Check.Equal("28", built.IdMapping["previous:27"]);
        Check.Equal(true, built.Effects.Any(e => e.Kind == "selectedNetworkIdReallocation"));
        // Fresh creation has no old instruction IDs to break ties. The actual
        // tqx02 FC/FBD readback still put AND.in1's OR before AND.in2's NOT.
        var blank = new XDocument(source);
        blank.Descendants(TypedGraphical.Net + "FlgNet").Single().ReplaceNodes(new XElement(TypedGraphical.Net + "Parts"), new XElement(TypedGraphical.Net + "Wires"));
        var fresh = TypedGraphical.Build(blank, model, context);
        var expectedFresh = new XDocument(expected);
        var expectedNetwork = expectedFresh.Descendants(TypedGraphical.Net + "FlgNet").Single();
        var accessIds = new Dictionary<string, string> { ["22"] = "23", ["23"] = "24", ["24"] = "22" };
        foreach (var attribute in expectedNetwork.Descendants().Attributes("UId"))
            if (accessIds.TryGetValue(attribute.Value, out string? current)) attribute.Value = current;
        var parts = expectedNetwork.Element(TypedGraphical.Net + "Parts")!;
        parts.ReplaceNodes(parts.Elements(TypedGraphical.Net + "Access").OrderBy(a => int.Parse(a.Attribute("UId")!.Value)).Concat(parts.Elements().Where(e => e.Name != TypedGraphical.Net + "Access")).ToArray());
        var wires = expectedNetwork.Element(TypedGraphical.Net + "Wires")!;
        var observedWireOrder = new[] { "29", "30", "32", "33", "34", "35", "31" }.Select(id => wires.Elements().Single(w => w.Attribute("UId")!.Value == id)).ToArray();
        for (int i = 0; i < observedWireOrder.Length; i++) observedWireOrder[i].SetAttributeValue("UId", i + 29);
        wires.ReplaceNodes(observedWireOrder);
        EngineeringSources.RequireNativePreservation(expectedFresh, fresh.Document);
        return Task.CompletedTask;
    }
    internal static Task NewNetworkIdsAreNativeScoped()
    {
        foreach (string language in new[] { "LAD", "FBD" })
        {
            var source = EngineeringSources.ReadXml(EngineeringSources.GraphicalFixture("ScopeHost", language));
            var second = new XElement(source.Descendants("SW.Blocks.CompileUnit").Single()); second.SetAttributeValue("ID", "2");
            source.Descendants("SW.Blocks.CompileUnit").Single().AddAfterSelf(second);
            var context = new TypedGraphicalContext { CpuFamily = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1" };
            context.Operands["input"] = new TypedGraphicalOperand { Identity = "/host/Enable", Scope = "LocalVariable", Components = ["Enable"], DataType = "Bool", DeclarationHash = "observed" };
            context.Operands["output"] = new TypedGraphicalOperand { Identity = "/host/Active", Scope = "LocalVariable", Components = ["Active"], DataType = "Bool", DeclarationHash = "observed", Writable = true };
            var model = new TypedGraphicalModel { SchemaVersion = 1, Language = language, Operation = "insertNetwork", InsertAfterNetworkId = "2",
                Nodes = language == "LAD" ? [new() { Key = "rail", Kind = "powerrail" }, new() { Key = "input", Kind = "contact", Operand = "input" }, new() { Key = "output", Kind = "coil", Operand = "output" }]
                    : [new() { Key = "output", Kind = "assign", Operand = "output" }],
                Connections = language == "LAD" ? [Wire("rail", "out", "input", "in"), Wire("input", "out", "output", "in")] : [Wire("$input", "value", "output", "in")] };
            var result = TypedGraphical.Build(source, model, context);
            var added = result.Document.Descendants("SW.Blocks.CompileUnit").Single(n => (string?)n.Attribute("ID") == result.NetworkId);
            int[] definitions = added.Descendants().Where(e => e.Name.LocalName is "Access" or "Part" or "Wire").Select(e => int.Parse(e.Attribute("UId")!.Value)).ToArray();
            Check.Equal(string.Join(",", Enumerable.Range(21, definitions.Length)), string.Join(",", definitions));
            Check.Equal(3, result.Document.Descendants("SW.Blocks.CompileUnit").Count());
            Check.Equal(3, result.Document.Descendants(TypedGraphical.Net + "Access").Count(e => (string?)e.Attribute("UId") == "21"));
        }
        return Task.CompletedTask;
    }
    internal static Task CapturedBranchRoundtripPreservesCompleteNativeXml()
    {
        string root = RepositoryRoot();
        var profiles = JObject.Parse(File.ReadAllText(Path.Combine(root, "fixtures", "typed-graphical", "profiles.v1.json")))["profiles"]!.ToObject<TypedGraphicalProfile[]>()!;
        var source = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(root, "fixtures", "typed-graphical", "native", "Tgp_Boolean_LAD.xml")));
        var context = new TypedGraphicalContext { CpuFamily = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1", Profiles = profiles };
        foreach (string key in new[] { "A", "B", "Inhibit", "Active" }) context.Operands[key] = new TypedGraphicalOperand {
            Identity = "/fixture/" + key, Scope = "LocalVariable", Components = [key], DataType = "Bool", Writable = key == "Active", DeclarationHash = "captured" };
        var model = new TypedGraphicalModel { SchemaVersion = 1, Language = "LAD", Operation = "replaceNetwork", NetworkId = "3",
            Nodes = [new() { Key = "rail", Kind = "powerrail" }, new() { Key = "a", Kind = "contact", Operand = "A" }, new() { Key = "b", Kind = "contact", Operand = "B" },
                new() { Key = "join", Kind = "branch", InputCount = 2 }, new() { Key = "inhibit", Kind = "contact", Operand = "Inhibit", Negated = true }, new() { Key = "active", Kind = "coil", Operand = "Active" }],
            Connections = [Wire("rail", "out", "a", "in"), Wire("rail", "out", "b", "in"), Wire("a", "out", "join", "in1"), Wire("b", "out", "join", "in2"),
                Wire("join", "out", "inhibit", "in"), Wire("inhibit", "out", "active", "in")] };
        var built = TypedGraphical.Build(source, model, context);
        EngineeringSources.RequireNativePreservation(source, built.Document);
        return Task.CompletedTask;
    }
    internal static Task ReplacementRetainsKnownNativeIds()
    {
        const string cpu = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1";
        foreach (string language in new[] { "LAD", "FBD" })
        {
            var source = EngineeringSources.ReadXml(EngineeringSources.GraphicalFixture("ReplaceHost", language));
            var context = new TypedGraphicalContext { CpuFamily = cpu };
            context.Operands["input"] = new TypedGraphicalOperand { Identity = "/host/Enable", DataType = "Bool", Scope = "LocalVariable", Components = ["Enable"], DeclarationHash = "observed" };
            context.Operands["output"] = new TypedGraphicalOperand { Identity = "/host/Active", DataType = "Bool", Scope = "LocalVariable", Components = ["Active"], DeclarationHash = "observed", Writable = true };
            var model = new TypedGraphicalModel { SchemaVersion = 1, Language = language, Operation = "replaceNetwork", NetworkId = "1",
                Nodes = language == "LAD" ? [new() { Key = "rail", Kind = "powerrail" }, new() { Key = "input", Kind = "contact", NativeUid = "23", Operand = "input" }, new() { Key = "output", Kind = "coil", NativeUid = "24", Operand = "output" }]
                    : [new() { Key = "output", Kind = "assign", NativeUid = "23", Operand = "output" }],
                Connections = language == "LAD" ? [Wire("rail", "out", "input", "in"), Wire("input", "out", "output", "in")] : [Wire("$input", "value", "output", "in")] };
            var beforeIds = source.Descendants().Attributes("UId").Select(a => a.Value).OrderBy(v => v).ToArray();
            var generated = TypedGraphical.Build(source, model, context);
            var afterIds = generated.Document.Descendants().Attributes("UId").Select(a => a.Value).OrderBy(v => v).ToArray();
            Check.Equal(string.Join(",", beforeIds), string.Join(",", afterIds));
            foreach (var oldWire in source.Descendants(TypedGraphical.Net + "Wire"))
            {
                var newWire = generated.Document.Descendants(TypedGraphical.Net + "Wire").Single(w => (string?)w.Attribute("UId") == (string?)oldWire.Attribute("UId"));
                Check.Equal(true, XNode.DeepEquals(oldWire, newWire));
            }
        }
        return Task.CompletedTask;
    }
    // Expected semantics are the independently authored and natively exported corpus.
    // This pure test does not mark any native builder or runtime acceptance gate passed.
    internal static Task ReadCapturedNetworks()
    {
        string root = RepositoryRoot();
        var catalog = JObject.Parse(File.ReadAllText(Path.Combine(root, "fixtures", "typed-graphical", "profiles.v1.json")));
        var profiles = catalog["profiles"]!.ToObject<TypedGraphicalProfile[]>()!;
        int count = 0;
        foreach (var source in catalog["sources"]!)
        {
            string path = Path.Combine(root, source.Value<string>("path")!);
            var document = EngineeringSources.ReadXml(File.ReadAllText(path));
            var graph = TypedGraphical.Read(document, path, profiles);
            foreach (var network in graph.Networks)
            {
                if (network.Language != "LAD" && network.Language != "FBD")
                {
                    Check.Equal("opaque", network.Coverage);
                    Check.Equal(document.Descendants("SW.Blocks.CompileUnit").Single(e => e.Attribute("ID")?.Value == network.NetworkId).ToString(SaveOptions.DisableFormatting), network.NativeXml);
                    continue;
                }
                if (network.Coverage != "complete")
                    throw new InvalidOperationException(Path.GetFileName(path) + " network " + network.NetworkId + " coverage " + network.Coverage + ": " + string.Join(", ", network.Nodes.Where(n => !n.Supported).Select(n => n.NativeName)));
                Check.Equal(true, network.Connections.All(c => c.Resolved));
                Check.Equal(true, network.Nodes.All(n => n.NativeXml.Length > 0 && n.SourceSha256.Length == 64));
                Check.Equal(document.Descendants("SW.Blocks.CompileUnit").Single(e => e.Attribute("ID")?.Value == network.NetworkId).ToString(SaveOptions.DisableFormatting), network.NativeXml);
                count++;
            }
        }
        Console.WriteLine("Typed corpus pure read verified " + count + " independently exported networks; no native or runtime builder assertions executed.");
        return Task.CompletedTask;
    }

    internal static Task EmitCapturedInstructionProfiles()
    {
        string root = RepositoryRoot();
        var catalog = JObject.Parse(File.ReadAllText(Path.Combine(root, "fixtures", "typed-graphical", "profiles.v1.json")));
        var profiles = catalog["profiles"]!.ToObject<TypedGraphicalProfile[]>()!;
        int count = 0;
        foreach (var profile in profiles.Where(p => !p.ReaderOnly && new[] { "compare", "timer", "counter", "and", "or", "not" }.Contains(p.Kind)))
        {
            var nodes = new List<TypedGraphicalNode>();
            var wires = new List<TypedGraphicalConnection>();
            var context = new TypedGraphicalContext { CpuFamily = profile.CpuFamily, TiaVersion = profile.TiaVersion, Route = profile.Route, Profiles = profiles, NetworkTemplateXml = profile.NetworkTemplateXml };
            var instruction = new TypedGraphicalNode { Key = "instruction", Kind = profile.Kind, DataType = profile.DataType, Comparison = profile.Kind == "compare" ? profile.Instruction : null,
                Instruction = profile.Kind == "compare" ? null : profile.Instruction, InputCount = profile.Kind == "not" ? 1 : profile.Kind == "and" || profile.Kind == "or" ? 2 : 0 };
            if (profile.Kind == "timer" || profile.Kind == "counter")
            {
                instruction.Instance = "storage";
                context.Instances["storage"] = new TypedGraphicalInstance { Identity = "/fixture/Storage", CalleePath = "instruction:" + profile.Instruction, DataType = profile.InstanceType,
                    DeclarationHash = new string('b', 64), Scope = "LocalVariable", Components = ["Storage"] };
            }
            nodes.Add(instruction);
            var profilePins = profile.Pins;
            if (new[] { "and", "or", "not" }.Contains(profile.Kind))
                profilePins = Enumerable.Range(1, profile.Kind == "not" ? 1 : 2).Select(i => new TypedGraphicalPin { Name = "in" + i, Direction = "input", DataType = "Bool" })
                    .Concat([new TypedGraphicalPin { Name = "out", Direction = "output", DataType = "Bool" }]).ToArray();
            string? flowInput = profile.Language == "LAD" ? profilePins.First(p => p.Direction == "input" && p.DataType == "Bool").Name : null;
            string? flowOutput = profile.Language == "LAD" ? profilePins.FirstOrDefault(p => p.Direction == "output" && p.DataType == "Bool")?.Name : null;
            if (profile.Language == "LAD") nodes.Add(new TypedGraphicalNode { Key = "rail", Kind = "powerrail" });
            foreach (var pin in profilePins)
            {
                if (pin.Name == flowInput) { wires.Add(Wire("rail", "out", "instruction", pin.Name)); continue; }
                string key = "operand_" + pin.Name;
                context.Operands[key] = new TypedGraphicalOperand { Key = key, Identity = "/fixture/" + key, Scope = "LocalVariable", Components = [key], DataType = pin.DataType,
                    Writable = pin.Direction != "input", DeclarationHash = new string('a', 64) };
                if (pin.Name == flowOutput)
                {
                    nodes.Add(new TypedGraphicalNode { Key = "coil", Kind = "coil", Operand = key });
                    wires.Add(Wire("instruction", pin.Name, "coil", "in"));
                }
                else wires.Add(pin.Direction == "output" ? Wire("instruction", pin.Name, "$" + key, "value") : Wire("$" + key, "value", "instruction", pin.Name));
            }
            var model = new TypedGraphicalModel { SchemaVersion = 1, Language = profile.Language, Operation = "insertNetwork", InsertAfterNetworkId = "1", Nodes = nodes.ToArray(), Connections = wires.ToArray() };
            var baseline = EngineeringSources.ReadXml(EngineeringSources.GraphicalFixture("PureProfileHost", profile.Language));
            var built = TypedGraphical.Build(baseline, model, context);
            var read = TypedGraphical.Read(built.Document, "/fixture/PureProfileHost", profiles);
            var created = read.Networks.Single(n => n.NetworkId == built.NetworkId);
            Check.Equal("complete", created.Coverage);
            Check.Equal(true, built.Profiles.Contains(profile.Id));
            Check.Equal(true, built.Effects.Any(e => e.Kind == "insertNetwork"));
            Check.Equal(baseline.ToString(SaveOptions.DisableFormatting), EngineeringSources.ReadXml(EngineeringSources.GraphicalFixture("PureProfileHost", profile.Language)).ToString(SaveOptions.DisableFormatting));
            count++;
        }
        Console.WriteLine("Typed corpus pure emission verified " + count + " captured profiles; native apply and runtime qualification are separate.");
        return Task.CompletedTask;
    }
    private static TypedGraphicalConnection Wire(string from, string fromPort, string to, string toPort) => new() { From = new() { Node = from, Port = fromPort }, To = new() { Node = to, Port = toPort } };
    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "fixtures", "typed-graphical", "profiles.v1.json"))) return current.FullName;
        throw new InvalidOperationException("The typed native profile corpus is missing.");
    }
}
