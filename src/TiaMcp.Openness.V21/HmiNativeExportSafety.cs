using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Temporary containment of the V21 HmiOpenLinkService InvalidCastException
    // observed on 3 November 2026. A list name is not a safe failure discriminator.
    internal static class HmiNativeExportSafety
    {
        internal const string ClassicGraphicList = "Siemens.Engineering.Hmi.TextGraphicList.GraphicList";

        internal static void RequireAllowed(string nativeType, string objectPath)
        {
            if (nativeType != ClassicGraphicList) return;
            throw new BridgeException("HmiNativeExportBlocked",
                "Classic graphic-list export is temporarily blocked after a native HmiOpenLink conversion failure terminated TIA Portal. " +
                "No graphic-list export was dispatched. Preservation requiring this content cannot authorize a change; an isolated native qualification is required before reopening this route.",
                new JObject { ["nativeType"] = nativeType, ["objectPath"] = objectPath, ["operation"] = "Export",
                    ["nativeOutcome"] = "notDispatched", ["automaticRetryAllowed"] = false });
        }
    }
}
