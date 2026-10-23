using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Deliberately self-contained: even a failed negative check restores the metadata.
        // The shared mutation journal and retained checkpoint account for a lost worker reply.
        private JObject WatchAuthoringSourceNegative(JObject args)
        {
            EnsureAttached();
            var binding = args["sourceBinding"] as JObject;
            if (!_componentQualificationEnabled || binding == null || binding.Count != 7 ||
                binding.Value<string>("profile") != "v21-direct-instance-declarations-1")
                throw new BridgeException("InvalidFixture", "Qualification and the complete exact source binding are required.");
            var selector = (JObject)binding.DeepClone(); selector["sourceBinding"] = binding.DeepClone();
            selector["tableName"] = "WT05_BoolCreation"; selector["group"] = "root"; selector["allowAbsent"] = true;
            selector["artifactDirectory"] = ArtifactDirectory(args);
            var selected = SelectPlc(selector); EnsureProjectOffline(selected.Project);
            if (!_ownsPortal || _portal.Projects.Count != 1 || !WatchOwnsScratch(selected.Project) || selected.Project.IsModified ||
                selected.Project.Path.FullName != binding.Value<string>("projectPath") || selected.Path != binding.Value<string>("plcPath") ||
                binding.Value<string>("blockPath") != selected.Path + "/blocks/TiaMcp_EnableGate" ||
                binding.Value<string>("instanceDbPath") != selected.Path + "/blocks/TiaMcp_EnableGate_DB")
                throw new BridgeException("ProjectIsolationRequired", "Only the saved exact newly owned root EnableGate fixture is admitted.");
            var block = selected.Software.BlockGroup.Blocks.Find("TiaMcp_EnableGate") as FB;
            if (block == null || block.IsKnowHowProtected || !string.IsNullOrEmpty(block.Namespace))
                throw new BridgeException("InvalidFixture", "Exact exportable root FB required.");
            string directory = Path.Combine(ArtifactDirectory(args), "negative-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); selector["artifactDirectory"] = directory;
            string marker = Path.Combine(ArtifactDirectory(args), "negative-intent-" + ChangeHash(selected.Project.Path.FullName) + ".json");
            using (SharedMutationExclusive("TIA MCP: owned watch stale-source negative"))
            {
                if (File.Exists(marker)) throw new BridgeException("RecoveryRequired", "This negative fixture mutation was already attempted; never replay.");
                VerifyWatchSource(selector);
                var before = WatchSnapshot(selector);
                if (before["target"].Value<bool?>("exists") != false)
                    throw new BridgeException("InvalidFixture", "Negative trial starts with an independently absent creation destination.");
                var result = new JObject { ["before"] = before, ["sourceBinding"] = binding.DeepClone(),
                    ["requestSha256"] = SharedMutationHash(args),
                    ["nativeWatchImportInvoked"] = false, ["automaticReplay"] = false };
                // Retain the complete pre-effect baseline before any property read or archive
                // can fail. This marker is immutable; the eventual receipt adds checkpoint/delta.
                using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(file)) { writer.Write(result.ToString(Formatting.Indented)); writer.Flush(); file.Flush(true); }
                string originalAuthor;
                try
                {
                    // Installed V21 PlcBlock exposes typed HeaderAuthor get/set; there is no
                    // dynamic attribute named Author. The XML leaf is also HeaderAuthor.
                    originalAuthor = block.HeaderAuthor;
                    if (originalAuthor == null || originalAuthor == "WT05_STALE_SOURCE")
                        throw new BridgeException("InvalidFixture", "A distinct readable HeaderAuthor baseline is required.");
                    result["originalAuthor"] = originalAuthor; result["temporaryAuthor"] = "WT05_STALE_SOURCE";
                    string archive = Path.Combine(directory, "before-source-negative.zap21");
                    selected.Project.Archive(new DirectoryInfo(directory), Path.GetFileName(archive), ProjectArchivationMode.Compressed);
                    result["checkpoint"] = FileArtifact(new FileInfo(archive));
                }
                catch (Exception error)
                {
                    result["failure"] = error.Message; result["nativeSourceWriteAttempted"] = false;
                    File.WriteAllText(Path.Combine(directory, "result.json"), result.ToString(Formatting.Indented));
                    throw new BridgeException("WatchSourceNegativeFailed", "Pre-effect source negative failed; retain baseline and reconcile without replay.", result);
                }
                Exception failure = null;
                try
                {
                    result["nativeSourceWriteAttempted"] = true;
                    block.HeaderAuthor = "WT05_STALE_SOURCE";
                    selected.Project.Save();
                    result["changed"] = WatchSnapshot(selector);
                    var changed = CaptureWatchInstance(selector); result["changedCapture"] = changed;
                    if (changed["binding"].Value<string>("blockSha256") == binding.Value<string>("blockSha256"))
                        throw new BridgeException("VerificationFailed", "Saved temporary HeaderAuthor did not change the captured FB export.");
                    try { VerifyWatchSource(selector); throw new BridgeException("VerificationFailed", "Stale source was accepted."); }
                    catch (BridgeException error) when (error.Code == "StaleSource")
                    { result["refusal"] = new JObject { ["code"] = error.Code, ["message"] = error.Message, ["guard"] = "VerifyWatchSource", ["checkpointOrImportAfterChange"] = false }; }
                }
                catch (Exception error) { failure = error; result["failure"] = error.Message; }
                finally
                {
                    try
                    {
                        block.HeaderAuthor = originalAuthor; selected.Project.Save();
                        result["restored"] = WatchSnapshot(selector); result["restoredCapture"] = CaptureWatchInstance(selector);
                        VerifyWatchSource(selector);
                        if (!JToken.DeepEquals(before["peers"], result["restored"]["peers"]) ||
                            !JToken.DeepEquals(before["target"]["semanticSha256"], result["restored"]["target"]["semanticSha256"]))
                            throw new BridgeException("VerificationFailed", "Restored complete native preservation snapshot differs.");
                        result["restorationPassed"] = true;
                        _watchOwnedScratchPaths.Add(selected.Project.Path.FullName);
                    }
                    catch (Exception error) { result["restorationFailure"] = error.Message; if (failure == null) failure = error; }
                    File.WriteAllText(Path.Combine(directory, "result.json"), result.ToString(Formatting.Indented));
                }
                if (failure != null) throw new BridgeException("WatchSourceNegativeFailed", "Negative trial failed; retain checkpoint and reconcile the owned mutation.", result);
                result["receipt"] = FileArtifact(new FileInfo(Path.Combine(directory, "result.json"))); return result;
            }
        }
    }
}
