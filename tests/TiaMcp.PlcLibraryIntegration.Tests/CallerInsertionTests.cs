using System.Xml.Linq;
using TiaMcp.Openness.V21;

internal static class CallerInsertionTests
{
    // Shape of a native V21 FC export: block comment 1/2, network 3..7, block title 8/9.
    const string Original = """
    <Document><Engineering version="V21" /><SW.Blocks.FC ID="0"><AttributeList><Name>Main</Name><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList><ObjectList>
    <MultilingualText ID="1" CompositionName="Comment"><ObjectList><MultilingualTextItem ID="2" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text>block</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
    <SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits"><AttributeList><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList><ObjectList>
    <MultilingualText ID="4" CompositionName="Comment"><ObjectList><MultilingualTextItem ID="5" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
    <MultilingualText ID="6" CompositionName="Title"><ObjectList><MultilingualTextItem ID="7" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text>first</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
    </ObjectList></SW.Blocks.CompileUnit>
    <MultilingualText ID="8" CompositionName="Title"><ObjectList><MultilingualTextItem ID="9" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text>title</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
    </ObjectList></SW.Blocks.FC></Document>
    """;

    // What V21 does: the new unit takes the first following ID, everything after it moves up by its ID count.
    static XDocument Insert(XDocument original, int shiftOverride = 0)
    {
        var installed = new XDocument(original);
        var target = installed.Descendants("SW.Blocks.CompileUnit").Single(e => (string)e.Attribute("ID") == "3");
        var following = installed.Descendants().Where(e => XNode.DocumentOrderComparer.Compare(e, target) > 0 && !e.Ancestors().Contains(target)).Attributes("ID").ToArray();
        int start = following.Length == 0 ? installed.Descendants().Attributes("ID").Max(a => Convert.ToInt32(a.Value, 16)) + 1 : Convert.ToInt32(following[0].Value, 16);
        var unit = new XElement("SW.Blocks.CompileUnit", new XAttribute("ID", (start).ToString("X")), new XAttribute("CompositionName", "CompileUnits"),
            new XElement("AttributeList", new XElement("ProgrammingLanguage", "FBD")),
            new XElement("ObjectList", Enumerable.Range(1, 4).Select(i => new XElement("MultilingualText", new XAttribute("ID", (start + i).ToString("X"))))));
        foreach (var a in following) a.Value = (Convert.ToInt32(a.Value, 16) + (shiftOverride == 0 ? 5 : shiftOverride)).ToString("X");
        // A whitespace-preserved export indents the new unit like its neighbours.
        if (target.NextNode is XText indent && string.IsNullOrWhiteSpace(indent.Value)) target.AddAfterSelf(new XText(indent.Value), unit); else target.AddAfterSelf(unit);
        return installed;
    }

    static XAttribute Id(XDocument d, string id) => d.Descendants().Attributes("ID").Single(a => a.Value == id);

    static void Rejects(XDocument original, XDocument installed)
    {
        try { PlcComponentCallerInsertion.Verify(original, installed, "3"); }
        catch (PlcComponentException e) when (e.Code == "ComponentCallerPreservationMismatch") { return; }
        throw new Exception("Expected ComponentCallerPreservationMismatch");
    }

    internal static void Run(Action<string, Action> test)
    {
        var original = XDocument.Parse(Original);
        test("existing caller accepts exactly the following-ID shift by the inserted count", () => {
            var proof = PlcComponentCallerInsertion.Verify(original, Insert(original), "3");
            Equal(true, proof.Value<bool>("verified")); Equal("8", proof.Value<string>("insertedNetworkId")); Equal(5, proof.Value<int>("insertedObjectIds"));
            Equal("8->D,9->E", string.Join(",", proof["shiftedObjectIds"].Values<string>()));
            Equal(null, PlcComponentCallerInsertion.ShiftBlocker(original));
        });
        test("whitespace-preserved native export accepts the shift and still rejects a changed indent", () => {
            var preserved = EngineeringSources.ReadXml(Original);
            Equal("8", PlcComponentCallerInsertion.Verify(preserved, Insert(preserved), "3").Value<string>("insertedNetworkId"));
            var changed = Insert(preserved); ((XText)changed.Descendants("MultilingualText").Last().PreviousNode).Value += " "; Rejects(preserved, changed);
        });
        test("terminal insertion without following IDs needs no shift", () => {
            var terminal = new XDocument(original); terminal.Descendants("MultilingualText").Last().Remove();
            Equal(0, ((Newtonsoft.Json.Linq.JArray)PlcComponentCallerInsertion.Verify(terminal, Insert(terminal), "3")["shiftedObjectIds"]).Count);
        });
        test("wrong shift amount fails closed", () => Rejects(original, Insert(original, 6)));
        test("unshifted following IDs collide and fail closed", () => { var i = Insert(original); Id(i, "D").Value = "8"; Id(i, "E").Value = "9"; Rejects(original, i); });
        test("swapped following order fails closed", () => { var i = Insert(original); Id(i, "D").Value = "X"; Id(i, "E").Value = "D"; Id(i, "X").Value = "E"; Rejects(original, i); });
        test("changed ID before the insertion point fails closed", () => { var i = Insert(original); Id(i, "2").Value = "F"; Rejects(original, i); });
        test("aliased ID spelling fails closed", () => { var i = Insert(original); Id(i, "E").Value = "0E"; Rejects(original, i); });
        test("changed following text fails closed", () => { var i = Insert(original); i.Descendants("Text").Last().Value = "other"; Rejects(original, i); });
        test("changed attribute outside the inserted unit fails closed", () => { var i = Insert(original); i.Descendants("SW.Blocks.CompileUnit").First().SetAttributeValue("CompositionName", "Other"); Rejects(original, i); });
        test("inserted unit away from the insertion network fails closed", () => {
            var i = Insert(original); var unit = i.Descendants("SW.Blocks.CompileUnit").Last(); unit.Remove(); i.Descendants("SW.Blocks.FC").Single().Element("ObjectList").Add(unit); Rejects(original, i);
        });
        test("second inserted network fails closed", () => { var i = Insert(Insert(original)); Rejects(original, i); });
        test("non-consecutive caller IDs block prediction", () => { var gap = new XDocument(original); Id(gap, "9").Value = "B"; Equal(true, PlcComponentCallerInsertion.ShiftBlocker(gap) != null); });

        // A multi-instance adds one direct Static member to the caller FB (which carries no IDs) besides the network.
        const string Ns = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
        XDocument Fb(XDocument fc) { var fb = new XDocument(fc); var block = fb.Descendants("SW.Blocks.FC").Single(); block.Name = "SW.Blocks.FB";
            block.Element("AttributeList").AddFirst(new XElement("Interface", new XElement(XName.Get("Sections", Ns), new XElement(XName.Get("Section", Ns), new XAttribute("Name", "Static"),
                new XElement(XName.Get("Member", Ns), new XAttribute("Name", "Kept"), new XAttribute("Datatype", "Bool")))))); return fb; }
        XDocument WithMember(XDocument fb, string name = "Gate") { var copy = new XDocument(fb);
            copy.Descendants(XName.Get("Section", Ns)).Single().Add(new XElement(XName.Get("Member", Ns), new XAttribute("Name", name), new XAttribute("Datatype", "\"Motor\""))); return copy; }
        var caller = Fb(original);
        test("multi-instance insertion accepts exactly one new Static member and the shift", () => {
            var proof = PlcComponentCallerInsertion.Verify(caller, WithMember(Insert(caller)), "3", "Gate");
            Equal("Gate", proof["addedStaticMember"].Value<string>("name")); Equal("\"Motor\"", proof["addedStaticMember"].Value<string>("dataType"));
        });
        test("multi-instance insertion without the member fails closed", () => { try { PlcComponentCallerInsertion.Verify(caller, Insert(caller), "3", "Gate"); } catch (PlcComponentException) { return; } throw new Exception("Expected a refusal"); });
        test("multi-instance member under another name fails closed", () => { try { PlcComponentCallerInsertion.Verify(caller, WithMember(Insert(caller), "Other"), "3", "Gate"); } catch (PlcComponentException) { return; } throw new Exception("Expected a refusal"); });
        test("a second changed declaration besides the member fails closed", () => {
            var i = WithMember(Insert(caller)); i.Descendants(XName.Get("Member", Ns)).First().SetAttributeValue("Datatype", "Int"); Rejects(caller, i);
            try { PlcComponentCallerInsertion.Verify(caller, i, "3", "Gate"); } catch (PlcComponentException) { return; } throw new Exception("Expected a refusal");
        });
        test("an unannounced new member fails the plain insertion proof", () => Rejects(caller, WithMember(Insert(caller))));
        test("a member in a formerly empty Static section restores the empty element exactly", () => {
            string text = Fb(original).ToString(SaveOptions.DisableFormatting);
            Equal(true, text.Contains("<Section Name=\"Static\"><Member Name=\"Kept\" Datatype=\"Bool\" /></Section>"));
            var empty = EngineeringSources.ReadXml(text.Replace("<Section Name=\"Static\"><Member Name=\"Kept\" Datatype=\"Bool\" /></Section>", "<Section Name=\"Static\" />"));
            var installed = Insert(empty); var section = installed.Descendants(XName.Get("Section", Ns)).Single();
            section.Add(new XText("\n  "), new XElement(XName.Get("Member", Ns), new XAttribute("Name", "Gate"), new XAttribute("Datatype", "\"Motor\"")), new XText("\n"));
            Equal("Gate", PlcComponentCallerInsertion.Verify(empty, installed, "3", "Gate")["addedStaticMember"].Value<string>("name"));
        });
    }

    static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
}
