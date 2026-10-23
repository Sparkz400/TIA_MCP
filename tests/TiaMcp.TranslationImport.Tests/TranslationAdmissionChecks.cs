using TiaMcp.Core;
using TiaMcp.Translations;
using static TiaMcp.Translations.TranslationJson;

internal static class TranslationAdmissionChecks
{
    internal static void Register(Action<string, Func<Task>> test, Func<Fixture> fixture)
    {
        static void Refuse(string code, Action action)
        { try { action(); } catch (BridgeException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
        static (TranslationAdmissionKey Key, TranslationAdmissionProfile Profile) Key(Fixture f)
        {
            var op = f.Prepare();
            var candidate = new TranslationWorkflowStore(f.Root).NativeCandidate(f.CandidateRef, f.CandidateReceipt);
            var context = new TranslationImportContext(f.Root, op, f.CandidateRef, f.CandidateReceipt, candidate.Candidate.WorkbookSha256, f.Scope);
            var key = TranslationProductionAdmission.Describe(context, new(new('1', 64), new('2', 64), f.Target.AdapterSha256, new('3', 64)));
            string package = new('4', 64);
            var proofs = TranslationProductionAdmission.NativeCases.Select(name => new TranslationAdmissionProof(name, HashJson(key), package, new('5', 64), "native"))
                .Concat(TranslationProductionAdmission.PortableCases.Select(name => new TranslationAdmissionProof(name, HashJson(key), package, new('6', 64), "portable"))).ToArray();
            return (key, new("synthetic-checker-only-never-registered", key, package, proofs));
        }
        test("exact admission descriptor binds every row, original peer and concrete binary", () => {
            var f = fixture(); var (key, profile) = Key(f);
            if (key.Rows.Length != 2 || key.Peers.Length != 2 || key.Rows.Count(row => row.OriginalTargetStateSha256 != row.CandidateTargetStateSha256) != 1)
                throw new Exception("Incomplete exact key");
            TranslationProductionAdmission.RequireComplete(profile, key);
            if (TranslationProductionImportAdapter.QualifiedProfiles.Length != 0) throw new Exception("Synthetic proof enabled production");
            return Task.CompletedTask;
        });
        var changes = new Dictionary<string, Func<TranslationAdmissionKey, TranslationAdmissionKey>> {
            ["host and dependencies"] = key => key with { Binaries = key.Binaries with { HostPackageSha256 = new('a', 64) } },
            ["translation assembly"] = key => key with { Binaries = key.Binaries with { TranslationAssemblySha256 = new('a', 64) } },
            ["worker"] = key => key with { Binaries = key.Binaries with { WorkerSha256 = new('a', 64) } },
            ["installed API"] = key => key with { Binaries = key.Binaries with { SiemensApiSha256 = new('a', 64) } },
            ["project"] = key => key with { ProjectResourceKey = "other" },
            ["TIA version"] = key => key with { TiaVersion = 20 },
            ["hardware"] = key => key with { HardwareIdentity = "other" },
            ["native identity"] = key => key with { NativeIdentity = "other" },
            ["layout"] = key => key with { ProfileSha256 = new('a', 64) },
            ["source culture"] = key => key with { Scope = key.Scope with { SourceCulture = "fr-FR" } },
            ["target culture"] = key => key with { Scope = key.Scope with { TargetCulture = "en-US" } },
            ["extra active culture"] = key => key with { Scope = key.Scope with { ActiveCultures = ["de-DE", "en-GB", "nl-NL"] } },
            ["category restriction"] = key => key with { Scope = key.Scope with { CategoryIds = ["BlockCommentCategoryData"] , ObjectSelections = ["Main"] } },
            ["baseline content"] = key => key with { BaselineContentSha256 = new('a', 64) },
            ["result content"] = key => key with { CandidateContentSha256 = new('a', 64) },
            ["key"] = key => key with { Rows = [key.Rows[0] with { NativeKey = "other" }, key.Rows[1]] },
            ["category"] = key => key with { Rows = [key.Rows[0] with { Category = "other" }, key.Rows[1]] },
            ["view path"] = key => key with { Rows = [key.Rows[0] with { ViewPath = "other" }, key.Rows[1]] },
            ["source state"] = key => key with { Rows = [key.Rows[0] with { SourceStateSha256 = new('a', 64) }, key.Rows[1]] },
            ["original target"] = key => key with { Rows = [key.Rows[0] with { OriginalTargetStateSha256 = new('a', 64) }, key.Rows[1]] },
            ["replacement target"] = key => key with { Rows = [key.Rows[0] with { CandidateTargetStateSha256 = new('a', 64) }, key.Rows[1]] },
            ["omitted row"] = key => key with { Rows = [key.Rows[0]] },
            ["peer identity"] = key => key with { Peers = [key.Peers[0] with { Identity = "other" }, key.Peers[1]] },
            ["peer profile"] = key => key with { Peers = [key.Peers[0] with { ComparisonProfile = "other" }, key.Peers[1]] },
            ["peer content"] = key => key with { Peers = [key.Peers[0] with { SemanticSha256 = new('a', 64) }, key.Peers[1]] },
            ["omitted peer"] = key => key with { Peers = [key.Peers[0]] }
        };
        foreach (var change in changes) test("exact admission refuses changed " + change.Key, () => {
            var (key, profile) = Key(fixture()); Refuse("TranslationAdmissionMismatch", () => TranslationProductionAdmission.RequireComplete(profile, change.Value(key))); return Task.CompletedTask;
        });
        foreach (string name in TranslationProductionAdmission.NativeCases.Concat(TranslationProductionAdmission.PortableCases))
            test("admission cannot omit required case " + name, () => {
                var (key, profile) = Key(fixture()); Refuse("TranslationAdmissionIncomplete", () => TranslationProductionAdmission.RequireComplete(profile with { Proofs = profile.Proofs.Where(proof => proof.Case != name).ToArray() }, key)); return Task.CompletedTask;
            });
        foreach (string damage in new[] { "synthetic-native", "other-key", "other-package", "duplicate" }) test("admission refuses " + damage + " evidence", () => {
            var (key, profile) = Key(fixture()); var proofs = profile.Proofs.ToArray();
            proofs[0] = damage switch {
                "synthetic-native" => proofs[0] with { Mode = "portable" }, "other-key" => proofs[0] with { KeySha256 = new('a', 64) },
                "other-package" => proofs[0] with { SourcePackageSha256 = new('a', 64) }, _ => proofs[1]
            };
            Refuse("TranslationAdmissionIncomplete", () => TranslationProductionAdmission.RequireComplete(profile with { Proofs = proofs }, key)); return Task.CompletedTask;
        });
        foreach (bool changeContent in new[] { false, true }) test("admission peer key " + (changeContent ? "detects actual unaffected content drift" : "accepts only permitted creation-time metadata variation"), () => {
            var f = fixture(); var artifacts = new TranslationImportArtifacts(f.Root);
            TranslationAdmissionKey Capture(string stamp, string source, string id)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes("<Document><DocumentInfo><Created>" + stamp + "</Created></DocumentInfo><UnchangedSource>" + source + "</UnchangedSource></Document>");
                var export = artifacts.RetainBytes(bytes);
                var observation = Fixture.Bind(f.Observation() with { Peers = [new("complete-export.xml", export.Sha256, export, "tia21-simaticml-translation-v1"), f.Peers[1]] });
                var op = f.Runner.Prepare(f.CandidateRef, f.CandidateReceipt, observation, id);
                var candidate = new TranslationWorkflowStore(f.Root).NativeCandidate(f.CandidateRef, f.CandidateReceipt);
                return TranslationProductionAdmission.Describe(new(f.Root, op, f.CandidateRef, f.CandidateReceipt, candidate.Candidate.WorkbookSha256, f.Scope),
                    new(new('1', 64), new('2', 64), f.Target.AdapterSha256, new('3', 64)));
            }
            var before = Capture("one", "German source", "original-capture");
            var after = Capture("two", changeContent ? "Changed German source" : "German source", "fresh-capture");
            if ((HashJson(before) == HashJson(after)) == changeContent) throw new Exception("Semantic key ignored content or retained volatile metadata");
            if (before.ProjectResourceKey != after.ProjectResourceKey || !before.Rows.Select(row => row.NativeKey).SequenceEqual(after.Rows.Select(row => row.NativeKey)))
                throw new Exception("Exact instance identity changed");
            return Task.CompletedTask;
        });
    }
}
