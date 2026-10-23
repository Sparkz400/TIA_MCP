using Newtonsoft.Json.Linq;

namespace TiaMcp.Simulator.V8
{
    /// <summary>Pure result classification; does not load Siemens APIs or accept credentials.</summary>
    internal static class SimulationPowerOnFailure
    {
        internal static SimulatorException Create(string runtimeCode, JObject instance)
        {
            if (runtimeCode != "WarningPasswordForProtectionError")
                return new SimulatorException("SimulationPowerOnRejected", "Power-on did not return a normal licensed result: " + runtimeCode,
                    new JObject { ["instance"] = instance });

            return new SimulatorException("SimulationConfigurationPasswordRequired",
                "The selected simulated CPU requires its confidential configuration protection password. This is separate from project/CPU user authentication and licensing.",
                new JObject
                {
                    ["instance"] = instance,
                    ["runtimeCode"] = runtimeCode,
                    ["credentialRequest"] = new JObject
                    {
                        ["kind"] = "cpuConfigurationProtectionPassword",
                        ["askAgentToPrompt"] = true,
                        ["target"] = new JObject { ["instanceId"] = instance["id"]?.DeepClone(), ["instanceName"] = instance["name"]?.DeepClone() },
                        ["prompt"] = "Ask the user for the confidential CPU configuration protection password for this identified target. Do not assume it matches a project or CPU account password.",
                        ["separateUserAuthentication"] = new JObject
                        {
                            ["when"] = "Only if project or CPU user authentication is separately required.",
                            ["kind"] = "usernameAndPassword",
                            ["prompt"] = "Ask the user for both the username and password for that identified target.",
                            ["assumeSharedOrDefaultCredentials"] = false
                        },
                        ["credentialSubmissionImplemented"] = false,
                        ["retainPlaintext"] = false,
                        ["handling"] = "Use protected credential input when a supported submission route is available. Do not include supplied credentials in MCP arguments, logs, reports, or retained plaintext."
                    }
                });
        }
    }
}
