using TiaMcp.Openness.V21;

// tag_create's prepare-time F-I/O check: absolute I/O addresses and their overlap with F-I/O address ranges.
internal static class TagAddressChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
        Check(TagAddressRules.Io("%I0.0") == ("Input", 0, 1) && TagAddressRules.Io("I5.7") == ("Input", 47, 1), "input bits parse with and without %");
        Check(TagAddressRules.Io("%QW4") == ("Output", 32, 16) && TagAddressRules.Io("%qd8") == ("Output", 64, 32) && TagAddressRules.Io("%IB2") == ("Input", 16, 8)
              && TagAddressRules.Io("%IL0") == ("Input", 0, 64), "byte, word, double and long word accesses parse case-insensitively");
        Check(TagAddressRules.Io("%E1.2") == ("Input", 10, 1) && TagAddressRules.Io("%AW2") == ("Output", 16, 16), "German mnemonics E and A parse");
        foreach (var other in new[] { "%M100.0", "%MW2", "%DB1.DBX0.0", "Tag_1", "%I0.8", "", null })
            Check(TagAddressRules.Io(other) == null, "not an absolute I/O address: " + (other ?? "null"));

        var ranges = new[] { ("Input", 0, 48, "/fdi"), ("Output", 0, 32, "/fdi"), ("Input", 10, 16, "/fdq") };
        Check(TagAddressRules.FIoOverlap("%I0.0", ranges) == "/fdi" && TagAddressRules.FIoOverlap("%I5.7", ranges) == "/fdi", "a bit inside the F-DI input range overlaps");
        Check(TagAddressRules.FIoOverlap("%IW5", ranges) == "/fdi", "a word straddling the range end overlaps");
        Check(TagAddressRules.FIoOverlap("%I6.0", ranges) == null && TagAddressRules.FIoOverlap("%IB9", ranges) == null, "addresses just outside every range do not overlap");
        Check(TagAddressRules.FIoOverlap("%I11.3", ranges) == "/fdq" && TagAddressRules.FIoOverlap("%Q3.7", ranges) == "/fdi", "direction and range select the F-I/O");
        Check(TagAddressRules.FIoOverlap("%Q4.0", ranges) == null && TagAddressRules.FIoOverlap("%M0.0", ranges) == null, "other directions and bit memory never overlap");
        Check(TagAddressRules.FIoOverlap("%I0.0", new[] { ("Input", 0, 0, "/empty") }) == null, "a zero-length range overlaps nothing");

        // tag_table_import_xml checks every imported tag's address; user constants carry none.
        var table = System.Xml.Linq.XDocument.Parse("<Document><SW.Tags.PlcTagTable ID=\"0\"><ObjectList>" +
            "<SW.Tags.PlcTag ID=\"1\" CompositionName=\"Tags\"><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M200.0</LogicalAddress><Name>A</Name></AttributeList></SW.Tags.PlcTag>" +
            "<SW.Tags.PlcTag ID=\"2\" CompositionName=\"Tags\"><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%I1.0</LogicalAddress><Name>B</Name></AttributeList></SW.Tags.PlcTag>" +
            "<SW.Tags.PlcUserConstant ID=\"3\" CompositionName=\"UserConstants\"><AttributeList><DataTypeName>Int</DataTypeName><Name>C</Name><Value>1</Value></AttributeList></SW.Tags.PlcUserConstant>" +
            "</ObjectList></SW.Tags.PlcTagTable></Document>");
        var addresses = TagAddressRules.TagTableAddresses(table);
        Check(addresses.SequenceEqual(new[] { "%M200.0", "%I1.0" }), "a tag-table document yields every tag's address and no constant");
        Check(addresses.Select(a => TagAddressRules.FIoOverlap(a, ranges)).Where(path => path != null).SequenceEqual(new[] { "/fdi" }), "the imported F-DI address is the one that overlaps");

        // vci_synchronize WorkspaceToProject reads every document of a tag-table package (VCI writes the table's SimaticML export).
        string Package(string address) => "<Document><Engineering version=\"V21\" /><SW.Tags.PlcTagTable ID=\"0\"><AttributeList><Name>Vci</Name></AttributeList><ObjectList>" +
            "<SW.Tags.PlcTag ID=\"1\" CompositionName=\"Tags\"><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>" + address + "</LogicalAddress><Name>T</Name></AttributeList></SW.Tags.PlcTag>" +
            "</ObjectList></SW.Tags.PlcTagTable></Document>";
        string[] Incoming(params string[] documents) => documents.SelectMany(xml => TagAddressRules.TagTableAddresses(System.Xml.Linq.XDocument.Parse(xml))).ToArray();
        Check(Incoming(Package("%I6.0"), Package("%Q4.0")).All(a => TagAddressRules.FIoOverlap(a, ranges) == null), "a VCI package on non-F I/O addresses passes");
        Check(Incoming(Package("%Q0.0"), Package("%I2.1")).Select(a => TagAddressRules.FIoOverlap(a, ranges)).First(path => path != null) == "/fdi", "a VCI package with an F-I/O address refuses");

        // library_instantiate cannot read a tag-table master copy's addresses, so any F-I/O range refuses it.
        Check(TagAddressRules.AnyFIo(ranges) == "/fdi", "a tag-table master copy refuses while the PLC owns an F-I/O range");
        Check(TagAddressRules.AnyFIo(Array.Empty<(string, int, int, string)>()) == null && TagAddressRules.AnyFIo(new[] { ("Input", 0, 0, "/empty") }) == null,
              "a tag-table master copy passes on a PLC without F-I/O ranges");
        return count;
    }
}
