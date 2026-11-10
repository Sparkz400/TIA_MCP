using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// tag_table_import_xml's row form and its advisory hardware cross-check.
internal static class TagTableRowChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
        string Refused(Action action) { try { action(); } catch (BridgeException error) { return error.Code; } return null; }
        static JArray Tags(params (string Name, string Type, string Address, string Comment)[] rows) => new JArray(rows.Select(row =>
        {
            var item = new JObject { ["name"] = row.Name, ["dataType"] = row.Type, ["logicalAddress"] = row.Address };
            if (row.Comment != null) item["comment"] = row.Comment;
            return item;
        }));

        // Addresses and elementary type names take the spelling a native export reads back.
        Check(TagTableRowRules.NormalizeAddress("e1.2") == "%I1.2" && TagTableRowRules.NormalizeAddress("%aw4") == "%QW4" && TagTableRowRules.NormalizeAddress(" %MD008 ") == "%MD8"
              && TagTableRowRules.NormalizeAddress("%I0.8") == null && TagTableRowRules.NormalizeAddress("%DB1.DBX0.0") == null, "row addresses normalize to international mnemonics with %");
        var rows = TagTableRowRules.Rows(Tags(("Start", "BOOL", "I0.0", "Start button"), ("Speed", "int", "%QW4", null), ("Udt", "\"Drive\"", "%I10.0", null)));
        Check(rows.Select(row => row.DataType + "@" + row.LogicalAddress).SequenceEqual(new[] { "Bool@%I0.0", "Int@%QW4", "\"Drive\"@%I10.0" }), "rows keep unknown type names and canonicalize elementary ones");
        Check(Refused(() => TagTableRowRules.Rows(Tags(("A", "Bool", "%I0.0", null), ("a", "Bool", "%I0.1", null)))) == "InvalidRequest", "duplicate row names refuse");
        Check(Refused(() => TagTableRowRules.Rows(Tags(("A", "Bool", "Tag_1", null)))) == "InvalidRequest", "a symbolic or unparseable address refuses");
        Check(Refused(() => TagTableRowRules.Rows(new JArray())) == "InvalidRequest" && Refused(() => TagTableRowRules.Rows(new JArray(new JObject { ["name"] = "A", ["dataType"] = "Bool", ["logicalAddress"] = "%I0.0", ["value"] = 1 }))) == "InvalidRequest",
              "an empty list and an unknown row field refuse");
        Check(Refused(() => TagTableRowRules.Document("T", null, rows, null, out _, out _)) == "InvalidRequest", "a comment without a culture refuses");

        // A new table holds exactly the rows.
        var created = TagTableRowRules.Document("Signals", null, rows, "en-US", out int added, out int updated);
        var table = created.Root.Element("SW.Tags.PlcTagTable");
        Check(added == 3 && updated == 0 && (string)table.Element("AttributeList").Element("Name") == "Signals" && table.Element("ObjectList").Elements("SW.Tags.PlcTag").Count() == 3, "a new table holds the rows");
        Check(created.Descendants().Attributes("ID").Select(id => id.Value).Distinct().Count() == created.Descendants().Attributes("ID").Count(), "generated IDs are unique");
        var comment = table.Descendants("MultilingualTextItem").Single().Element("AttributeList");
        Check((string)comment.Element("Culture") == "en-US" && (string)comment.Element("Text") == "Start button", "a row comment becomes the tag comment in the given culture");
        Check(TagAddressRules.TagTableAddresses(created).SequenceEqual(new[] { "%I0.0", "%QW4", "%I10.0" }), "the generated document yields its addresses to the F-I/O check");

        // Rows merge into the table's own export: untouched members stay byte-identical, a named tag is updated in place.
        var existing = XDocument.Parse("<Document><Engineering version=\"V21\" /><SW.Tags.PlcTagTable ID=\"0\"><AttributeList><Name>Signals</Name></AttributeList><ObjectList>" +
            "<SW.Tags.PlcTag ID=\"1\" CompositionName=\"Tags\"><AttributeList><DataTypeName>Bool</DataTypeName><ExternalWritable>false</ExternalWritable><LogicalAddress>%M1.0</LogicalAddress><Name>Keep</Name></AttributeList></SW.Tags.PlcTag>" +
            "<SW.Tags.PlcTag ID=\"A\" CompositionName=\"Tags\"><AttributeList><DataTypeName>Bool</DataTypeName><ExternalWritable>false</ExternalWritable><LogicalAddress>%I0.0</LogicalAddress><Name>start</Name></AttributeList></SW.Tags.PlcTag>" +
            "<SW.Tags.PlcUserConstant ID=\"B\" CompositionName=\"UserConstants\"><AttributeList><DataTypeName>Int</DataTypeName><Name>Limit</Name><Value>1</Value></AttributeList></SW.Tags.PlcUserConstant>" +
            "</ObjectList></SW.Tags.PlcTagTable></Document>");
        string keep = existing.Descendants("SW.Tags.PlcTag").First().ToString(SaveOptions.DisableFormatting);
        var merged = TagTableRowRules.Document("Signals", existing, TagTableRowRules.Rows(Tags(("Start", "Bool", "%I2.0", "Moved"), ("Stop", "Bool", "%I2.1", null))), "de-DE", out added, out updated);
        var tags = merged.Descendants("SW.Tags.PlcTag").ToArray();
        Check(added == 1 && updated == 1 && tags.Length == 3 && merged.Descendants("SW.Tags.PlcUserConstant").Count() == 1, "rows add new tags and update named ones, keeping other members");
        Check(tags[0].ToString(SaveOptions.DisableFormatting) == keep && existing.Descendants("SW.Tags.PlcTag").Count() == 2, "an unnamed member and the export itself stay unchanged");
        Check((string)tags[1].Element("AttributeList").Element("Name") == "start" && (string)tags[1].Element("AttributeList").Element("LogicalAddress") == "%I2.0"
              && (string)tags[1].Element("AttributeList").Element("ExternalWritable") == "false" && (string)tags[1].Descendants("Text").Single() == "Moved", "an updated tag keeps its native name and other attributes");
        Check(new[] { "C", "D", "E" }.Contains((string)tags[2].Attribute("ID")) && merged.Descendants().Attributes("ID").Select(id => id.Value).Distinct().Count() == merged.Descendants().Attributes("ID").Count(), "new IDs continue after the highest hexadecimal ID");
        Check(Refused(() => TagTableRowRules.Document("Signals", existing, TagTableRowRules.Rows(Tags(("limit", "Int", "%MW2", null))), null, out _, out _)) == "TagScopeMismatch", "a row naming an existing constant refuses");

        // The hardware cross-check reports and never throws.
        var ranges = new[] { ("Input", 0, 16, "/di16"), ("Output", 4, 16, "/ao"), ("Input", 2, 0, "/empty") };
        JObject Cross(Func<IEnumerable<(string, int, int, string)>> configured, params (string Name, string Type, string Address, string Comment)[] list) =>
            TagTableRowRules.CrossCheck(TagTableRowRules.Document("T", null, TagTableRowRules.Rows(Tags(list)), null, out _, out _), configured);
        var passed = Cross(() => ranges, ("A", "Bool", "%I1.7", null), ("B", "Int", "%QW4", null), ("M", "Bool", "%M0.0", null));
        Check(passed.Value<string>("status") == "passed" && passed.Value<int>("ioTagCount") == 2 && passed.Value<bool>("blocking") == false && TagTableRowRules.Warning(passed) == null, "addresses inside configured ranges pass");
        var found = Cross(() => ranges, ("A", "Bool", "%I2.0", null), ("B", "Word", "%QW5", null), ("C", "Bool", "%Q0.0", null), ("D", "Int", "%IB0", null));
        Check(found.Value<string>("status") == "findings" && ((JArray)found["findings"]).Select(f => f.Value<string>("tag") + ":" + f.Value<string>("finding"))
              .SequenceEqual(new[] { "A:noConfiguredAddress", "B:partlyConfigured", "C:noConfiguredAddress", "D:dataTypeWidthMismatch" }), "unconfigured, straddling and width-mismatched addresses are findings");
        Check(TagTableRowRules.Warning(found).Contains("does not block the import"), "findings warn without blocking");
        var unavailable = Cross(() => throw new BridgeException("TraversalLimit", "too large"), ("A", "Bool", "%I0.0", null), ("B", "Bool", "%I0.1", null));
        Check(unavailable.Value<string>("status") == "unavailable" && unavailable.Value<string>("unavailableReason") == "too large" && unavailable.Value<int>("ioTagCount") == 2
              && TagTableRowRules.Warning(unavailable).Contains("not blocked"), "an unreadable hardware configuration is reported, not thrown");
        bool asked = false;
        var memoryOnly = Cross(() => { asked = true; return ranges; }, ("M", "Bool", "%M0.0", null));
        Check(memoryOnly.Value<string>("status") == "passed" && !asked, "a table without I/O tags does not read the hardware configuration");
        var many = Cross(() => ranges, Enumerable.Range(0, 120).Select(i => ("T" + i, "Bool", "%I" + (100 + i / 8) + "." + (i % 8), (string)null)).ToArray());
        Check(many.Value<int>("findingCount") == 120 && ((JArray)many["findings"]).Count == 100 && many.Value<bool>("findingsTruncated"), "findings are bounded and counted");
        return count;
    }
}
