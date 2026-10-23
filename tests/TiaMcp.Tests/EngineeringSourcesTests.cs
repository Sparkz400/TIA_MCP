using System.Xml;
using System.Xml.Linq;
using TiaMcp.Openness.V21;
using EngineeringBridgeException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests
{
    internal static class EngineeringSourcesTests
    {
        internal static Task AllDeclarationsArePreviewed()
        {
            var declarations = EngineeringSources.DeclaredBlocks("FUNCTION \"Visible\" : Void BEGIN END_FUNCTION; FUNCTION_BLOCK \"AlsoCreated\" BEGIN END_FUNCTION_BLOCK");
            Check.Equal(2, declarations.Length);
            Check.Equal("Visible", declarations[0]);
            Check.Equal("AlsoCreated", declarations[1]);
            var quoted = EngineeringSources.DeclaredBlocks("FUNCTION \"My named gate\" : Void BEGIN END_FUNCTION");
            Check.Equal("My named gate", quoted[0]);
            Check.Equal("SampleType", EngineeringSources.DeclaredTypes("TYPE \"SampleType\" STRUCT Value : Bool; END_STRUCT; END_TYPE")[0]);
            foreach (var source in new[] { EngineeringSources.SclFixture("Gate"), EngineeringSources.StlFixture("Gate") })
                Check.Equal("Gate", EngineeringSources.DeclaredBlocks(source)[0]);
            return Task.CompletedTask;
        }

        internal static Task CommentsAndLiteralsDoNotCreateDeclarations()
        {
            const string source = """
                // FUNCTION "CommentedOut" : Void BEGIN END_FUNCTION
                (* nested (* FUNCTION_BLOCK "NestedComment" *) comment *)
                FUNCTION "Actual" : Void
                BEGIN
                  #Text := 'FUNCTION "OnlyAString" END_FUNCTION';
                  #Escaped := 'It$'s TYPE "NotAType"';
                  #Doubled := 'It''s END_FUNCTION';
                END_FUNCTION
                """;
            var declarations = EngineeringSources.DeclaredBlocks(source);
            Check.Equal(1, declarations.Length);
            Check.Equal("Actual", declarations[0]);
            return Task.CompletedTask;
        }

        internal static async Task ScopeAndBalanceAreEnforced()
        {
            foreach (var invalid in new[]
            {
                "FUNCTION \"A\" : Void BEGIN", "END_FUNCTION", "FUNCTION \"A\" : Void BEGIN END_DATA_BLOCK",
                "FUNCTION \"A\" : Void FUNCTION \"B\" : Void END_FUNCTION END_FUNCTION",
                "FUNCTION \"A\" : Void END_FUNCTION junk",
                "FUNCTION \"A\" : Void END_FUNCTION FUNCTION \"A\" : Void END_FUNCTION",
                "FUNCTION \"Gate\" : Void END_FUNCTION FUNCTION \"gate\" : Void END_FUNCTION",
                "FUNCTION FC1 : Void BEGIN END_FUNCTION", "DATA_BLOCK DB12 BEGIN END_DATA_BLOCK",
                "FUNCTION FC 10 : Void BEGIN END_FUNCTION", "DATA_BLOCK DB 12 BEGIN END_DATA_BLOCK",
                "TYPE \"HiddenType\" STRUCT Value : Bool; END_STRUCT END_TYPE",
                "FUNCTION \"A\" : Void VAR_GLOBAL X : Bool; END_VAR END_FUNCTION",
                "CONFIGURATION \"Config\" END_CONFIGURATION", "FUNCTION \"A\" : Void BEGIN 'unterminated END_FUNCTION",
                "FUNCTION \"Escaped \"\"name\"\"\" : Void END_FUNCTION", "FUNCTION \"Escaped$NName\" : Void END_FUNCTION",
                "(* unterminated", "FUNCTION \"line\nbreak\" : Void END_FUNCTION"
            })
                await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringSources.DeclaredBlocks(invalid)));
            await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringSources.DeclaredTypes(EngineeringSources.SclFixture("WrongWorkflow"))));
        }

        internal static async Task XmlEnvelopeRejectsHiddenObjectsAndDuplicateIds()
        {
            foreach (var language in new[] { "LAD", "FBD" })
            {
                var blocks = EngineeringSources.BlockDeclarations(EngineeringSources.ReadXml(EngineeringSources.GraphicalFixture("Gate", language)));
                Check.Equal(1, blocks.Length);
                Check.Equal("Gate", blocks[0].Name);
                Check.Equal(language, blocks[0].Language);
                Check.Equal(false, blocks[0].IsSafety);
            }
            var safe = EngineeringSources.BlockDeclarations(EngineeringSources.ReadXml("<Document><SW.Blocks.FB ID='0'><AttributeList><Name>Safe</Name><ProgrammingLanguage>F_LAD</ProgrammingLanguage></AttributeList></SW.Blocks.FB></Document>"));
            Check.Equal(true, safe[0].IsSafety);
            foreach (var xml in new[]
            {
                "<Document><SW.Tags.PlcTagTable><AttributeList><Name>Hidden</Name></AttributeList></SW.Tags.PlcTagTable></Document>",
                "<Document><SW.Blocks.FC ID='0'><AttributeList><Name>A</Name><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList><ObjectList><SW.Blocks.CompileUnit ID='0'/></ObjectList></SW.Blocks.FC></Document>",
                "<Document><SW.Blocks.FC ID='0'><AttributeList><Name>A</Name><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.FC><SW.Blocks.FC ID='0'><AttributeList><Name>B</Name><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>",
                "<Document><SW.Blocks.FC><AttributeList><Name>A</Name><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList><ObjectList><SW.Blocks.FC><AttributeList><Name>Hidden</Name></AttributeList></SW.Blocks.FC></ObjectList></SW.Blocks.FC></Document>",
                "<Document><SW.Blocks.FC><AttributeList><Name>NoLanguage</Name></AttributeList></SW.Blocks.FC></Document>",
                "<Document xmlns='unexpected'><SW.Blocks.FC/></Document>"
            })
                await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringSources.BlockDeclarations(EngineeringSources.ReadXml(xml))));
            await Check.ThrowsAsync<XmlException>(() => Task.FromResult(EngineeringSources.ReadXml("<!DOCTYPE Document [<!ENTITY secret SYSTEM 'file:///not-read'>]><Document>&secret;</Document>")));
            var tagTable = EngineeringSources.NamedXmlObjects(EngineeringSources.ReadXml("<Document><Engineering version='V21'/><SW.Tags.PlcTagTable><AttributeList><Name>Signals</Name></AttributeList></SW.Tags.PlcTagTable></Document>"), "SW.Tags.PlcTagTable");
            Check.Equal("Signals", tagTable[0]);
            await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.FromResult(EngineeringSources.NamedXmlObjects(EngineeringSources.ReadXml("<Document><SW.Types.PlcStruct><AttributeList><Name>HiddenType</Name></AttributeList></SW.Types.PlcStruct></Document>"), "SW.Tags.PlcTagTable")));
        }

        internal static Task GraphicalPatchesPreserveUntouchedNativeContent()
        {
            foreach (string language in new[] { "LAD", "FBD" })
            {
                var document = DecoratedGraphicalDocument(language);
                var expected = new XDocument(document);
                var access = Network(expected).Descendants(Net + "Access").Single(e => e.Attribute("UId")?.Value == "21");
                access.Descendants(Net + "Component").Single().SetAttributeValue("Name", "EnableAlternate");
                EngineeringSources.SetGraphicalOperand(document, "1", "21", new[] { "EnableAlternate" });
                AssertSameXml(expected, document);

                // These are preservation fixtures, not claims of TIA compile qualification.
                // Native instruction payloads stay opaque to the patch operation.
                foreach (string instruction in new[] { "O", "Eq", "Add", "Move", "P", "SR", "TON", "CTU" })
                {
                    var before = new XDocument(document);
                    string fragment = new XElement(Net + "Part", new XAttribute("UId", "100"), new XAttribute("Name", instruction),
                        new XElement(Net + "TemplateValue", new XAttribute("Name", "Card"), new XAttribute("Type", "Cardinality"), "2"),
                        new XElement("{urn:native-extension}Opaque", new XAttribute("keep", "yes"), "vendor content")).ToString();
                    EngineeringSources.PatchGraphicalNetwork(document, "1", new[] { fragment }, Array.Empty<string>());
                    Check.Equal(instruction, Network(document).Element(Net + "Parts")!.Elements(Net + "Part").Single(e => e.Attribute("UId")?.Value == "100").Attribute("Name")!.Value);
                    EngineeringSources.PatchGraphicalNetwork(document, "1", Array.Empty<string>(), new[] { "100" });
                    AssertSameXml(before, document);
                }
                // Editing one native wire supports branches without reconstructing its peers.
                string branch = new XElement(Net + "Wire", new XAttribute("UId", "101"),
                    new XElement(Net + "IdentCon", new XAttribute("UId", "21")),
                    new XElement(Net + "NameCon", new XAttribute("UId", "23"), new XAttribute("Name", "in1")),
                    new XElement(Net + "NameCon", new XAttribute("UId", "23"), new XAttribute("Name", "in2"))).ToString();
                var branchExpected = new XDocument(document);
                Network(branchExpected).Element(Net + "Wires")!.Add(XElement.Parse(branch, LoadOptions.PreserveWhitespace));
                EngineeringSources.PatchGraphicalNetwork(document, "1", new[] { branch }, Array.Empty<string>());
                AssertSameXml(branchExpected, document);
            }
            return Task.CompletedTask;
        }

        internal static async Task GraphicalPatchFailuresAreAtomic()
        {
            var document = DecoratedGraphicalDocument("LAD");
            string before = document.ToString(SaveOptions.DisableFormatting);
            string wire = $"<Wire xmlns='{Net}' UId='120'><IdentCon UId='21'/><NameCon UId='999' Name='in'/></Wire>";
            await SourceError("UnresolvedNetworkReference", () => EngineeringSources.PatchGraphicalNetwork(document, "1", new[] { wire }, Array.Empty<string>()));
            await SourceError("UnresolvedNetworkReference", () => EngineeringSources.PatchGraphicalNetwork(document, "1", Array.Empty<string>(), new[] { "21" }));
            await SourceError("InvalidNetworkPatch", () => EngineeringSources.PatchGraphicalNetwork(document, "1", new[] { $"<Part xmlns='{Net}' UId='21' Name='Contact'/>" }, Array.Empty<string>()));
            await SourceError("InvalidNetworkPatch", () => EngineeringSources.PatchGraphicalNetwork(document, "1", new[] { $"<Part xmlns='{Net}' UId='21' Name='Contact'/>" }, new[] { "21" }));
            await SourceError("InvalidNetworkPatch", () => EngineeringSources.PatchGraphicalNetwork(document, "1", new[] { "<Part xmlns='urn:wrong' UId='100' Name='Move'/>" }, Array.Empty<string>()));
            await SourceError("InvalidNetworkPatch", () => EngineeringSources.PatchGraphicalNetwork(document, "1", new[] { $"<Part xmlns='{Net}' UId='100' Name='Move'><Instance UId='21'/></Part>" }, Array.Empty<string>()));
            await SourceError("InvalidNetworkPatch", () => EngineeringSources.PatchGraphicalNetwork(document, "1", new[] { $"<Part xmlns='{Net}' UId='100' Name='Move'><SW.Tags.PlcTagTable xmlns=''><AttributeList><Name>Hidden</Name></AttributeList></SW.Tags.PlcTagTable></Part>" }, Array.Empty<string>()));
            await SourceError("UnsupportedOperand", () => EngineeringSources.SetGraphicalOperand(document, "1", "21", new[] { "A", "B" }));
            Check.Equal(before, document.ToString(SaveOptions.DisableFormatting));
            var indexed = Network(document).Descendants(Net + "Access").First().Descendants(Net + "Component").Single();
            indexed.Add(new XElement(Net + "Access", new XAttribute("Scope", "LiteralConstant")));
            before = document.ToString(SaveOptions.DisableFormatting);
            await SourceError("UnsupportedOperand", () => EngineeringSources.SetGraphicalOperand(document, "1", "21", new[] { "Other" }));
            Check.Equal(before, document.ToString(SaveOptions.DisableFormatting));
            document.Root!.Element("SW.Blocks.FC")!.Element("AttributeList")!.Element("ProgrammingLanguage")!.Value = "F_LAD";
            await SourceError("SafetyBoundary", () => EngineeringSources.SetGraphicalOperand(document, "1", "21", new[] { "Other" }));
        }

        internal static async Task NativeNetworkInsertionPreservesExistingUnitsAndChecksIds()
        {
            var document = DecoratedGraphicalDocument("LAD");
            var incoming = new XElement(document.Descendants("SW.Blocks.CompileUnit").Single());
            var before = new XDocument(document);
            await SourceError("InvalidSimaticML", () => EngineeringSources.InsertGraphicalNetwork(document, "1", incoming.ToString()));
            AssertSameXml(before, document);
            foreach (var id in incoming.DescendantsAndSelf().Attributes("ID")) id.Value = (int.Parse(id.Value) + 100).ToString();
            var malicious = new XElement(incoming);
            malicious.Add(new XElement("ObjectList", new XElement("SW.Tags.PlcTagTable", new XElement("AttributeList", new XElement("Name", "Hidden")))));
            await SourceError("XmlScopeMismatch", () => EngineeringSources.InsertGraphicalNetwork(document, "1", malicious.ToString()));
            AssertSameXml(before, document);
            string newId = EngineeringSources.InsertGraphicalNetwork(document, "1", incoming.ToString(SaveOptions.DisableFormatting));
            Check.Equal("101", newId);
            var inserted = document.Descendants("SW.Blocks.CompileUnit").Single(e => e.Attribute("ID")?.Value == newId);
            Check.Equal(incoming.ToString(SaveOptions.DisableFormatting), inserted.ToString(SaveOptions.DisableFormatting));
            inserted.Remove();
            AssertSameXml(before, document);
        }

        internal static async Task NativeCallsPreserveInstancesAndExposeUnresolvedArguments()
        {
            // CallInfo/Parameter layout copied from exported V21 SCL FC and multi-instance FB fixtures.
            var document = SclCallDocument();
            var calls = EngineeringSources.ReadNativeCalls(document);
            Check.Equal(3, calls.Length);
            Check.Equal("notApplicable", calls[0].InstanceKind);
            Check.Equal("notApplicable", calls[0].InstanceResolution);
            Check.Equal("HelperFC", calls[0].Name);
            Check.Equal("HelperFC", calls[0].CalledTargetComponents.Single());
            Check.Equal("symbolicOnly", calls[0].CalledTargetResolution);
            Check.Equal("HelperFC", calls[0].InstanceComponents.Single());
            Check.Equal("multiInstance", calls[1].InstanceKind);
            Check.Equal("First", calls[1].InstanceComponents.Single());
            Check.Equal("symbolicOnly", calls[1].InstanceResolution);
            Check.Equal("symbolic", calls[1].Parameters[0].Resolution);
            Check.Equal("readOrInOut", calls[1].Parameters[0].Direction);
            Check.Equal("write", calls[1].Parameters[1].Direction);
            Check.Equal("unresolved", calls[1].Parameters[2].Resolution);
            Check.Equal("UnsupportedOperand", calls[1].Parameters[2].Reason);
            Check.Equal("instance", calls[2].InstanceKind);

            var freshNativeFc = new XDocument(document);
            freshNativeFc.Descendants(St + "CallInfo").First().Element(St + "Instance")!.Element(St + "Component")!.SetAttributeValue("Name", "LayerCallee");
            Check.Equal("LayerCallee", EngineeringSources.ReadNativeCalls(freshNativeFc)[0].Name);
            var qualifiedFc = new XDocument(document);
            qualifiedFc.Descendants(St + "CallInfo").First().Element(St + "Instance")!.AddFirst(new XElement(St + "Component", new XAttribute("Name", "UnitNamespace")));
            var qualified = EngineeringSources.ReadNativeCalls(qualifiedFc)[0];
            Check.Equal<string?>(null, qualified.Name);
            Check.Equal("UnitNamespace", qualified.CalledTargetComponents[0]);
            Check.Equal("HelperFC", qualified.CalledTargetComponents[1]);
            Check.Equal("symbolicOnly", qualified.CalledTargetResolution);
            var indirectFc = new XDocument(document);
            indirectFc.Descendants(St + "CallInfo").First().Element(St + "Instance")!.SetAttributeValue("Scope", "LocalVariable");
            Check.Equal<string?>(null, EngineeringSources.ReadNativeCalls(indirectFc)[0].Name);
            Check.Equal("unresolved", EngineeringSources.ReadNativeCalls(indirectFc)[0].CalledTargetResolution);
            var indexedFc = new XDocument(document);
            indexedFc.Descendants(St + "CallInfo").First().Element(St + "Instance")!.Element(St + "Component")!.Add(new XElement(St + "Index", "i"));
            Check.Equal<string?>(null, EngineeringSources.ReadNativeCalls(indexedFc)[0].Name);
            Check.Equal("unresolved", EngineeringSources.ReadNativeCalls(indexedFc)[0].CalledTargetResolution);

            var expected = new XDocument(document);
            expected.Descendants(St + "Access").Single(e => e.Attribute("UId")?.Value == "31").Descendants(St + "Component").Single().SetAttributeValue("Name", "EnableB");
            EngineeringSources.SetCallParameterOperand(document, "3", "23", "Enable", new[] { "EnableB" });
            AssertSameXml(expected, document);
            string before = document.ToString(SaveOptions.DisableFormatting);
            await SourceError("UnsupportedOperand", () => EngineeringSources.SetCallParameterOperand(document, "3", "23", "Expression", new[] { "Other" }));
            await SourceError("CallNotFound", () => EngineeringSources.SetCallParameterOperand(document, "3", "missing", "Enable", new[] { "Other" }));
            Check.Equal(before, document.ToString(SaveOptions.DisableFormatting));
        }

        internal static async Task GraphicalCallRewiringDoesNotMutateSharedAccesses()
        {
            var document = DecoratedGraphicalDocument("FBD");
            string callXml = $"<Call xmlns='{Net}' UId='100'><CallInfo Name='MemoryCell' BlockType='FB'><Instance Scope='LocalVariable' UId='101'><Component Name='First'/></Instance><Parameter Name='Enable' Section='Input' Type='Bool'/><Parameter Name='CountValue' Section='Output' Type='Int'/><Parameter Name='Unwired' Section='Input' Type='Bool'/></CallInfo></Call>";
            string wireXml = $"<Wire xmlns='{Net}' UId='102'><IdentCon UId='21'/><NameCon UId='100' Name='Enable'/></Wire>";
            string outputXml = $"<Wire xmlns='{Net}' UId='103'><NameCon UId='100' Name='CountValue'/><IdentCon UId='22'/></Wire>";
            EngineeringSources.PatchGraphicalNetwork(document, "1", new[] { callXml, wireXml, outputXml }, Array.Empty<string>());
            var call = EngineeringSources.ReadNativeCalls(document).Single();
            Check.Equal("100", call.CallUid);
            Check.Equal("multiInstance", call.InstanceKind);
            Check.Equal("read", call.Parameters[0].Direction);
            Check.Equal("21", call.Parameters[0].AccessUid);
            Check.Equal("write", call.Parameters[1].Direction);
            Check.Equal("UnresolvedCallConnection", call.Parameters[2].Reason);
            var before = new XDocument(document);
            await SourceError("SharedCallOperand", () => EngineeringSources.SetCallParameterOperand(document, "1", "100", "Enable", new[] { "Unexpected" }));
            AssertSameXml(before, document);
            var expected = new XDocument(document);
            Network(expected).Descendants(Net + "Wire").Single(e => e.Attribute("UId")?.Value == "102").Element(Net + "IdentCon")!.SetAttributeValue("UId", "22");
            EngineeringSources.SetGraphicalCallConnection(document, "1", "100", "Enable", "22");
            AssertSameXml(expected, document);
            Network(document).Descendants(Net + "Wire").Single(e => e.Attribute("UId")?.Value == "102")
                .Add(new XElement(Net + "NameCon", new XAttribute("UId", "23"), new XAttribute("Name", "in")));
            before = new XDocument(document);
            await SourceError("SharedCallConnection", () => EngineeringSources.SetGraphicalCallConnection(document, "1", "100", "Enable", "21"));
            AssertSameXml(before, document);
            Check.Equal("SharedCallConnection", EngineeringSources.ReadNativeCalls(document).Single().Parameters[0].Reason);
            Check.Equal(true, EngineeringSources.ReadNativeCalls(document).Single().Parameters[0].ConnectionXml.Contains("Name=\"in\""));
        }

        internal static async Task NativeReadbackDetectsCollateralLoss()
        {
            const string original = """
                <!--outer comment--><Document><DocumentInfo><Created>old</Created></DocumentInfo>
                  <SW.Blocks.FC ID="0"><AttributeList><Name>Gate</Name><ProgrammingLanguage>LAD</ProgrammingLanguage>
                    <Interface><Member Name="Enable" Datatype="Bool"/></Interface></AttributeList>
                    <NetworkSource><n:FlgNet xmlns:n="urn:native"><n:Part UId="21" Name="Contact" /></n:FlgNet></NetworkSource>
                    <!--keep comment--><MultilingualText CompositionName="Comment"><Text>  Prüfung – 温度  </Text></MultilingualText>
                    <VendorExtension Version="7"><Payload><![CDATA[opaque <content>]]></Payload><Text>   </Text></VendorExtension>
                    <Spacing xml:space="preserve"> <Child/> </Spacing>
                  </SW.Blocks.FC>
                </Document>
                """;
            var expected = EngineeringSources.ReadXml(original);
            var formatted = EngineeringSources.ReadXml(original.Replace("<Created>old</Created>", "<Created>new</Created>")
                .Replace("<n:FlgNet xmlns:n=\"urn:native\"><n:Part UId=\"21\" Name=\"Contact\" /></n:FlgNet>", "<FlgNet xmlns=\"urn:native\">\n  <Part Name=\"Contact\" UId=\"21\"/>\n</FlgNet>")
                .Replace("<![CDATA[opaque <content>]]>", "opaque &lt;content&gt;"));
            EngineeringSources.RequireNativePreservation(expected, formatted);
            foreach (string changed in new[]
            {
                original.Replace("<!--outer comment-->", ""), original.Replace("<!--keep comment-->", ""),
                original.Replace("Name=\"Enable\"", "Name=\"Changed\""), original.Replace("  Prüfung – 温度  ", "Prüfung – 温度"),
                original.Replace("opaque <content>", "lost"), original.Replace("Version=\"7\"", "Version=\"8\""),
                original.Replace("<Text>   </Text>", "<Text/>"), original.Replace("ID=\"0\"", "ID=\"9\""),
                original.Replace("<Child/> </Spacing>", "<Child/></Spacing>"), original.Replace("Name=\"Contact\"", "Name=\"Coil\"")
            })
                await SourceError("NativePreservationMismatch", () => EngineeringSources.RequireNativePreservation(expected, EngineeringSources.ReadXml(changed)));
            var qualified = EngineeringSources.ReadXml("<Document xmlns:x='urn:expected'><Vendor Value='x:Type'/></Document>");
            await SourceError("NativePreservationMismatch", () => EngineeringSources.RequireNativePreservation(qualified,
                EngineeringSources.ReadXml("<Document xmlns:x='urn:changed'><Vendor Value='x:Type'/></Document>")));
        }

        internal static async Task GraphicalFragmentsUseNativeDefaultNamespace()
        {
            var document = DecoratedGraphicalDocument("LAD");
            var expected = new XDocument(document);
            var original = Network(expected).Descendants(Net + "Access").Single(e => e.Attribute("UId")?.Value == "21");
            original.Descendants(Net + "Component").Single().SetAttributeValue("Name", "EnableAlternate");
            string fragment = $"<ns0:Access xmlns:ns0='{Net}' Scope='LocalVariable' UId='21'><ns0:Symbol><ns0:Component Name='EnableAlternate'/></ns0:Symbol></ns0:Access>";
            EngineeringSources.PatchGraphicalNetwork(document, "1", new[] { fragment }, Array.Empty<string>());
            EngineeringSources.RequireNativePreservation(expected, document);
            Check.Equal(false, document.ToString(SaveOptions.DisableFormatting).Contains("ns0:"));

            var ambiguous = fragment.Replace("Name='EnableAlternate'", "Name='EnableAlternate' NativeType='ns0:Qualified'");
            var before = new XDocument(document);
            await SourceError("UnsupportedNetworkPrefix", () => EngineeringSources.PatchGraphicalNetwork(document, "1", new[] { ambiguous }, Array.Empty<string>()));
            AssertSameXml(before, document);

            var incoming = new XElement(document.Descendants("SW.Blocks.CompileUnit").Single());
            foreach (var id in incoming.DescendantsAndSelf().Attributes("ID")) id.Value = (int.Parse(id.Value) + 100).ToString();
            foreach (var declaration in incoming.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration && a.Value == Net.NamespaceName).ToArray()) declaration.Remove();
            incoming.Add(new XAttribute(XNamespace.Xmlns + "ns0", Net.NamespaceName));
            string incomingXml = incoming.ToString(SaveOptions.DisableFormatting);
            Check.Equal(true, incomingXml.Contains("ns0:FlgNet"));
            EngineeringSources.InsertGraphicalNetwork(document, "1", incomingXml);
            Check.Equal(false, document.ToString(SaveOptions.DisableFormatting).Contains("ns0:"));
            Check.Equal(2, document.Descendants(Net + "FlgNet").Count());
        }

        private static readonly XNamespace Net = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
        private static readonly XNamespace St = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";
        private static XElement Network(XDocument document) => document.Descendants(Net + "FlgNet").Single();
        private static XDocument DecoratedGraphicalDocument(string language)
        {
            var document = EngineeringSources.ReadXml(EngineeringSources.GraphicalFixture("Gate", language));
            document.Root!.AddFirst(new XComment("Document comment must survive"));
            var unit = document.Descendants("SW.Blocks.CompileUnit").Single();
            unit.Add(new XElement("ObjectList", new XElement("MultilingualText", new XAttribute("ID", "90"), new XAttribute("CompositionName", "Comment"),
                new XElement("ObjectList", new XElement("MultilingualTextItem", new XAttribute("ID", "91"), new XElement("AttributeList",
                    new XElement("Culture", "de-DE"), new XElement("Text", "Keep comments & symbols <unchanged>")))))));
            Network(document).Element(Net + "Parts")!.Add(new XElement("{urn:future-native}Instruction", new XAttribute("mode", "opaque"), new XCData("Keep <all> native content")));
            Network(document).Add(new XElement("{urn:future-native}NetworkMetadata", "unsupported native payload"));
            return document;
        }

        private static XDocument SclCallDocument() => EngineeringSources.ReadXml($"""
            <Document><SW.Blocks.FB ID="0"><AttributeList><Name>Multi</Name><ProgrammingLanguage>SCL</ProgrammingLanguage><Interface><Opaque keep="true" /></Interface></AttributeList>
              <ObjectList><SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits"><AttributeList><ProgrammingLanguage>SCL</ProgrammingLanguage><NetworkSource><StructuredText xmlns="{St}">
                <Access Scope="Call" UId="4"><CallInfo UId="5" BlockType="FC"><Instance Scope="GlobalVariable" UId="6"><Component Name="HelperFC" UId="7"/></Instance><Parameter Name="Enable" UId="8"><Token Text=":=" UId="9"/><Access Scope="LocalVariable" UId="10"><Symbol UId="11"><Component Name="Enable" UId="12"/></Symbol></Access></Parameter></CallInfo></Access>
                <Access Scope="Call" UId="22"><CallInfo UId="23" BlockType="FB" Name="MemoryCell"><Instance Scope="LocalVariable" UId="24"><Component Name="First" UId="25"/></Instance><Token Text="(" UId="26"/>
                  <Parameter Name="Enable" UId="27"><Blank Num="1" UId="28"/><Token Text=":=" UId="29"/><Blank Num="1" UId="30"/><Access Scope="LocalVariable" UId="31"><Symbol UId="32"><Component Name="EnableA" UId="33"/></Symbol></Access></Parameter>
                  <Token Text="," UId="34"/><!-- preserve comment and instance relationship -->
                  <Parameter Name="CountValue" UId="45"><Blank Num="1" UId="46"/><Token Text="=&gt;" UId="47"/><Blank Num="1" UId="48"/><Access Scope="LocalVariable" UId="49"><Symbol UId="50"><Component Name="CountA" UId="51"/></Symbol></Access></Parameter>
                  <Parameter Name="Expression" UId="52"><Token Text=":=" UId="53"/><Access Scope="LocalVariable" UId="54"><Symbol UId="55"><Component Name="Value" UId="56"/></Symbol></Access><Token Text="+" UId="57"/><Token Text="1" UId="58"/></Parameter>
                <Token Text=")" UId="59"/></CallInfo></Access>
                <Access Scope="Call" UId="60"><CallInfo UId="61" BlockType="FB" Name="MemoryCell"><Instance Scope="GlobalVariable" UId="62"><Component Name="MemoryInstance" UId="63"/></Instance></CallInfo></Access>
              </StructuredText></NetworkSource></AttributeList><ObjectList><MultilingualText ID="4" CompositionName="Comment"><Text>Keep this network comment</Text></MultilingualText></ObjectList></SW.Blocks.CompileUnit></ObjectList>
            </SW.Blocks.FB></Document>
            """);

        private static void AssertSameXml(XDocument expected, XDocument actual) => Check.Equal(expected.ToString(SaveOptions.DisableFormatting), actual.ToString(SaveOptions.DisableFormatting));
        private static async Task SourceError(string code, Action action)
        {
            var exception = await Check.ThrowsAsync<EngineeringBridgeException>(() => { action(); return Task.CompletedTask; });
            Check.Equal(code, exception.Code);
        }
    }
}

// Portable worker routines are linked into tests. This local error type avoids
// loading the worker or Siemens assemblies.
namespace TiaMcp.Openness.V21
{
    internal sealed class BridgeException(string code, string message) : Exception(message)
    {
        internal string Code { get; } = code;
    }
}
