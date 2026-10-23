using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Translations;
using static TiaMcp.Translations.TranslationJson;

internal static class TranslationRegistryChecks
{
    internal static void Register(Action<string, Func<Task>> test, string output)
    {
        static void Refuse(string code, Action action)
        { try { action(); } catch (BridgeException error) when (error.Code == code) { return; } throw new Exception("Expected " + code); }
        (TranslationProductionRegistry Registry, string Installation, string Worker, string Config, TranslationImportContext Context) FixtureRegistry()
        {
            string root = Path.Combine(output, "registry-" + Guid.NewGuid().ToString("N"));
            string host = Path.Combine(root, "host"), worker = Path.Combine(root, "worker"), api = Path.Combine(root, "api");
            foreach (string path in new[] { host, worker, api }) Directory.CreateDirectory(path);
            foreach (string name in new[] { "TiaMcp.Host.exe", "TiaMcp.Host.dll", "TiaMcp.Translations.dll", "TiaMcp.Core.dll", "TiaMcp.Host.deps.json", "TiaMcp.Host.runtimeconfig.json" })
                File.WriteAllText(Path.Combine(host, name), "Synthetic deployment fingerprint bytes, never executable: " + name);
            string executable = Path.Combine(worker, "TiaMcp.Openness.V21.exe"); File.WriteAllText(executable, "Synthetic worker, never executable");
            File.WriteAllText(Path.Combine(worker, "Newtonsoft.Json.dll"), "Synthetic worker dependency");
            File.WriteAllText(Path.Combine(api, "Siemens.Engineering.Base.dll"), "Synthetic installed API identity");
            var fixture = new Fixture(Path.Combine(root, "store"), adapterHash: Sha256(File.ReadAllBytes(executable)), exactAdmission: true);
            var op = fixture.Prepare(); var candidate = new TranslationWorkflowStore(fixture.Root).NativeCandidate(fixture.CandidateRef, fixture.CandidateReceipt);
            var context = new TranslationImportContext(fixture.Root, op, fixture.CandidateRef, fixture.CandidateReceipt, candidate.Candidate.WorkbookSha256, fixture.Scope);
            var registry = new TranslationProductionRegistry(host, executable, api);
            var key = TranslationProductionAdmission.Describe(context, registry.Binaries());
            byte[] receipt = Encoding.UTF8.GetBytes("{\"syntheticTestOnly\":true,\"nativeExecuted\":false}"); string digest = Sha256(receipt), source = new('4', 64);
            var proofs = TranslationProductionAdmission.NativeCases.Select(name => new TranslationAdmissionProof(name, HashJson(key), source, digest, "native"))
                .Concat(TranslationProductionAdmission.PortableCases.Select(name => new TranslationAdmissionProof(name, HashJson(key), source, digest, "portable"))).ToArray();
            var profile = new TranslationAdmissionProfile("synthetic-loader-check-never-installed", key, source, proofs);
            var entry = new TranslationAdmissionEntry(profile.Id, op.Plan.Target.ProjectFilePath, "synthetic/PLC", profile, [new("evidence/" + digest + ".json", digest)]);
            string config = Path.Combine(host, "translation-admission", "registry.json");
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(config)!, "evidence"));
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(config)!, entry.Evidence[0].Path.Replace('/', Path.DirectorySeparatorChar)), receipt);
            File.WriteAllText(config, Canonical(new TranslationAdmissionRegistryDocument(1, [entry])));
            return (registry, host, worker, config, context);
        }
        static string Canonical<T>(T value) => TiaMcp.Core.Engineering.CanonicalJson.Serialize(Node(value));
        const string id = "synthetic-loader-check-never-installed";
        test("fixed admission registry reads reviewed data without changing executable identity", () => {
            var f = FixtureRegistry(); var before = f.Registry.Binaries(); f.Registry.RequireAdmission(id, f.Context);
            if (HashJson(before) != HashJson(f.Registry.Binaries()) || TranslationProductionImportAdapter.QualifiedProfiles.Length != 0)
                throw new Exception("Registry data changed binaries or enabled compiled production profiles");
            return Task.CompletedTask;
        });
        foreach (string field in new[] { "Host DLL", "Host dependency", "worker dependency" }) test("installed admission refuses changed " + field + " with unchanged apphost", () => {
            var f = FixtureRegistry(); string apphost = Sha256(File.ReadAllBytes(Path.Combine(f.Installation, "TiaMcp.Host.exe")));
            string target = field == "Host DLL" ? Path.Combine(f.Installation, "TiaMcp.Host.dll") : field == "Host dependency" ? Path.Combine(f.Installation, "TiaMcp.Core.dll") : Path.Combine(f.Worker, "Newtonsoft.Json.dll");
            File.AppendAllText(target, "modified"); Refuse("TranslationAdmissionMismatch", () => f.Registry.Resolve(id));
            if (Sha256(File.ReadAllBytes(Path.Combine(f.Installation, "TiaMcp.Host.exe"))) != apphost) throw new Exception("Apphost unexpectedly changed");
            return Task.CompletedTask;
        });
        foreach (string damage in new[] { "missing-registry", "unknown-root", "unknown-nested", "duplicate-property", "missing-gate", "foreign-instance", "traversal", "evidence-tampering" })
            test("installed admission refuses " + damage, () => {
                var f = FixtureRegistry(); var json = JsonNode.Parse(File.ReadAllText(f.Config))!; string code = "TranslationAdmissionInvalid";
                switch (damage)
                {
                    case "missing-registry": File.Move(f.Config, f.Config + ".uninstalled"); code = "TranslationImportProfileUnqualified"; break;
                    case "unknown-root": json["callerEnable"] = true; break;
                    case "unknown-nested": json["entries"]![0]!["profile"]!["key"]!["callerEnable"] = true; break;
                    case "duplicate-property": File.WriteAllText(f.Config, File.ReadAllText(f.Config).Replace("\"version\":1", "\"version\":1,\"version\":1")); break;
                    case "missing-gate": json["entries"]![0]!["profile"]!["proofs"]!.AsArray().RemoveAt(0); code = "TranslationAdmissionIncomplete"; break;
                    case "foreign-instance": json["entries"]![0]!["projectPath"] = Path.Combine(output, "foreign.ap21"); break;
                    case "traversal": json["entries"]![0]!["evidence"]![0]!["path"] = "../outside.json"; break;
                    case "evidence-tampering": File.AppendAllText(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(f.Config)!, "evidence")).Single(), "changed"); code = "TranslationAdmissionMismatch"; break;
                }
                if (damage is not ("missing-registry" or "duplicate-property" or "evidence-tampering")) File.WriteAllText(f.Config, json.ToJsonString());
                Refuse(code, () => f.Registry.Resolve(id)); return Task.CompletedTask;
            });
    }
}
