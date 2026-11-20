using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Simatic.Simulation.Runtime;
using TiaMcp.Simulation;

namespace TiaMcp.Simulator.V8
{
    // sim_prune: opt-in retention of storage this adapter created. Listing is the default; nothing is deleted without delete:true and an
    // explicit choice. The file rules live in SimulationStoragePrune; this part supplies the live instances and the creation receipts.
    internal sealed partial class SimulatorSession
    {
        private const int PruneDeleteLimit = 200;

        private JObject Prune(JObject args)
        {
            string scope = RequiredString(args, "scope", 16);
            if (scope != "storage" && scope != "archives") throw new SimulatorException("InvalidRequest", "scope must be storage or archives.");
            if (Given(args, "delete") && args["delete"].Type != JTokenType.Boolean) throw new SimulatorException("InvalidRequest", "delete must be a boolean.");
            bool delete = args.Value<bool?>("delete") ?? false;
            List<string> names = null;
            if (Given(args, "names"))
            {
                var array = args["names"] as JArray;
                if (array == null || array.Count < 1 || array.Count > PruneDeleteLimit || array.Any(n => n.Type != JTokenType.String))
                    throw new SimulatorException("InvalidRequest", "names must be 1-" + PruneDeleteLimit + " strings.");
                names = array.Select(n => n.Value<string>()).Distinct(StringComparer.Ordinal).ToList();
            }
            int? days = Given(args, "olderThanDays") ? OptionalInteger(args, "olderThanDays", 0, 1, 3650) : (int?)null;
            int? megabytes = Given(args, "maxTotalMegabytes") ? OptionalInteger(args, "maxTotalMegabytes", 0, 0, 1048576) : (int?)null;
            if (names != null && (days != null || megabytes != null)) throw new SimulatorException("InvalidRequest", "Use names, or olderThanDays and maxTotalMegabytes, not both.");
            if (delete && names == null && days == null && megabytes == null)
                throw new SimulatorException("InvalidRequest", "delete needs names, olderThanDays or maxTotalMegabytes; list first without delete.");

            string directory = Path.GetFullPath(RequiredString(args, scope == "storage" ? "artifactDirectory" : "archiveDirectory", 1024));
            SimulationTicket.VerifyPlainDirectory(directory);
            Func<string, bool> live = name => SimulationRuntimeManager.RegisteredInstanceInfo.Any(i => i.Name == name) || _instances.Values.Any(s => s.Name == name);
            var candidates = scope == "storage" ? SimulationStoragePrune.Storage(directory, SimulationTicket.MarkerName, CreationReceipts(directory), live) : SimulationStoragePrune.Archives(directory);
            DateTime? cutoff = days == null ? (DateTime?)null : DateTime.UtcNow.AddDays(-days.Value);
            var selected = Control(() => SimulationStoragePrune.Select(candidates, names, cutoff, megabytes == null ? (long?)null : megabytes.Value * 1048576L));

            var result = Envelope();
            result["scope"] = scope;
            result["directory"] = directory;
            result["delete"] = delete;
            result["candidates"] = new JArray(candidates.Select(c => new JObject { ["name"] = c.Name, ["kind"] = c.Kind, ["bytes"] = c.Bytes,
                ["writtenUtc"] = c.WrittenUtc.ToString("O"), ["eligible"] = c.Refusal == null, ["refusal"] = c.Refusal, ["selected"] = selected.Contains(c) }));
            result["totalBytes"] = candidates.Sum(c => c.Bytes);
            result["selectedCount"] = selected.Count;
            result["selectedBytes"] = selected.Sum(c => c.Bytes);
            if (delete)
            {
                var deleted = new JArray(); var refused = new JArray();
                foreach (var candidate in selected.Take(PruneDeleteLimit))
                {
                    string refusal;
                    try { refusal = SimulationStoragePrune.Delete(candidate, live); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { refusal = "deleteFailed: " + ex.Message; }
                    if (refusal == null) deleted.Add(new JObject { ["name"] = candidate.Name, ["bytes"] = candidate.Bytes });
                    else refused.Add(new JObject { ["name"] = candidate.Name, ["refusal"] = refusal });
                }
                result["deleted"] = deleted;
                result["refused"] = refused;
            }
            result["note"] = "Only storage of an instance whose creation receipt says it was unregistered (and which is neither registered nor selected), and archives with an unchanged receipt written by sim_storage, are ever deleted. Nothing else in these folders is touched.";
            return result;
        }

        // The signed creation receipts under the simulation folder; an unreadable or foreign one is skipped, so it can only make a folder ineligible.
        private static List<JObject> CreationReceipts(string directory)
        {
            var receipts = new List<JObject>();
            if (!Directory.Exists(directory)) return receipts;
            foreach (var file in new DirectoryInfo(directory).GetFiles("creation-*.json").Take(2000))
            {
                string id = file.Name.Substring("creation-".Length, file.Name.Length - "creation-".Length - ".json".Length);
                try { receipts.Add(SimulationCreationJournal.Read(directory, id)); } catch (Exception) { }
            }
            return receipts;
        }
    }
}
