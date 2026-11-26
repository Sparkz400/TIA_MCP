using TiaMcp.Openness.V21;

// Offline checks of HardwareEngineeringRules.ProfileRowPath against the bundled profile of a CPU 1511-1 PN, captured on an
// English TIA: built-in items found under their German names by position, everything else only by exact name.
internal static class ProfileRowPathChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS " + label); }
        var profile = HardwareEngineeringRules.Profile("OrderNumber:6ES7 511-1AL03-0AB0/V4.1", null) ?? throw new InvalidOperationException("CPU 1511-1 PN profile missing.");
        const string english = "items/PROFINET%20interface_1", englishPort = english + "/items/Port_1";
        const string german = "items/PROFINET-Schnittstelle_1", germanPort = german + "/items/Port_1";
        var interfaceChain = new[] { (32768, true) };
        var portChain = new[] { (32768, true), (32769, true) };

        Check(HardwareEngineeringRules.ProfileRowPath(profile, english, interfaceChain) == english, "An exact profile path is kept");
        Check(HardwareEngineeringRules.ProfileRowPath(profile, english, null) == english, "An exact profile path needs no position chain");
        Check(HardwareEngineeringRules.ProfileRowPath(profile, german, interfaceChain) == english, "A built-in interface under its German name finds its row by position");
        Check(HardwareEngineeringRules.ProfileRowPath(profile, germanPort, portChain) == englishPort, "A port below a German-named interface finds its row by both positions");
        Check(HardwareEngineeringRules.ProfileRowPath(profile, german, null) == german, "Without a position chain an unknown name stays unknown");
        Check(HardwareEngineeringRules.ProfileRowPath(profile, german, new[] { (32769, true) }) == german, "A built-in item at another position matches no row");
        Check(HardwareEngineeringRules.ProfileRowPath(profile, german, new[] { (32768, false) }) == german, "An item that is not built in is matched by name only");
        Check(HardwareEngineeringRules.ProfileRowPath(profile, germanPort, interfaceChain) == germanPort, "A chain of another length matches no row");
        Check(HardwareEngineeringRules.ProfileRowPath(profile, germanPort.Replace("Port_1", "Anschluss_1"), new[] { (32768, true), (32769, false) }) == germanPort.Replace("Port_1", "Anschluss_1"),
            "A renamed item that is not built in is not matched by position");
        Check(HardwareEngineeringRules.ProfileRowPath(null, german, interfaceChain) == german, "No profile, no match");

        // The write check then reads the matched row: InterfaceOperatingMode is declared writable on the interface.
        HardwareEngineeringRules.RequireProfileWritable(profile, HardwareEngineeringRules.ProfileRowPath(profile, german, interfaceChain), null, new[] { "InterfaceOperatingMode" });
        Check(true, "The German-named interface's InterfaceOperatingMode is writable through its profile row");
        try
        {
            HardwareEngineeringRules.RequireProfileWritable(profile, german, null, new[] { "InterfaceOperatingMode" });
            throw new InvalidOperationException("The unmatched German path must still refuse.");
        }
        catch (BridgeException error) when (error.Code == "AttributeNotWritable") { Check(true, "The unmatched German path refuses as before"); }
        return count;
    }
}
