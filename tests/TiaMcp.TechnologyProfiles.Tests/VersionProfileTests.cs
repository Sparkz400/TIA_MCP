using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// The worker's to_create profile gate over the shipped contract and synthetic documents.
internal static class VersionProfileTests
{
    const string Cpu = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1";

    internal static void Run()
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; }
        void Malformed(string json, string label)
        {
            try { TechnologyVersionProfileRules.Parse(json); } catch (Exception e) when (e is FormatException or Newtonsoft.Json.JsonException) { count++; return; }
            throw new Exception("Expected a malformed profile document: " + label);
        }
        var shipped = TechnologyVersionProfileRules.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "technology-version-profiles.v1.json")));

        var pid = TechnologyVersionProfileRules.Admit(shipped, Cpu, "PID_Compact", "3.0", false);
        Check(pid.Value<bool>("admitted") && (string?)pid["profile"]!["outcome"] == "accepted" && (string?)pid["profile"]!["evidenceId"] == "local.technology_changes.20261028",
              "The shipped scratch CPU admits PID_Compact 3.0 with its evidence");
        Check(TechnologyVersionProfileRules.Admit(shipped, Cpu, "PID_Compact", "3.00", false).Value<bool>("admitted"), "Versions compare as native Version values");
        var older = TechnologyVersionProfileRules.Admit(shipped, Cpu, "PID_Compact", "2.3", false);
        Check(!older.Value<bool>("admitted") && (string?)older["code"] == "TechnologyVersionUnprofiled" && older["details"]!["acceptedVersions"]!.ToObject<string[]>()!.SequenceEqual(["3.0"]),
              "An unrecorded version on a profiled CPU refuses and names the accepted versions");
        var acknowledged = TechnologyVersionProfileRules.Admit(shipped, Cpu, "PID_Compact", "2.3", true);
        Check(acknowledged.Value<bool>("admitted") && (string?)acknowledged["profile"]!["outcome"] == "unprofiled", "unprofiledAcknowledged admits an unrecorded pair as unprofiled");
        var otherCpu = TechnologyVersionProfileRules.Admit(shipped, "OrderNumber:6ES7 214-1AG40-0XB0/V4.7", "PID_Compact", "3.0", false);
        Check(!otherCpu.Value<bool>("admitted") && (string?)otherCpu["code"] == "TechnologyVersionUnprofiled" && otherCpu["details"]!.Value<bool>("cpuProfiled") == false,
              "A CPU without cells refuses even a version accepted elsewhere");
        Check(!TechnologyVersionProfileRules.Admit(shipped, Cpu, "pid_compact", "3.0", false).Value<bool>("admitted"), "Type identifiers match ordinally");

        string Document(string cells) => "{\"schemaVersion\":\"technology-version-profiles.v1\",\"tiaVersion\":21,\"rule\":\"r\",\"cpus\":[{\"cpuTypeIdentifier\":\"" + Cpu + "\",\"cpuTypeName\":\"CPU\",\"cells\":[" + cells + "]}]}";
        const string refusedCell = "{\"typeIdentifier\":\"TO_Cam\",\"version\":\"10.0\",\"outcome\":\"refused\",\"evidenceId\":\"local.x\",\"nativeRefusal\":{\"code\":\"EngineeringError\",\"message\":\"Native text.\"}}";
        var refusing = TechnologyVersionProfileRules.Parse(Document(refusedCell));
        foreach (bool ack in new[] { false, true })
        {
            var refused = TechnologyVersionProfileRules.Admit(refusing, Cpu, "TO_Cam", "10.0", ack);
            Check(!refused.Value<bool>("admitted") && (string?)refused["code"] == "TechnologyVersionRefused" && ((string?)refused["message"])!.Contains("Native text.")
                  && (string?)refused["details"]!["nativeRefusal"]!["code"] == "EngineeringError", "A recorded refusal refuses with its native text, acknowledged or not: " + ack);
        }

        Malformed(Document(refusedCell).Replace("\"rule\":\"r\"", "\"rule\":\"r\",\"rule\":\"s\""), "duplicate field");
        Malformed(Document(refusedCell).Replace("\"tiaVersion\":21", "\"tiaVersion\":20"), "another TIA version");
        Malformed(Document(refusedCell.Replace(",\"nativeRefusal\":{\"code\":\"EngineeringError\",\"message\":\"Native text.\"}", "")), "a refusal without native text");
        Malformed(Document(refusedCell + "," + refusedCell.Replace("\"10.0\"", "\"10.00\"")), "the same version twice");
        Malformed(Document(refusedCell.Replace("\"outcome\":\"refused\"", "\"outcome\":\"substituted\"")), "an unknown outcome");
        Malformed(Document(refusedCell.Replace("\"version\":\"10.0\"", "\"version\":\"latest\"")), "a non-numeric version");
        Malformed(Document("{\"typeIdentifier\":\"PID_Compact\",\"version\":\"3.0\",\"outcome\":\"accepted\",\"evidenceId\":\"e\",\"extra\":1}"), "an unknown cell field");
        Console.WriteLine($"PASS technology version profiles: {count} checks.");
    }
}
