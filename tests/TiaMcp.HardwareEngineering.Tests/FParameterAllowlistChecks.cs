using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// safety_fparameter_set's write gate: the never-written classes and the shipped, derived allowlist.
internal static class FParameterAllowlistChecks
{
    const string Fdi = "OrderNumber:6ES7 136-6BA01-0CA0/V2.0";

    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
        void Malformed(string json, string label)
        {
            try { FParameterAllowlistRules.Parse(json); } catch (Exception e) when (e is FormatException or Newtonsoft.Json.JsonException) { Check(true, "malformed allowlist refused: " + label); return; }
            throw new Exception("Expected a malformed allowlist: " + label);
        }
        foreach (var name in new[] { "Failsafe_FSourceAddress", "Failsafe_CentralFSourceAddress", "Failsafe_FParameterSignatureWithAddresses", "Failsafe_FIODBNumber",
                     "Failsafe_ManualAssignmentFIODBNumber", "Failsafe_FSIL", "Failsafe_F_CRC_Length", "Failsafe_FParVersion", "Failsafe_FCheckiPar", "Failsafe_FBlockID", "Name", null })
            Check(FParameterAllowlistRules.Denied(name), "never written: " + (name ?? "null"));
        foreach (var name in new[] { "Failsafe_FDestinationAddress", "Failsafe_FMonitoringtime", "Failsafe_ShortCircuitTest_0", "Failsafe_BehaviorAfterChannelFault", "Failsafe_ManualAssignmentFMonitoringtime" })
            Check(!FParameterAllowlistRules.Denied(name), "eligible for discovery: " + name);

        var shipped = FParameterAllowlistRules.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "safety-fparameter-allowlist.v1.json")));
        Check((string)FParameterAllowlistRules.Qualification(shipped, Fdi, "Failsafe_FDestinationAddress")?["evidenceId"] == "local.safety_changes.20261028",
              "the shipped allowlist qualifies the F-DI's F-destination address with its evidence");
        Check(FParameterAllowlistRules.Qualification(shipped, Fdi, "Failsafe_FMonitoringtime") == null, "the F-DI's F-monitoring time is not qualified");
        Check(FParameterAllowlistRules.Qualification(shipped, "OrderNumber:6ES7 136-6DB00-0CA0/V1.0", "Failsafe_FDestinationAddress") == null, "another module type qualifies nothing by default");

        string Document(string parameters, string modules = "") => "{\"schemaVersion\":\"safety-fparameter-allowlist.v1\",\"tiaVersion\":21,\"rule\":\"r\",\"modules\":[{\"moduleTypeIdentifier\":\"" + Fdi +
            "\",\"moduleTypeName\":\"F-DI\",\"parameters\":[" + parameters + "]}" + modules + "]}";
        const string row = "{\"name\":\"Failsafe_ShortCircuitTest_0\",\"evidenceId\":\"local.x\"}";
        Check(FParameterAllowlistRules.Qualification(FParameterAllowlistRules.Parse(Document(row)), Fdi, "Failsafe_ShortCircuitTest_0") != null, "a derived row qualifies its parameter");
        Malformed(Document(row.Replace("ShortCircuitTest_0", "FSourceAddress")), "a never-written parameter is listed");
        Malformed(Document(row + "," + row), "a parameter twice");
        Malformed(Document(row, ",{\"moduleTypeIdentifier\":\"" + Fdi + "\",\"moduleTypeName\":\"F-DI\",\"parameters\":[]}"), "a module twice");
        Malformed(Document(row.Replace("}", ",\"value\":1}")), "an unknown field");
        Malformed(Document(row).Replace("\"tiaVersion\":21", "\"tiaVersion\":\"21\""), "a text TIA version");
        return count;
    }
}
