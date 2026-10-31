using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// safety_fparameter_set's write gate: the never-written classes, the module key and the shipped, derived allowlist.
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

        Check(FParameterAllowlistRules.ModuleKey(null, Fdi) == Fdi, "an ET 200SP F-I/O item without a type identifier keys by its module");
        Check(FParameterAllowlistRules.ModuleKey("OrderNumber:6ES7 526-1BH00-0AB0/V1.0", "OrderNumber:6ES7 590-1AF30-0AA0") == "OrderNumber:6ES7 526-1BH00-0AB0/V1.0", "a central F-module keys by itself");
        Check(FParameterAllowlistRules.ModuleKey(null, "OrderNumber:6ES7 590-1AF30-0AA0") == null, "a rail without firmware never keys a row");
        Check(FParameterAllowlistRules.ModuleKey("GSD:ABC.XML/M/IDM_1", null) == "GSD:ABC.XML/M/IDM_1", "a GSD module keys by itself");
        Check(FParameterAllowlistRules.ModuleKey("", "") == null, "empty identifiers key nothing");

        var shipped = FParameterAllowlistRules.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "safety-fparameter-allowlist.v1.json")));
        var destination = FParameterAllowlistRules.Row(shipped, Fdi, "Failsafe_FDestinationAddress");
        Check(FParameterAllowlistRules.Qualified(destination) && (string)destination["evidenceId"] == "local.safety.fparameter_discovery.20261031",
              "the shipped F-DI F-destination row is qualified by the discovery run");
        Check(FParameterAllowlistRules.Row(shipped, Fdi, "Failsafe_ManualAssignmentFIODBNumber") == null, "the F-I/O DB assignment is never listed");
        Check(FParameterAllowlistRules.Row(shipped, Fdi, "Failsafe_FMonitoringtime") == null, "the F-DI's F-monitoring time is not listed");
        Check(FParameterAllowlistRules.Row(shipped, "OrderNumber:6ES7 136-6DB00-0CA0/V1.0", "Failsafe_FDestinationAddress") == null, "another module type lists nothing by default");

        string Document(string parameters, string modules = "", string identity = Fdi) => "{\"schemaVersion\":\"safety-fparameter-allowlist.v1\",\"tiaVersion\":21,\"rule\":\"r\",\"modules\":[{\"moduleTypeIdentifier\":\"" + identity +
            "\",\"moduleTypeName\":\"F-DI\",\"parameters\":[" + parameters + "]}" + modules + "]}";
        const string row = "{\"name\":\"Failsafe_ShortCircuitTest_0\",\"status\":\"qualified\",\"evidenceId\":\"local.x\"}";
        Check(FParameterAllowlistRules.Qualified(FParameterAllowlistRules.Row(FParameterAllowlistRules.Parse(Document(row)), Fdi, "Failsafe_ShortCircuitTest_0")), "a derived row qualifies its parameter");
        Malformed(Document(row.Replace("ShortCircuitTest_0", "FSourceAddress")), "a never-written parameter is listed");
        Malformed(Document(row + "," + row), "a parameter twice");
        Malformed(Document(row, ",{\"moduleTypeIdentifier\":\"" + Fdi + "\",\"moduleTypeName\":\"F-DI\",\"parameters\":[]}"), "a module twice");
        Malformed(Document(row.Replace("}", ",\"value\":1}")), "an unknown field");
        Malformed(Document(row.Replace("\"status\":\"qualified\",\"evidenceId\":\"local.x\"", "\"status\":\"pending\",\"evidenceId\":\"local.x\"")), "a pending row with evidence instead of a reason");
        Malformed(Document(row.Replace("qualified", "accepted")), "an unknown status");
        Malformed(Document(row, identity: "OrderNumber:6ES7 590-1AF30-0AA0"), "a rail identity without firmware");
        Malformed(Document(row).Replace("\"tiaVersion\":21", "\"tiaVersion\":\"21\""), "a text TIA version");
        return count;
    }
}
