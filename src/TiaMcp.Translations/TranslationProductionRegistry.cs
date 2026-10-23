using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

internal sealed record TranslationAdmissionFile(string Path, string Sha256);
internal sealed record TranslationAdmissionEntry(string Id, string ProjectPath, string PlcPath,
    TranslationAdmissionProfile Profile, TranslationAdmissionFile[] Evidence);
internal sealed record TranslationAdmissionRegistryDocument(int Version, TranslationAdmissionEntry[] Entries);

/// <summary>
/// Trusted deployment configuration, never an upload, environment setting or MCP
/// argument. The installation operator reviews native receipts before installing
/// this registry. The loader verifies retained receipt integrity, not their substantive
/// native conclusions; independent operator review establishes those conclusions. It does not create
/// qualification authority from a caller's booleans or synthetic test results.
/// </summary>
internal sealed class TranslationProductionRegistry(string installationDirectory, string workerPath, string apiDirectory)
{
    private readonly string installation = System.IO.Path.GetFullPath(installationDirectory);
    private string Root => System.IO.Path.Combine(installation, "translation-admission");
    private static readonly JsonSerializerOptions Strict = new(TranslationJson.Options) {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 32
    };

    internal TranslationAdmissionEntry Resolve(string id)
    {
        Require(Regex.IsMatch(id ?? "", "^[a-z0-9][a-z0-9.-]{0,95}$"), "TranslationAdmissionInvalid", "Exact installed profile ID required.");
        string path = System.IO.Path.Combine(Root, "registry.json");
        Require(File.Exists(path), "TranslationImportProfileUnqualified", "No operator-reviewed translation admission registry is installed.");
        TranslationRetention.NoLinks(path);
        byte[] bytes = TranslationRetention.ReadBounded(path, 1024 * 1024);
        TranslationAdmissionRegistryDocument registry;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            UniqueProperties(document.RootElement);
            registry = JsonSerializer.Deserialize<TranslationAdmissionRegistryDocument>(bytes, Strict)!;
        }
        catch (JsonException error) { throw new BridgeException("TranslationAdmissionInvalid", "Invalid installed admission registry: " + error.Message); }
        Require(registry is { Version: 1, Entries.Length: > 0 and <= 32 } && registry.Entries.All(entry => entry is not null) &&
            registry.Entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() == registry.Entries.Length,
            "TranslationAdmissionInvalid", "Bounded unique version-one installed entries required.");
        var selected = registry.Entries.SingleOrDefault(entry => entry.Id == id);
        Require(selected is not null && selected.Profile is not null && selected.Profile.Key is not null && selected.Id == selected.Profile.Id,
            "TranslationImportProfileUnqualified", "Exact operator-reviewed profile is absent.");
        Require(System.IO.Path.IsPathFullyQualified(selected!.ProjectPath) &&
            EngineeringResourceKey.ForPath("project", selected.ProjectPath) == selected.Profile.Key.ProjectResourceKey &&
            !string.IsNullOrWhiteSpace(selected.PlcPath) && selected.PlcPath.Length <= 8192 && !selected.PlcPath.Any(char.IsControl),
            "TranslationAdmissionInvalid", "Installed selector differs from its exact project instance key.");
        TranslationProductionAdmission.RequireComplete(selected.Profile, selected.Profile.Key);
        Require(HashJson(selected.Profile.Key.Binaries) == HashJson(Binaries()), "TranslationAdmissionMismatch", "Installed admission pins different executable dependencies or Siemens API.");
        Require(selected.Evidence is { Length: > 0 and <= 128 } && selected.Evidence.All(file => file is not null) &&
            selected.Evidence.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() == selected.Evidence.Length &&
            selected.Evidence.Select(file => file.Sha256).Order(StringComparer.Ordinal).SequenceEqual(selected.Profile.Proofs.Select(proof => proof.EvidenceSha256).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            "TranslationAdmissionIncomplete", "Registry must retain exactly the reviewed evidence files used by its proof ledger.");
        foreach (var evidence in selected.Evidence)
        {
            Require(Regex.IsMatch(evidence.Path ?? "", "^evidence/[a-f0-9]{64}\\.json$") && evidence.Path == "evidence/" + evidence.Sha256 + ".json",
                "TranslationAdmissionInvalid", "Admission evidence must be a fixed content-addressed installation file.");
            string evidencePath = System.IO.Path.Combine(Root, evidence.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            TranslationRetention.NoLinks(evidencePath);
            Require(Sha256(TranslationRetention.ReadBounded(evidencePath, 16 * 1024 * 1024)) == evidence.Sha256,
                "TranslationAdmissionMismatch", "Reviewed installed evidence changed.");
        }
        return Clone(selected);
    }

    internal void RequireAdmission(string id, TranslationImportContext context)
    {
        var entry = Resolve(id);
        Require(EngineeringResourceKey.ForPath("project", context.Operation.Plan.Target.ProjectFilePath) == entry.Profile.Key.ProjectResourceKey,
            "TranslationAdmissionMismatch", "Operation belongs to another exact project instance.");
        TranslationProductionAdmission.RequireComplete(entry.Profile, TranslationProductionAdmission.Describe(context, Binaries()));
    }

    internal TranslationAdmissionBinaries Binaries()
    {
        string worker = System.IO.Path.GetFullPath(workerPath), api = System.IO.Path.GetFullPath(apiDirectory);
        TranslationRetention.NoLinks(worker); TranslationRetention.NoLinks(api);
        var files = Executables(installation, "host").Concat(Executables(System.IO.Path.GetDirectoryName(worker)!, "worker")).ToArray();
        Require(files.Any(file => file.Path == "host/TiaMcp.Host.dll") && files.Any(file => file.Path == "host/TiaMcp.Translations.dll") &&
            files.Any(file => file.Path == "worker/" + System.IO.Path.GetFileName(worker)), "TranslationAdmissionInvalid", "Complete Host implementation and owning worker deployment required.");
        var apiFiles = Directory.GetFiles(api, "Siemens.Engineering*.dll").Order(StringComparer.Ordinal).Select(path => {
            TranslationRetention.NoLinks(path); return new TranslationAdmissionFile(System.IO.Path.GetFileName(path), Sha256(File.ReadAllBytes(path)));
        }).ToArray();
        Require(apiFiles.Length is > 0 and <= 128 && apiFiles.Any(file => file.Path == "Siemens.Engineering.Base.dll"), "TranslationAdmissionInvalid", "Installed Siemens API inventory required.");
        return new(HashJson(files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray()),
            Sha256(File.ReadAllBytes(System.IO.Path.Combine(installation, "TiaMcp.Translations.dll"))), Sha256(File.ReadAllBytes(worker)), HashJson(apiFiles));
    }

    // Configuration/evidence is intentionally separate from executable identity;
    // adding the reviewed registry does not change the binary being qualified.
    // Every deployed exe/dll/deps/runtimeconfig/config is included recursively.
    private static IEnumerable<TranslationAdmissionFile> Executables(string directory, string prefix)
    {
        TranslationRetention.NoLinks(directory);
        var pending = new Stack<string>(); pending.Push(directory); int count = 0, directories = 0;
        while (pending.Count != 0)
        {
            string folder = pending.Pop(); TranslationRetention.NoLinks(folder);
            Require(++directories <= 1024, "TranslationAdmissionInvalid", "Deployment directory bound exceeded.");
            foreach (string child in Directory.GetDirectories(folder))
            {
                TranslationRetention.NoLinks(child);
                if (folder == directory && System.IO.Path.GetFileName(child) == "translation-admission") continue;
                pending.Push(child); Require(pending.Count <= 1024, "TranslationAdmissionInvalid", "Deployment directory bound exceeded.");
            }
            foreach (string path in Directory.GetFiles(folder))
            {
                string name = System.IO.Path.GetFileName(path);
                if (!(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".config", StringComparison.OrdinalIgnoreCase))) continue;
                TranslationRetention.NoLinks(path); Require(++count <= 5000, "TranslationAdmissionInvalid", "Deployment file bound exceeded.");
                yield return new(prefix + "/" + System.IO.Path.GetRelativePath(directory, path).Replace('\\', '/'), Sha256(File.ReadAllBytes(path)));
            }
        }
    }
    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException("Duplicate admission property."); UniqueProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) UniqueProperties(item);
    }
}
