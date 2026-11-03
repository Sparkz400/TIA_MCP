using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private JObject PrepareClassicScreenBatch(EngineeringChange change, Project project)
        {
            var effects = new JArray();
            var receipts = new JArray();
            foreach (var request in ClassicScreenBatchRules.Members(change.Payload))
            {
                var identity = (JObject)request["target"];
                var discovered = HmiResolveNativeTarget(project, identity);
                var entry = HmiEngineeringObject(project, identity);
                RequireHmiEngineeringGate(HmiEngineeringGate(change, project, discovered.Family, discovered.Profile, entry), change.HmiEvidence);
                var member = new EngineeringChange { Id = change.Id, Kind = change.Kind, Payload = (JObject)request.DeepClone(),
                    Directory = change.HmiScreenMembers.Count == 0 ? change.Directory : Path.Combine(change.Directory, "screen-" + change.HmiScreenMembers.Count), State = "prepared" };
                Directory.CreateDirectory(member.Directory);
                member.Payload["operation"] = "classic_xml_import";
                effects.Add(PrepareClassicXmlImport(member, discovered.Software, (IEngineeringObject)entry.Native));
                change.HmiScreenMembers.Add(member);
                foreach (var backup in member.Backups) change.Backups.Add(backup.DeepClone());
                receipts.Add(new JObject { ["target"] = identity.DeepClone(), ["input"] = ChangeArtifact(member.InputPath), ["state"] = "prepared" });
            }
            change.HmiEvidence["screens"] = receipts;
            change.InputPath = change.HmiScreenMembers[0].InputPath;
            change.InputHash = change.HmiScreenMembers[0].InputHash;
            PreflightClassicScreenBatch(change, project);
            return new JObject { ["operation"] = "classic_xml_import", ["inputSha256"] = change.InputHash, ["screenCount"] = effects.Count, ["screens"] = effects,
                ["checkpointCount"] = 1, ["baselineCompileCount"] = 1, ["finalCompileCount"] = 1,
                ["recoveryPolicy"] = "Restore every attempted screen and verify the full regular-screen scope; unknown outcomes retain ownership." };
        }

        private void ApplyClassicScreenBatch(EngineeringChange change, Project project)
        {
            for (int index = 0; index < change.HmiScreenMembers.Count; index++)
            {
                var member = change.HmiScreenMembers[index];
                var identity = (JObject)member.Payload["target"];
                var target = HmiResolveNativeTarget(project, identity);
                var entry = HmiEngineeringObject(project, identity);
                RequireHmiEngineeringGate(HmiEngineeringGate(change, project, target.Family, target.Profile, entry), change.HmiEvidence);
                ValidatePreparedFiles(member);
                member.HmiScreenTouched = true; // Durable before dispatch, including a native call that throws after writing.
                change.HmiEvidence["screens"][index]["state"] = "import_started";
                change.Stages.Add("hmi_screen_" + index + "_import_started");
                WriteChangeJournal(change);
                InvalidateHmiObservations();
                MeasureChange(change, "import-readback-screen-" + index, () => { ApplyClassicXmlImport(member, target.Software, (IEngineeringObject)entry.Native); return true; });
                foreach (var readback in member.Readbacks) change.Readbacks.Add(readback.DeepClone());
                change.HmiEvidence["screens"][index]["state"] = "readback_verified";
                change.Stages.Add("hmi_screen_" + index + "_readback_verified");
                WriteChangeJournal(change);
            }
        }
    }
}
