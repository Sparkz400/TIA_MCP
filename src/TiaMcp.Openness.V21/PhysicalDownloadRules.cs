#nullable disable
namespace TiaMcp.Openness.V21
{
    // How a real-PLC download answers each question TIA asks. Pure, so every answer is unit-tested. Anything not listed, or
    // anything that needs an acknowledgement the caller did not give, returns null with a refusal code and stops the download.
    internal static class PhysicalDownloadRules
    {
        internal static string Selection(string type, bool allowStop, bool allowReinitialize, bool startAfterDownload, out string refusal)
        {
            refusal = null;
            switch (type)
            {
                case "ConsistentBlocksDownload": return "ConsistentDownload";
                case "AlarmTextLibrariesDownload": return "ConsistentDownload";
                case "UserManagementDownload": return "KeepOnlineUserManagementData";
                case "DataBlockReinitializationOrKeepActualValues":
                    // Keeping actual values never needs a stop; reinitializing does, and needs both acknowledgements.
                    return allowReinitialize && allowStop ? "StopPlcAndReinitialize" : "KeepActualValues";
                case "DataBlockReinitialization":
                    if (allowReinitialize && allowStop) return "StopPlcAndReinitialize";
                    refusal = "ReinitializeNotAcknowledged"; return null;
                case "StopModules":
                    if (allowStop) return "StopAll";
                    refusal = "StopNotAcknowledged"; return null;
                case "StartModules": return startAfterDownload ? "StartModule" : "NoAction";
                case "PlcMasterSecretPassword":
                    refusal = "PhysicalAuthenticationRequired"; return null;
                default:
                    refusal = "UnsupportedDownloadConfiguration"; return null;
            }
        }

        internal static string Remedy(string refusal)
        {
            switch (refusal)
            {
                case "StopNotAcknowledged": return "the PLC must stop for this download. Repeat with acknowledgeStop:true only if stopping it is safe.";
                case "ReinitializeNotAcknowledged": return "data blocks must be reinitialized, which resets their actual values and stops the PLC. Repeat with acknowledgeReinitialize:true and acknowledgeStop:true only if that is safe.";
                case "PhysicalAuthenticationRequired": return "the PLC's configuration is protected by a password, which this route never supplies. Download from the TIA Portal.";
                default: return "this route does not answer it. Download from the TIA Portal.";
            }
        }
    }
}
