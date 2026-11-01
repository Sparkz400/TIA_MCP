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
        return count;
    }
}
