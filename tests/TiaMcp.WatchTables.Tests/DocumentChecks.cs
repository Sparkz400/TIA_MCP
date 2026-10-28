using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Core.WatchTables;

internal static class DocumentChecks
{
    private const string Prefix = "SW.WatchAndForceTables.";
    private const string Fixture = """
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <Engineering version="V21" />
          <DocumentInfo><Created>2026-10-15T00:00:00Z</Created><ExportSetting>WithDefaults</ExportSetting><OpaqueInfo>  </OpaqueInfo></DocumentInfo>
          <SW.WatchAndForceTables.PlcWatchTable ID="0">
            <AttributeList><Name keep="yes">Fixture</Name><OpaqueTable>  </OpaqueTable></AttributeList>
            <ObjectList>
              <!-- before first -->
              <SW.WatchAndForceTables.PlcWatchTableEntry ID="1" CompositionName="Entries" custom="retained">
                <AttributeList><Name>"DB".Bool</Name><DisplayFormat>Bool</DisplayFormat><ModifyValue>FALSE</ModifyValue><Opaque whitespace="yes">  </Opaque></AttributeList>
                <ObjectList><MultilingualText ID="2" CompositionName="Comment"><ObjectList>
                  <MultilingualTextItem ID="3" CompositionName="Items"><AttributeList><Culture>de-DE</Culture><Text sentinel="yes">  </Text></AttributeList></MultilingualTextItem>
                  <MultilingualTextItem ID="4" CompositionName="Items"><AttributeList><Culture>en-GB</Culture><Text>peer</Text></AttributeList></MultilingualTextItem>
                </ObjectList></MultilingualText></ObjectList>
              </SW.WatchAndForceTables.PlcWatchTableEntry>
              <?opaque fixed-position?>
              <!-- between rows -->
              <SW.WatchAndForceTables.PlcTableCommentEntry ID="5" CompositionName="Entries"><ObjectList><MultilingualText ID="6" CompositionName="Comment"><ObjectList>
                <MultilingualTextItem ID="7" CompositionName="Items"><AttributeList><Culture>de-DE</Culture><Text>comment row</Text></AttributeList></MultilingualTextItem>
              </ObjectList></MultilingualText></ObjectList></SW.WatchAndForceTables.PlcTableCommentEntry>
              <!-- after last -->
            </ObjectList>
          </SW.WatchAndForceTables.PlcWatchTable>
        </Document>
        """;
    internal static (string, Action)[] Checks => [
        ("Document no-op and defensive byte copies", NoOp),
        ("Document exact field edit preserves every unselected XML node", FieldPreservation),
        ("Document cleared scalar uses native empty element without relaxing hashes", ClearedScalar),
        ("Document comments retain Unicode whitespace and CRLF", CommentValues),
        ("Document rename preserves opaque attributes", Rename),
        ("Document adopts same-session export metadata and keeps the table", AdoptDocumentInfo),
        ("Document reorder preserves inter-row comments PI and whitespace slots", Reorder),
        ("Document duplicate allocates unique native IDs and preserves source", Duplicate),
        ("Document remove affects exact row only", Remove),
        ("Document hash distinguishes blank Text and unknown payload whitespace", HashWhitespace),
        ("Document hash tolerates known indentation timestamp and encoding", HashVolatile),
        ("Document hash binds annotations outside the root without changing ordinary captures", HashDocumentAnnotations),
        ("Document scalar edits reject opaque child markup without mutation", OpaqueRefusal),
        ("Document scalar reads refuse flattened opaque markup", FieldReadRefusal),
        ("Document comment culture reads and edits refuse opaque identity markup", CultureMarkupRefusal),
        ("Document language clone refuses loss of opaque culture content", CultureCloneRefusal),
        ("Document comment cloning preserves opaque metadata and every peer", CommentClonePreservation),
        ("Document direct comment edit preserves opaque text in unselected languages", UnselectedCommentPreservation),
        ("Document force fields, comments, clone, delete and reorder preserve every unselected node", ForceEdits),
        ("Document force edits refuse ForceIntention, ModifyValue, rename and selected rows atomically", ForceRefusal),
        ("Document wrong versions exports object types and ambiguous fields reject", ProfileRefusal),
        ("Document hexadecimal ID duplicates aliases malformed and missing reject", IdRefusal),
        ("Document DTD oversized deep malformed and row count limits reject", Limits),
        ("Document invalid edits permutations ordinals and XML text reject atomically", InvalidEdits),
        ("Document clone refuses attribute and element ID references", ReferenceRefusal)
    ];
    private static byte[] Bytes(string? xml = null) => Encoding.UTF8.GetBytes(xml ?? Fixture);
    private static WatchTableDocument Open(string? xml = null) => new(Bytes(xml));
    private static XDocument Parse(byte[] bytes) => XDocument.Load(new MemoryStream(bytes), LoadOptions.PreserveWhitespace);
    private static XElement Table(XDocument doc) => doc.Root!.Elements().Single(e => e.Name.LocalName.StartsWith(Prefix));
    private static XElement[] Rows(XDocument doc) => Table(doc).Element("ObjectList")!.Elements().ToArray();
    private static void True(bool value) { if (!value) throw new Exception("Document oracle assertion failed."); }
    private static void Equal<T>(T before, T after) { if (!EqualityComparer<T>.Default.Equals(before, after)) throw new Exception($"Expected [{before}], observed [{after}]"); }
    private static void Refuse(Action action) { try { action(); } catch (Exception error) when (error is InvalidDataException or XmlException) { return; } throw new Exception("Unsafe document input was accepted."); }
    private static void XmlEqual(XDocument expected, byte[] actual) => True(XNode.DeepEquals(expected.Root, Parse(actual).Root));
    private static void NoOp()
    {
        byte[] bytes = Bytes(); var doc = new WatchTableDocument(bytes); bytes[0] = 0; var output = doc.ToBytes(); output[0] = 0;
        True(doc.ToBytes().SequenceEqual(Bytes())); Equal(2, doc.Count); Equal("Fixture", doc.Name);
        doc.Rename("Fixture"); doc.SetField(0, "ModifyValue", "FALSE"); doc.SetComment(0, "de-DE", "  "); doc.Reorder([0, 1]);
        True(doc.ToBytes().SequenceEqual(Bytes()));
    }
    private static void FieldPreservation()
    {
        var expected = Parse(Bytes()); Rows(expected)[0].Element("AttributeList")!.Element("ModifyValue")!.Value = "TRUE";
        var doc = Open(); doc.SetField(0, "ModifyValue", "TRUE"); XmlEqual(expected, doc.ToBytes());
        Equal("TRUE", new WatchTableDocument(doc.ToBytes()).Field(0, "ModifyValue"));
    }
    private static void FieldReadRefusal()
    {
        foreach (string field in new[] { "Name", "Address", "DisplayFormat", "ModifyValue" })
        foreach (string markup in new[] { "<opaque>value</opaque>", "value<!-- retain -->", "value<?vendor retain?>" })
        {
            var xml = Parse(Bytes()); var attributes = Rows(xml)[0].Element("AttributeList")!;
            attributes.Element(field)?.Remove(); attributes.Add(XElement.Parse("<" + field + ">" + markup + "</" + field + ">"));
            byte[] input = Bytes(xml.ToString(SaveOptions.DisableFormatting)); var doc = new WatchTableDocument(input);
            Refuse(() => doc.Field(0, field)); Refuse(() => WatchTableAuthoring.Rows(doc));
            True(input.SequenceEqual(doc.ToBytes()));
        }
        Equal<string?>(null, Open().Field(0, "Address"));
    }
    private static void CultureMarkupRefusal()
    {
        foreach (string markup in new[] { "<opaque>de-DE</opaque>", "de-DE<!-- retain -->", "de-DE<?vendor retain?>" })
        {
            byte[] input = Bytes(Fixture.Replace("<Culture>de-DE</Culture>", "<Culture>" + markup + "</Culture>"));
            foreach (Action<WatchTableDocument> action in new Action<WatchTableDocument>[] {
                d => d.Comments(0), d => d.SetComment(0, "de-DE", "changed"),
                d => d.UpsertComment(0, "fr-FR", "new language"), d => d.RetainCommentCultures(0, new[] { "en-GB" }),
                d => WatchTableAuthoring.Rows(d) })
            {
                var doc = new WatchTableDocument(input); Refuse(() => action(doc));
                True(input.SequenceEqual(doc.ToBytes()));
            }
        }
    }
    private static void CommentClonePreservation()
    {
        var expected = Parse(Bytes()); var source = expected.Descendants("MultilingualTextItem").First();
        source.Element("AttributeList")!.Element("Culture")!.SetAttributeValue("vendor", "retain");
        source.Add(new XComment("retain item metadata"), new XProcessingInstruction("vendor", "retain"));
        byte[] input = Bytes(expected.ToString(SaveOptions.DisableFormatting));
        var clone = new XElement(source); clone.SetAttributeValue("ID", "8");
        clone.Element("AttributeList")!.Element("Culture")!.Value = "fr-FR";
        clone.Element("AttributeList")!.Element("Text")!.Value = "Bonjour Ω";
        source.Parent!.Add(clone);
        var doc = new WatchTableDocument(input); doc.UpsertComment(0, "fr-FR", "Bonjour Ω");
        XmlEqual(expected, doc.ToBytes()); Equal("Bonjour Ω", new WatchTableDocument(doc.ToBytes()).Comments(0)["fr-FR"]);
    }
    private static void CultureCloneRefusal()
    {
        byte[] input = Bytes(Fixture.Replace("<Culture>de-DE</Culture>", "<Culture>de-DE<!-- language metadata --></Culture>"));
        var doc = new WatchTableDocument(input); Refuse(() => doc.UpsertComment(0, "fr-FR", "Bonjour"));
        True(input.SequenceEqual(doc.ToBytes()));
    }
    private static void UnselectedCommentPreservation()
    {
        foreach (string markup in new[] { "<opaque>peer</opaque>", "peer<!-- retain -->", "peer<?vendor retain?>" })
        {
            byte[] input = Bytes(Fixture.Replace("<Text>peer</Text>", "<Text>" + markup + "</Text>"));
            var expected = Parse(input); expected.Descendants("Text").First().Value = "changed";
            var doc = new WatchTableDocument(input); doc.SetComment(0, "de-DE", "changed");
            XmlEqual(expected, doc.ToBytes());
        }
    }
    private static void ClearedScalar()
    {
        var expected = Parse(Bytes()); Rows(expected)[0].Element("AttributeList")!.Element("ModifyValue")!.RemoveNodes();
        var doc = Open(); doc.SetField(0, "ModifyValue", ""); XmlEqual(expected, doc.ToBytes());
        True(Parse(doc.ToBytes()).Descendants("ModifyValue").Single().IsEmpty);
        Equal("", new WatchTableDocument(doc.ToBytes()).Field(0, "ModifyValue"));
        var explicitEnd = Parse(doc.ToBytes()); explicitEnd.Descendants("ModifyValue").Single().Value = "";
        True(WatchTableDocument.SemanticHash(doc.ToBytes()) != WatchTableDocument.SemanticHash(Encoding.UTF8.GetBytes(explicitEnd.ToString(SaveOptions.DisableFormatting))));
    }
    private static void CommentValues()
    {
        foreach (string value in new[] { "", " ", "\t", " Ω e\u0301 東京 😀\r\n\tend\r " })
        {
            var expected = Parse(Bytes()); expected.Descendants("Text").First().Value = value;
            var doc = Open(); doc.SetComment(0, "de-DE", value); XmlEqual(expected, doc.ToBytes());
            Equal(value, Parse(doc.ToBytes()).Descendants("Text").First().Value);
        }
    }
    private static void Rename()
    { var expected = Parse(Bytes()); Table(expected).Element("AttributeList")!.Element("Name")!.Value = "New Ω"; var doc = Open(); doc.Rename("New Ω"); XmlEqual(expected, doc.ToBytes()); }
    private static void AdoptDocumentInfo()
    {
        const string Stale = "<DocumentInfo><Created>2026-10-15T00:00:00Z</Created><ExportSetting>WithDefaults</ExportSetting><OpaqueInfo>  </OpaqueInfo></DocumentInfo>";
        const string Current = "<DocumentInfo><Created>2026-10-28T07:19:50Z</Created><ExportSetting>WithDefaults</ExportSetting><InstalledProducts><Product><DisplayVersion>V21 Update 3</DisplayVersion></Product></InstalledProducts></DocumentInfo>";
        var doc = Open(); doc.AdoptDocumentInfo(new WatchTableDocument(Bytes(ForceFixture.Replace(Stale, Current))));
        byte[] expected = Bytes(Fixture.Replace(Stale, Current));
        XmlEqual(Parse(expected), doc.ToBytes()); Equal(WatchTableDocument.SemanticHash(expected), WatchTableDocument.SemanticHash(doc.ToBytes()));
        True(WatchTableDocument.SemanticHash(Bytes()) != WatchTableDocument.SemanticHash(doc.ToBytes()));
        var same = Open(); same.AdoptDocumentInfo(Open()); True(same.ToBytes().SequenceEqual(Bytes()));
        Refuse(() => Open().AdoptDocumentInfo(null!));
        Refuse(() => Open(Fixture.Replace("PlcWatchTable", "PlcForceTable").Replace("<ModifyValue>FALSE</ModifyValue>", "<ForceIntention>true</ForceIntention>")).AdoptDocumentInfo(Open()));
    }
    private static void Reorder()
    {
        var expected = Parse(Bytes()); var rows = Rows(expected); var replacements = rows.Reverse().Select(r => new XElement(r)).ToArray();
        for (int i = 0; i < rows.Length; i++) rows[i].ReplaceWith(replacements[i]);
        var doc = Open(); doc.Reorder([1, 0]); XmlEqual(expected, doc.ToBytes());
        Equal("comment row", Parse(doc.ToBytes()).Descendants("Text").First().Value); doc.Reorder([1, 0]); XmlEqual(Parse(Bytes()), doc.ToBytes());
    }
    private static void Duplicate()
    {
        var doc = Open(); doc.Duplicate(0, 1); Equal(3, doc.Count);
        var after = Parse(doc.ToBytes()); var rows = Rows(after); var originalRows = Rows(Parse(Bytes()));
        True(XNode.DeepEquals(originalRows[0], rows[0]) && XNode.DeepEquals(originalRows[1], rows[2]));
        var clone = new XElement(rows[1]); var ids = clone.DescendantsAndSelf().Attributes("ID").ToArray();
        var oldIds = originalRows[0].DescendantsAndSelf().Attributes("ID").ToArray();
        for (int i = 0; i < ids.Length; i++) ids[i].Value = oldIds[i].Value;
        True(XNode.DeepEquals(originalRows[0], clone));
        var allIds = after.Descendants().Attributes("ID").Select(a => uint.Parse(a.Value, NumberStyles.HexNumber)).ToArray();
        Equal(allIds.Length, allIds.Distinct().Count()); _ = new WatchTableDocument(doc.ToBytes());
        var append = Open(); append.Duplicate(1, 2); Equal(Prefix + "PlcTableCommentEntry", Rows(Parse(append.ToBytes()))[2].Name.LocalName);
    }
    private static void Remove()
    { var expected = Parse(Bytes()); Rows(expected)[0].Remove(); var doc = Open(); doc.Remove(0); XmlEqual(expected, doc.ToBytes()); Equal(1, doc.Count); }
    private static void HashWhitespace()
    {
        string original = WatchTableDocument.SemanticHash(Bytes());
        foreach (string changed in new[] { Fixture.Replace("sentinel=\"yes\">  </Text>", "sentinel=\"yes\"> </Text>"), Fixture.Replace("<OpaqueInfo>  </OpaqueInfo>", "<OpaqueInfo> </OpaqueInfo>"), Fixture.Replace("whitespace=\"yes\">  </Opaque>", "whitespace=\"yes\"> </Opaque>") })
            True(original != WatchTableDocument.SemanticHash(Bytes(changed)));
        string unknown = Fixture.Replace("<OpaqueInfo>  </OpaqueInfo>", "<OpaqueInfo><child/>  </OpaqueInfo>");
        True(WatchTableDocument.SemanticHash(Bytes(unknown)) != WatchTableDocument.SemanticHash(Bytes(unknown.Replace("<child/>  ", "<child/> "))));
        string opaqueContainer = Fixture.Replace("<OpaqueInfo>  </OpaqueInfo>", "<OpaqueInfo><AttributeList>  <Text>x</Text></AttributeList></OpaqueInfo>");
        True(WatchTableDocument.SemanticHash(Bytes(opaqueContainer)) != WatchTableDocument.SemanticHash(Bytes(opaqueContainer.Replace("<AttributeList>  <Text>", "<AttributeList> <Text>"))));
        var preserve = Fixture.Replace("<ObjectList>\n", "<ObjectList xml:space=\"preserve\">\n");
        True(WatchTableDocument.SemanticHash(Bytes(preserve)) != WatchTableDocument.SemanticHash(Bytes(preserve.Replace("<!-- before first -->", " <!-- before first -->"))));
    }
    private static void HashVolatile()
    {
        string hash = WatchTableDocument.SemanticHash(Bytes());
        Equal(hash, WatchTableDocument.SemanticHash(Bytes(Fixture.Replace("2026-10-15T00:00:00Z", "2027-01-01T12:00:00Z"))));
        var tree = Parse(Bytes()); foreach (var node in tree.DescendantNodes().OfType<XText>().Where(t => t.Parent?.HasElements == true && string.IsNullOrWhiteSpace(t.Value)).ToArray()) node.Remove();
        Equal(hash, WatchTableDocument.SemanticHash(Encoding.UTF8.GetBytes(tree.ToString(SaveOptions.DisableFormatting))));
        string utf16 = Fixture.Replace("encoding=\"utf-8\"", "encoding=\"utf-16\"");
        byte[] data = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(utf16)).ToArray();
        True(new WatchTableDocument(data).ToBytes().SequenceEqual(data)); Equal(hash, WatchTableDocument.SemanticHash(data));
    }
    private static void OpaqueRefusal()
    {
        var doc = Open(Fixture.Replace("<ModifyValue>FALSE</ModifyValue>", "<ModifyValue><Vendor>FALSE</Vendor></ModifyValue>")); byte[] before = doc.ToBytes();
        Refuse(() => doc.SetField(0, "ModifyValue", "TRUE")); True(before.SequenceEqual(doc.ToBytes()));
        doc = Open(Fixture.Replace("sentinel=\"yes\">  </Text>", "sentinel=\"yes\"><Vendor>opaque</Vendor></Text>")); before = doc.ToBytes();
        Refuse(() => doc.SetComment(0, "de-DE", "new")); True(before.SequenceEqual(doc.ToBytes()));
    }
    private static void HashDocumentAnnotations()
    {
        string baseline = WatchTableDocument.SemanticHash(Bytes());
        var hashes = new HashSet<string>(StringComparer.Ordinal) { baseline };
        foreach (string annotation in new[] { "<!-- external metadata -->", "<!-- changed metadata -->", "<?vendor external?>", "<?vendor changed?>" })
        foreach (bool before in new[] { true, false })
        {
            string xml = before ? Fixture.Replace("<Document>", annotation + "\n<Document>") : Fixture + "\n" + annotation;
            byte[] input = Bytes(xml); var doc = new WatchTableDocument(input);
            True(input.SequenceEqual(doc.ToBytes())); True(hashes.Add(WatchTableDocument.SemanticHash(input)));
            var expected = Parse(input); Rows(expected)[0].Element("AttributeList")!.Element("ModifyValue")!.Value = "TRUE";
            doc.SetField(0, "ModifyValue", "TRUE");
            True(XNode.DeepEquals(expected, Parse(doc.ToBytes())));
            Equal(WatchTableDocument.SemanticHash(input), WatchTableDocument.SemanticHash(Bytes(xml + "\n \t")));
        }
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "native-owned-v21"), "*.xml"))
        {
            byte[] original = File.ReadAllBytes(file); var tree = Parse(original);
            string legacy = Path.GetFileName(file) switch {
                "ForceTable.xml" => "5d1c654e89ebb79ac649da01736e1657bdc099e6537ea08951f4d35ec113a64b",
                "UnchangedPeer.xml" => "e4838eba304912d39395c8b2187ccda1720c040e83ed21bd99f00c981ac9d486",
                "MilestoneWatch.before.xml" or "MilestoneWatch.restored.xml" => "65c8582be5b6a217f7abef9b3ead5afe1bb8d515f8223018dd4f50a87fad32ff",
                "MilestoneWatch.applied.xml" or "MilestoneWatch.original.xml" or "MilestoneWatch.reopened.xml" => "7837d36bf9901c87908b792fd05c8318c7ed485b4d7683da3efcc1cb3b348bfe",
                _ => throw new Exception("New fixture requires an independently recorded compatibility expectation.") };
            Equal(legacy, WatchTableDocument.SemanticHash(original));
            tree.Root!.AddBeforeSelf(new XComment("new outside-root content"));
            True(WatchTableDocument.SemanticHash(original) != WatchTableDocument.SemanticHash(Bytes(tree.ToString(SaveOptions.DisableFormatting))));
        }
    }
    private static readonly string ForceFixture = Fixture.Replace("PlcWatchTable", "PlcForceTable").Replace("ModifyValue", "ForceValue")
        .Replace("<DisplayFormat>", "<Address>%I0.0:P</Address><DisplayFormat>");
    private static void ForceEdits()
    {
        foreach (var (field, value) in new[] { ("ForceValue", "TRUE"), ("Address", "%Q0.0:P"), ("Name", "\"Valve\""), ("DisplayFormat", "Hex"), ("ForceValue", "") })
        {
            var expected = Parse(Bytes(ForceFixture)); var target = Rows(expected)[0].Element("AttributeList")!.Element(field)!;
            if (value.Length == 0) target.RemoveNodes(); else target.Value = value;
            var doc = Open(ForceFixture); True(doc.IsForce); doc.SetField(0, field, value); XmlEqual(expected, doc.ToBytes());
            Equal(value, new WatchTableDocument(doc.ToBytes()).Field(0, field));
        }
        var comment = Parse(Bytes(ForceFixture)); comment.Descendants("Text").First().Value = "Ventil {0}";
        var edited = Open(ForceFixture); edited.SetComment(0, "de-DE", "Ventil {0}"); XmlEqual(comment, edited.ToBytes());
        var removed = Parse(Bytes(ForceFixture)); Rows(removed)[0].Remove();
        edited = Open(ForceFixture); edited.Remove(0); XmlEqual(removed, edited.ToBytes());
        edited = Open(ForceFixture); edited.Reorder([1, 0]); edited.Reorder([1, 0]); XmlEqual(Parse(Bytes(ForceFixture)), edited.ToBytes());
        edited = Open(ForceFixture); edited.Duplicate(0, 2); Equal(3, edited.Count); Equal(Prefix + "PlcForceTableEntry", Rows(Parse(edited.ToBytes()))[2].Name.LocalName);
        foreach (string unselected in new[] { "false", "False" })
        { var doc = Open(ForceFixture.Replace("<ForceValue>", "<ForceIntention>" + unselected + "</ForceIntention><ForceValue>")); doc.SetField(0, "ForceValue", "TRUE"); Equal("TRUE", doc.Field(0, "ForceValue")); }
        // Genuine empty native force table: parse and no-op only; it has no rows to edit.
        byte[] empty = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-owned-v21", "ForceTable.xml"));
        var native = new WatchTableDocument(empty); True(native.IsForce); Equal(0, native.Count); True(native.ToBytes().SequenceEqual(empty));
    }
    private static void ForceRefusal()
    {
        var doc = Open(ForceFixture); byte[] before = doc.ToBytes();
        foreach (Action action in new Action[] { () => doc.Rename("new"), () => doc.SetField(0, "ModifyValue", "TRUE"), () => doc.SetField(0, "ForceIntention", "true"),
            () => doc.SetField(0, "ForceIntention", "false"), () => doc.SetField(0, "MonitorTrigger", "Permanent"), () => doc.SetField(1, "ForceValue", "TRUE") }) Refuse(action);
        True(before.SequenceEqual(doc.ToBytes()));
        foreach (string selected in new[] { "true", "True", "TRUE", "1", "", "<v>false</v>", "false<!-- x -->" })
        {
            doc = Open(ForceFixture.Replace("<ForceValue>", "<ForceIntention>" + selected + "</ForceIntention><ForceValue>")); before = doc.ToBytes();
            foreach (Action action in new Action[] { () => doc.SetField(0, "ForceValue", "TRUE"), () => doc.SetComment(0, "de-DE", "new"), () => doc.UpsertComment(0, "fr-FR", "new"),
                () => doc.Duplicate(0, 1), () => doc.Remove(0), () => doc.Reorder([1, 0]), () => doc.RequireNoForceSelection() }) Refuse(action);
            True(before.SequenceEqual(doc.ToBytes()));
        }
        Refuse(() => new WatchTableDocument(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-owned-v21", "ForceTable.xml"))).Rename("Other"));
    }
    private static void ProfileRefusal()
    {
        foreach (string bad in new[] {
            Fixture.Replace("version=\"V21\"", "version=\"V20\""), Fixture.Replace("WithDefaults", "None"),
            Fixture.Replace("<Engineering version=\"V21\" />", "<Engineering version=\"V21\" /><Engineering version=\"V21\" />"),
            Fixture.Replace("</Document>", "<SW.Blocks.FB ID=\"F\"/></Document>"), Fixture.Replace("PlcWatchTableEntry", "PlcForceTableEntry"),
            Fixture.Replace("<DisplayFormat>Bool</DisplayFormat>", "<DisplayFormat>Bool</DisplayFormat><DisplayFormat>Hex</DisplayFormat>"),
            Fixture.Replace("<Name keep=\"yes\">Fixture</Name>", "<Name keep=\"yes\">Fixture</Name><Name>Other</Name>"),
            Fixture.Replace("custom=\"retained\">", "custom=\"retained\"><AttributeList/>"),
            Fixture.Replace("CompositionName=\"Entries\"", "CompositionName=\"Other\""),
            Fixture.Replace("PlcTableCommentEntry", "UnexpectedEntry") }) Refuse(() => Open(bad));
    }
    private static void IdRefusal()
    {
        foreach (string bad in new[] { Fixture.Replace("ID=\"5\"", "ID=\"1\""), Fixture.Replace("ID=\"5\"", "ID=\"01\""), Fixture.Replace("ID=\"5\"", "ID=\"zz\""), Fixture.Replace("ID=\"5\"", "ID=\"\""), Fixture.Replace("ID=\"5\"", ""), Fixture.Replace("ID=\"5\"", "ID=\"FFFFFFFFF\""), Fixture.Replace("ID=\"5\"", "ID=\"a\"").Replace("ID=\"7\"", "ID=\"A\"") }) Refuse(() => Open(bad));
    }
    private static void Limits()
    {
        Refuse(() => new WatchTableDocument([])); Refuse(() => new WatchTableDocument(new byte[16 * 1024 * 1024 + 1]));
        Refuse(() => Open(Fixture.Replace("<Document>", "<!DOCTYPE Document [<!ENTITY p SYSTEM 'file:///never-read'>]><Document>")));
        Refuse(() => Open(Fixture.Replace("<OpaqueInfo>  </OpaqueInfo>", string.Concat(Enumerable.Repeat("<n>", 130)) + string.Concat(Enumerable.Repeat("</n>", 130)))));
        Refuse(() => Open("<Document>"));
        var doc = Parse(Bytes()); var list = Table(doc).Element("ObjectList")!; list.RemoveNodes();
        list.Add(Enumerable.Range(1, 10001).Select(i => new XElement(Prefix + "PlcTableCommentEntry", new XAttribute("ID", i.ToString("X")), new XAttribute("CompositionName", "Entries"))));
        Refuse(() => new WatchTableDocument(Encoding.UTF8.GetBytes(doc.ToString())));
    }
    private static void InvalidEdits()
    {
        var doc = Open(); byte[] before = doc.ToBytes();
        foreach (Action action in new Action[] { () => doc.Reorder([0, 0]), () => doc.Reorder([0]), () => doc.Reorder([0, 2]), () => doc.SetField(-1, "Name", "x"), () => doc.SetField(0, "ForceValue", "x"), () => doc.SetField(1, "Name", "x"), () => doc.SetField(0, "Address", "x"), () => doc.SetField(0, "Name", "bad\0"), () => doc.SetComment(0, "de-DE", "bad\uD800"), () => doc.SetComment(0, "fr-FR", "x"), () => doc.Rename("\t"), () => doc.Duplicate(0, 3), () => doc.Remove(2) })
        { Refuse(action); True(before.SequenceEqual(doc.ToBytes())); }
    }
    private static void ReferenceRefusal()
    {
        foreach (string xml in new[] { Fixture.Replace("custom=\"retained\"", "RefId=\"1\""), Fixture.Replace("custom=\"retained\"", "TargetId=\"1\""), Fixture.Replace("<Opaque whitespace=\"yes\">  </Opaque>", "<Reference>1</Reference>") })
        { var doc = Open(xml); byte[] before = doc.ToBytes(); Refuse(() => doc.Duplicate(0, 1)); True(before.SequenceEqual(doc.ToBytes())); }
    }

    internal static int Evidence(string captureDirectory, string outputPath)
    {
        if (File.Exists(outputPath)) throw new Exception("Use a fresh document evidence path.");
        var checks = new JsonArray();
        foreach (var (name, action) in Checks) { action(); checks.Add(name); }
        var receipt = JsonNode.Parse(File.ReadAllBytes(Path.Combine(captureDirectory, "result.json")))!;
        var exports = new JsonArray();
        foreach (var item in receipt["watch"]!["exports"]!.AsArray())
        {
            string name = Path.GetFileName(item!["artifact"]!["path"]!.GetValue<string>()); byte[] bytes = File.ReadAllBytes(Path.Combine(captureDirectory, name));
            Equal(item["artifact"]!["sha256"]!.GetValue<string>(), WatchTableDocument.Hash(bytes));
            var doc = new WatchTableDocument(bytes); var xml = Parse(bytes); var rows = Rows(xml);
            Equal(item["name"]!.GetValue<string>(), doc.Name); Equal(item["entries"]!.AsArray().Count, doc.Count); Equal(rows.Length, doc.Count);
            True(bytes.SequenceEqual(doc.ToBytes())); Equal(item["kind"]!.GetValue<string>() == "force", doc.IsForce);
            if (!doc.IsForce)
            {
                int row = Array.FindIndex(rows, r => r.Name == Prefix + "PlcWatchTableEntry" && r.Element("AttributeList")!.Element("ModifyValue") != null);
                True(row >= 0); var expected = new XDocument(xml); Rows(expected)[row].Element("AttributeList")!.Element("ModifyValue")!.Value = "TRUE";
                doc.SetField(row, "ModifyValue", "TRUE"); XmlEqual(expected, doc.ToBytes());
                var clone = new WatchTableDocument(bytes); clone.Duplicate(row, clone.Count); Equal(rows.Length + 1, new WatchTableDocument(clone.ToBytes()).Count);
                var order = new WatchTableDocument(bytes); order.Reorder(Enumerable.Range(0, order.Count).Reverse().ToArray()); order.Reorder(Enumerable.Range(0, order.Count).Reverse().ToArray()); XmlEqual(xml, order.ToBytes());
            }
            exports.Add(new JsonObject { ["file"] = name, ["sha256"] = WatchTableDocument.Hash(bytes), ["rows"] = rows.Length,
                ["nativeKind"] = item["kind"]!.DeepClone(), ["unchangedOriginalBytes"] = true, ["offlinePreservationVerified"] = true });
        }
        var report = new JsonObject { ["kind"] = "watch-document-independent-file-only-verification", ["allPassed"] = true,
            ["portableCheckGroups"] = checks, ["genuineExports"] = exports, ["nativeInvoked"] = false,
            ["nativeImportQualifiedByThisCheck"] = false, ["profile"] = WatchTableDocument.Profile,
            ["captureReceiptSha256"] = WatchTableDocument.Hash(File.ReadAllBytes(Path.Combine(captureDirectory, "result.json"))) };
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(output); writer.Write(report.ToJsonString(new() { WriteIndented = true }));
        Console.WriteLine($"{checks.Count} portable document groups and {exports.Count} genuine exports independently verified; native execution false.");
        return 0;
    }
}
