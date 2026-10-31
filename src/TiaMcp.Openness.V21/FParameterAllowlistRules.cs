#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // contracts/safety-fparameter-allowlist.v1.json: per F-I/O module catalog type, the F-parameters whose write, readback and
    // restore passed natively. scripts/safety_fparameter_allowlist.py derives it from a discovery run; nothing is hard-coded here.
    internal static class FParameterAllowlistRules
    {
        internal const string Schema = "safety-fparameter-allowlist.v1";
        private static readonly ContractDocument Contract = new ContractDocument("F-parameter allowlist");

        // Never written, whatever a module declares or a document lists: F-source addresses (the CPU's central F-source address
        // sets them), F-parameter signatures, SIL/CRC/version and F-I/O DB assignment.
        private static readonly string[] DeniedParts = { "SourceAddress", "Signature", "FIODB", "SIL", "CRC", "Version", "FCheck", "FBlockID" };

        internal static bool Denied(string name) =>
            name == null || !name.StartsWith("Failsafe_", StringComparison.Ordinal) || DeniedParts.Any(part => name.IndexOf(part, StringComparison.Ordinal) >= 0);

        /// <summary>The strictly validated document; any deviation throws, so a malformed contract refuses every F-parameter write.</summary>
        internal static JObject Parse(string json)
        {
            var document = Contract.Load(json, Schema);
            Contract.Fields(document, "schemaVersion tiaVersion rule modules");
            var modules = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in Contract.Objects(document, "modules"))
            {
                Contract.Fields(module, "moduleTypeIdentifier moduleTypeName parameters");
                if (!modules.Add(Contract.Text(module, "moduleTypeIdentifier"))) throw Contract.Invalid("Duplicate module type identifier.");
                Contract.Text(module, "moduleTypeName");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in Contract.Objects(module, "parameters"))
                {
                    Contract.Fields(parameter, "name evidenceId");
                    string name = Contract.Text(parameter, "name");
                    if (Denied(name)) throw Contract.Invalid("A denied or non-F-parameter is listed: " + name);
                    if (!names.Add(name)) throw Contract.Invalid("Duplicate F-parameter " + name + ".");
                    Contract.Text(parameter, "evidenceId");
                }
            }
            return document;
        }

        /// <summary>The allowlist row (name, evidenceId) of this parameter on this module type, or null.</summary>
        internal static JObject Qualification(JObject document, string moduleTypeIdentifier, string name) =>
            document["modules"].Children<JObject>().Where(module => module.Value<string>("moduleTypeIdentifier") == moduleTypeIdentifier)
                .SelectMany(module => module["parameters"].Children<JObject>()).FirstOrDefault(row => row.Value<string>("name") == name)?.DeepClone() as JObject;
    }
}
