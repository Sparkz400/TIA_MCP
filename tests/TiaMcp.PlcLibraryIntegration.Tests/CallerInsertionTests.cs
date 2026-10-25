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
        target.AddAfterSelf(unit);
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
    }

    static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
}
