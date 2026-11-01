#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // contracts/technology-version-profiles.v1.json: the create-and-refuse outcome V21 recorded per CPU type identifier,
    // technology type and version. V21 exposes no CPU-specific type/version catalog, so these cells are the only source.
    internal static class TechnologyVersionProfileRules
    {
        internal const string Schema = "technology-version-profiles.v1";
        private static readonly ContractDocument Contract = new ContractDocument("Technology version profiles");

        /// <summary>The strictly validated document; any deviation throws, so a malformed contract refuses every creation.</summary>
        internal static JObject Parse(string json)
        {
            var document = Contract.Load(json, Schema);
            Contract.Fields(document, "schemaVersion tiaVersion rule cpus");
            var cpus = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cpu in Contract.Objects(document, "cpus"))
            {
                Contract.Fields(cpu, "cpuTypeIdentifier cpuTypeName cells");
                if (!cpus.Add(Contract.Text(cpu, "cpuTypeIdentifier"))) throw Contract.Invalid("Duplicate CPU type identifier.");
                Contract.Text(cpu, "cpuTypeName");
                var cells = new HashSet<string>(StringComparer.Ordinal);
                foreach (var cell in Contract.Objects(cpu, "cells"))
                {
                    string outcome = Contract.Text(cell, "outcome");
                    if (outcome != "accepted" && outcome != "refused") throw Contract.Invalid("A cell outcome is accepted or refused.");
                    Contract.Fields(cell, "typeIdentifier version outcome evidenceId" + (outcome == "refused" ? " nativeRefusal" : ""));
                    if (!cells.Add(Contract.Text(cell, "typeIdentifier") + "\n" + DottedVersion(Contract.Text(cell, "version")))) throw Contract.Invalid("Duplicate type/version cell.");
                    Contract.Text(cell, "evidenceId");
                    if (outcome == "refused")
                    {
                        var refusal = cell["nativeRefusal"] as JObject;
                        Contract.Fields(refusal, "code message");
                        Contract.Text(refusal, "code"); Contract.Text(refusal, "message");
                    }
                }
            }
            return document;
        }

        /// <summary>{admitted, profile} for an accepted cell or an acknowledged unprofiled pair, otherwise {admitted false, code, message, details}.</summary>
        internal static JObject Admit(JObject document, string cpuTypeIdentifier, string typeIdentifier, string version, bool unprofiledAcknowledged)
        {
            // to_create preparation has already parsed the requested version.
            var requested = Version.TryParse(version, out var parsed) ? parsed : throw Contract.Invalid("The requested version is not a version: " + version);
            var cpu = document["cpus"].Children<JObject>().FirstOrDefault(row => row.Value<string>("cpuTypeIdentifier") == cpuTypeIdentifier);
            var cells = cpu?["cells"].Children<JObject>().Where(row => row.Value<string>("typeIdentifier") == typeIdentifier).ToArray() ?? new JObject[0];
            var cell = cells.FirstOrDefault(row => DottedVersion(row.Value<string>("version")) == requested);
            var accepted = new JArray(cells.Where(row => row.Value<string>("outcome") == "accepted").Select(row => row["version"].DeepClone()));
            var profile = new JObject { ["schemaVersion"] = Schema, ["cpuTypeIdentifier"] = cpuTypeIdentifier, ["typeIdentifier"] = typeIdentifier,
                ["version"] = version, ["cpuProfiled"] = cpu != null, ["acceptedVersions"] = accepted };
            string pair = typeIdentifier + " " + version + " on " + cpuTypeIdentifier;
            if (cell?.Value<string>("outcome") == "accepted")
            {
                profile["outcome"] = "accepted"; profile["evidenceId"] = cell["evidenceId"].DeepClone();
                return new JObject { ["admitted"] = true, ["profile"] = profile };
            }
            if (cell != null)
            {
                profile["outcome"] = "refused"; profile["evidenceId"] = cell["evidenceId"].DeepClone(); profile["nativeRefusal"] = cell["nativeRefusal"].DeepClone();
                return new JObject { ["admitted"] = false, ["code"] = "TechnologyVersionRefused", ["details"] = profile,
                    ["message"] = "V21 refused creating " + pair + " (" + cell.Value<string>("evidenceId") + "): " + cell["nativeRefusal"].Value<string>("message") + " Nothing was prepared." };
            }
            profile["outcome"] = "unprofiled";
            if (unprofiledAcknowledged) return new JObject { ["admitted"] = true, ["profile"] = profile };
            return new JObject { ["admitted"] = false, ["code"] = "TechnologyVersionUnprofiled", ["details"] = profile,
                ["message"] = "No recorded technology version profile covers " + pair + (accepted.Count == 0 ? "." : "; recorded accepted versions: " + string.Join(", ", accepted.Select(v => (string)v)) + ".") +
                    " Set unprofiledAcknowledged true to let Siemens validate the pair at creation; a native refusal then requires checkpoint recovery." };
        }

        // The Motion Control families scripts/probe_technology_versions.py creates (S7-1500 and S7-1200 lines).
        internal static readonly string[] MotionTypes = { "TO_PositioningAxis", "TO_SpeedAxis", "TO_SynchronousAxis", "TO_ExternalEncoder", "TO_MeasuringInput", "TO_OutputCam", "TO_Cam", "TO_CommandTable" };

        /// <summary>True when creating typeIdentifier makes it the PLC's first motion object, given the types of every technology object
        /// the PLC holds. That create adds the know-how protected MC_Servo and MC_Interpolator OBs, which stay inconsistent until the PLC
        /// compiles (local.technology.version_profiles.20261031); a later one, after a compiled motion object, does not
        /// (local.technology.motion_connections.20261030 created its encoder and measuring input without compile).</summary>
        internal static bool FirstMotionObject(string typeIdentifier, IEnumerable<string> existingTypes) =>
            MotionTypes.Contains(typeIdentifier, StringComparer.Ordinal) && !existingTypes.Any(type => MotionTypes.Contains(type, StringComparer.Ordinal));

        // Version equality, as the native Create(name, type, Version) receives it: "10.0" and "10.00" are one version, "10.0.0" another.
        private static Version DottedVersion(string text) =>
            text != null && Regex.IsMatch(text, @"^\d{1,4}(\.\d{1,4}){1,3}$") && Version.TryParse(text, out var version) ? version : throw Contract.Invalid("version must be dotted numeric: " + text);
    }
}
