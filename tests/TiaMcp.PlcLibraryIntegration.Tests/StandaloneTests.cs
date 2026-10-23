using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

internal static class StandaloneTests
{
    internal static void Run(Action<string, Action> test)
    {
        foreach (string language in new[] { "LAD", "FBD" })
            test(language + " standalone FC recipe compiles exact pinned FB call and global instance", () => {
                var f = new Fixture(); f.Standalone(language); f.AddDependency(); var plan = f.Build();
                var payload = (JObject)plan["steps"][1]["prepareArguments"]["payload"]; TypedGraphicalPayload.Validate(payload);
                Equal("createBlock", payload["model"].Value<string>("operation")); Equal("FC", payload["model"]["creation"].Value<string>("blockType"));
                Equal(true, payload["blockPath"] == null && payload["model"]["insertAfterNetworkId"] == null);
                Equal("/plc/blocks/MotorCaller", plan["caller"].Value<string>("blockPath")); Equal(true, plan["caller"].Value<bool>("initiallyAbsent"));
                Equal(0, ((JArray)plan["blockers"]).Count); Equal(false, plan["caller"].Value<bool>("obCallAdded")); Equal(false, plan.Value<bool>("applySupported"));
                string repository = RepositoryFiles.Root();
                var profiles = JObject.Parse(File.ReadAllText(Path.Combine(repository, "fixtures/typed-graphical/profiles.v1.json")))["profiles"].ToObject<TypedGraphicalProfile[]>();
                var context = new TypedGraphicalContext { CpuFamily = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1", Profiles = profiles };
                context.Callees["/plc/blocks/Motor"] = new TypedGraphicalCallee { Path = "/plc/blocks/Motor", Name = "Motor", BlockType = "FB", InterfaceHash = "fixture", Parameters = [new() { Name = "Enable", Section = "Input", DataType = "Bool" }, new() { Name = "Active", Section = "Output", DataType = "Bool" }] };
                context.Instances["instance"] = new TypedGraphicalInstance { Identity = "/plc/blocks/Motor_1", Scope = "GlobalVariable", Components = ["Motor_1"], CalleePath = "/plc/blocks/Motor", DataType = "\"Motor\"", DeclarationHash = "fixture" };
                foreach (JObject operand in (JArray)payload["model"]["operands"])
                    context.Operands[operand.Value<string>("key")] = new TypedGraphicalOperand { Key = operand.Value<string>("key"), Identity = operand.Value<string>("key"), Scope = operand.Value<string>("scope") == "constant" ? "LiteralConstant" : "GlobalVariable", DataType = "Bool", Components = operand["components"]?.ToObject<string[]>() ?? [], Value = operand.Value<string>("value"), Writable = operand.Value<string>("key") == "p1", DeclarationHash = "fixture" };
                // Use the same captured creation-template and replaceNetwork route
                // as PrepareTypedGraphical, including the otherwise-blocking title IDs.
                var model = payload["model"].ToObject<TypedGraphicalModel>();
                var template = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(repository, "fixtures/typed-graphical/native/Tgp_TemplateFC_" + language + ".xml")));
                var original = TypedGraphicalCreationTemplate.Create(model, "", 42, template);
                var unit = original.Descendants("SW.Blocks.CompileUnit").Single();
                context.NetworkTemplateXml = unit.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
                model.Operation = "replaceNetwork"; model.NetworkId = unit.Attribute("ID").Value;
                var built = TypedGraphical.Build(original, model, context);
                Equal(1, built.Document.Descendants(TypedGraphical.Net + "Call").Count());
                Equal("MotorCaller", built.Document.Root.Element("SW.Blocks.FC").Element("AttributeList").Element("Name").Value);
                Equal("42", built.Document.Root.Element("SW.Blocks.FC").Element("AttributeList").Element("Number").Value);
            });
        test("standalone caller cannot adopt an existing FC", () => Reject("LibraryConflict", f => f.Payload["integration"]["caller"]["name"] = "Main"));
        test("standalone caller cannot collide with its FB", () => Reject("LibraryConflict", f => f.Payload["integration"]["caller"]["name"] = "motor"));
        test("standalone caller cannot collide with a dependency", () => Reject("LibraryConflict", f => { f.AddDependency(); f.Payload["integration"]["caller"]["name"] = "MotorState"; }));
        test("standalone caller cannot collide with instance DB", () => Reject("LibraryConflict", f => f.Payload["integration"]["caller"]["name"] = "Motor_1"));
        test("standalone caller cannot select existing block path", () => Reject("InvalidIntegrationRecipe", f => f.Payload["integration"]["caller"]["blockPath"] = "/plc/blocks/Main"));
        test("standalone caller cannot insert existing network", () => Reject("InvalidIntegrationRecipe", f => f.Payload["integration"]["caller"]["insertAfterNetworkId"] = "3"));
        test("standalone caller only supports FC", () => Reject("InvalidIntegrationRecipe", f => f.Payload["integration"]["caller"]["blockType"] = "OB"));
        test("standalone caller requires explicit numbering policy", () => Reject("InvalidIntegrationRecipe", f => ((JObject)f.Payload["integration"]["caller"]).Remove("numberPolicy")));
        test("standalone caller rejects unsupported language", () => Reject("InvalidIntegrationRecipe", f => f.Payload["integration"]["caller"]["language"] = "SCL"));
        test("standalone caller rejects unknown mode", () => Reject("InvalidIntegrationRecipe", f => f.Payload["integration"]["caller"]["mode"] = "standalone"));
        test("standalone caller requires absent native export", () => Reject("IntegrationCallerMismatch", f => f.Caller = new Fixture().Caller));
        test("explicit existing mode preserves legacy metadata blocker", () => {
            var f = new Fixture(); f.Payload["integration"]["caller"]["mode"] = "existing";
            f.Caller.Root.Element("SW.Blocks.OB").Element("ObjectList").Add(new System.Xml.Linq.XElement("MultilingualText", new System.Xml.Linq.XAttribute("ID", "9")));
            Equal("NativeIdPreservationUnsupported", f.Build()["blockers"][0].Value<string>("code"));
        });
    }
    private static void Reject(string code, Action<Fixture> change) { var f = new Fixture(); f.Standalone(); change(f); try { f.Build(); } catch (PlcIntegrationPlanException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
}
