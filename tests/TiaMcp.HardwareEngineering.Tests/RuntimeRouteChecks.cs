using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// Pure decisions of the CPU parameter, compile-only, tag-table lifecycle and online simulator download kinds.
internal static class RuntimeRouteChecks
{
    private static int _count;

    private static void Pass(string name, bool condition)
    {
        if (!condition) throw new Exception("Check failed: " + name);
        _count++; Console.WriteLine("PASS " + name);
    }

    private static string Code(Action action)
    {
        try { action(); return null; }
        catch (BridgeException error) { return error.Code; }
        catch (ArgumentException) { return "ArgumentException"; }
    }

    internal static int Run()
    {
        string parameter = HardwareEngineeringRules.CpuParameterAttributes[0];
        Pass("cpu parameter outside the subset refused", Code(() => HardwareEngineeringRules.RequireCpuParameterRequest(new JObject { ["Comment"] = "x" })) == "UnsupportedAttribute");
        Pass("cpu parameter request needs a value", Code(() => HardwareEngineeringRules.RequireCpuParameterRequest(new JObject())) == "InvalidRequest");
        Pass("cpu parameter null refused", Code(() => HardwareEngineeringRules.RequireCpuParameterRequest(new JObject { [parameter] = null })) == "InvalidRequest");
        Pass("cpu parameter object refused", Code(() => HardwareEngineeringRules.RequireCpuParameterRequest(new JObject { [parameter] = new JObject() })) == "InvalidRequest");
        Pass("disabling access control refused", Code(() => HardwareEngineeringRules.RequireCpuParameterRequest(new JObject { ["PlcAccessControlConfiguration"] = "Disabled" })) == "ProtectionDowngradeRefused"
            && Code(() => HardwareEngineeringRules.RequireCpuParameterRequest(new JObject { ["PlcAccessControlConfiguration"] = 0 })) == "ProtectionDowngradeRefused"
            && Code(() => HardwareEngineeringRules.RequireCpuParameterRequest(new JObject { ["PlcAccessControlConfiguration"] = 2 })) == "ProtectionDowngradeRefused"
            && Code(() => HardwareEngineeringRules.RequireCpuParameterRequest(new JObject { ["PlcAccessControlConfiguration"] = "disabled" })) == "ProtectionDowngradeRefused"
            && Code(() => HardwareEngineeringRules.RequireCpuParameterRequest(new JObject { ["PlcAccessControlConfiguration"] = " EnabledWithAccessControl" })) == "ProtectionDowngradeRefused"
            && Code(() => HardwareEngineeringRules.RequireCpuParameterRequest(new JObject { ["PlcAccessControlConfiguration"] = "EnabledWithAccessControlViaAccessLevel" })) == null);
        Pass("cpu parameter scalar admitted", Code(() => HardwareEngineeringRules.RequireCpuParameterRequest(new JObject { [parameter] = 150 })) == null);
        Pass("unchanged cpu parameters refused", Code(() => HardwareEngineeringRules.RequireCpuParametersChanged(new JObject { [parameter] = 150L }, new JObject { [parameter] = 150 })) == "InvalidRequest");
        var enabledMinimum = new JObject { ["CycleEnableMinimumCycleTime"] = true, ["CycleMinimumCycleTime"] = 5L };
        var disabledMinimum = new JObject { ["CycleEnableMinimumCycleTime"] = false };
        Pass("disabling the minimum cycle time drops its value", JToken.DeepEquals(HardwareEngineeringRules.CpuParametersAfter(enabledMinimum, new JObject { ["CycleEnableMinimumCycleTime"] = false }), disabledMinimum)
            && Code(() => HardwareEngineeringRules.RequireCpuParametersChanged(enabledMinimum, new JObject { ["CycleEnableMinimumCycleTime"] = false })) == null);
        Pass("a minimum cycle time of a disabled flag refused", Code(() => HardwareEngineeringRules.RequireCpuParametersChanged(disabledMinimum, new JObject { ["CycleMinimumCycleTime"] = 3 })) == "InvalidRequest"
            && Code(() => HardwareEngineeringRules.RequireCpuParametersChanged(enabledMinimum, new JObject { ["CycleEnableMinimumCycleTime"] = false, ["CycleMinimumCycleTime"] = 3 })) == "InvalidRequest");
        Pass("enabling the minimum cycle time needs its value", Code(() => HardwareEngineeringRules.RequireCpuParametersChanged(disabledMinimum, new JObject { ["CycleEnableMinimumCycleTime"] = true })) == "InvalidRequest"
            && Code(() => HardwareEngineeringRules.RequireCpuParametersChanged(disabledMinimum, new JObject { ["CycleEnableMinimumCycleTime"] = true, ["CycleMinimumCycleTime"] = 1 })) == null);
        Pass("changed cpu parameter admitted", Code(() => HardwareEngineeringRules.RequireCpuParametersChanged(new JObject { [parameter] = 150L }, new JObject { [parameter] = 200 })) == null);

        Pass("project compile needs an empty payload", CompileOnlyRules.PayloadRefusal("project_compile", new JObject()) == null && CompileOnlyRules.PayloadRefusal("project_compile", new JObject { ["x"] = 1 }) != null);
        Pass("device compile needs only devicePath", CompileOnlyRules.PayloadRefusal("device_compile", new JObject { ["devicePath"] = "/projects/P/devices/S" }) == null
            && CompileOnlyRules.PayloadRefusal("device_compile", new JObject { ["devicePath"] = " " }) != null
            && CompileOnlyRules.PayloadRefusal("device_compile", new JObject { ["devicePath"] = "/d", ["plcPath"] = "/p" }) != null);
        Pass("hmi compile needs only a target object", CompileOnlyRules.PayloadRefusal("hmi_compile", new JObject { ["target"] = new JObject() }) == null
            && CompileOnlyRules.PayloadRefusal("hmi_compile", new JObject { ["target"] = "/hmi" }) != null);
        Pass("unknown compile kind refused", CompileOnlyRules.PayloadRefusal("plc_compile", new JObject()) != null);
        Pass("compile targets must equal the preview in order", CompileOnlyRules.SameTargets(new[] { "a", "b" }, new[] { "a", "b" })
            && !CompileOnlyRules.SameTargets(new[] { "a", "b" }, new[] { "b", "a" }) && !CompileOnlyRules.SameTargets(new[] { "a" }, new[] { "a", "c" }) && !CompileOnlyRules.SameTargets(null, new[] { "a" }));

        var tables = new[] { "Default tag table", "Motors" };
        Pass("tag table create admitted", TagTableLifecycleRules.Refusal("tag_table_create", "Valves", null, false, false, 0, tables) == null);
        Pass("tag table create collision is case-insensitive", TagTableLifecycleRules.Refusal("tag_table_create", "motors", null, false, false, 0, tables) == "ObjectExists");
        Pass("tag table rename admitted", TagTableLifecycleRules.Refusal("tag_table_rename", "Motors", "Drives", true, false, 4, tables) == null);
        Pass("tag table rename onto another table refused", TagTableLifecycleRules.Refusal("tag_table_rename", "Motors", "default tag table", true, false, 4, tables) == "ObjectExists");
        Pass("tag table case-only rename admitted", TagTableLifecycleRules.Refusal("tag_table_rename", "Motors", "MOTORS", true, false, 4, tables) == null);
        Pass("tag table identical rename refused", TagTableLifecycleRules.Refusal("tag_table_rename", "Motors", "Motors", true, false, 4, tables) == "InvalidRequest");
        Pass("absent tag table refused", TagTableLifecycleRules.Refusal("tag_table_rename", "Pumps", "P", false, false, 0, tables) == "ObjectNotFound"
            && TagTableLifecycleRules.Refusal("tag_table_delete", "Pumps", null, false, false, 0, tables) == "ObjectNotFound");
        Pass("empty user tag table delete admitted", TagTableLifecycleRules.Refusal("tag_table_delete", "Motors", null, true, false, 0, tables) == null);
        Pass("non-empty tag table delete refused", TagTableLifecycleRules.Refusal("tag_table_delete", "Motors", null, true, false, 1, tables) == "TagTableNotEmpty");
        Pass("default tag table delete refused", TagTableLifecycleRules.Refusal("tag_table_delete", "Default tag table", null, true, true, 0, tables) == "DefaultTagTable");

        var keep = new JObject { ["softwareScope"] = "changes", ["dataBlockValues"] = "keepActualValues", ["allowStop"] = false };
        var reinitialize = new JObject { ["softwareScope"] = "all", ["dataBlockValues"] = "reinitialize", ["allowStop"] = true };
        Pass("download options are explicit", Code(() => SimulationOnlineDownloadRules.Options(keep)) == null && Code(() => SimulationOnlineDownloadRules.Options(reinitialize)) == null
            && Code(() => SimulationOnlineDownloadRules.Options(new JObject { ["softwareScope"] = "all", ["dataBlockValues"] = "keepActualValues" })) == "ArgumentException"
            && Code(() => SimulationOnlineDownloadRules.Options(new JObject { ["softwareScope"] = "hardware", ["dataBlockValues"] = "keepActualValues", ["allowStop"] = false })) == "ArgumentException"
            && Code(() => SimulationOnlineDownloadRules.Options(new JObject { ["softwareScope"] = "all", ["dataBlockValues"] = "reinitialize", ["allowStop"] = false })) == "ArgumentException");
        string Select(string type, bool pre, JObject options, bool running, out string refusal) => SimulationOnlineDownloadRules.Selection(type, pre, SimulationOnlineDownloadRules.Options(options), running, out refusal);
        Pass("consistent blocks and alarm texts download consistently", Select("ConsistentBlocksDownload", true, keep, true, out _) == "ConsistentDownload" && Select("AlarmTextLibrariesDownload", true, keep, true, out _) == "ConsistentDownload");
        Pass("online user management is kept", Select("UserManagementDownload", true, reinitialize, true, out _) == "KeepOnlineUserManagementData");
        Pass("keep actual values follows the option", Select("DataBlockReinitializationOrKeepActualValues", true, keep, true, out _) == "KeepActualValues"
            && Select("DataBlockReinitializationOrKeepActualValues", true, reinitialize, true, out _) == "StopPlcAndReinitialize");
        Pass("a forced reinitialization is refused when values must be kept", Select("DataBlockReinitialization", true, keep, true, out var forced) == null && forced == "SimulationDownloadReinitializationRequired"
            && Select("DataBlockReinitialization", true, reinitialize, true, out _) == "StopPlcAndReinitialize");
        Pass("stopping needs allowStop", Select("StopModules", true, keep, true, out var stop) == null && stop == "SimulationDownloadStopRequired" && Select("StopModules", true, reinitialize, true, out _) == "StopAll");
        Pass("a CPU found in RUN is restarted, one found in STOP is not", Select("StartModules", false, reinitialize, true, out _) == "StartModule" && Select("StartModules", false, reinitialize, false, out _) == "NoAction");
        Pass("unreviewed download configurations are refused", Select("OverwriteSystemData", true, reinitialize, true, out var other) == null && other == "UnsupportedDownloadConfiguration"
            && Select("ResetModule", true, reinitialize, true, out _) == null && Select("StartModules", true, reinitialize, true, out _) == null && Select("StopModules", false, reinitialize, true, out _) == null);
        Pass("plain download information is acknowledged", Select("DownloadConfiguration", true, keep, true, out _) == "acknowledgedInformation");
        Pass("online download outcome is not recomputable", !AdmissionReconcileRules.OutcomeRecomputable("simulation_download_online"));
        Pass("cpu parameter, compile and tag table outcomes are recomputable", new[] { "hardware_cpu_parameters", "device_compile", "project_compile", "hmi_compile", "tag_table_create", "tag_table_rename", "tag_table_delete" }
            .All(AdmissionReconcileRules.OutcomeRecomputable));
        Pass("cpu parameters join the configured-hardware preservation rules", HardwareEngineeringRules.Applies("hardware_cpu_parameters"));
        return _count;
    }
}
