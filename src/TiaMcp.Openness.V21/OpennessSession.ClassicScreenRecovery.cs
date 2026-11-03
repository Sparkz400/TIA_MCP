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
        // Called once, inside the original apply's exclusive lease. Never replays the candidate.
        // Restore only the screen we imported, and only while every peer still equals the pre-import capture.
        private void RecoverFailedClassicScreen(EngineeringChange change, Project project)
        {
            if (change.Kind != "hmi_classic_screen_edit" || change.Payload.Value<string>("operation") != "classic_xml_import") return;
            try
            {
                InvalidateHmiObservations();
                var baseline = change.HmiEvidence?["preImport"] as JObject ?? change.HmiBeforeManifest;
                RequireHmiScope(change, baseline);
                var current = CaptureHmiEngineeringScope(change, project, "failure");
                RequireHmiScope(change, current);
                var touched = change.HmiScreenMembers.Where(m => m.HmiScreenTouched).ToArray();
                if (touched.Length == 0)
                {
                    ClassicScreenRecoveryRules.RequireRestoredScope(baseline, current);
                    change.HmiEvidence["failureUnchanged"] = current;
                    change.AfterFingerprint = ClassicScreenCapturedFingerprint(change, project, current);
                    change.Stages.Add("hmi_failure_before_import_verified");
                    change.State = "not_applied";
                    return;
                }
                HmiPreservationRules.RequireUnrelatedPreserved(ClassicScreenRecoveryRules.RequireManifest(baseline),
                    ClassicScreenRecoveryRules.RequireManifest(current), touched.Select(m => m.Payload["target"].Value<string>("objectPath")), Array.Empty<string>());
                // A missing/corrupt checkpoint or backup is never replaced with reconstructed XML.
                RequireScreenRecoveryFile(change.Directory, new JObject { ["path"] = change.ArchivePath, ["sha256"] = change.ArchiveHash });
                change.Stages.Add("hmi_rollback_started");
                WriteChangeJournal(change);
                // Check every backup before restoring any member. Restore only attempted writes,
                // in reverse order; never replay candidates after an exception or lost response.
                foreach (var member in touched)
                    RequireScreenRecoveryFile(change.Directory, baseline["artifacts"]?[member.Payload["target"].Value<string>("objectPath")] as JObject);
                foreach (var member in touched.Reverse())
                {
                    var identity = (JObject)member.Payload["target"];
                    string before = RequireScreenRecoveryFile(change.Directory, baseline["artifacts"]?[identity.Value<string>("objectPath")] as JObject);
                    InvalidateHmiObservations();
                    var target = ResolveHmiTarget(project, identity);
                    var native = HmiEngineeringObject(project, identity).Native as IEngineeringObject
                        ?? throw new BridgeException("RecoveryRequired", "An original screen identity is no longer available.");
                    RequireClassicImportUnlinked(native);
                    var restored = ClassicXmlImportRoute(target.Software, native)(new FileInfo(before))
                        ?? throw new BridgeException("RecoveryRequired", "Rollback did not restore an original screen identity.");
                    string readback = Path.Combine(member.Directory, "hmi-rollback-screen.xml");
                    ClassicObjectExport(restored)(new FileInfo(readback));
                    EngineeringSources.RequireNativePreservation(HmiPreservationRules.ReadXml(File.ReadAllText(before)), HmiPreservationRules.ReadXml(File.ReadAllText(readback)));
                    change.Readbacks.Add(new JObject { ["role"] = "hmi-rollback-screen", ["artifact"] = ChangeArtifact(readback) });
                    int index = change.HmiScreenMembers.IndexOf(member);
                    change.HmiEvidence["screens"][index]["state"] = "rollback_readback_verified";
                    change.Stages.Add("hmi_screen_" + index + "_rollback_readback_verified");
                    WriteChangeJournal(change);
                }
                change.Stages.Add("hmi_rollback_imported");
                WriteChangeJournal(change);
                var compilation = CompileHmiEngineeringChange(change, project);
                change.HmiEvidence["rollbackCompile"] = compilation;
                if (compilation.Value<int?>("errorCount") != 0 || !ClassicScreenCompileRules.ProblemsUnchanged(change.HmiEvidence["baselineCompile"] as JObject, compilation))
                    throw new BridgeException("RecoveryRequired", "Rollback compiler diagnostics do not match the successful baseline.");
                change.Stages.Add("hmi_rollback_compiled");
                var after = CaptureHmiEngineeringScope(change, project, "rollback");
                ClassicScreenRecoveryRules.RequireRestoredScope(baseline, after);
                change.HmiEvidence["rollback"] = after;
                InvalidateHmiObservations();
                project.Save();
                change.Stages.Add("hmi_rollback_saved");
                WriteChangeJournal(change);
                var saved = CaptureHmiEngineeringScope(change, project, "rollback-after-save");
                ClassicScreenRecoveryRules.RequireRestoredScope(baseline, saved);
                change.HmiEvidence["rollback-after-save"] = saved;
                change.Stages.Add("hmi_rollback_verified");
                change.AfterFingerprint = ClassicScreenCapturedFingerprint(change, project, saved);
                change.State = "rolled_back";
            }
            catch (Exception recovery)
            {
                change.RecoveryFailure = "In-place screen recovery could not be verified: " + recovery.Message;
                change.State = "recovery_required";
            }
        }

        private static string RequireScreenRecoveryFile(string directory, JObject artifact)
        {
            string path = artifact?.Value<string>("path"), hash = artifact?.Value<string>("sha256");
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!ProjectPathIsAbsolute(path) || !Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(hash) || !File.Exists(path))
                throw new BridgeException("RecoveryEvidenceInvalid", "A retained recovery artifact is missing, outside its change directory, or changed.");
            try { ClassicScreenRecoveryRules.RequireUnlinkedArtifact(path); }
            catch (ArgumentException error) { throw new BridgeException("RecoveryEvidenceInvalid", error.Message); }
            if (ChangeFileHash(path) != hash) throw new BridgeException("RecoveryEvidenceInvalid", "A retained recovery artifact changed.");
            return path;
        }

        // Only use a capture made under the still-held exclusive lease, with no subsequent native write.
        private static string ClassicScreenCapturedFingerprint(EngineeringChange change, Project project, JObject manifest)
        {
            RequireHmiScope(change, manifest);
            return HmiEngineeringRules.ClassicScreenFingerprint(project.Path.FullName, project.LastModified.ToString("O"), project.IsModified, manifest.Value<string>("contentHash"), false);
        }

        // Read-only reconciliation of a completed save in the currently open project. The terminal
        // journal, native ExclusiveAccess, fresh full screen scope and project save fingerprint replace
        // the old requirement to close the user's TIA process. Nonterminal operations still stay held.
        private JObject ReconcileClassicScreenInOpenProject(JObject args)
        {
            if (args.Value<string>("operation") != "change_apply") return null;
            var resources = args["resources"] as JArray;
            var journalResource = resources?.OfType<JObject>().SingleOrDefault(r => r.Value<string>("effect") == "legacyJournalCheckpointOutput");
            if (journalResource == null) return null;
            string directory = journalResource.Value<string>("path");
            if (!ProjectPathIsAbsolute(directory)) return null;
            string journalPath = Path.Combine(directory, "journal.json");
            if (!File.Exists(journalPath) || new FileInfo(journalPath).Length > 64 * 1024 * 1024) return null;
            var journal = JObject.Parse(File.ReadAllText(journalPath));
            if (journal.Value<string>("kind") != "hmi_classic_screen_edit") return null;
            string projectPath = journal.Value<string>("projectPath");
            var project = _portal.Projects.FirstOrDefault(p => SameSimulationPath(p.Path.FullName, projectPath));
            if (project == null) return null;
            var result = Envelope();
            result["scopeSha256"] = args["scopeSha256"]?.DeepClone();
            result["operation"] = "change_apply";
            result["verificationMode"] = ClassicScreenRecoveryRules.VerificationMode;
            result["changeKind"] = "hmi_classic_screen_edit";
            result["nativeMutationInvoked"] = false;
            result["originalProcessRunning"] = PortalProcesses.Discover().Any(p => p.ProcessId == args.Value<int>("admittedProcessId"));
            var rows = new JArray(resources.OfType<JObject>().Select(r => new JObject { ["kind"] = r["kind"], ["path"] = r["path"], ["effect"] = r["effect"], ["disposition"] = null, ["reasons"] = new JArray() }));
            result["resources"] = rows;
            try
            {
                using (SharedMutationExclusive("TIA MCP: verify completed Classic screen change"))
                {
                    result["nativeExclusiveAccess"] = true;
                    EnsureProjectOffline(project);
                    string journalHash = ChangeFileHash(journalPath);
                    journal = JObject.Parse(File.ReadAllText(journalPath));
                    var manifest = ClassicScreenRecoveryRules.TerminalManifest(journal);
                    if ("change-" + journal.Value<string>("changeId") != Path.GetFileName(directory)
                        || ChangeHash(journal["preview"].ToString(Newtonsoft.Json.Formatting.None)) != journal.Value<string>("previewHash"))
                        throw new BridgeException("RecoveryEvidenceInvalid", "The terminal journal is not bound to its prepared preview.");
                    if (resources.Count != 3 || rows.Count != 3
                        || rows.Count(r => r.Value<string>("kind") == "project" && r.Value<string>("effect") == "checkpointMutationCompileSave" && SameSimulationPath(r.Value<string>("path"), projectPath)) != 1
                        || rows.Count(r => r.Value<string>("kind") == "workspace" && r.Value<string>("effect") == "legacyJournalCheckpointOutput" && SameSimulationPath(r.Value<string>("path"), directory)) != 1)
                        throw new BridgeException("RecoveryEvidenceInvalid", "The admission does not match the exact screen change resources.");
                    var input = rows.OfType<JObject>().Single(r => r.Value<string>("kind") == "workspace" && r.Value<string>("effect") == "reviewedMutationInput");
                    string expectedInput = Path.Combine(directory, "hmi-classic-expected.xml");
                    if (!SameSimulationPath(input.Value<string>("path"), expectedInput)) throw new BridgeException("RecoveryEvidenceInvalid", "The admitted input differs from the screen candidate.");
                    RequireScreenRecoveryFile(directory, new JObject { ["path"] = expectedInput, ["sha256"] = journal["hmiEvidence"]?["proposedEffect"]?["inputSha256"] });
                    if (journal["hmiEvidence"]?["screens"] is JArray members)
                    {
                        var requests = ClassicScreenBatchRules.Members((JObject)journal["preview"]["payload"]);
                        if (members.Count != requests.Length) throw new BridgeException("RecoveryEvidenceInvalid", "The batch receipts do not cover every prepared screen.");
                        for (int i = 0; i < members.Count; i++)
                        {
                            string memberPath = RequireScreenRecoveryFile(directory, members[i]["input"] as JObject);
                            if (!JToken.DeepEquals(members[i]["target"], requests[i]["target"]) || File.ReadAllText(memberPath) != requests[i].Value<string>("xml"))
                                throw new BridgeException("RecoveryEvidenceInvalid", "A batch input differs from the retained prepared request.");
                        }
                    }
                    RequireScreenRecoveryFile(directory, new JObject { ["path"] = journal["recoveryArchive"], ["sha256"] = journal["recoveryArchiveSha256"] });
                    foreach (var artifact in ((JObject)manifest["artifacts"]).Properties()) RequireScreenRecoveryFile(directory, artifact.Value as JObject);
                    result["retainedArtifactsVerified"] = true;
                    var payload = (JObject)journal["preview"]["payload"].DeepClone();
                    // Re-resolve the exact paths in this attachment; a prior attachment's session token is not reusable.
                    ((JObject)payload["target"]).Remove("sessionGeneration");
                    var observed = new EngineeringChange { Kind = "hmi_classic_screen_edit", Directory = Path.Combine(Path.GetDirectoryName(args.Value<string>("retrieveDirectory")), "cr_" + Guid.NewGuid().ToString("N")), Payload = payload };
                    Directory.CreateDirectory(observed.Directory);
                    var current = CaptureHmiEngineeringScope(observed, project, "terminal-reconcile");
                    ClassicScreenRecoveryRules.RequireRestoredScope(manifest, current);
                    string fingerprint = HmiEngineeringRules.ClassicScreenFingerprint(project.Path.FullName, project.LastModified.ToString("O"), project.IsModified, current.Value<string>("contentHash"), false);
                    if (project.IsModified || fingerprint != journal.Value<string>("afterFingerprint") || journalHash != ChangeFileHash(journalPath))
                        throw new BridgeException("StaleRevision", "The open project or terminal journal differs from the recorded saved outcome.");
                    result["terminalState"] = journal["state"].DeepClone();
                    result["terminalJournalUnchanged"] = true;
                    result["savedProjectFingerprintMatches"] = true;
                    result["screenScopeMatches"] = true;
                    result["projectIsModified"] = false;
                    result["scopePolicy"] = HmiPreservationRules.ClassicScreenPolicy;
                    result["manifest"] = current;
                    result["journalSha256"] = journalHash;
                    foreach (JObject row in rows)
                    {
                        row["disposition"] = row.Value<string>("effect") == "reviewedMutationInput" ? "unchanged" : "verifiedApplied";
                        row["basis"] = "Terminal screen journal, retained artifact hashes, exclusive native readback and exact saved project fingerprint verified; no mutation replayed.";
                    }
                }
            }
            catch (Exception error)
            {
                result["verificationFailure"] = error.Message;
                foreach (JObject row in rows) { row["disposition"] = null; ((JArray)row["reasons"]).Add(error.Message); }
            }
            return result;
        }
    }
}
