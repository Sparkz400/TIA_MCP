#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // contracts/safety-fparameter-allowlist.v1.json: per F-I/O module catalog type, the F-parameters whose write, readback and
    // restore passed natively. scripts/safety_fparameter_allowlist.py derives it from a discovery run; nothing is hard-coded here.
    internal static class FParameterAllowlistRules
    {
        internal const string Schema = "safety-fparameter-allowlist.v1";
        private static readonly ContractDocument Contract = new ContractDocument("F-parameter allowlist");
        // A module's own catalog identity: an order number with firmware, or a GSD item. Racks and rails (no firmware) never key a row.
        private static readonly Regex ModuleIdentity = new Regex(@"^(OrderNumber:.+/V\d|GSD:)", RegexOptions.CultureInvariant);

        // Never written, whatever a module declares or a document lists: F-source addresses (the CPU's central F-source address
        // sets them), F-parameter signatures, SIL/CRC/version and F-I/O DB assignment.
        private static readonly string[] DeniedParts = { "SourceAddress", "Signature", "FIODB", "SIL", "CRC", "Version", "FCheck", "FBlockID" };

        internal static bool Denied(string name) =>
            name == null || !name.StartsWith("Failsafe_", StringComparison.Ordinal) || DeniedParts.Any(part => name.IndexOf(part, StringComparison.Ordinal) >= 0);

        /// <summary>The allowlist key of an F-I/O: its own type identifier, else its direct parent's, when that is a module identity; else null.</summary>
        internal static string ModuleKey(string own, string parent) =>
            new[] { own, parent }.FirstOrDefault(identifier => identifier != null && ModuleIdentity.IsMatch(identifier));

        /// <summary>The strictly validated document; any deviation throws, so a malformed contract refuses every F-parameter write.</summary>
        internal static JObject Parse(string json)
        {
            var document = Contract.Load(json, Schema);
            Contract.Fields(document, "schemaVersion tiaVersion rule modules");
            var modules = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in Contract.Objects(document, "modules"))
            {
                Contract.Fields(module, "moduleTypeIdentifier moduleTypeName parameters");
                string identity = Contract.Text(module, "moduleTypeIdentifier");
                if (!ModuleIdentity.IsMatch(identity)) throw Contract.Invalid("A module type identifier needs an order number with firmware or a GSD identity: " + identity);
                if (!modules.Add(identity)) throw Contract.Invalid("Duplicate module type identifier.");
                Contract.Text(module, "moduleTypeName");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in Contract.Objects(module, "parameters"))
                {
                    string status = Contract.Text(parameter, "status");
                    if (status != "qualified" && status != "pending") throw Contract.Invalid("A parameter status is qualified or pending.");
                    Contract.Fields(parameter, status == "qualified" ? "name status evidenceId" : "name status reason");
                    string name = Contract.Text(parameter, "name");
                    if (Denied(name)) throw Contract.Invalid("A denied or non-F-parameter is listed: " + name);
                    if (!names.Add(name)) throw Contract.Invalid("Duplicate F-parameter " + name + ".");
                    Contract.Text(parameter, status == "qualified" ? "evidenceId" : "reason");
                }
            }
            return document;
        }

        /// <summary>The allowlist row of this parameter on this module type (qualified or pending), or null.</summary>
        internal static JObject Row(JObject document, string moduleTypeIdentifier, string name) =>
            document["modules"].Children<JObject>().Where(module => module.Value<string>("moduleTypeIdentifier") == moduleTypeIdentifier)
                .SelectMany(module => module["parameters"].Children<JObject>()).FirstOrDefault(row => row.Value<string>("name") == name)?.DeepClone() as JObject;

        internal static bool Qualified(JObject row) => row?.Value<string>("status") == "qualified";
    }
}
