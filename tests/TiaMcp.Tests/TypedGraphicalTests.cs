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

    internal static Task TemplateCulturesFollowTheTargetProject()
    {
        static XDocument Text(params (string Culture, string Text)[] items) => new XDocument(new XElement("MultilingualText",
            new XElement("ObjectList", items.Select(i => new XElement("MultilingualTextItem", new XElement("AttributeList", new XElement("Culture", i.Culture), new XElement("Text", i.Text)))))));
        static string Cultures(XDocument d) => string.Join(",", d.Descendants("Culture").Select(c => c.Value));
        var active = new[] { "en-US" };
        var moved = Text(("de-DE", "")); TypedGraphical.RetargetTemplateCultures(moved, active, "en-US"); Check.Equal("en-US", Cultures(moved));
        var dropped = Text(("de-DE", ""), ("en-US", "")); TypedGraphical.RetargetTemplateCultures(dropped, active, "en-US"); Check.Equal("en-US", Cultures(dropped));
        var kept = Text(("en-US", "")); TypedGraphical.RetargetTemplateCultures(kept, active, "en-US"); Check.Equal("en-US", Cultures(kept));
        try { TypedGraphical.RetargetTemplateCultures(Text(("de-DE", "Kommentar")), active, "en-US"); }
        catch (EngineeringBridgeException error) { Check.Equal("ProjectCultureUnavailable", error.Code); return Task.CompletedTask; }
        throw new InvalidOperationException("Template text in an inactive culture was discarded.");
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
        foreach (string language in new[] { "LAD", "FBD" })
        {
            // Same namespace: unchanged, and the result equals the native capture.
            var (captured, authored, qualified) = CapturedUnitCall(language);
            var actual = TypedGraphical.Build(captured, authored, qualified);
            var info = actual.Document.Descendants(Net + "CallInfo").Single();
            Check.Equal("TiaMcp_UnitA.TgpUnit_NumericFC", info.Attribute("Name")!.Value);
            Check.Equal(null, info.Attribute("Namespace"));
            EngineeringSources.RequireNativePreservation(captured, actual.Document);
            // A caller in another namespace of the same unit uses the same absolute qualified Name and namespace-route profile.
            qualified.TargetNamespace = "TiaMcp_UnitA.Alternate"; qualified.Route = "namespace";
            var crossNamespace = TypedGraphical.Build(captured, authored, qualified);
            Check.Equal("TiaMcp_UnitA.TgpUnit_NumericFC", crossNamespace.Document.Descendants(Net + "CallInfo").Single().Attribute("Name")!.Value);
            Check.Equal(true, crossNamespace.Profiles.Contains("v21.fw41.namespace." + language.ToLowerInvariant() + ".call.v1"));
            // Without a bound namespace-qualified profile the namespaced call stays refused.
            qualified.Profiles = qualified.Profiles.Select(p => { var copy = JObject.FromObject(p).ToObject<TypedGraphicalProfile>()!; if (copy.Kind == "call") copy.CalleeNameEncoding = null; return copy; }).ToArray();
            await Error("GraphicalCalleeUnsupported", () => TypedGraphical.Build(captured, authored, qualified));
        }
    }

    internal static async Task CrossUnitCallsRequireRelationPublicationAndTheirOwnProfile()
    {
        foreach (string language in new[] { "LAD", "FBD" })
        {
            var (captured, authored, context) = CapturedUnitCall(language);
            var catalog = context.Profiles;
            var callee = context.Callees.Values.Single();
            // Caller in TiaMcp_UnitB calls TiaMcp_UnitA's block. The cross-unit profile is unit-test-only: none is captured.
            context.TargetScopePath = "Devices/PLC/units/TiaMcp_UnitB"; context.TargetNamespace = "TiaMcp_UnitB";
            callee.ScopePath = "Devices/PLC/units/TiaMcp_UnitA"; callee.UnitAccess = "Published";
            context.RelatedScopePaths = [callee.ScopePath];
            var crossUnit = JObject.FromObject(catalog.Single(p => p.Kind == "call" && p.Route == "softwareUnit" && p.Language == language)).ToObject<TypedGraphicalProfile>()!;
            crossUnit.Id += ".crossunit-test"; crossUnit.CalleeNameEncoding = "crossUnitQualified";
            context.Profiles = catalog.Concat([crossUnit]).ToArray();
            var accepted = TypedGraphical.Build(captured, authored, context);
            Check.Equal("TiaMcp_UnitA.TgpUnit_NumericFC", accepted.Document.Descendants(Net + "CallInfo").Single().Attribute("Name")!.Value);
            Check.Equal(true, accepted.Profiles.Contains(crossUnit.Id));
            context.RelatedScopePaths = [];
            await Error("GraphicalUnitRelationMissing", () => TypedGraphical.Build(captured, authored, context));
            context.RelatedScopePaths = [callee.ScopePath];
            callee.UnitAccess = "Unpublished";
            await Error("GraphicalCalleeUnpublished", () => TypedGraphical.Build(captured, authored, context));
            callee.UnitAccess = "Published";
            // The in-unit namespace-qualified profile never serves a cross-unit call.
            context.Profiles = catalog;
            await Error("GraphicalProfileUnavailable", () => TypedGraphical.Build(captured, authored, context));
            // A same-unit call ignores the cross-unit slot and keeps its captured profile.
            context.Profiles = catalog.Concat([crossUnit]).ToArray();
            callee.ScopePath = context.TargetScopePath;
            Check.Equal(false, TypedGraphical.Build(captured, authored, context).Profiles.Contains(crossUnit.Id));
        }
    }

    // The captured software-unit numeric FC caller with its exact callee and the deployed catalog profiles.
    private static (XDocument Captured, TypedGraphicalModel Model, TypedGraphicalContext Context) CapturedUnitCall(string language)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "fixtures", "typed-graphical", "profiles.v1.json"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Missing qualified native call corpus.");
        string fixtures = Path.Combine(root.FullName, "fixtures", "typed-graphical");
        var profiles = JObject.Parse(File.ReadAllText(Path.Combine(fixtures, "profiles.v1.json")))["profiles"]!.ToObject<TypedGraphicalProfile[]>()!;
        var cases = JObject.Parse(File.ReadAllText(Path.Combine(fixtures, "numeric-cases.v1.json")))["cases"]!;
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
        return (captured, authored, qualified);
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
            // Observed V21 export: the inserted network takes IDs 8..C and the block title 8/9 becomes D/E.
            // The session derives the envelope from the caller's own compile unit with its texts cleared.
            var envelope = new XElement(native.Descendants("SW.Blocks.CompileUnit").First());
            foreach (var text in envelope.Descendants("Text")) text.Value = "";
            var context = Context(); context.NetworkTemplateXml = envelope.ToString(SaveOptions.DisableFormatting);
            var inserted = TypedGraphical.Build(native, model, context);
            Check.Equal(unchanged, native.ToString(SaveOptions.DisableFormatting));
            Check.Equal("8", inserted.NetworkId);
            var unit = inserted.Document.Descendants("SW.Blocks.CompileUnit").Single(u => u.Attribute("ID")?.Value == "8");
            Check.Equal("8,9,A,B,C", string.Join(",", unit.DescendantsAndSelf().Attributes("ID").Select(a => a.Value)));
            Check.Equal(true, inserted.Effects.Any(e => e.Kind == "followingObjectIdShift" && e.Description.Contains("8->D", StringComparison.Ordinal)));
            var restored = new XDocument(inserted.Document);
            restored.Descendants("SW.Blocks.CompileUnit").Single(u => u.Attribute("ID")?.Value == "8").Remove();
            foreach (var id in restored.Descendants().Attributes("ID").Where(a => Convert.ToInt32(a.Value, 16) >= 0xD)) id.Value = (Convert.ToInt32(id.Value, 16) - 5).ToString("X");
            Check.Equal(unchanged, restored.ToString(SaveOptions.DisableFormatting));
            var gapped = new XDocument(native); gapped.Descendants().Attributes("ID").Last().Value = "F";
            var error = await Check.ThrowsAsync<EngineeringBridgeException>(() => { TypedGraphical.Build(gapped, model, context); return Task.CompletedTask; });
            Check.Equal("NativeIdPreservationUnsupported", error.Code);
        }
    }

    internal static async Task FirstNetworkGoesBeforeTheBlockTitle()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "fixtures", "typed-graphical", "profiles.v1.json"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Missing typed native template corpus.");
        foreach (string language in new[] { "LAD", "FBD" })
        {
            var native = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(root.FullName, "fixtures", "typed-graphical", "native", "Tgp_Boolean_" + language + ".xml")));
            var envelope = new XElement(native.Descendants("SW.Blocks.CompileUnit").Single());
            foreach (var text in envelope.Descendants("Text")) text.Value = "";
            // Network-free derivation of the native block: no compile unit, consecutive IDs (title 8/9 -> 3/4).
            var empty = new XDocument(native);
            empty.Descendants("SW.Blocks.CompileUnit").Single().Remove();
            foreach (var id in empty.Descendants().Attributes("ID").Where(a => Convert.ToInt32(a.Value, 16) >= 8)) id.Value = (Convert.ToInt32(id.Value, 16) - 5).ToString("X");
            string unchanged = empty.ToString(SaveOptions.DisableFormatting);
            var model = BooleanModel(language); model.InsertAfterNetworkId = null;
            var context = Context(); context.NetworkTemplateXml = envelope.ToString(SaveOptions.DisableFormatting);
            var inserted = TypedGraphical.Build(empty, model, context);
            Check.Equal(unchanged, empty.ToString(SaveOptions.DisableFormatting));
            Check.Equal("3", inserted.NetworkId);
            var objects = inserted.Document.Root!.Elements().Single(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)).Element("ObjectList")!.Elements().ToArray();
            Check.Equal("Comment,CompileUnits,Title", string.Join(",", objects.Select(e => (string?)e.Attribute("CompositionName"))));
            Check.Equal("3,4,5,6,7", string.Join(",", objects[1].DescendantsAndSelf().Attributes("ID").Select(a => a.Value)));
            Check.Equal("8,9", string.Join(",", objects[2].DescendantsAndSelf().Attributes("ID").Select(a => a.Value)));
            var restored = new XDocument(inserted.Document);
            restored.Descendants("SW.Blocks.CompileUnit").Single().Remove();
            foreach (var id in restored.Descendants().Attributes("ID").Where(a => Convert.ToInt32(a.Value, 16) >= 8)) id.Value = (Convert.ToInt32(id.Value, 16) - 5).ToString("X");
            Check.Equal(unchanged, restored.ToString(SaveOptions.DisableFormatting));
            var untitled = new XDocument(empty); untitled.Descendants("MultilingualText").Single(e => (string?)e.Attribute("CompositionName") == "Title").Remove();
            await Error("NativeIdPreservationUnsupported", () => TypedGraphical.Build(untitled, model, context));
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

    // Families added 27 October 2026 have no captured native profile yet: these unit-test-only
    // profiles exercise the builder contract, not native acceptance.
    internal static async Task RawPayloadAcceptsOnlyExactNewFamilies()
    {
        static JObject Payload(string node) => JObject.Parse("{\"blockPath\":\"/b\",\"model\":{\"schemaVersion\":1,\"language\":\"FBD\",\"operation\":\"insertNetwork\",\"nodes\":[" + node + "],\"connections\":[],\"operands\":[]}}");
        foreach (var valid in new[] { "{\"key\":\"m\",\"kind\":\"move\",\"dataType\":\"DInt\"}", "{\"key\":\"a\",\"kind\":\"math\",\"instruction\":\"DIV\",\"dataType\":\"Real\"}",
            "{\"key\":\"e\",\"kind\":\"edge\",\"instruction\":\"F_TRIG\",\"instance\":\"s\"}", "{\"key\":\"t\",\"kind\":\"timer\",\"instruction\":\"TONR\",\"dataType\":\"Time\",\"instance\":\"s\"}" })
            TypedGraphicalPayload.Validate(Payload(valid));
        foreach (var invalid in new[] { "{\"key\":\"m\",\"kind\":\"move\"}", "{\"key\":\"a\",\"kind\":\"math\",\"dataType\":\"Int\"}", "{\"key\":\"a\",\"kind\":\"math\",\"instruction\":\"MOD\",\"dataType\":\"Int\"}",
            "{\"key\":\"a\",\"kind\":\"math\",\"instruction\":\"TON\",\"dataType\":\"Int\"}", "{\"key\":\"e\",\"kind\":\"edge\",\"instruction\":\"R_TRIG\"}",
            "{\"key\":\"e\",\"kind\":\"edge\",\"instruction\":\"P_TRIG\",\"instance\":\"s\"}", "{\"key\":\"t\",\"kind\":\"timer\",\"instruction\":\"ADD\",\"instance\":\"s\"}" })
            await Error("InvalidTypedGraphicalPayload", () => TypedGraphicalPayload.Validate(Payload(invalid)));
    }

    internal static async Task NumericComparisonsUseExactDIntAndRealOverloads()
    {
        foreach (string type in new[] { "DInt", "Real" })
        foreach (string op in new[] { "eq", "ne", "lt", "le", "gt", "ge" })
        foreach (string language in new[] { "LAD", "FBD" })
        {
            var pins = new List<TypedGraphicalPin> { Pin("in1", "input", type), Pin("in2", "input", type), Pin("out", "output", "Bool") };
            if (language == "LAD") pins.Insert(0, Pin("pre", "input", "Bool"));
            string native = char.ToUpperInvariant(op[0]) + op[1..];
            var context = Context(); context.Profiles = [Profile(language, "compare", op, type, native, $"<TemplateValue Name='SrcType' Type='Type'>{type}</TemplateValue>", [.. pins])];
            context.Operands["left"] = Operand("left", "Left", type, false); context.Operands["right"] = Operand("right", "Right", type, false);
            var model = BoxModel(language, new() { Key = "cmp", Kind = "compare", Comparison = op, DataType = type }, language == "LAD" ? "pre" : null, "out", ("in1", "left", false), ("in2", "right", false));
            var built = TypedGraphical.Build(Fixture(language), model, context);
            var part = Added(built).Descendants(Net + "Part").Single(p => p.Attribute("Name")?.Value == native);
            Check.Equal(type, part.Elements(Net + "TemplateValue").Single(v => v.Attribute("Name")?.Value == "SrcType").Value);
            Check.Equal("complete", Readback(built, context).Coverage);
            context.Operands["right"] = Operand("right", "Right", type == "DInt" ? "Int" : "LReal", false);
            await Error("OperandTypeMismatch", () => TypedGraphical.Build(Fixture(language), model, context));
        }
        var dint = Context();
        dint.Operands["left"] = Operand("left", "Left", "DInt", false); dint.Operands["right"] = Operand("right", "Right", "DInt", false);
        var request = BoxModel("FBD", new() { Key = "cmp", Kind = "compare", Comparison = "eq", DataType = "DInt" }, null, "out", ("in1", "left", false), ("in2", "right", false));
        dint.Profiles = [ComparisonProfile()];
        await Error("GraphicalProfileUnavailable", () => TypedGraphical.Build(Fixture("FBD"), request, dint));
        var mislabeled = ComparisonProfile(); mislabeled.DataType = "DInt"; mislabeled.Pins = [Pin("in1", "input", "DInt"), Pin("in2", "input", "DInt"), Pin("out", "output", "Bool")];
        dint.Profiles = [mislabeled];
        await Error("GraphicalProfileUnavailable", () => TypedGraphical.Build(Fixture("FBD"), request, dint));
        request.Nodes[0].DataType = "Time";
        await Error("GraphicalComparisonUnsupported", () => TypedGraphical.Build(Fixture("FBD"), request, dint));
    }

    internal static async Task MoveAndArithmeticKeepOneExactType()
    {
        foreach (string type in new[] { "Int", "DInt", "Real" })
        foreach (string language in new[] { "LAD", "FBD" })
        {
            foreach (string op in new[] { "ADD", "SUB", "MUL", "DIV" })
            {
                string native = op[0] + op[1..].ToLowerInvariant();
                var context = MathContext(language, op, type, native, $"<TemplateValue Name='SrcType' Type='Type'>{type}</TemplateValue>");
                var model = BoxModel(language, new() { Key = "math", Kind = "math", Instruction = op, DataType = type }, "en", language == "LAD" ? "eno" : null, ("in1", "left", false), ("in2", "right", false), ("out", "result", true));
                var built = TypedGraphical.Build(Fixture(language), model, context);
                Check.Equal(type, Added(built).Descendants(Net + "Part").Single(p => p.Attribute("Name")?.Value == native).Element(Net + "TemplateValue")!.Value);
                Check.Equal("complete", Readback(built, context).Coverage);
                context.Operands["result"] = Operand("result", "Result", type == "Real" ? "LReal" : "LInt", true);
                await Error("OperandTypeMismatch", () => TypedGraphical.Build(Fixture(language), model, context));
            }
            var move = Context();
            move.Profiles = [Profile(language, "move", null, type, "Move", "<TemplateValue Name='Card' Type='Cardinality'>1</TemplateValue>", Pin("en", "input", "Bool", false), Pin("in", "input", type), Pin("out1", "output", type), Pin("eno", "output", "Bool", false))];
            move.Operands["left"] = Operand("left", "Left", type, false); move.Operands["result"] = Operand("result", "Result", type, true);
            var moveModel = BoxModel(language, new() { Key = "move", Kind = "move", DataType = type }, "en", language == "LAD" ? "eno" : null, ("in", "left", false), ("out1", "result", true));
            var moved = TypedGraphical.Build(Fixture(language), moveModel, move);
            Check.Equal("1", Added(moved).Descendants(Net + "Part").Single(p => p.Attribute("Name")?.Value == "Move").Element(Net + "TemplateValue")!.Value);
            Check.Equal("complete", Readback(moved, move).Coverage);
            move.Operands["left"] = Operand("left", "Left", type == "Int" ? "DInt" : "Int", false);
            await Error("OperandTypeMismatch", () => TypedGraphical.Build(Fixture(language), moveModel, move));
        }
        TypedGraphicalModel Arithmetic(string op, string? type) => BoxModel("FBD", new() { Key = "math", Kind = "math", Instruction = op, DataType = type }, "en", null, ("in1", "left", false), ("in2", "right", false), ("out", "result", true));
        const string intType = "<TemplateValue Name='SrcType' Type='Type'>Int</TemplateValue>";
        foreach (var (op, type) in new (string, string?)[] { ("ADD", "Bool"), ("ADD", "Time"), ("MOD", "Int"), ("ADD", null) })
            await Error("GraphicalArithmeticUnsupported", () => TypedGraphical.Build(Fixture("FBD"), Arithmetic(op, type), MathContext("FBD", "ADD", "Int", "Add", intType)));
        await Error("GraphicalProfileUnavailable", () => TypedGraphical.Build(Fixture("FBD"), Arithmetic("ADD", "DInt"), MathContext("FBD", "ADD", "Int", "Add", intType)));
        var wrongPins = MathContext("FBD", "ADD", "DInt", "Add", "<TemplateValue Name='SrcType' Type='Type'>DInt</TemplateValue>"); wrongPins.Profiles[0].Pins[1].DataType = "Int";
        await Error("GraphicalProfileUnavailable", () => TypedGraphical.Build(Fixture("FBD"), Arithmetic("ADD", "DInt"), wrongPins));
        foreach (var (native, body) in new[] { ("Add", intType), ("Add", ""), ("Sub", "<TemplateValue Name='SrcType' Type='Type'>DInt</TemplateValue>") })
            await Error("GraphicalProfileUnavailable", () => TypedGraphical.Build(Fixture("FBD"), Arithmetic("ADD", "DInt"), MathContext("FBD", "ADD", "DInt", native, body)));
        var moveRequest = BoxModel("FBD", new() { Key = "move", Kind = "move", DataType = "String" }, "en", null);
        await Error("GraphicalMoveUnsupported", () => TypedGraphical.Build(Fixture("FBD"), moveRequest, Context()));
        moveRequest.Nodes[0].DataType = "Int"; moveRequest.Nodes[0].Instruction = "ADD";
        await Error("InvalidTypedGraphicalModel", () => TypedGraphical.Build(Fixture("FBD"), moveRequest, Context()));
        moveRequest.Nodes[0].Instruction = null; moveRequest.Nodes[0].Instance = "state";
        await Error("GraphicalInstanceMismatch", () => TypedGraphical.Build(Fixture("FBD"), moveRequest, Context()));
    }

    internal static async Task TonrAndEdgeDetectorsRequireExactIecStorage()
    {
        foreach (var (kind, instruction, type, storage, pins) in new (string, string, string?, string, TypedGraphicalPin[])[]
        {
            ("timer", "TONR", "Time", "TONR_TIME", [Pin("IN", "input", "Bool"), Pin("R", "input", "Bool"), Pin("PT", "input", "Time"), Pin("Q", "output", "Bool", false), Pin("ET", "output", "Time", false)]),
            ("edge", "R_TRIG", null, "R_TRIG", [Pin("CLK", "input", "Bool"), Pin("Q", "output", "Bool", false)]),
            ("edge", "F_TRIG", null, "F_TRIG", [Pin("CLK", "input", "Bool"), Pin("Q", "output", "Bool", false)])
        })
        foreach (string language in new[] { "LAD", "FBD" })
        {
            var context = Context();
            var profile = Profile(language, kind, instruction, type, instruction, "<Instance Scope='LocalVariable' UId='701'><Component Name='Prototype'/></Instance>", pins);
            profile.InstanceType = storage; context.Profiles = [profile];
            context.Operands["reset"] = Operand("reset", "Reset", "Bool", false); context.Operands["preset"] = Operand("preset", "Preset", "Time", false);
            context.Operands["elapsed"] = Operand("elapsed", "Elapsed", "Time", true);
            context.Instances["state"] = new() { Identity = "exact-caller::Dedicated", Scope = "LocalVariable", Components = ["Dedicated"], CalleePath = "instruction:" + instruction, DataType = storage, DeclarationHash = new string('d', 64) };
            var node = new TypedGraphicalNode { Key = "stateful", Kind = kind, Instruction = instruction, DataType = type, Instance = "state" };
            var model = BoxModel(language, node, kind == "timer" ? "IN" : "CLK", "Q", kind == "timer" ? [("R", "reset", false), ("PT", "preset", false), ("ET", "elapsed", true)] : []);
            var built = TypedGraphical.Build(Fixture(language), model, context);
            var nativeInstance = Added(built).Descendants(Net + "Instance").Single();
            Check.Equal("Dedicated", nativeInstance.Element(Net + "Component")!.Attribute("Name")!.Value);
            Check.Equal(false, nativeInstance.Attribute("UId")!.Value == "701");
            Check.Equal(true, built.Effects.Any(e => e.Kind == "instanceDependency" && e.Identity == "exact-caller::Dedicated"));
            Check.Equal("complete", Readback(built, context).Coverage);
            context.Instances["state"].DataType = "TON_TIME";
            await Error("GraphicalInstanceMismatch", () => TypedGraphical.Build(Fixture(language), model, context));
            // No captured global storage type exists for the new families, so a global instance is refused.
            context.Instances["state"].DataType = storage; context.Instances["state"].Scope = "GlobalVariable";
            await Error("GraphicalInstanceMismatch", () => TypedGraphical.Build(Fixture(language), model, context));
            context.Instances["state"].Scope = "LocalVariable"; node.Instance = null;
            await Error("GraphicalInstanceUnresolved", () => TypedGraphical.Build(Fixture(language), model, context));
            node.Instance = "state";
            if (kind != "edge") continue;
            node.DataType = "Bool";
            await Error("InvalidTypedGraphicalModel", () => TypedGraphical.Build(Fixture(language), model, context));
            node.DataType = null; node.Instruction = "P_TRIG";
            await Error("GraphicalInstructionUnsupported", () => TypedGraphical.Build(Fixture(language), model, context));
        }
    }

    private static TypedGraphicalContext MathContext(string language, string op, string type, string native, string body)
    {
        var context = Context();
        context.Profiles = [Profile(language, "math", op, type, native, body, Pin("en", "input", "Bool", false), Pin("in1", "input", type), Pin("in2", "input", type), Pin("out", "output", type), Pin("eno", "output", "Bool", false))];
        foreach (var (key, name, writable) in new[] { ("left", "Left", false), ("right", "Right", false), ("result", "Result", true) }) context.Operands[key] = Operand(key, name, type, writable);
        return context;
    }
    private static TypedGraphicalProfile Profile(string language, string kind, string? instruction, string? type, string native, string body, params TypedGraphicalPin[] pins)
    {
        var profile = ComparisonProfile();
        profile.Id = $"unit-test-only-{language}-{kind}-{instruction}-{type}"; profile.Language = language; profile.Kind = kind; profile.Instruction = instruction; profile.DataType = type;
        profile.NativeName = native; profile.NativeTemplateXml = $"<Part xmlns='{Net}' Name='{native}' UId='700'>{body}</Part>"; profile.Pins = pins;
        return profile;
    }
    private static TypedGraphicalPin Pin(string name, string direction, string type, bool required = true) => new() { Name = name, Direction = direction, DataType = type, Required = required };
    // LAD feeds flowIn from the power rail and flowOut into a coil; FBD feeds flowIn from the Bool input operand and flowOut into an assignment.
    private static TypedGraphicalModel BoxModel(string language, TypedGraphicalNode box, string? flowIn, string? flowOut, params (string Pin, string Operand, bool Output)[] data)
    {
        var nodes = new List<TypedGraphicalNode> { box };
        var wires = new List<TypedGraphicalConnection>();
        if (language == "LAD") { nodes.Add(new() { Key = "rail", Kind = "powerrail" }); if (flowIn != null) wires.Add(Wire("rail", "out", box.Key, flowIn)); }
        else if (flowIn != null) wires.Add(Wire("$input", "value", box.Key, flowIn));
        if (flowOut != null) { nodes.Add(new() { Key = "sink", Kind = language == "LAD" ? "coil" : "assign", Operand = "output" }); wires.Add(Wire(box.Key, flowOut, "sink", "in")); }
        foreach (var (pin, operand, output) in data) wires.Add(output ? Wire(box.Key, pin, "$" + operand, "value") : Wire("$" + operand, "value", box.Key, pin));
        return new() { SchemaVersion = 1, Language = language, Operation = "insertNetwork", InsertAfterNetworkId = "1", Nodes = [.. nodes], Connections = [.. wires], Operands = [] };
    }
    private static XElement Added(TypedGraphicalBuildResult built) => built.Document.Descendants("SW.Blocks.CompileUnit").Single(unit => unit.Attribute("ID")?.Value == built.NetworkId);
    private static TypedGraphicalReadNetwork Readback(TypedGraphicalBuildResult built, TypedGraphicalContext context) =>
        TypedGraphical.Read(built.Document, "/fixture/Independent", context.Profiles).Networks.Single(network => network.NetworkId == built.NetworkId);

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

    internal static Task DependentEffectManifestFollowsRoute()
    {
        Check.Equal("instance-effects.v1.json", TypedGraphicalMultiInstanceTemplate.EffectManifestName("standard"));
        Check.Equal("instance-effects-softwareUnit.v1.json", TypedGraphicalMultiInstanceTemplate.EffectManifestName("softwareUnit"));
        Check.Equal("instance-effects-namespace.v1.json", TypedGraphicalMultiInstanceTemplate.EffectManifestName("namespace"));
        return Task.CompletedTask;
    }

    internal static async Task IecDependentEffectAppendsOnlyTheCapturedMember()
    {
        XNamespace ns = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
        XElement Iec(string name, string type, string extra = "") => XElement.Parse(
            $"<Member xmlns='{ns}' Name='{name}' Datatype='{type}' Version='1.0' Remanence='NonRetain'{extra}><Sections><Section Name='None'>" +
            "<Member Name='IN' Datatype='Bool'/><Member Name='Q' Datatype='Bool'/></Section></Sections></Member>");
        XDocument Db() => XDocument.Parse("<Document><SW.Blocks.InstanceDB><AttributeList><InstanceOfName>U.Effect</InstanceOfName><InstanceOfType>FB</InstanceOfType>" +
            $"<Interface><Sections xmlns='{ns}'><Section Name='Input'/><Section Name='Output'/><Section Name='InOut'/><Section Name='Static'>" +
            "<Member Name='RetainedMarker' Datatype='Int'/></Section></Sections></Interface></AttributeList></SW.Blocks.InstanceDB></Document>");
        var caller = Iec("Timer_TON", "TON_TIME"); var dependent = Iec("Timer_TON", "TON_TIME", " Accessibility='Public'");
        var result = TypedGraphicalMultiInstanceTemplate.AppendSystemDependent(Db(), "U.Effect", Iec("Fresh", "TON_TIME"), caller, dependent);
        var statics = result.Descendants(ns + "Section").Single(s => (string)s.Attribute("Name") == "Static").Elements().ToArray();
        Check.Equal("RetainedMarker,Fresh", string.Join(",", statics.Select(m => (string)m.Attribute("Name"))));
        Check.Equal("Public", (string)statics[1].Attribute("Accessibility"));
        Check.Equal("instance-effects-iec-namespace.v1.json", TypedGraphicalMultiInstanceTemplate.EffectManifestName("namespace", true));
        foreach (var (declaration, existing) in new[] { (Iec("Fresh", "TON_TIME", " Remanence2='Retain'"), "Fresh"), (Iec("Fresh", "TOF_TIME"), "Fresh"),
                                                         (Iec("Fresh", "\"U.Other\""), "Fresh"), (Iec("RetainedMarker", "TON_TIME"), "RetainedMarker") })
            await Error("DependencyProfileUnavailable", () => TypedGraphicalMultiInstanceTemplate.AppendSystemDependent(Db(), "U.Effect", declaration, caller, dependent));
    }

    internal static async Task TransitiveEffectReproducesTheCapturedParents()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "fixtures", "typed-graphical", "instance-effects-transitive.v1.json"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Missing captured transitive effect.");
        XDocument Read(string name, string capture = "instance-effects-transitive") => EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(root.FullName, "fixtures", "typed-graphical", "native", capture, name + ".xml")));
        string[] added = { "Child", "Timer_TON", "Timer_TOF", "Timer_TP", "Counter_CTU", "Counter_CTD", "Counter_CTUD" };
        XElement[] Members(string prefix, string capture) => Read("after-" + prefix + "NestEffect_DB", capture).Descendants().Where(e => e.Name.LocalName == "Member" && added.Contains((string?)e.Attribute("Name"))
            && e.Parent!.Attribute("Name")?.Value == "Static" && e.Parent.Parent!.Parent!.Name.LocalName == "Interface").ToArray();
        var members = Members("Tgp_", "instance-effects-transitive");
        // Every route, both depths, parent FBs and parent DBs: the captured direct members reproduce the native after state exactly.
        foreach (var (capture, prefix, changed) in new[] { ("instance-effects-transitive", "Tgp_", "Tgp_NestEffect"),
            ("instance-effects-transitive-softwareUnit", "TgpUnit_", "TiaMcp_UnitA.TgpUnit_NestEffect"),
            ("instance-effects-transitive-namespace", "TgpNamespace_", "TiaMcp_UnitA.Alternate.TgpNamespace_NestEffect") })
        {
            var routeMembers = Members(prefix, capture);
            Check.Equal(string.Join(",", added), string.Join(",", routeMembers.Select(m => (string?)m.Attribute("Name"))));
            foreach (var parent in new[] { "NestParent", "NestGrand", "NestParent_DB", "NestGrand_DB" })
            {
                var expected = Read("before-" + prefix + parent, capture);
                foreach (var member in routeMembers) expected = TypedGraphicalMultiInstanceTemplate.AppendNested(expected, changed, member);
                EngineeringSources.RequireNativePreservation(Read("after-" + prefix + parent, capture), expected);
            }
        }
        Check.Equal("instance-effects-transitive.v1.json", TypedGraphicalMultiInstanceTemplate.TransitiveManifestName("standard"));
        Check.Equal("instance-effects-transitive-softwareUnit.v1.json", TypedGraphicalMultiInstanceTemplate.TransitiveManifestName("softwareUnit"));
        // A route host references the changed FB by its namespace-qualified name (as native route exports do); only that name matches.
        var scoped = Read("before-Tgp_NestParent");
        foreach (var host in scoped.Descendants().Where(e => (string?)e.Attribute("Datatype") == "\"Tgp_NestEffect\""))
            host.SetAttributeValue("Datatype", "\"TiaMcp_UnitA.TgpUnit_NestEffect\"");
        TypedGraphicalMultiInstanceTemplate.AppendNested(scoped, "TiaMcp_UnitA.TgpUnit_NestEffect", members[0]);
        await Error("DependencyProfileUnavailable", () => TypedGraphicalMultiInstanceTemplate.AppendNested(scoped, "TgpUnit_NestEffect", members[0]));
        var once =TypedGraphicalMultiInstanceTemplate.AppendNested(Read("before-Tgp_NestParent"), "Tgp_NestEffect", members[0]);
        await Error("DependencyProfileUnavailable", () => TypedGraphicalMultiInstanceTemplate.AppendNested(once, "Tgp_NestEffect", members[0]));
        await Error("DependencyProfileUnavailable", () => TypedGraphicalMultiInstanceTemplate.AppendNested(Read("before-Tgp_NestEffect_DB"), "Tgp_NestEffect", members[0]));
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
