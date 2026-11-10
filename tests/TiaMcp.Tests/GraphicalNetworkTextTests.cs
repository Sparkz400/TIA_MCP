using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class GraphicalNetworkTextTests
{
    private static string Fixture(string name)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
        {
            string path = Path.Combine(current.FullName, "fixtures", "typed-graphical", "native", "6es7-513-1am03-0ab0-v4-1", name);
            if (File.Exists(path)) return path;
        }
        throw new InvalidOperationException("Native graphical fixture is missing: " + name);
    }

    private static GraphicalNetworkText.Network Single(string name)
    {
        var networks = GraphicalNetworkText.Render(XDocument.Load(Fixture(name)));
        Check.Equal(1, networks.Length);
        Check.Equal(true, networks[0].Rendered);
        return networks[0];
    }

    internal static Task LadderLogicBecomesBooleanStatements()
    {
        // Two parallel contacts into an O box, a negated series contact and a coil.
        var lad = Single("Tgp_Boolean_LAD.xml");
        Check.Equal("LAD", lad.Language);
        Check.Equal("#Active := (#A OR #B) AND NOT #Inhibit", lad.Text);
        // The FBD form negates the AND box input instead of a contact; the text is the same.
        Check.Equal("#Active := (#A OR #B) AND NOT #Inhibit", Single("Tgp_Boolean_FBD.xml").Text);
        Check.Equal("#Result := #Left > #Right", Single("Tgp_Gt_LAD.xml").Text);
        return Task.CompletedTask;
    }

    // A hand-written StructuredText v4 network in the shape TIA exports: tokens, blanks and line breaks in document order.
    private const string SclNetwork = """
        <Document><SW.Blocks.FC ID="0"><ObjectList><SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits"><AttributeList>
        <NetworkSource><StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4">
          <Token Text="REGION" /><Blank /><Text>Outputs</Text><NewLine />
          <Blank Num="4" /><LineComment><Text> vacuum valve</Text></LineComment><NewLine />
          <Blank Num="4" /><Access Scope="GlobalVariable"><Symbol><Component Name="o452_0_xVacuum" /></Symbol></Access>
          <Blank /><Token Text=":=" /><Blank />
          <Access Scope="GlobalVariable"><Symbol><Component Name="Station DB"><BooleanAttribute Name="HasQuotes">true</BooleanAttribute></Component>
            <Token Text="." /><Component Name="COP" /><Token Text="." /><Component Name="Bit"><Token Text="[" />
            <Access Scope="LiteralConstant"><Constant><ConstantType>Int</ConstantType><ConstantValue>3</ConstantValue></Constant></Access><Token Text="]" /></Component>
          </Symbol></Access>
          <Blank /><Token Text="AND" /><Blank /><Token Text="NOT" /><Blank />
          <Access Scope="LocalVariable"><Symbol><Component Name="Inhibit" /></Symbol></Access><Token Text=";" /><NewLine Num="2" />
          <Blank Num="4" /><Access Scope="LocalVariable"><Symbol><Component Name="Value" /></Symbol></Access><Blank /><Token Text=":=" /><Blank />
          <Access Scope="Call"><Instruction Name="PEEK"><TemplateValue Name="value_type" Type="Type">Byte</TemplateValue><Token Text="(" />
            <Parameter Name="area"><Blank /><Token Text=":=" /><Blank /><Access Scope="GlobalConstant"><Constant Name="AREA_INPUT" /></Access></Parameter>
            <Token Text=")" /></Instruction></Access><Token Text=";" /><NewLine />
          <Blank Num="4" /><Access Scope="Call"><CallInfo BlockType="FB" Name="fbStation"><Instance Scope="LocalVariable"><Component Name="Station" /></Instance>
            <Token Text="(" /><Parameter Name="OUT_Done"><Blank /><Token Text="=&gt;" /><Blank />
            <Access Scope="GlobalVariable"><Symbol><Component Name="Tag_1" /><Component Name="Word" SliceAccessModifier="x4" /></Symbol></Access></Parameter>
            <Token Text=")" /></CallInfo></Access><Token Text=";" /><NewLine />
          <Token Text="END_REGION" />
        </StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>
        """;

    internal static Task StructuredTextBecomesSclSource()
    {
        var document = XDocument.Parse(SclNetwork);
        var network = GraphicalNetworkText.Render(document).Single();
        Check.Equal("SCL", network.Language);
        Check.Equal(true, network.Rendered);
        Check.Equal(string.Join(Environment.NewLine,
            "REGION Outputs",
            "    // vacuum valve",
            "    o452_0_xVacuum := \"Station DB\".COP.Bit[3] AND NOT #Inhibit;",
            "",
            "    #Value := PEEK(area := \"AREA_INPUT\");",
            "    #Station(OUT_Done => Tag_1.Word.%X4);",
            "END_REGION"), network.Text);
        // The response bound cuts a long network; the caller's own bound (the text artifact) keeps it whole.
        var cut = GraphicalNetworkText.Render(document, null, 20).Single();
        Check.Equal(true, cut.Truncated);
        Check.Equal(true, cut.Text.StartsWith("REGION Outputs", StringComparison.Ordinal) && cut.Text.EndsWith("... (truncated)", StringComparison.Ordinal));
        Check.Equal(false, GraphicalNetworkText.Render(document, null, int.MaxValue).Single().Truncated);
        return Task.CompletedTask;
    }

    // Shape of a native V21 DB export: typed members carry nested sections, array overrides are sparse Subelements.
    private const string DataBlock = """
        <Document><SW.Blocks.GlobalDB ID="0"><AttributeList><Interface>
        <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Static">
          <Member Name="Enable" Datatype="Bool" Remanence="Retain"><Comment><MultiLanguageText Lang="de-DE">Freigabe</MultiLanguageText><MultiLanguageText Lang="en-US">Release
        of the station</MultiLanguageText></Comment><StartValue>TRUE</StartValue></Member>
          <Member Name="Item" Datatype="&quot;SampleType&quot;" Remanence="NonRetain"><Sections><Section Name="None">
            <Member Name="Samples" Datatype="Array[-2..2] of Int">
              <Subelement Path="2"><StartValue>23</StartValue><Comment><MultiLanguageText Lang="en-US">last</MultiLanguageText></Comment></Subelement>
            </Member>
            <Member Name="Meta" Datatype="Struct"><Member Name="Count" Datatype="DInt"><StartValue>5</StartValue></Member></Member>
          </Section></Sections></Member>
          <Member Name="Timer" Datatype="TON_TIME"><Sections><Section Name="Input"><Member Name="PT" Datatype="Time"><StartValue>T#2s</StartValue></Member></Section><Section Name="InOut" /></Sections></Member>
        </Section></Sections></Interface><Name>Data</Name><ProgrammingLanguage>DB</ProgrammingLanguage></AttributeList></SW.Blocks.GlobalDB></Document>
        """;

    internal static Task DataBlockInterfaceBecomesText()
    {
        var document = XDocument.Parse(DataBlock);
        string expected = string.Join(Environment.NewLine,
            "Interface of \"Data\" [GlobalDB]",
            "Static",
            "  Enable : Bool := TRUE {Retain}  // Release of the station",
            "  Item : \"SampleType\"",
            "    Samples : Array[-2..2] of Int",
            "      [2] := 23  // last",
            "    Meta : Struct",
            "      Count : DInt := 5",
            "  Timer : TON_TIME",
            "    (Input)",
            "      PT : Time := T#2s");
        var rendered = GraphicalNetworkText.RenderInterface(document)!;
        Check.Equal(expected, rendered.Text); Check.Equal(7, rendered.Members); Check.Equal(false, rendered.Truncated);
        Check.Equal(true, GraphicalNetworkText.RenderInterface(document, "de-DE")!.Text.Contains("// Freigabe", StringComparison.Ordinal));
        var cut = GraphicalNetworkText.RenderInterface(document, null, 40)!;
        Check.Equal(true, cut.Truncated); Check.Equal(true, cut.Text.EndsWith("... (truncated)", StringComparison.Ordinal));
        // A block without networks used to get a 0-byte text file: its interface is the text, complete in the artifact.
        var response = GraphicalNetworkText.Describe(document, null, out string? complete);
        Check.Equal(expected + Environment.NewLine, complete);
        Check.Equal(0, ((JArray)response["networks"]!).Count);
        Check.Equal(expected, response["interface"]!.Value<string>("text")); Check.Equal(7, response["interface"]!.Value<int>("members"));
        Check.Equal(GraphicalNetworkText.InterfaceCoverage, response.Value<string>("textCoverage"));
        Check.Equal(false, response.ContainsKey("textUnavailable"));
        var instance = XDocument.Parse(DataBlock.Replace("SW.Blocks.GlobalDB", "SW.Blocks.InstanceDB").Replace("<Name>Data</Name>", "<Name>Data</Name><InstanceOfName>Gate</InstanceOfName>"));
        Check.Equal(true, GraphicalNetworkText.RenderInterface(instance)!.Text.StartsWith("Interface of \"Data\" [InstanceDB of \"Gate\"]", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    internal static Task ResponseCarriesEachTextOnceAndNoEmptyFile()
    {
        // The whole block in one piece is the text artifact; the response has each network once.
        var response = GraphicalNetworkText.Describe(XDocument.Parse(SclNetwork), null, out string? complete);
        Check.Equal(false, response.ContainsKey("text")); Check.Equal(false, response.ContainsKey("interface"));
        var network = (JObject)((JArray)response["networks"]!).Single();
        Check.Equal(true, network.Value<string>("text")!.StartsWith("REGION Outputs", StringComparison.Ordinal));
        Check.Equal(true, complete!.StartsWith("Network 1", StringComparison.Ordinal) && complete.Contains("REGION Outputs", StringComparison.Ordinal));
        Check.Equal(GraphicalNetworkText.Coverage, response.Value<string>("textCoverage"));
        // Nothing renderable: no text to write, and the response says so instead of pointing at an empty file.
        foreach (string xml in new[] {
            "<Document><SW.Blocks.GlobalDB ID=\"0\"><AttributeList><Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\"><Section Name=\"Static\" /></Sections></Interface><Name>Empty</Name></AttributeList></SW.Blocks.GlobalDB></Document>",
            "<Document><SW.Blocks.ArrayDB ID=\"0\"><AttributeList><Name>NoInterface</Name></AttributeList></SW.Blocks.ArrayDB></Document>" })
        {
            response = GraphicalNetworkText.Describe(XDocument.Parse(xml), null, out complete);
            Check.Equal<string?>(null, complete);
            Check.Equal(true, response["textUnavailable"]!.Value<string>("reason")!.Contains("no text form", StringComparison.Ordinal));
            Check.Equal(false, response.ContainsKey("textCoverage")); Check.Equal(false, response.ContainsKey("interface"));
        }
        return Task.CompletedTask;
    }

    internal static Task BoxesAndCallsShowPinBindings()
    {
        Check.Equal("TON<Time> #Storage(IN := #Enable, PT := #Preset, ET => #Elapsed)" + Environment.NewLine + "#Done := #Storage.Q", Single("Tgp_TON_LAD.xml").Text);
        Check.Equal("CALL \"TiaMcp_EnableGate\" \"TiaMcp_EnableGate_DB\"(Enable := #Enable, Active => #Active)", Single("Tgp_FB_global_LAD.xml").Text);
        return Task.CompletedTask;
    }

    internal static Task OperandsTitlesAndUnrenderedNetworks()
    {
        const string ns = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
        var document = XDocument.Parse($"""
            <Document><SW.Blocks.FC ID="0"><ObjectList>
              <SW.Blocks.CompileUnit ID="1" CompositionName="CompileUnits"><AttributeList><NetworkSource><FlgNet xmlns="{ns}"><Parts>
                <Access Scope="GlobalVariable" UId="21"><Symbol><Component Name="FormatManagement" /><Component Name="Real"><Access Scope="LiteralConstant"><Constant><ConstantType>DInt</ConstantType><ConstantValue>210</ConstantValue></Constant></Access></Component></Symbol></Access>
                <Access Scope="GlobalVariable" UId="22"><Symbol><Component Name="Out" /></Symbol></Access>
                <Access Scope="GlobalVariable" UId="23"><Address Area="Input" Type="Bool" BitOffset="11" /></Access>
                <Access Scope="LocalVariable" UId="24"><Symbol><Component Name="w" SliceAccessModifier="x3" /></Symbol></Access>
                <Part Name="Contact" UId="25" /><Part Name="Contact" UId="26"><Negated Name="operand" /></Part><Part Name="SCoil" UId="27" /><Part Name="Contact" UId="28" />
              </Parts><Wires>
                <Wire UId="30"><Powerrail /><NameCon UId="25" Name="in" /></Wire>
                <Wire UId="31"><IdentCon UId="23" /><NameCon UId="25" Name="operand" /></Wire>
                <Wire UId="32"><NameCon UId="25" Name="out" /><NameCon UId="26" Name="in" /></Wire>
                <Wire UId="33"><IdentCon UId="24" /><NameCon UId="26" Name="operand" /></Wire>
                <Wire UId="34"><NameCon UId="26" Name="out" /><NameCon UId="27" Name="in" /></Wire>
                <Wire UId="35"><IdentCon UId="21" /><NameCon UId="27" Name="operand" /></Wire>
                <Wire UId="36"><Powerrail /><NameCon UId="28" Name="in" /></Wire>
                <Wire UId="37"><IdentCon UId="22" /><NameCon UId="28" Name="operand" /></Wire>
              </Wires></FlgNet></NetworkSource><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>
              <ObjectList><MultilingualText ID="2" CompositionName="Title"><ObjectList>
                <MultilingualTextItem ID="3" CompositionName="Items"><AttributeList><Culture>de-DE</Culture><Text>Niederhalter</Text></AttributeList></MultilingualTextItem>
                <MultilingualTextItem ID="4" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text>Downholder</Text></AttributeList></MultilingualTextItem>
              </ObjectList></MultilingualText></ObjectList></SW.Blocks.CompileUnit>
              <SW.Blocks.CompileUnit ID="5" CompositionName="CompileUnits"><AttributeList><NetworkSource><StatementList /></NetworkSource><ProgrammingLanguage>STL</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit>
            </ObjectList></SW.Blocks.FC></Document>
            """);
        var networks = GraphicalNetworkText.Render(document);
        Check.Equal(2, networks.Length);
        Check.Equal("Downholder", networks[0].Title);
        Check.Equal("Niederhalter", GraphicalNetworkText.Render(document, "de-DE")[0].Title);
        Check.Equal("SET \"FormatManagement\".Real[210] IF %I1.3 AND NOT #w.%X3" + Environment.NewLine + "(unconnected) \"Out\"", networks[0].Text);
        Check.Equal(false, networks[1].Rendered);
        Check.Equal(true, networks[1].Text.Contains("generatedSource", StringComparison.Ordinal));
        string text = GraphicalNetworkText.Format(networks);
        Check.Equal(true, text.StartsWith("Network 1: Downholder [LAD, ID 1]", StringComparison.Ordinal));
        Check.Equal(true, text.Contains("Network 2 [STL, ID 5]", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    internal static Task NativeCorpusRendersWithoutUnresolvedReferences()
    {
        var directory = Path.GetDirectoryName(Fixture("Tgp_Boolean_LAD.xml"))!;
        var dump = Environment.GetEnvironmentVariable("TIAMCP_GRAPHICAL_TEXT_DUMP");
        foreach (var file in Directory.GetFiles(directory, "*.xml").Order(StringComparer.Ordinal))
        {
            var networks = GraphicalNetworkText.Render(XDocument.Load(file));
            if (dump != null) File.AppendAllText(dump, "== " + Path.GetFileName(file) + Environment.NewLine + GraphicalNetworkText.Format(networks));
            foreach (var network in networks.Where(n => n.Rendered))
                if (network.Text.Contains("(loop", StringComparison.Ordinal) || network.Text.Contains(":= ?", StringComparison.Ordinal) || network.Text.Contains(" ?", StringComparison.Ordinal))
                    throw new InvalidOperationException(Path.GetFileName(file) + " renders an unresolved reference: " + network.Text);
        }
        return Task.CompletedTask;
    }
}
