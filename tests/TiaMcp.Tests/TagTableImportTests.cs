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
}
