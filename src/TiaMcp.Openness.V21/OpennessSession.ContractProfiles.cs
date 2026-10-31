#nullable disable
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;

namespace TiaMcp.Openness.V21
{
    // Recorded native profiles shipped as contracts/ documents embedded in this worker.
    internal sealed partial class OpennessSession
    {
        private static readonly Lazy<JObject> TechnologyVersionProfiles = new Lazy<JObject>(() => TechnologyVersionProfileRules.Parse(EmbeddedContract("technology-version-profiles.v1.json")));
        private static readonly Lazy<JObject> FParameterAllowlist = new Lazy<JObject>(() => FParameterAllowlistRules.Parse(EmbeddedContract("safety-fparameter-allowlist.v1.json")));

        private static string EmbeddedContract(string name)
        {
            using (var stream = typeof(OpennessSession).Assembly.GetManifestResourceStream("TiaMcp.Contracts." + name)
                ?? throw new BridgeException("ContractUnavailable", "The worker build lacks the embedded contract " + name + "."))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
                return reader.ReadToEnd();
        }

        // to_create's profile gate, at prepare and again right before the native create.
        private static JObject AdmitTechnologyVersion(PlcSelection plc, JObject payload)
        {
            var flag = payload["unprofiledAcknowledged"];
            if (flag != null && flag.Type != JTokenType.Boolean) throw new BridgeException("InvalidRequest", "unprofiledAcknowledged must be a boolean.");
            var decision = TechnologyVersionProfileRules.Admit(TechnologyVersionProfiles.Value, SoftwareCpuTypeIdentifier(plc.Software),
                payload.Value<string>("typeIdentifier"), payload.Value<string>("version"), flag?.Value<bool>() == true);
            if (decision.Value<bool>("admitted")) return (JObject)decision["profile"];
            throw new BridgeException(decision.Value<string>("code"), decision.Value<string>("message"), (JObject)decision["details"]);
        }

        // The CPU DeviceItem owning the software, as WebServerCpu walks it; its TypeIdentifier carries order number and firmware.
        private static string SoftwareCpuTypeIdentifier(PlcSoftware software)
        {
            for (IEngineeringInstance owner = software.Parent; owner != null && !(owner is Device); owner = owner.Parent)
                if (owner is DeviceItem item && !string.IsNullOrEmpty(item.TypeIdentifier)) return item.TypeIdentifier;
            throw new BridgeException("UnsupportedCapability", "The selected PLC's CPU type identifier cannot be resolved; no technology version profile applies.");
        }
    }
}
