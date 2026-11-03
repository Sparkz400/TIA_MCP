using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static T MeasureChange<T>(EngineeringChange change, string phase, Func<T> body)
        {
            var entry = new JObject { ["phase"] = phase, ["startedAtUtc"] = DateTime.UtcNow.ToString("O"), ["state"] = "running" };
            change.PhaseTimings.Add(entry);
            SaveChangeProgress(change);
            var clock = Stopwatch.StartNew();
            bool failed = false;
            try { T value = body(); entry["state"] = "completed"; return value; }
            catch { failed = true; entry["state"] = "failed"; throw; }
            finally
            {
                entry["elapsedMilliseconds"] = clock.ElapsedMilliseconds; entry["finishedAtUtc"] = DateTime.UtcNow.ToString("O");
                try { SaveChangeProgress(change); }
                catch (Exception error) when (failed) { entry["progressWriteFailure"] = error.Message; }
            }
        }
        private static void SaveChangeProgress(EngineeringChange change)
        {
            string path = Path.Combine(change.Directory, "progress.json"), temporary = path + ".new";
            File.WriteAllText(temporary, new JObject { ["changeId"] = change.Id, ["state"] = change.State,
                ["phases"] = change.PhaseTimings.DeepClone(), ["automaticRetryAllowed"] = false }.ToString());
            if (File.Exists(path)) TiaMcp.Core.JournalFileReplacement.Replace(temporary, path); else File.Move(temporary, path);
        }
    }
}
