using System.Text;
using System.Text.Json;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.Security;

public sealed record CredentialBinding(EngineeringExternalIdentity Resource, string Purpose, string SubjectId);
public sealed record CredentialReference(string SchemaVersion, string VaultId, string CredentialId, string BindingSha256, string CipherSha256);
public sealed record EngineeringCredentialRequirement(string Name, string UsePhase, CredentialReference Reference, CredentialBinding Binding);

/// <summary>Public metadata validation only; neither readability nor native authentication.</summary>
public static class ProtectedCredentialGuard
{
    public static string BindingHash(CredentialBinding binding)
    {
        ValidateBinding(binding);
        return CanonicalJson.Sha256(JsonSerializer.SerializeToNode(binding, BridgeProtocol.Json)!);
    }
    public static void ValidateBinding(CredentialBinding binding)
    {
        if (binding is null || binding.Resource is null || binding.Resource.ResourceId is null) throw Error("CredentialContextMismatch");
        _ = EngineeringResourceKey.ForExternal(binding.Resource);
        Text(binding.Purpose); Text(binding.SubjectId);
    }
    public static void ValidateReference(CredentialReference reference)
    {
        if (reference is null || reference.SchemaVersion != "protected-credential-reference.v1" || !IsId(reference.VaultId) || !IsId(reference.CredentialId)) throw Error("CredentialInputInvalid");
        EngineeringIdentityGuard.Sha256(reference.BindingSha256, "binding"); EngineeringIdentityGuard.Sha256(reference.CipherSha256, "ciphertext");
    }
    internal static void ValidateRequirements(EngineeringOperationPlan plan)
    {
        if (plan.Credentials is null) return;
        if (plan.Credentials.Count is < 1 or > 64 || plan.Credentials.Any(x => x is null)) throw Error("CredentialInputInvalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var references = new Dictionary<string, CredentialReference>(StringComparer.Ordinal);
        foreach (var item in plan.Credentials)
        {
            if (item.Name is not { Length: > 0 and <= 128 } || item.Name[0] is < 'a' or > 'z' ||
                item.Name.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')) ||
                !names.Add(item.Name) || item.UsePhase is not ("dispatch" or "recovery")) throw Error("CredentialInputInvalid");
            ValidateReference(item.Reference); ValidateBinding(item.Binding);
            if (item.Reference.BindingSha256 != BindingHash(item.Binding) || item.Reference.VaultId != plan.Credentials[0].Reference.VaultId)
                throw Error("CredentialContextMismatch");
            string key = EngineeringResourceKey.ForExternal(item.Binding.Resource);
            if (!plan.Resources.Any(r => r.Kind == "external" && EngineeringResourceKey.Overlaps(r.ResourceKey, key))) throw Error("CredentialContextMismatch");
            if (references.TryGetValue(item.Reference.CredentialId, out var prior) && prior != item.Reference) throw Error("CredentialContextMismatch");
            references[item.Reference.CredentialId] = item.Reference;
        }
    }
    private static void Text(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl)) throw Error("CredentialInputInvalid");
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); } catch (EncoderFallbackException) { throw Error("CredentialInputInvalid"); }
    }
    public static bool IsId(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static BridgeException Error(string code) => new(code, "Protected credential operation refused; no secret was returned. Check the local context, ownership and retained evidence.");
}
