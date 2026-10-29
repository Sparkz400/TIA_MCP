using System;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure answers for an online software download to an owned PLCSIM instance. Every download configuration TIA raises is
    // answered from the reviewed options or refused; nothing outside this table is acknowledged.
    internal static class SimulationOnlineDownloadRules
    {
        internal static JObject Options(JObject payload)
        {
            string scope = payload?.Value<string>("softwareScope"), values = payload?.Value<string>("dataBlockValues");
            if (payload?["softwareScope"]?.Type != JTokenType.String || scope != "all" && scope != "changes")
                throw new ArgumentException("softwareScope must be all (DownloadOptions.Software) or changes (DownloadOptions.SoftwareOnlyChanges).");
            if (payload["dataBlockValues"]?.Type != JTokenType.String || values != "keepActualValues" && values != "reinitialize")
                throw new ArgumentException("dataBlockValues must be keepActualValues or reinitialize.");
            if (payload["allowStop"]?.Type != JTokenType.Boolean) throw new ArgumentException("allowStop must be an explicit boolean.");
            if (values == "reinitialize" && payload.Value<bool>("allowStop") != true)
                throw new ArgumentException("Reinitializing data blocks stops the CPU; set allowStop to true.");
            return new JObject { ["softwareScope"] = scope, ["dataBlockValues"] = values, ["allowStop"] = payload.Value<bool>("allowStop") };
        }

        /// <summary>The selection for one configuration, or null with the refusal code. PlcMasterSecretPassword is answered by the caller.</summary>
        internal static string Selection(string type, bool preDownload, JObject options, bool wasRunning, out string refusal)
        {
            refusal = null;
            bool keep = options.Value<string>("dataBlockValues") == "keepActualValues", stop = options.Value<bool>("allowStop");
            if (type == "DownloadConfiguration") return "acknowledgedInformation";
            if (!preDownload)
            {
                // Restore the state found before the download; never start a CPU that was in STOP.
                if (type == "StartModules") return wasRunning ? "StartModule" : "NoAction";
                refusal = "UnsupportedDownloadConfiguration"; return null;
            }
            switch (type)
            {
                case "ConsistentBlocksDownload": return "ConsistentDownload";
                case "AlarmTextLibrariesDownload": return "ConsistentDownload";
                // The runtime-user login depends on the online user data, so it is always kept.
                case "UserManagementDownload": return "KeepOnlineUserManagementData";
                case "DataBlockReinitializationOrKeepActualValues": return keep ? "KeepActualValues" : "StopPlcAndReinitialize";
                case "DataBlockReinitialization":
                    if (keep) refusal = "SimulationDownloadReinitializationRequired";
                    return keep ? null : "StopPlcAndReinitialize";
                case "StopModules":
                    if (!stop) refusal = "SimulationDownloadStopRequired";
                    return stop ? "StopAll" : null;
                default: refusal = "UnsupportedDownloadConfiguration"; return null;
            }
        }
    }
}
