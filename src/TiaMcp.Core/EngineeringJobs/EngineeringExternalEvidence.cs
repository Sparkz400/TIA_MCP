using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.EngineeringJobs;

/// <summary>Context and byte binding for trusted provider readbacks; never provider authentication or a restore API.</summary>
public static class EngineeringExternalEvidence
{
    public static string OutcomeGate(string resourceKey) => "external.readback." + CanonicalJson.Sha256(JsonValue.Create(resourceKey)!);
    internal static void ValidateState(EngineeringExternalState state)
    {
        if (state is null || state.Presence is not ("present" or "absent")) throw Error("Only independently known present/absent external states can satisfy an obligation.");
        EngineeringIdentityGuard.Sha256(state.Sha256, "externalStateSha256");
    }
    internal static void Verify(string root, EngineeringOperationRecord operation, int revision, EngineeringResourceEffect resource,
        string phase, EngineeringExternalState expected, EngineeringArtifactReference artifact)
    {
        if (artifact is null) throw Error("A separate external readback receipt is required.");
        byte[] bytes = ProjectReviewStore.ReadArtifact(root, new() { ["path"] = artifact.RelativePath, ["sha256"] = artifact.Sha256 }, 64 * 1024);
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            void Fields(JsonElement e, string fields)
            {
                if (e.ValueKind != JsonValueKind.Object) throw Error("Expected an external observation object.");
                string[] keys = fields.Split(' '); var actual = e.EnumerateObject().Select(p => p.Name).ToArray();
                if (actual.Length != keys.Length || actual.Distinct(StringComparer.Ordinal).Count() != keys.Length || keys.Any(k => !e.TryGetProperty(k, out _))) throw Error("Missing, duplicate or unknown external observation fields.");
            }
            Fields(doc.RootElement, "schemaVersion operationId planSha256 revision resourceKey phase observationId state stateArtifact");
            Fields(doc.RootElement.GetProperty("state"), "presence sha256"); Fields(doc.RootElement.GetProperty("stateArtifact"), "relativePath sha256");
            var value = JsonSerializer.Deserialize<EngineeringExternalObservation>(bytes, BridgeProtocol.Json)!;
            ValidateState(value.State);
            if (value.SchemaVersion != "engineering-external-observation.v1" || value.OperationId != operation.Id || value.PlanSha256 != operation.PlanSha256 ||
                value.Revision != revision || value.ResourceKey != resource.ResourceKey || value.Phase != phase || value.State != expected ||
                string.IsNullOrWhiteSpace(value.ObservationId) || value.ObservationId.Length > 256 || value.ObservationId.Any(char.IsControl))
                throw Error("External observation does not bind this exact operation, revision, resource, phase and expected state.");
            _ = new UTF8Encoding(false, true).GetByteCount(value.ObservationId);
            if (value.StateArtifact is null || value.StateArtifact.Sha256 != value.State.Sha256) throw Error("External state must bind its independently retained provider bytes.");
            EngineeringIdentityGuard.Sha256(value.StateArtifact.Sha256, "externalStateSha256");
            string path = PackageStorage.Under(root, PackageStorage.Relative(value.StateArtifact.RelativePath));
            if (File.Exists(path) && new FileInfo(path).Length > 64L * 1024 * 1024) throw new BridgeException("ArtifactCapacity", "External state evidence exceeds 64 MiB.");
            if (!File.Exists(path) || PackageStorage.FileHash(path) != value.State.Sha256) throw new BridgeException("ArtifactChanged", "Independent external state evidence is missing or changed.");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or EncoderFallbackException)
        { throw Error("External observation is malformed."); }
    }
    private static BridgeException Error(string message) => new("ExternalEvidenceInvalid", message);
}
