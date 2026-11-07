using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;
using TagException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

internal static class TagTableImportTests
{
    static XElement Table(params XElement[] tags) => new("SW.Tags.PlcTagTable", new XAttribute("ID", "0"),
        new XElement("AttributeList", new XElement("Name", "Signals")), new XElement("ObjectList", tags));
    static XElement Tag(string name, string address = "%M0.0") => new("SW.Tags.PlcTag", new XAttribute("ID", "1"), new XAttribute("CompositionName", "Tags"),
        new XElement("AttributeList", new XElement("Name", name), new XElement("DataTypeName", "Bool"), new XElement("LogicalAddress", address)));

    internal static async Task ReadbackAndPreservation()
    {
        var before = Table(Tag("Existing")); var request = Table(Tag("Existing"), Tag("Added", "%M0.1"));
        var after = Table(Tag("Existing"), Tag("Added", "%M0.1"));
        TagTableImportRules.Verify(before, request, after);
        var omission = await Check.ThrowsAsync<TagException>(() => Task.Run(() => TagTableImportRules.RequireExistingMembers(before, Table(Tag("Added", "%M0.1")))));
        Check.Equal("TagScopeMismatch", omission.Code);
        var missing = await Check.ThrowsAsync<TagException>(() => Task.Run(() => TagTableImportRules.Verify(before, request, Table(Tag("Added", "%M0.1")))));
        Check.Equal("ImportVerificationFailed", missing.Code);
        var drift = await Check.ThrowsAsync<TagException>(() => Task.Run(() => TagTableImportRules.Verify(before, request, Table(Tag("Existing", "%M4.0"), Tag("Added", "%M0.1")))));
        Check.Equal("NativePreservationMismatch", drift.Code);
        await Check.ThrowsAsync<TagException>(() => Task.Run(() => TagTableImportRules.Verify(before, request, Table(Tag("Existing"), Tag("Added", "%M0.2")))));
        await Check.ThrowsAsync<TagException>(() => Task.Run(() => TagTableImportRules.Verify(before, request, Table(Tag("Existing"), Tag("Added(1)", "%M0.1")))));
        var comment = new XElement("ObjectList", new XElement("MultilingualText", new XAttribute("CompositionName", "Comment"),
            new XElement("ObjectList", new XElement("MultilingualTextItem", new XAttribute("CompositionName", "Items"),
                new XElement("AttributeList", new XElement("Culture", "en-GB"), new XElement("Text", "Expected comment"))))));
        request.Element("ObjectList")!.Elements().Last().Add(comment);
        after.Element("ObjectList")!.Elements().Last().Add(new XElement(comment));
        TagTableImportRules.Verify(before, request, after);
        after.Descendants("Text").Single().Value = "Changed comment";
        await Check.ThrowsAsync<TagException>(() => Task.Run(() => TagTableImportRules.Verify(before, request, after)));
        var tableComment = new XElement(comment.Elements().Single());
        var titledBefore = Table(Tag("Existing")); titledBefore.Element("ObjectList")!.Add(new XElement(tableComment));
        var titledAfter = Table(Tag("Existing"), Tag("Added", "%M0.1")); titledAfter.Element("ObjectList")!.Add(new XElement(tableComment));
        var plainRequest = Table(Tag("Existing"), Tag("Added", "%M0.1"));
        TagTableImportRules.Verify(titledBefore, plainRequest, titledAfter);
        titledAfter.Descendants("Text").Single().Value = "Unexpected table change";
        await Check.ThrowsAsync<TagException>(() => Task.Run(() => TagTableImportRules.Verify(titledBefore, plainRequest, titledAfter)));
        plainRequest.Element("ObjectList")!.Add(new XElement(tableComment));
        await Check.ThrowsAsync<TagException>(() => Task.Run(() => TagTableImportRules.Verify(before, plainRequest, titledAfter)));
        var hidden = Table(new XElement("SW.Blocks.FB", new XElement("AttributeList", new XElement("Name", "Hidden"))));
        await Check.ThrowsAsync<TagException>(() => Task.Run(() => TagTableImportRules.Validate(new XDocument(new XElement("Document", hidden)))));
    }

    internal static Task ContentFingerprint()
    {
        var first = Table(Tag("One"), Tag("Two", "%M1.0"));
        var reordered = Table(Tag("Two", "%M1.0"), Tag("One"));
        reordered.DescendantsAndSelf().Attributes("ID").ToList().ForEach(a => a.Value = "999");
        Check.Equal(TagTableImportRules.Canonical(first), TagTableImportRules.Canonical(reordered));
        reordered.Descendants("LogicalAddress").First().Value = "%M2.0";
        Check.Equal(false, TagTableImportRules.Canonical(first) == TagTableImportRules.Canonical(reordered));
        return Task.CompletedTask;
    }

    static JObject Compile(params string[] problems)
    {
        var messages = new JArray(new JObject { ["state"] = problems.Length > 0 ? "Error" : "Success", ["depth"] = 0, ["path"] = "UnrelatedBlock", ["description"] = "" });
        foreach (string problem in problems) messages.Add(new JObject { ["state"] = "Error", ["depth"] = 1, ["path"] = "4", ["description"] = problem });
        messages.Add(new JObject { ["state"] = problems.Length > 0 ? "Error" : "Information", ["depth"] = 0, ["path"] = null,
            ["description"] = $"Compiling finished (errors: {problems.Length}; warnings: 0)" });
        return new JObject { ["errorCount"] = problems.Length, ["warningCount"] = 0, ["targets"] = new JArray(new JObject {
            ["target"] = "/plc/Fixture", ["state"] = problems.Length > 0 ? "Error" : "Success", ["errorCount"] = problems.Length,
            ["warningCount"] = 0, ["messagesTruncated"] = false, ["messages"] = messages }) };
    }

    internal static Task CompileDelta()
    {
        Check.Equal(true, TagTableImportRules.NoNewCompileProblems(Compile("Existing error"), Compile("Existing error")));
        Check.Equal(true, TagTableImportRules.NoNewCompileProblems(Compile("Existing error"), Compile()));
        Check.Equal(false, TagTableImportRules.NoNewCompileProblems(Compile("Existing error"), Compile("New error")));
        Check.Equal(false, TagTableImportRules.NoNewCompileProblems(Compile("Existing error"), Compile("Existing error", "Existing error")));
        var truncated = Compile("Existing error"); truncated["targets"]![0]!["messagesTruncated"] = true;
        Check.Equal(false, TagTableImportRules.NoNewCompileProblems(truncated, Compile("Existing error")));
        var moved = Compile("Existing error"); moved["targets"]![0]!["messages"]![0]!["path"] = "DifferentBlock";
        Check.Equal(false, TagTableImportRules.NoNewCompileProblems(Compile("Existing error"), moved));
        var incomplete = Compile("Existing error"); ((JArray)incomplete["targets"]![0]!["messages"]!).RemoveAt(1);
        Check.Equal(false, TagTableImportRules.NoNewCompileProblems(incomplete, incomplete));
        var otherTarget = Compile("Existing error"); otherTarget["targets"]![0]!["target"] = "/plc/Other";
        Check.Equal(false, TagTableImportRules.NoNewCompileProblems(Compile("Existing error"), otherTarget));
        return Task.CompletedTask;
    }

    internal static Task CompileBaselineClassification()
    {
        var unchanged = CompileBaselineRules.Compare(Compile("Duplicate IP", "Other device"), Compile("Duplicate IP", "Other device"));
        Check.Equal(true, unchanged.Value<bool>("verified"));
        Check.Equal(2, unchanged.Value<int>("existingCount"));
        var resolved = CompileBaselineRules.Compare(Compile("Duplicate IP", "Other device"), Compile("Duplicate IP"));
        Check.Equal(true, resolved.Value<bool>("verified"));
        Check.Equal(1, resolved.Value<int>("resolvedCount"));
        Check.Equal("Other device", resolved["resolved"]![0]![1]!.Value<string>());
        var introduced = CompileBaselineRules.Compare(Compile("Duplicate IP"), Compile("Duplicate IP", "Target error"));
        Check.Equal(false, introduced.Value<bool>("verified"));
        Check.Equal(true, introduced.Value<bool>("complete"));
        Check.Equal("Target error", introduced["introduced"]![0]![1]!.Value<string>());
        var worsened = CompileBaselineRules.Compare(Compile("Duplicate IP"), Compile("Duplicate IP", "Duplicate IP"));
        Check.Equal(1, worsened.Value<int>("introducedCount"));
        var otherTarget = Compile("Duplicate IP"); otherTarget["targets"]![0]!["target"] = "/plc/Other";
        var scope = CompileBaselineRules.Compare(Compile("Duplicate IP"), otherTarget);
        Check.Equal(false, scope.Value<bool>("scopeUnchanged"));
        Check.Equal(false, scope.Value<bool>("verified"));
        // Accepts: a new error fails, a clean compile passes, and a new warning beside an unchanged error passes.
        Check.Equal(false, CompileBaselineRules.Accepts(Compile("Duplicate IP"), Compile("Duplicate IP", "Target error"), introduced));
        Check.Equal(true, CompileBaselineRules.Accepts(Compile("Duplicate IP"), Compile(), CompileBaselineRules.Compare(Compile("Duplicate IP"), Compile())));
        var warned = Compile("Duplicate IP", "New warning");
        warned["targets"]![0]!["messages"]![2]!["state"] = "Warning";
        foreach (var node in new[] { warned, (JObject)warned["targets"]![0]! }) { node["errorCount"] = 1; node["warningCount"] = 1; }
        warned["targets"]![0]!["messages"]!.Last!["description"] = "Compiling finished (errors: 1; warnings: 1)";
        var warning = CompileBaselineRules.Compare(Compile("Duplicate IP"), warned);
        Check.Equal(false, warning.Value<bool>("verified"));
        Check.Equal(0, warning.Value<int>("introducedErrorCount"));
        Check.Equal(true, CompileBaselineRules.Accepts(Compile("Duplicate IP"), warned, warning));
        Check.Equal(false, CompileBaselineRules.Accepts(Compile("Duplicate IP"), Compile("Duplicate IP"), CompileBaselineRules.Compare(null, Compile("Duplicate IP"))));
        return Task.CompletedTask;
    }

    internal static Task SourceRefreshParts()
    {
        static string Block(string number, string member, string code) =>
            $"<Document><SW.Blocks.FC ID=\"0\"><AttributeList><Interface><Sections><Section Name=\"Input\"><Member Name=\"{member}\" Datatype=\"Bool\" /></Section></Sections></Interface>" +
            $"<Number>{number}</Number><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList><ObjectList><SW.Blocks.CompileUnit ID=\"3\"><AttributeList><NetworkSource>{code}</NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";
        var before = SourceRefreshRules.Parts(Block("1", "a", "x"));
        var reserialized = SourceRefreshRules.Parts(Block("1", "a", "x").Replace("ID=\"3\"", "ID=\"9\""));
        Check.Equal(before.Interface, reserialized.Interface);
        Check.Equal(before.Body, reserialized.Body);
        Check.Equal("1", before.Number);
        Check.Equal(false, before.Interface == SourceRefreshRules.Parts(Block("1", "b", "x")).Interface);
        Check.Equal(before.Body, SourceRefreshRules.Parts(Block("1", "b", "x")).Body);
        Check.Equal(false, before.Body == SourceRefreshRules.Parts(Block("1", "a", "y")).Body);
        Check.Equal(false, before.Body == SourceRefreshRules.Parts(Block("2", "a", "x")).Body);
        var spaced = SourceRefreshRules.Parts(Block("1", "a", "x").Replace("<NetworkSource>x</NetworkSource>", "<NetworkSource><Blank Num=\"2\" UId=\"7\" />x</NetworkSource>"));
        Check.Equal(before.Body, spaced.Body);
        Check.Equal(false, before.Formatted == spaced.Formatted);
        return Task.CompletedTask;
    }

    internal static Task UnresolvedSymbols()
    {
        var compile = Compile("Tag \"UnresolvedFlag\" not defined.", "Data type 'Int' cannot be converted implicitly into data type 'Bool'.");
        var symbols = UnresolvedSymbolRules.From(compile);
        Check.Equal(1, symbols.Count);
        Check.Equal("Tag", symbols[0]!["kind"]!.Value<string>());
        Check.Equal("UnresolvedFlag", symbols[0]!["name"]!.Value<string>());
        Check.Equal("UnrelatedBlock", symbols[0]!["location"]![0]!.Value<string>());
        Check.Equal(0, UnresolvedSymbolRules.From(Compile()).Count);
        return Task.CompletedTask;
    }

    internal static async Task ConfirmedIntent()
    {
        var intent = JObject.Parse("""{"decisions":[{"id":"door-reset","source":"user, 7 Nov","statement":"Reset is only allowed with the door closed.","target":"Servos","symbols":["Reset","DoorOpen"]}],"observedFacts":["Drive1 telegram 1 at byte 256"],"assumptions":["SS1 ramp time is set in the drive"]}""");
        var recorded = IntentRules.Normalize(intent);
        Check.Equal("door-reset", recorded["decisions"]![0]!["id"]!.Value<string>());
        Check.Equal(true, recorded["verificationScope"]!["machineSafetyBehavior"]!.Value<string>()!.StartsWith("Not verified"));
        var earlier = new[] { JObject.Parse("""{"id":"door-reset","changeId":"c1","statement":"Reset is only allowed with the door closed.","target":"Servos"}""") };
        IntentRules.Compare(recorded, earlier);
        Check.Equal(1, ((JArray)recorded["previouslyConfirmed"]!).Count);
        Check.Equal(0, ((JArray)recorded["conflicts"]!).Count);
        var changed = IntentRules.Normalize(JObject.Parse("""{"decisions":[{"id":"door-reset","source":"user","statement":"Reset is always allowed.","target":"Servos"}]}"""));
        IntentRules.Compare(changed, earlier);
        Check.Equal("c1", changed["conflicts"]![0]!["earlierChangeId"]!.Value<string>());
        await Check.ThrowsAsync<TiaMcp.Openness.V21.BridgeException>(() => Task.Run(() => IntentRules.Normalize(JObject.Parse("""{"decisions":[{"id":"a","source":"u"}]}"""))));
        await Check.ThrowsAsync<TiaMcp.Openness.V21.BridgeException>(() => Task.Run(() => IntentRules.Normalize(JObject.Parse("""{"guess":"door switch is probably X1"}"""))));
    }

    internal static Task ChangeMilestones()
    {
        static JArray Stages(params string[] s) => new(s);
        var done = ChangeMilestoneRules.From("applied", Stages("prechange_project_saved", "recovery_archive_created", "baseline_compile_finished", "mutation_applied", "compile_finished", "changed_project_saved"),
            new JObject { ["compilerSucceeded"] = false });
        Check.Equal(true, done.Value<bool>("projectEditComplete"));
        Check.Equal(true, done.Value<string>("summary")!.Contains("still has compiler errors"));
        var failed = ChangeMilestoneRules.From("recovery_required", Stages("prechange_project_saved", "recovery_archive_created", "mutation_applied", "compile_finished", "change_finished_with_failure"),
            new JObject { ["compilerSucceeded"] = false });
        Check.Equal(false, failed.Value<bool>("projectEditComplete"));
        Check.Equal("notReached", failed["milestones"]!.Single(m => m.Value<string>("milestone") == "saved")!.Value<string>("status"));
        var prepared = ChangeMilestoneRules.From("prepared", Stages(), null);
        Check.Equal(false, prepared.Value<bool>("projectEditComplete"));
        Check.Equal(true, prepared.Value<string>("summary")!.StartsWith("Prepared only"));
        return Task.CompletedTask;
    }
}
