using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using TiaMcp.Openness.V21;
using Newtonsoft.Json.Linq;
using EngineeringBridgeException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

// Independently authored unit-test XML below is a preservation/reader fixture.
// It is not a Siemens-authored export or evidence of native/runtime qualification.
internal static class TypedGraphicalTests
{
    private static readonly XNamespace Net = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";

    internal static async Task RawPayloadRejectsUnknownNullAndCoercedValues()
    {
        JObject Valid() => JObject.Parse("""
            {"blockPath":"Devices/PLC/Program blocks/Independent","model":{"schemaVersion":1,"language":"FBD","operation":"insertNetwork","nodes":[{"key":"a","kind":"assign","operand":"out"}],"connections":[{"from":{"node":"$in","port":"value"},"to":{"node":"a","port":"in"}}],"operands":[{"key":"in","scope":"local","components":["Enable"]},{"key":"out","scope":"local","components":["Active"]}]}}
            """);
        TypedGraphicalPayload.Validate(Valid());
        foreach (var corrupt in new Action<JObject>[]
        {
            payload => payload["unknown"] = true,
            payload => payload.Remove("blockPath"),
            payload => payload["model"]!["schemaVersion"] = "1",
            payload => payload["model"]!["schemaVersion"] = 1.0,
            payload => payload["model"]!["profiles"] = new JArray(),
            payload => payload["model"]!["language"] = "LAD",
            payload => payload["model"]!["nodes"] = JValue.CreateNull(),
            payload => payload["model"]!["nodes"]![0] = JValue.CreateNull(),
            payload => payload["model"]!["operands"]![0] = JValue.CreateNull(),
            payload => payload["model"]!["connections"]![0]!["from"] = JValue.CreateNull(),
            payload => payload["model"]!["nodes"]![0]!["negated"] = "true",
            payload => payload["model"]!["nodes"]![0]!["writable"] = true,
            payload => payload["model"]!["nodes"]![0]!["nativeUid"] = "21",
            payload => payload["model"]!["operands"]![0]!["components"]![0] = 7,
            payload => payload["model"]!["networkId"] = "1",
            payload => payload["model"]!["creation"] = JValue.CreateNull(),
            payload => payload["instances"] = new JArray(JValue.CreateNull()),
            payload => payload["instances"] = JArray.Parse("[{\"key\":\"i\",\"kind\":\"createMulti\",\"name\":\"State\",\"blockPath\":\"unexpected\"}]"),
            payload => payload["model"]!["operands"]![0]!["scope"] = "constant"
        })
        {
            var payload = Valid(); corrupt(payload);
            await Error("InvalidTypedGraphicalPayload", () => TypedGraphicalPayload.Validate(payload));
        }
        var creation = Valid(); creation.Remove("blockPath");
        creation["model"]!["operation"] = "createBlock";
        creation["model"]!["creation"] = JObject.Parse("{\"name\":\"NewBlock\",\"blockType\":\"FB\",\"interface\":[]}");
        TypedGraphicalPayload.Validate(creation);
        creation["model"]!["creation"]!["interface"] = new JArray(JValue.CreateNull());
        await Error("InvalidTypedGraphicalPayload", () => TypedGraphicalPayload.Validate(creation));
    }

    internal static Task ReaderRetainsOpaqueContentAndReportsCoverage()
    {
        foreach (var language in new[] { "LAD", "FBD" })
        {
            var source = Fixture(language);
            var before = source.ToString(SaveOptions.DisableFormatting);
            var view = TypedGraphical.Read(source, "Devices/PLC/Program blocks/Independent");
            Check.Equal(1, view.SchemaVersion);
            Check.Equal("Devices/PLC/Program blocks/Independent", view.BlockPath);
            Check.Equal(before, view.NativeXml);
            Check.Equal(before, source.ToString(SaveOptions.DisableFormatting));
            Check.Equal(64, view.SourceSha256.Length);
            Check.Equal(1, view.Networks.Length);
            var network = view.Networks.Single();
            Check.Equal("1", network.NetworkId);
            Check.Equal(language, network.Language);
            Check.Equal(false, network.Coverage == "complete");
            var opaque = network.Nodes.Single(node => node.NativeUid == "900");
            Check.Equal(false, opaque.Supported);
            Check.Equal(true, opaque.NativeXml.Contains("vendor-value", StringComparison.Ordinal));
            Check.Equal(64, opaque.SourceSha256.Length);
            Check.Equal(true, network.NativeXml.Contains("opaque comment", StringComparison.Ordinal));
            Check.Equal(true, network.Connections.Any(connection => !connection.Resolved));
            foreach (bool duplicateParts in new[] { false, true })
            {
                var protectedSource = EngineeringSources.ReadXml(EngineeringSources.GraphicalFixture("Protected", language));
                if (duplicateParts) protectedSource.Descendants(TypedGraphical.Net + "FlgNet").Single().Add(new XElement(TypedGraphical.Net + "Parts"));
                else protectedSource.Descendants(TypedGraphical.Net + "Component").First().Value = "unknown native payload";
                var protectedRead = TypedGraphical.Read(protectedSource, "/protected");
                Check.Equal(false, protectedRead.Networks.Single().Coverage == "complete");
                Check.Equal(protectedSource.ToString(SaveOptions.DisableFormatting), protectedRead.NativeXml);
            }
        }
        return Task.CompletedTask;
    }

    internal static Task InsertPreservesEntirePriorDocumentAndAllocatesDeterministically()
    {
        foreach (var language in new[] { "LAD", "FBD" })
        {
            var source = Fixture(language);
            var before = source.ToString(SaveOptions.DisableFormatting);
            var first = TypedGraphical.Build(source, BooleanModel(language), Context());
            var second = TypedGraphical.Build(source, BooleanModel(language), Context());
            Check.Equal(before, source.ToString(SaveOptions.DisableFormatting));
            Check.Equal(first.Document.ToString(SaveOptions.DisableFormatting), second.Document.ToString(SaveOptions.DisableFormatting));
            Check.Equal(first.NetworkId, second.NetworkId);
            Check.Equal(string.Join("|", first.IdMapping.OrderBy(item => item.Key)), string.Join("|", second.IdMapping.OrderBy(item => item.Key)));
            var unchanged = new XDocument(first.Document);
            unchanged.Descendants("SW.Blocks.CompileUnit").Single(unit => unit.Attribute("ID")?.Value == first.NetworkId).Remove();
            Check.Equal(before, unchanged.ToString(SaveOptions.DisableFormatting));
            var nativeIds = first.Document.Descendants().Attributes("ID").Select(attribute => attribute.Value).ToArray();
            Check.Equal(nativeIds.Length, nativeIds.Distinct(StringComparer.Ordinal).Count());
            var added = first.Document.Descendants("SW.Blocks.CompileUnit").Single(unit => unit.Attribute("ID")?.Value == first.NetworkId);
            var definitions = added.Descendants().Where(element => element.Name.LocalName is "Part" or "Access" or "Wire").Select(element => element.Attribute("UId")!.Value).ToArray();
            Check.Equal(definitions.Length, definitions.Distinct(StringComparer.Ordinal).Count());
            Check.Equal(false, definitions.Contains("900", StringComparer.Ordinal));
            Check.Equal(false, definitions.Contains("901", StringComparer.Ordinal));
            Check.Equal(true, first.Effects.Length > 0);
        }
        return Task.CompletedTask;
    }

    internal static async Task VersionsLanguagesAndTopologyFailClosed()
    {
        var source = Fixture("LAD");
        var changed = BooleanModel("LAD");
        changed.SchemaVersion = 2;
        await Error("TypedGraphicalVersionUnsupported", () => TypedGraphical.Build(source, changed, Context()));
        await Error("GraphicalLanguageMismatch", () => TypedGraphical.Build(source, BooleanModel("FBD"), Context()));
        changed = BooleanModel("LAD");
        changed.Nodes[1].Key = changed.Nodes[0].Key;
        await AnyError(() => TypedGraphical.Build(source, changed, Context()));
        changed = BooleanModel("LAD");
        changed.Connections[0].To.Node = "absent";
        await Error("InvalidGraphTopology", () => TypedGraphical.Build(source, changed, Context()));
        changed = BooleanModel("LAD");
        changed.Connections = changed.Connections.Concat([Wire("coil", "out", "contact", "in")]).ToArray();
        await AnyError(() => TypedGraphical.Build(source, changed, Context()));
    }

    internal static async Task TypesWritesAndUnresolvedOperandsNeverGuess()
    {
        var source = Fixture("LAD");
        var context = Context();
        context.Operands.Remove("output");
        await Error("UnresolvedOperand", () => TypedGraphical.Build(source, BooleanModel("LAD"), context));
        context = Context();
        context.Operands["input"].DataType = "Int";
        await Error("OperandTypeMismatch", () => TypedGraphical.Build(source, BooleanModel("LAD"), context));
        context = Context();
        context.Operands["output"].Writable = false;
        await Error("OperandNotWritable", () => TypedGraphical.Build(source, BooleanModel("LAD"), context));
        context = Context();
        context.Operands["output"].Scope = "LiteralConstant";
        context.Operands["output"].Value = "true";
        context.Operands["output"].Writable = false;
        await AnyError(() => TypedGraphical.Build(source, BooleanModel("LAD"), context));
    }

    internal static async Task OpaqueNetworksCannotBeReplacedAsCompleteGraphs()
    {
        foreach (var language in new[] { "LAD", "FBD" })
        {
            var source = Fixture(language);
            var before = source.ToString(SaveOptions.DisableFormatting);
            var model = BooleanModel(language);
            model.Operation = "replaceNetwork";
            model.InsertAfterNetworkId = null;
            model.NetworkId = "1";
            await Error("OpaqueNetworkBoundary", () => TypedGraphical.Build(source, model, Context()));
            Check.Equal(before, source.ToString(SaveOptions.DisableFormatting));
        }
    }

    internal static async Task AdvancedProfilesCannotBeEnabledByNameOrWrongTarget()
    {
        var model = ComparisonModel();
        var context = Context();
        context.Operands["left"] = Operand("left", "Left", "Int", false);
        context.Operands["right"] = Operand("right", "Right", "Int", false);
        await Error("GraphicalProfileUnavailable", () => TypedGraphical.Build(Fixture("FBD"), model, context));
        foreach (var corrupt in new Action<TypedGraphicalProfile>[]
        {
            profile => profile.Language = "LAD", profile => profile.TiaVersion = "V20",
            profile => profile.CpuFamily = "S7-1200", profile => profile.Route = "softwareUnit",
            profile => profile.NativeFormatValidated = false, profile => profile.SourceSha256 = "",
            profile => profile.Provenance = ""
        })
        {
            var profile = ComparisonProfile();
            corrupt(profile);
            context.Profiles = [profile];
            await Error("GraphicalProfileUnavailable", () => TypedGraphical.Build(Fixture("FBD"), model, context));
        }
    }

    internal static Task ComparisonUsesIndependentPinAndOverloadExpectations()
    {
        var context = Context();
        context.Operands["left"] = Operand("left", "Left", "Int", false);
        context.Operands["right"] = Operand("right", "Right", "Int", false);
        context.Profiles = [ComparisonProfile()];
        var result = TypedGraphical.Build(Fixture("FBD"), ComparisonModel(), context);
        var network = result.Document.Descendants("SW.Blocks.CompileUnit").Single(unit => unit.Attribute("ID")?.Value == result.NetworkId);
        var comparison = network.Descendants(Net + "Part").Single(part => part.Attribute("Name")?.Value == "Eq");
        Check.Equal("Int", comparison.Elements(Net + "TemplateValue").Single(value => value.Attribute("Name")?.Value == "SrcType").Value);
        Check.Equal(true, network.Descendants(Net + "NameCon").Any(pin => pin.Attribute("UId")?.Value == comparison.Attribute("UId")?.Value && pin.Attribute("Name")?.Value == "in1"));
        Check.Equal(true, network.Descendants(Net + "NameCon").Any(pin => pin.Attribute("UId")?.Value == comparison.Attribute("UId")?.Value && pin.Attribute("Name")?.Value == "in2"));
        Check.Equal(true, network.Descendants(Net + "NameCon").Any(pin => pin.Attribute("UId")?.Value == comparison.Attribute("UId")?.Value && pin.Attribute("Name")?.Value == "out"));
        return Task.CompletedTask;
    }

    internal static async Task ConstantsRespectExactSignedUnsignedAndTimeBounds()
    {
        foreach (var pair in new (string Type, string Value)[]
        {
            ("Bool", "true"), ("Bool", "false"), ("SInt", "-128"), ("SInt", "127"),
            ("Int", "-32768"), ("Int", "32767"), ("UInt", "65535"),
            ("LInt", "-9223372036854775808"), ("ULInt", "18446744073709551615"),
            ("Time", "T#-2147483648ms"), ("Time", "T#2147483647ms"),
            ("LTime", "LT#9223372036854775807ns"), ("Real", "-0.25"), ("LReal", "1.5e100")
        }) TypedGraphical.ValidateConstant(pair.Type, pair.Value);
        foreach (var pair in new (string Type, string Value)[]
        {
            ("Bool", "1"), ("Bool", "True"), ("SInt", "-129"), ("SInt", "128"),
            ("Int", "32768"), ("UInt", "-1"), ("UInt", "65536"),
            ("LInt", "9223372036854775808"), ("ULInt", "18446744073709551616"),
            ("Time", "T#2147483648ms"), ("Time", "200"), ("Time", "T#1s"),
            ("LTime", "LT#9223372036854775808ns"), ("Real", "NaN"), ("Real", "Infinity"),
            ("Real", "1e40"), ("Int", "1.5"), ("Word", "16#FFFF"), ("Unknown", "1")
        }) await Error("InvalidTypedConstant", () => TypedGraphical.ValidateConstant(pair.Type, pair.Value));
    }

    internal static async Task CallsBindExactInterfacesAndRejectInvalidDirections()
    {
        var (model, context) = CallFixture(false);
        var result = TypedGraphical.Build(Fixture("FBD"), model, context);
        var unit = result.Document.Descendants("SW.Blocks.CompileUnit").Single(e => e.Attribute("ID")?.Value == result.NetworkId);
        var call = unit.Descendants(Net + "CallInfo").Single();
        Check.Equal("IndependentCallee", call.Attribute("Name")!.Value);
        Check.Equal("FC", call.Attribute("BlockType")!.Value);
        Check.Equal("Input:Value:Int|Return:Ret_Val:Int|Output:Result:Int|InOut:Accumulator:Int",
            string.Join("|", call.Elements(Net + "Parameter").Select(p => p.Attribute("Section")!.Value + ":" + p.Attribute("Name")!.Value + ":" + p.Attribute("Type")!.Value)));
        Check.Equal(true, result.Effects.Any(e => e.Kind == "dependency" && e.Identity == model.Nodes[0].CalleePath));
        context.Callees[model.Nodes[0].CalleePath].Path = "Devices/OtherPLC/Program blocks/IndependentCallee";
        await Error("GraphicalCalleeUnresolved", () => TypedGraphical.Build(Fixture("FBD"), model, context));
        (model, context) = CallFixture(false);
        context.Operands["acc"].Writable = false;
        await Error("OperandNotWritable", () => TypedGraphical.Build(Fixture("FBD"), model, context));
        (model, context) = CallFixture(false);
        model.Connections = model.Connections.Where(c => c.To.Port != "Value").ToArray();
        await Error("GraphicalMissingArgument", () => TypedGraphical.Build(Fixture("FBD"), model, context));
        (model, context) = CallFixture(false);
        model.Connections[0].To.Port = "Result";
        await Error("InvalidGraphTopology", () => TypedGraphical.Build(Fixture("FBD"), model, context));
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "fixtures", "typed-graphical", "profiles.v1.json"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Missing qualified native call corpus.");
        string fixtures = Path.Combine(root.FullName, "fixtures", "typed-graphical");
        var profiles = JObject.Parse(File.ReadAllText(Path.Combine(fixtures, "profiles.v1.json")))["profiles"]!.ToObject<TypedGraphicalProfile[]>()!;
        var cases = JObject.Parse(File.ReadAllText(Path.Combine(fixtures, "numeric-cases.v1.json")))["cases"]!;
        foreach (string language in new[] { "LAD", "FBD" })
        {
            var captured = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(fixtures, "native", "TgpUnit_NumericFC_" + language + ".xml")));
            var authored = cases.Single(c => c.Value<string>("family") == "numeric-fc" && c.Value<string>("language") == language)["payload"]!["model"]!.ToObject<TypedGraphicalModel>()!;
            authored.NetworkId = "3";
            const string calleePath = "/captured/unit/TgpUnit_NumericFC";
            authored.Nodes.Single(n => n.Kind == "call").CalleePath = calleePath;
            var qualified = Context(); qualified.Route = "softwareUnit"; qualified.TargetNamespace = "TiaMcp_UnitA"; qualified.Profiles = profiles;
            qualified.Operands.Clear();
            foreach (var operand in authored.Operands)
                qualified.Operands[operand.Key] = operand.Scope == "constant" ? new() { Identity = "literal:true", Scope = "LiteralConstant", DataType = "Bool", Value = "true", DeclarationHash = "captured" }
                    : new() { Identity = "/captured/" + operand.Key, Scope = "LocalVariable", Components = operand.Components, DataType = "Int", Writable = operand.Key != "Value", DeclarationHash = "captured" };
            qualified.Callees[calleePath] = new() { Path = calleePath, Name = "TgpUnit_NumericFC", Namespace = "TiaMcp_UnitA", BlockType = "FC", InterfaceHash = new string('a', 64), Parameters = [
                new() { Name = "Value", Section = "Input", DataType = "Int" }, new() { Name = "Result", Section = "Output", DataType = "Int" },
                new() { Name = "Accumulator", Section = "InOut", DataType = "Int" }, new() { Name = "Ret_Val", Section = "Return", DataType = "Int" }] };
            var actual = TypedGraphical.Build(captured, authored, qualified);
            var info = actual.Document.Descendants(Net + "CallInfo").Single();
            Check.Equal("TiaMcp_UnitA.TgpUnit_NumericFC", info.Attribute("Name")!.Value);
            Check.Equal(null, info.Attribute("Namespace"));
            EngineeringSources.RequireNativePreservation(captured, actual.Document);
            qualified.TargetNamespace = "TiaMcp_UnitA.Alternate";
            await Error("GraphicalCalleeUnsupported", () => TypedGraphical.Build(captured, authored, qualified));
        }
    }

    internal static async Task FbInstancesRequireExactOwnershipAndIntentionalSharing()
    {
        var (model, context) = CallFixture(true);
        var result = TypedGraphical.Build(Fixture("FBD"), model, context);
        var instance = result.Document.Descendants(Net + "Instance").Single();
        Check.Equal("GlobalVariable", instance.Attribute("Scope")!.Value);
        Check.Equal("ExistingState", instance.Elements(Net + "Component").Single().Attribute("Name")!.Value);
        context.Instances["state"].CalleePath = "Devices/OtherPLC/Program blocks/IndependentCallee";
        await Error("GraphicalInstanceMismatch", () => TypedGraphical.Build(Fixture("FBD"), model, context));
        (model, context) = CallFixture(true);
        context.Instances.Clear();
        await Error("GraphicalInstanceUnresolved", () => TypedGraphical.Build(Fixture("FBD"), model, context));
        (model, context) = CallFixture(true);
        model.Nodes = model.Nodes.Concat([new TypedGraphicalNode { Key = "other", Kind = "call", CalleePath = model.Nodes[0].CalleePath, Instance = "alias" }]).ToArray();
        context.Instances["alias"] = context.Instances["state"];
        await Error("GraphicalSharedInstance", () => TypedGraphical.Build(Fixture("FBD"), model, context));
    }

    internal static async Task BoundedEditsPreserveIdsAndRejectSharedUnknownAccess()
    {
        var source = Fixture("LAD");
        var model = new TypedGraphicalModel { SchemaVersion = 1, Language = "LAD", Operation = "editNetwork", NetworkId = "1",
            Nodes = [new() { Key = "contact", Kind = "contact", NativeUid = "23", Operand = "alternate" }],
            Connections = [], Operands = [Request("alternate", "Alternate")] };
        var context = Context(); context.Operands["alternate"] = Operand("alternate", "Alternate", "Bool", false);
        var expected = new XDocument(source);
        expected.Descendants(Net + "Access").Single(e => e.Attribute("UId")?.Value == "21").Descendants(Net + "Component").Single().SetAttributeValue("Name", "Alternate");
        var result = TypedGraphical.Build(source, model, context);
        Check.Equal(expected.ToString(SaveOptions.DisableFormatting), result.Document.ToString(SaveOptions.DisableFormatting));
        Check.Equal("23", result.IdMapping["node:contact"]);
        Check.Equal("21", result.IdMapping["operand:alternate"]);
        source.Descendants(Net + "Wires").Single().Add(new XElement(Net + "Wire", new XAttribute("UId", "777"),
            new XElement(Net + "IdentCon", new XAttribute("UId", "21")), new XElement(Net + "NameCon", new XAttribute("UId", "900"), new XAttribute("Name", "operand"))));
        await Error("OpaqueNetworkBoundary", () => TypedGraphical.Build(source, model, context));
        source = Fixture("LAD");
        source.Descendants(Net + "Access").Single(e => e.Attribute("UId")?.Value == "21").SetAttributeValue("VendorAttribute", "retain");
        await Error("OpaqueNetworkBoundary", () => TypedGraphical.Build(source, model, context));
    }

    internal static async Task OpaqueDuplicateIdsAndUnsupportedRoutesFailClosed()
    {
        var source = Fixture("LAD");
        source.Descendants().Single(e => e.Name.LocalName == "Payload").SetAttributeValue("UId", "23");
        await AnyError(() => TypedGraphical.Build(source, BooleanModel("LAD"), Context()));
        await AnyError(() => TypedGraphical.Read(source, "exact"));
        source = Fixture("LAD");
        source.Descendants().Single(e => e.Name.LocalName == "Payload").SetAttributeValue("UId", "00023");
        await AnyError(() => TypedGraphical.Build(source, BooleanModel("LAD"), Context()));
        await AnyError(() => TypedGraphical.Read(source, "exact"));
        source = Fixture("LAD");
        source.Descendants("OpaqueBlockExtension").Single().SetAttributeValue("ID", "00");
        await AnyError(() => TypedGraphical.Build(source, BooleanModel("LAD"), Context()));
        foreach (string route in new[] { "softwareUnit", "namespace", "invented" })
        {
            var context = Context(); context.Route = route;
            await AnyError(() => TypedGraphical.Build(Fixture("LAD"), BooleanModel("LAD"), context));
        }
        var version = Context(); version.TiaVersion = "V20";
        await Error("GraphicalTargetUnsupported", () => TypedGraphical.Build(Fixture("LAD"), BooleanModel("LAD"), version));
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "fixtures", "typed-graphical", "profiles.v1.json"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Missing independent native insertion evidence.");
        foreach (string language in new[] { "LAD", "FBD" })
        {
            var native = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(root.FullName, "fixtures", "typed-graphical", "native", "Tgp_Boolean_" + language + ".xml")));
            string unchanged = native.ToString(SaveOptions.DisableFormatting);
            var model = BooleanModel(language); model.InsertAfterNetworkId = "3";
            var error = await Check.ThrowsAsync<EngineeringBridgeException>(() => { TypedGraphical.Build(native, model, Context()); return Task.CompletedTask; });
            Check.Equal("NativeIdPreservationUnsupported", error.Code);
            Check.Equal(true, error.Message.Contains("8->", StringComparison.Ordinal));
            Check.Equal(unchanged, native.ToString(SaveOptions.DisableFormatting));
        }
    }

    internal static async Task CreationRetainsCompleteNativeDefaultsAndRejectsMissingPrototypes()
    {
        foreach (var language in new[] { "LAD", "FBD" })
        foreach (var blockType in new[] { "FC", "FB" })
        {
            var source = CreationFixture(language, blockType);
            string before = source.ToString(SaveOptions.DisableFormatting);
            var model = BooleanModel(language); model.Operation = "createBlock"; model.InsertAfterNetworkId = null;
            model.Creation = new() { Name = "Created", BlockType = blockType, Interface = [
                new() { Name = "NewInput", Section = "Input", DataType = "Bool" },
                new() { Name = "NewOutput", Section = "Output", DataType = "Bool" }] };
            var created = TypedGraphicalCreationTemplate.Create(model, "", 321, source);
            Check.Equal(before, source.ToString(SaveOptions.DisableFormatting));
            var block = created.Root!.Element("SW.Blocks." + blockType)!;
            Check.Equal("Created", block.Element("AttributeList")!.Element("Name")!.Value);
            Check.Equal("321", block.Element("AttributeList")!.Element("Number")!.Value);
            Check.Equal("false", block.Element("AttributeList")!.Element("AutoNumber")!.Value);
            Check.Equal("retain", block.Attribute("VendorAttribute")!.Value);
            Check.Equal("100", block.Element("AttributeList")!.Element("MemoryReserve")!.Value);
            var input = block.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "NewInput");
            Check.Equal("Public", input.Attribute("Accessibility")!.Value);
            Check.Equal("true", input.Descendants().Single(e => e.Name.LocalName == "BooleanAttribute").Value);
            var unit = block.Descendants("SW.Blocks.CompileUnit").Single();
            Check.Equal("1", unit.Attribute("ID")!.Value);
            Check.Equal(0, unit.Descendants(Net + "Parts").Single().Elements().Count());
            Check.Equal(0, unit.Descendants(Net + "Wires").Single().Elements().Count());
            Check.Equal(source.Descendants("MultilingualText").Count(), created.Descendants("MultilingualText").Count());
            var withoutDefault = new XDocument(created);
            withoutDefault.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "NewInput").Attribute("Accessibility")!.Remove();
            await Error("NativePreservationMismatch", () => EngineeringSources.RequireNativePreservation(withoutDefault, created));
            model.Creation.Interface[0].DataType = "Int";
            await Error("GraphicalCreationTemplateUnavailable", () => TypedGraphicalCreationTemplate.Create(model, "", 321, source));
            model.Creation.Interface[0].DataType = "Bool";
            await Error("GraphicalCreationTemplateUnavailable", () => TypedGraphicalCreationTemplate.Create(model, "", 0, source));
            model.Language = language == "LAD" ? "FBD" : "LAD";
            await Error("GraphicalCreationTemplateUnavailable", () => TypedGraphicalCreationTemplate.Create(model, "", 321, source));
        }
    }

    internal static async Task BranchFanoutAndStatefulStorageFollowIndependentExpectations()
    {
        var context = Context();
        var branch = ComparisonProfile(); branch.Id = "unit-test-only-lad-branch"; branch.Language = "LAD"; branch.Kind = "branch"; branch.Instruction = null; branch.DataType = null;
        branch.NativeName = "O"; branch.NativeTemplateXml = "<Part xmlns='http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5' Name='O' UId='700'><TemplateValue Name='Card' Type='Cardinality'>2</TemplateValue></Part>";
        context.Profiles = [branch]; context.Operands["second"] = Operand("second", "Second", "Bool", false);
        var model = BooleanModel("LAD");
        model.Nodes = [new() { Key = "rail", Kind = "powerrail" }, new() { Key = "a", Kind = "contact", Operand = "input" }, new() { Key = "b", Kind = "contact", Operand = "second", Negated = true },
            new() { Key = "join", Kind = "branch", InputCount = 2 }, new() { Key = "coil", Kind = "coil", Operand = "output" }];
        model.Connections = [Wire("rail", "out", "a", "in"), Wire("rail", "out", "b", "in"), Wire("a", "out", "join", "in1"), Wire("b", "out", "join", "in2"), Wire("join", "out", "coil", "in")];
        var built = TypedGraphical.Build(Fixture("LAD"), model, context);
        var unit = built.Document.Descendants("SW.Blocks.CompileUnit").Single(e => e.Attribute("ID")?.Value == built.NetworkId);
        var railWire = unit.Descendants(Net + "Wire").Single(e => e.Element(Net + "Powerrail") != null);
        Check.Equal(2, railWire.Elements(Net + "NameCon").Count());
        Check.Equal("2", unit.Descendants(Net + "TemplateValue").Single().Value);
        model.Nodes.Single(n => n.Kind == "branch").InputCount = 3;
        await Error("GraphicalMissingArgument", () => TypedGraphical.Build(Fixture("LAD"), model, context));

        foreach (string instruction in new[] { "TON", "TOF", "TP", "CTU", "CTD", "CTUD" })
        {
            bool timer = instruction is "TON" or "TOF" or "TP";
            string inputPin = timer ? "IN" : instruction == "CTD" ? "CD" : "CU";
            string outputPin = instruction == "CTUD" ? "QU" : "Q";
            context = Context();
            var profile = ComparisonProfile(); profile.Kind = timer ? "timer" : "counter"; profile.Instruction = instruction; profile.DataType = timer ? "Time" : "Int";
            profile.NativeName = instruction; profile.InstanceType = instruction + (timer ? "_TIME" : "_INT");
            profile.NativeTemplateXml = $"<Part xmlns='http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5' Name='{instruction}' UId='700'><Instance Scope='LocalVariable' UId='701'><Component Name='Prototype'/></Instance></Part>";
            profile.Pins = [new() { Name = inputPin, Direction = "input", DataType = "Bool" }, new() { Name = outputPin, Direction = "output", DataType = "Bool" }];
            context.Profiles = [profile];
            context.Instances["state"] = new() { Identity = "exact-caller::Dedicated", Scope = "LocalVariable", Components = ["Dedicated"], CalleePath = "instruction:" + instruction, DataType = profile.InstanceType, DeclarationHash = new string('d', 64) };
            model = new() { SchemaVersion = 1, Language = "FBD", Operation = "insertNetwork", InsertAfterNetworkId = "1",
                Nodes = [new() { Key = "stateful", Kind = profile.Kind, Instruction = instruction, DataType = profile.DataType, Instance = "state" }],
                Operands = [], Connections = [Wire("$input", "value", "stateful", inputPin), Wire("stateful", outputPin, "$output", "value")] };
            var before = profile.NativeTemplateXml;
            built = TypedGraphical.Build(Fixture("FBD"), model, context);
            Check.Equal(before, profile.NativeTemplateXml);
            var nativeInstance = built.Document.Descendants(Net + "Instance").Single();
            Check.Equal("LocalVariable", nativeInstance.Attribute("Scope")!.Value);
            Check.Equal("Dedicated", nativeInstance.Element(Net + "Component")!.Attribute("Name")!.Value);
            Check.Equal(false, nativeInstance.Attribute("UId")!.Value == "701");
            context.Instances["state"].DataType = "WrongType";
            await Error("GraphicalInstanceMismatch", () => TypedGraphical.Build(Fixture("FBD"), model, context));
        }
    }

    private static XDocument CreationFixture(string language, string blockType)
    {
        var source = Fixture(language);
        var block = source.Root!.Element("SW.Blocks.FC")!; block.Name = "SW.Blocks." + blockType;
        block.Element("AttributeList")!.Add(new XElement("AutoNumber", "true"), new XElement("Number", "7"), new XElement("MemoryReserve", "100"));
        foreach (var text in source.Descendants("Text")) text.Value = "";
        foreach (var member in source.Descendants().Where(e => e.Name.LocalName == "Member"))
        {
            member.SetAttributeValue("Accessibility", "Public");
            member.Add(new XElement(member.Name.Namespace + "AttributeList", new XElement(member.Name.Namespace + "BooleanAttribute", new XAttribute("Name", "ExternalAccessible"), new XAttribute("SystemDefined", "true"), "true")));
        }
        if (blockType == "FB") source.Descendants().Single(e => e.Name.LocalName == "Section" && e.Attribute("Name")?.Value == "Return").Remove();
        return source;
    }

    internal static async Task GlobalInstanceTemplatesRetainObservedDefaults()
    {
        var callee = CreationFixture("LAD", "FB");
        var calleeSections = callee.Descendants().Single(e => e.Name.LocalName == "Sections");
        calleeSections.Add(new XElement(calleeSections.Name.Namespace + "Section", new XAttribute("Name", "InOut")),
            new XElement(calleeSections.Name.Namespace + "Section", new XAttribute("Name", "Static")));
        var template = new XDocument(callee);
        var db = template.Root!.Element("SW.Blocks.FB")!; db.Name = "SW.Blocks.InstanceDB";
        db.Element("AttributeList")!.Element("ProgrammingLanguage")!.Value = "DB";
        db.Element("AttributeList")!.Add(new XElement("InstanceOfName", "PrototypeCallee"), new XElement("InstanceOfType", "FB"));
        db.Descendants("SW.Blocks.CompileUnit").Remove();
        string before = template.ToString(SaveOptions.DisableFormatting);
        callee.Root!.Element("SW.Blocks.FB")!.Element("AttributeList")!.Element("Name")!.Value = "ExactCallee";
        calleeSections.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "Enable").SetAttributeValue("Name", "RenamedEnable");
        var actual = TypedGraphicalInstanceTemplate.Create("NewInstance", 456, callee, template);
        Check.Equal(before, template.ToString(SaveOptions.DisableFormatting));
        var attributes = actual.Root!.Element("SW.Blocks.InstanceDB")!.Element("AttributeList")!;
        Check.Equal("ExactCallee", attributes.Element("InstanceOfName")!.Value);
        Check.Equal("FB", attributes.Element("InstanceOfType")!.Value);
        Check.Equal("456", attributes.Element("Number")!.Value);
        Check.Equal("false", attributes.Element("AutoNumber")!.Value);
        Check.Equal("NewInstance", attributes.Element("Name")!.Value);
        Check.Equal(true, actual.Descendants().Any(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "RenamedEnable"));
        Check.Equal(false, actual.Descendants().Any(e => e.Name.LocalName == "InstanceOfNumber"));
        Check.Equal(4, actual.Descendants().Count(e => e.Name.LocalName == "Section"));
        var missingDefault = new XDocument(actual);
        missingDefault.Descendants().First(e => e.Name.LocalName == "BooleanAttribute").Remove();
        await Error("NativePreservationMismatch", () => EngineeringSources.RequireNativePreservation(missingDefault, actual));
        var member = calleeSections.Descendants().First(e => e.Name.LocalName == "Member");
        member.SetAttributeValue("Datatype", "Int");
        await Error("GraphicalInstanceTemplateUnavailable", () => TypedGraphicalInstanceTemplate.Create("NewInstance", 456, callee, template));
        member.SetAttributeValue("Datatype", "Bool");
        member.Add(new XElement(member.Name.Namespace + "StartValue", "true"));
        await Error("GraphicalInstanceTemplateUnavailable", () => TypedGraphicalInstanceTemplate.Create("NewInstance", 456, callee, template));
    }

    internal static Task CapturedCreationAndInstanceDefaultsRemainComplete()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "fixtures", "typed-graphical", "profiles.v1.json"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Missing typed native template corpus.");
        string fixtures = Path.Combine(root.FullName, "fixtures", "typed-graphical");
        var catalog = JObject.Parse(File.ReadAllText(Path.Combine(fixtures, "profiles.v1.json")));
        foreach (var entry in catalog["creationTemplates"]!)
        {
            var template = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(fixtures, entry.Value<string>("path")!)));
            var declarations = template.Descendants().Where(e => e.Name.LocalName == "Section")
                .SelectMany(section => section.Elements().Where(e => e.Name.LocalName == "Member").Select(member => new TypedGraphicalDeclaration
                { Section = section.Attribute("Name")!.Value, Name = "Fresh_" + member.Attribute("Name")!.Value, DataType = member.Attribute("Datatype")!.Value })).ToArray();
            var model = new TypedGraphicalModel { SchemaVersion = 1, Operation = "createBlock", Language = entry.Value<string>("language")!,
                Creation = new() { Name = "FreshBlock", BlockType = entry.Value<string>("blockType")!, Interface = declarations } };
            var created = TypedGraphicalCreationTemplate.Create(model, template.Descendants("Namespace").FirstOrDefault()?.Value ?? "", 999, template);
            Check.Equal("999", created.Descendants("Number").Single().Value);
            Check.Equal(entry.Value<string>("route") == "standard" ? "false" : template.Descendants("AutoNumber").Single().Value,
                created.Descendants("AutoNumber").Single().Value);
            Check.Equal(template.Descendants("MemoryLayout").Count(), created.Descendants("MemoryLayout").Count());
            foreach (var original in template.Descendants().Where(e => e.Name.LocalName == "Member"))
            {
                var expected = new XElement(original); expected.SetAttributeValue("Name", "Fresh_" + original.Attribute("Name")!.Value);
                var actual = created.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == expected.Attribute("Name")!.Value);
                EngineeringSources.RequireNativePreservation(new XDocument(new XElement("Document", expected)), new XDocument(new XElement("Document", new XElement(actual))));
            }
        }
        foreach (var entry in catalog["globalInstanceTemplates"]!)
        {
            var template = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(fixtures, entry.Value<string>("path")!)));
            var reference = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(fixtures, entry.Value<string>("referenceCalleePath")!)));
            var result = TypedGraphicalInstanceTemplate.Create("FreshState", 998, reference, template);
            var expected = new XDocument(template); var attributes = expected.Root!.Element("SW.Blocks.InstanceDB")!.Element("AttributeList")!;
            attributes.Element("Name")!.Value = "FreshState"; attributes.Element("Number")!.Value = "998";
            if (entry.Value<string>("route") == "standard") attributes.Element("AutoNumber")!.Value = "false";
            EngineeringSources.RequireNativePreservation(expected, result);
        }
        foreach (var entry in catalog["multiInstanceTemplates"]!)
        {
            var caller = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(fixtures, entry.Value<string>("path")!)));
            var callee = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(fixtures, entry.Value<string>("referenceCalleePath")!)));
            var captured = caller.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == entry.Value<string>("memberName"));
            var actual = TypedGraphicalMultiInstanceTemplate.Create("FreshChild", captured.Attribute("Datatype")!.Value, callee, callee, captured);
            var expected = new XElement(captured); expected.SetAttributeValue("Name", "FreshChild");
            EngineeringSources.RequireNativePreservation(new XDocument(new XElement("Document", expected)), new XDocument(new XElement("Document", actual)));
        }
        string effects = Path.Combine(fixtures, "native", "instance-effects");
        var beforeInstance = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(effects, "before-instance.xml")));
        var afterInstance = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(effects, "after-instance.xml")));
        var afterCaller = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(effects, "after-caller.xml")));
        var sourceChild = afterCaller.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "Child");
        var targetChild = afterInstance.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "Child");
        var dependent = TypedGraphicalMultiInstanceTemplate.AppendDependent(beforeInstance, "Tgp_InstanceEffect", sourceChild, sourceChild, targetChild);
        EngineeringSources.RequireNativePreservation(afterInstance, dependent);
        Check.Equal("42", dependent.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "RetainedMarker").Elements().Single(e => e.Name.LocalName == "StartValue").Value);
        return Task.CompletedTask;
    }

    internal static async Task MultiInstanceTemplatesPreserveObservedShapes()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "fixtures", "typed-graphical", "profiles.v1.json"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Missing typed native template corpus.");
        var observed = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(root.FullName, "fixtures", "typed-graphical", "native", "Tgp_FB_multi_LAD.xml")));
        var captured = new XElement(observed.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "Child"));
        // Unit adaptation of independently observed scalar defaults. The separate
        // native qualification uses hash-bound callee/caller/DB capture pairs.
        var reference = new XDocument(observed);
        reference.Root!.Element("SW.Blocks.FB")!.Element("AttributeList")!.Element("Name")!.Value = "TiaMcp_EnableGate";
        reference.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "Child").Remove();
        var actual = new XDocument(reference);
        actual.Root!.Element("SW.Blocks.FB")!.Element("AttributeList")!.Element("Name")!.Value = "FreshCallee";
        actual.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "Enable").SetAttributeValue("Name", "FreshEnable");
        string capturedBefore = captured.ToString(SaveOptions.DisableFormatting);
        var declaration = TypedGraphicalMultiInstanceTemplate.Create("FreshChild", "\"FreshCallee\"", actual, reference, captured);
        Check.Equal(capturedBefore, captured.ToString(SaveOptions.DisableFormatting));
        Check.Equal(null, declaration.Attribute("Remanence"));
        Check.Equal("true", declaration.Elements().Single(e => e.Name.LocalName == "AttributeList").Elements().Single(e => e.Attribute("Name")?.Value == "SetPoint").Value);
        var nested = declaration.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "FreshEnable");
        Check.Equal(2, nested.Attributes().Count()); Check.Equal(false, nested.HasElements);
        var expected = new XElement(captured); expected.SetAttributeValue("Name", "FreshChild"); expected.SetAttributeValue("Datatype", "\"FreshCallee\"");
        expected.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "Enable").SetAttributeValue("Name", "FreshEnable");
        EngineeringSources.RequireNativePreservation(new XDocument(new XElement("Document", expected)), new XDocument(new XElement("Document", new XElement(declaration))));
        await Error("GraphicalInstanceTemplateUnavailable", () => TypedGraphicalMultiInstanceTemplate.Create("FreshChild", "\"WrongCallee\"", actual, reference, captured));
        var badCallee = new XDocument(actual);
        badCallee.Descendants().First(e => e.Name.LocalName == "Member").Add(new XElement(nested.Name.Namespace + "StartValue", "true"));
        await Error("GraphicalInstanceTemplateUnavailable", () => TypedGraphicalMultiInstanceTemplate.Create("FreshChild", "\"FreshCallee\"", badCallee, reference, captured));

        var dbSections = new XElement(nested.Name.Namespace + "Sections", new[] { "Input", "Output", "InOut", "Static" }.Select(section =>
            new XElement(nested.Name.Namespace + "Section", new XAttribute("Name", section))));
        dbSections.Elements().Single(e => e.Attribute("Name")?.Value == "Static").Add(new XElement(nested.Name.Namespace + "Member",
            new XAttribute("Name", "Existing"), new XAttribute("Datatype", "Int"), new XElement(nested.Name.Namespace + "StartValue", "42")));
        var db = new XDocument(new XElement("Document", new XElement("SW.Blocks.InstanceDB", new XAttribute("ID", "0"), new XElement("AttributeList",
            new XElement("Name", "CallerState"), new XElement("InstanceOfName", "Caller"), new XElement("InstanceOfType", "FB"), new XElement("Interface", dbSections)))));
        string dbBefore = db.ToString(SaveOptions.DisableFormatting);
        var updated = TypedGraphicalMultiInstanceTemplate.AppendDependent(db, "Caller", declaration, captured, captured);
        Check.Equal(dbBefore, db.ToString(SaveOptions.DisableFormatting));
        var added = updated.Descendants().Single(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == "FreshChild");
        Check.Equal("42", updated.Descendants().Single(e => e.Name.LocalName == "StartValue").Value);
        added.Remove(); EngineeringSources.RequireNativePreservation(db, updated);
        await Error("DependencyProfileUnavailable", () => TypedGraphicalMultiInstanceTemplate.AppendDependent(db, "OtherCaller", declaration, captured, captured));
        await Error("DependencyProfileUnavailable", () => TypedGraphicalMultiInstanceTemplate.AppendDependent(db, "Caller", declaration, captured, null!));
    }

    private static (TypedGraphicalModel, TypedGraphicalContext) CallFixture(bool fb)
    {
        const string path = "Devices/PLC/Program blocks/IndependentCallee";
        var context = Context();
        foreach (var pair in new[] { ("value", "Value", false), ("result", "Result", true), ("acc", "Accumulator", true), ("return", "ReturnValue", true) })
            context.Operands[pair.Item1] = Operand(pair.Item1, pair.Item2, "Int", pair.Item3);
        context.Callees[path] = new() { Path = path, Name = "IndependentCallee", BlockType = fb ? "FB" : "FC", InterfaceHash = new string('b', 64),
            Parameters = fb ? [new() { Name = "Value", Section = "Input", DataType = "Int" }, new() { Name = "Result", Section = "Output", DataType = "Int" }]
                : [new() { Name = "Value", Section = "Input", DataType = "Int" }, new() { Name = "Result", Section = "Output", DataType = "Int" }, new() { Name = "Accumulator", Section = "InOut", DataType = "Int" }, new() { Name = "Ret_Val", Section = "Return", DataType = "Int" }] };
        var profile = ComparisonProfile(); profile.Kind = "call"; profile.Instruction = null; profile.DataType = null; profile.Pins = []; profile.NativeName = "Call";
        context.Profiles = [profile];
        context.Instances["state"] = new() { Identity = "Devices/PLC/Program blocks/ExistingState", Scope = "GlobalVariable", Components = ["ExistingState"], CalleePath = path, DataType = "IndependentCallee", DeclarationHash = new string('c', 64) };
        return (new() { SchemaVersion = 1, Language = "FBD", Operation = "insertNetwork", InsertAfterNetworkId = "1",
            Nodes = [new() { Key = "call", Kind = "call", CalleePath = path, Instance = fb ? "state" : null }],
            Operands = [], Connections = fb ? [Wire("$value", "value", "call", "Value"), Wire("call", "Result", "$result", "value")]
                : [Wire("$value", "value", "call", "Value"), Wire("call", "Result", "$result", "value"), Wire("$acc", "value", "call", "Accumulator"), Wire("call", "Ret_Val", "$return", "value")] }, context);
    }

    private static TypedGraphicalModel BooleanModel(string language) => new()
    {
        SchemaVersion = 1, Language = language, Operation = "insertNetwork", InsertAfterNetworkId = "1",
        Operands = [Request("input", "Enable"), Request("output", "Active")],
        Nodes = language == "LAD"
            ? [new() { Key = "rail", Kind = "powerrail" }, new() { Key = "contact", Kind = "contact", Operand = "input", Negated = true }, new() { Key = "coil", Kind = "coil", Operand = "output" }]
            : [new() { Key = "assign", Kind = "assign", Operand = "output" }],
        Connections = language == "LAD" ? [Wire("rail", "out", "contact", "in"), Wire("contact", "out", "coil", "in")]
            : [Wire("$input", "value", "assign", "in")]
    };

    private static TypedGraphicalModel ComparisonModel() => new()
    {
        SchemaVersion = 1, Language = "FBD", Operation = "insertNetwork", InsertAfterNetworkId = "1",
        Operands = [Request("left", "Left", "Int"), Request("right", "Right", "Int"), Request("output", "Active")],
        Nodes = [new() { Key = "eq", Kind = "compare", Comparison = "eq", DataType = "Int" }, new() { Key = "assign", Kind = "assign", Operand = "output" }],
        Connections = [Wire("$left", "value", "eq", "in1"), Wire("$right", "value", "eq", "in2"), Wire("eq", "out", "assign", "in")]
    };

    private static TypedGraphicalProfile ComparisonProfile()
    {
        const string template = "<Part xmlns='http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5' Name='Eq' UId='700'><TemplateValue Name='SrcType' Type='Type'>Int</TemplateValue></Part>";
        return new() { Id = "unit-test-only-fbd-eq-int", Language = "FBD", Kind = "compare", Instruction = "eq", DataType = "Int",
            TiaVersion = "V21", CpuFamily = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1", Route = "standard", NativeName = "Eq", NativeTemplateXml = template,
            SourceSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(template))),
            Provenance = "Independently authored unit-test native structure; not a native qualification claim.", NativeFormatValidated = true,
            Pins = [new() { Name = "in1", Direction = "input", DataType = "Int" }, new() { Name = "in2", Direction = "input", DataType = "Int" }, new() { Name = "out", Direction = "output", DataType = "Bool" }] };
    }

    private static TypedGraphicalOperandRequest Request(string key, string name, string type = "Bool") => new() { Key = key, Scope = "local", Components = [name], DataType = type };
    private static TypedGraphicalOperand Operand(string key, string name, string type, bool writable) => new()
    { Key = key, Identity = "Devices/PLC/Program blocks/Independent::" + name, Scope = "LocalVariable", Components = [name], DataType = type, Writable = writable, DeclarationHash = new string('a', 64) };
    private static TypedGraphicalContext Context() => new()
    {
        TiaVersion = "V21", CpuFamily = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1", Route = "standard",
        Operands = new(StringComparer.Ordinal) { ["input"] = Operand("input", "Enable", "Bool", false), ["output"] = Operand("output", "Active", "Bool", true) }
    };
    private static TypedGraphicalConnection Wire(string from, string fromPort, string to, string toPort) => new()
    { From = new() { Node = from, Port = fromPort }, To = new() { Node = to, Port = toPort } };
    private static async Task AnyError(Action action) => await Check.ThrowsAsync<EngineeringBridgeException>(() => { action(); return Task.CompletedTask; });
    private static async Task Error(string code, Action action)
    {
        var error = await Check.ThrowsAsync<EngineeringBridgeException>(() => { action(); return Task.CompletedTask; });
        Check.Equal(code, error.Code);
    }

    private static XDocument Fixture(string language)
    {
        var document = EngineeringSources.ReadXml($$"""
        <Document><Engineering version="V21"/><SW.Blocks.FC ID="0" VendorAttribute="retain">
          <AttributeList><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Input"><Member Name="Enable" Datatype="Bool"/></Section><Section Name="Output"><Member Name="Active" Datatype="Bool"/></Section><Section Name="Return"><Member Name="Ret_Val" Datatype="Void"/></Section></Sections></Interface><Name>Independent</Name><Namespace/><ProgrammingLanguage>{{language}}</ProgrammingLanguage><MemoryLayout>Optimized</MemoryLayout></AttributeList>
          <ObjectList><SW.Blocks.CompileUnit ID="1" CompositionName="CompileUnits"><AttributeList><NetworkSource><FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"><Parts>
            <Access Scope="LocalVariable" UId="21"><Symbol><Component Name="Enable"/></Symbol></Access>
            <Access Scope="LocalVariable" UId="22"><Symbol><Component Name="Active"/></Symbol></Access>
            <Part Name="Contact" UId="23"/><Part Name="Coil" UId="24"/>
            <!-- opaque comment --><Part Name="VendorInstruction" UId="900" xmlns:v="urn:vendor-extension"><v:Payload UId="901" mode="retain">vendor-value</v:Payload></Part>
          </Parts><Wires><Wire UId="30"><IdentCon UId="21"/><NameCon UId="23" Name="operand"/></Wire><Wire UId="31"><IdentCon UId="22"/><NameCon UId="24" Name="operand"/></Wire><Wire UId="32"><Powerrail/><NameCon UId="23" Name="in"/></Wire><Wire UId="33"><NameCon UId="23" Name="out"/><NameCon UId="24" Name="in"/></Wire><Wire UId="34"><NameCon UId="900" Name="out"/><NameCon UId="999" Name="unknown"/></Wire></Wires></FlgNet></NetworkSource><ProgrammingLanguage>{{language}}</ProgrammingLanguage></AttributeList>
            <ObjectList><MultilingualText ID="2" CompositionName="Comment"><ObjectList><MultilingualTextItem ID="3" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text>Keep the unrelated network comment.</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList>
          </SW.Blocks.CompileUnit><MultilingualText ID="4" CompositionName="Title"><ObjectList><MultilingualTextItem ID="5" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text>Independent baseline</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText><OpaqueBlockExtension ID="800" keep="yes"/></ObjectList>
        </SW.Blocks.FC></Document>
        """);
        // Synthetic terminal insertion fixture: following native objects would
        // be renumbered by V21 and are rejected in the captured-case test.
        var objects = document.Root!.Element("SW.Blocks.FC")!.Element("ObjectList")!;
        var preceding = objects.Elements().Where(e => e.Name.LocalName != "SW.Blocks.CompileUnit").ToArray();
        foreach (var item in preceding) item.Remove();
        objects.AddFirst(preceding);
        return document;
    }
}
