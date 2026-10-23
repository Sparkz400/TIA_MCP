using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.TestSuite;

public sealed record TestSuiteDefinitionManifest(string Id, string Family, string Name, string Extension,
    EngineeringObjectReference Reference, string OriginalSha256, int OriginalBytes, string ProfileId,
    EngineeringObjectReference[] RequiredScope, string[] Dependencies, bool ScopeComplete, bool EffectsReviewed,
    string[] UnresolvedSyntax);

/// <summary>Opaque original definitions are retained exactly; no vendor grammar is invented.</summary>
public sealed class TestSuiteDefinitionCapture
{
    private readonly byte[] original;
    private readonly string manifestJson;
    public byte[] OriginalBytes => original.ToArray();
    public TestSuiteDefinitionManifest Manifest => JsonSerializer.Deserialize<TestSuiteDefinitionManifest>(manifestJson, BridgeProtocol.Json)!;
    public string ManifestSha256 { get; }

    public TestSuiteDefinitionCapture(string id, string family, string name, string extension,
        EngineeringObjectReference reference, byte[] bytes, string profileId, EngineeringObjectReference[] requiredScope,
        string[] dependencies, bool scopeComplete, bool effectsReviewed, string[] unresolvedSyntax)
    {
        EngineeringIdentityGuard.Validate(reference);
        if (!TestSuiteRunPlanner.Text(id, 256) || !TestSuiteRunPlanner.Text(name, 256) || !TestSuiteRunPlanner.Text(profileId, 256) ||
            bytes is null || bytes.Length is < 1 or > 16 * 1024 * 1024)
            throw new BridgeException("InvalidDefinition", "A bounded exact ID, name, profile and nonempty original definition are required.");
        var expectedExtension = family switch { "style" => ".xml", "application" => ".tat", "system" => ".tst", "applicationSet" => ".ttst", _ => throw new BridgeException("InvalidDefinition", "Unknown Test Suite definition family.") };
        if (!string.Equals(extension, expectedExtension, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("InvalidDefinition", "Definition extension disagrees with its explicitly selected family.");
        if (requiredScope is null || requiredScope.Length is < 1 or > 10000 || dependencies is null || dependencies.Length > 1000 || unresolvedSyntax is null || unresolvedSyntax.Length > 1000)
            throw new BridgeException("InvalidDefinition", "Scope/dependency/syntax bounds exceeded.");
        foreach (var item in requiredScope) EngineeringIdentityGuard.EnsureSameSnapshot(reference, item);
        if (requiredScope.Select(r => r.Object).Distinct().Count() != requiredScope.Length || dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Length ||
            dependencies.Any(d => !TestSuiteRunPlanner.Text(d, 256)) || unresolvedSyntax.Any(d => !TestSuiteRunPlanner.Text(d, 2000)))
            throw new BridgeException("InvalidDefinition", "Duplicate or invalid exact scope/dependency/syntax entries.");
        // Only XML well-formedness is established here; no native rule schema is claimed.
        if (family == "style")
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            int nodes = 0;
            while (reader.Read()) if (++nodes > 100000 || reader.Depth > 128) throw new BridgeException("DefinitionLimit", "XML definition exceeds node/depth limits.");
        }
        original = bytes.ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(original));
        if (reference.ContentSha256 is not null && reference.ContentSha256 != hash)
            throw new BridgeException("StaleContent", "Definition bytes differ from the captured object content hash.");
        var manifest = new TestSuiteDefinitionManifest(id, family, name, extension, reference with { ContentSha256 = hash }, hash, original.Length,
            profileId, requiredScope, dependencies, scopeComplete, effectsReviewed, unresolvedSyntax);
        manifestJson = CanonicalJson.Serialize(JsonSerializer.SerializeToNode(manifest, BridgeProtocol.Json)!);
        ManifestSha256 = CanonicalJson.Sha256(JsonNode.Parse(manifestJson)!);
    }
}

public sealed record TestSuiteRunPlan(string PlanSha256, string ExecutionMode, string[] DispatchDefinitionIds,
    string[] RequiredDefinitionIds, string[] ExcludedDefinitionIds, TestSuiteDefinitionManifest[] Definitions,
    string[] Blockers, bool NativeExecutionAuthorized = false);

public static class TestSuiteRunPlanner
{
    public static TestSuiteRunPlan Prepare(TestSuiteDefinitionCapture[] captures, string[] selected, string[] excluded, string executionMode)
    {
        if (captures is null || captures.Length is < 1 or > 10000 || captures.Any(c => c is null) || selected is null || selected.Length is < 1 or > 1000 || excluded is null || excluded.Length > 1000)
            throw new BridgeException("RunPlanLimit", "Definition, selection or exclusion bounds exceeded.");
        var manifests = captures.Select(c => c.Manifest).ToArray();
        return Build(manifests, selected, excluded, executionMode);
    }

    private static TestSuiteRunPlan Build(TestSuiteDefinitionManifest[] manifests, string[] selected, string[] excluded, string executionMode)
    {
        if (manifests is null || manifests.Length is < 1 or > 10000 || selected is null || selected.Length is < 1 or > 1000 || excluded is null || excluded.Length > 1000)
            throw new BridgeException("RunPlanLimit", "Definition, selection or exclusion bounds exceeded.");
        long scopeCount = 0;
        foreach (var m in manifests)
        {
            if (m is null || !Text(m.Id, 256) || !Text(m.Name, 256) || !Text(m.ProfileId, 256) || m.OriginalBytes is < 1 or > 16 * 1024 * 1024 ||
                m.RequiredScope is null || m.RequiredScope.Length is < 1 or > 10000 || m.Dependencies is null || m.Dependencies.Length > 1000 ||
                m.UnresolvedSyntax is null || m.UnresolvedSyntax.Length > 1000)
                throw new BridgeException("InvalidDefinition", "Missing or out-of-bounds manifest fields.");
            scopeCount += m.RequiredScope.Length;
            if (scopeCount > 100000) throw new BridgeException("RunPlanLimit", "Aggregate scope exceeds 100,000 references.");
            var extension = m.Family switch { "style" => ".xml", "application" => ".tat", "system" => ".tst", "applicationSet" => ".ttst", _ => null };
            if (extension is null || !string.Equals(m.Extension, extension, StringComparison.OrdinalIgnoreCase) ||
                m.RequiredScope.Select(r => r?.Object).Distinct().Count() != m.RequiredScope.Length ||
                m.Dependencies.Any(d => !Text(d, 256)) || m.Dependencies.Distinct(StringComparer.Ordinal).Count() != m.Dependencies.Length ||
                m.UnresolvedSyntax.Any(s => !Text(s, 2000)))
                throw new BridgeException("InvalidDefinition", "Invalid family, scope, dependency or syntax entries.");
            EngineeringIdentityGuard.Validate(m.Reference);
            EngineeringIdentityGuard.Sha256(m.OriginalSha256, "originalSha256");
            if (m.Reference.ContentSha256 != m.OriginalSha256) throw new BridgeException("InvalidDefinition", "Definition content identity is inconsistent.");
            foreach (var reference in m.RequiredScope) EngineeringIdentityGuard.EnsureSameSnapshot(m.Reference, reference);
            EngineeringIdentityGuard.EnsureSameSnapshot(manifests[0].Reference, m.Reference);
        }
        if (manifests.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != manifests.Length || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length || excluded.Distinct(StringComparer.Ordinal).Count() != excluded.Length)
            throw new BridgeException("AmbiguousDefinition", "Duplicate definition, selection or exclusion IDs.");
        var byId = manifests.ToDictionary(m => m.Id, StringComparer.Ordinal);
        if (selected.Concat(excluded).Any(s => !Text(s, 256) || !byId.ContainsKey(s)) || selected.Intersect(excluded, StringComparer.Ordinal).Any())
            throw new BridgeException("InvalidSelection", "Unknown or simultaneously selected/excluded definition.");
        string family = executionMode switch { "style" => "style", "systemManaged" or "externallyManaged" => "application", "endpoint" => "system", _ => throw new BridgeException("InvalidExecutionMode", "Use an explicit style, application-instance or endpoint mode.") };
        var required = new List<string>(); var active = new HashSet<string>(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        var blockers = new List<string>();
        void Visit(string id, int depth)
        {
            if (depth > 64 || !active.Add(id)) throw new BridgeException("DefinitionCycle", "Cyclic or over-depth definition dependency.");
            if (!byId.TryGetValue(id, out var definition)) throw new BridgeException("MissingDependency", "Definition dependency is absent: " + id);
            if (definition.Family != family && !(family == "application" && definition.Family == "applicationSet"))
                throw new BridgeException("MixedExecutionResources", "A plan cannot mix style, simulator and endpoint resources.");
            EngineeringIdentityGuard.EnsureSameSnapshot(manifests[0].Reference, definition.Reference);
            if (excluded.Contains(id, StringComparer.Ordinal)) blockers.Add("Required dependency is explicitly excluded: " + id);
            if (!definition.ScopeComplete) blockers.Add("Scope not completely observed: " + id);
            if (!definition.EffectsReviewed || definition.UnresolvedSyntax.Length != 0) blockers.Add("Definition effects/grammar unresolved: " + id);
            if (seen.Add(id))
            {
                required.Add(id);
                if (required.Count > 1000) throw new BridgeException("RunPlanLimit", "Required dependency closure exceeds 1,000 definitions.");
                foreach (var dependency in definition.Dependencies) Visit(dependency, depth + 1);
            }
            active.Remove(id);
        }
        foreach (var id in selected) Visit(id, 0);
        // Explicit exclusions remain required coverage, including their dependencies.
        foreach (var id in excluded) Visit(id, 0);
        // Do not dispatch set members a second time or split dependency-bearing sets.
        var setMembers = new HashSet<string>(StringComparer.Ordinal);
        var memberOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        void AddMembers(string id, string root, HashSet<string> rootMembers)
        {
            foreach (var member in byId[id].Dependencies)
            {
                if (!rootMembers.Add(member)) continue;
                setMembers.Add(member);
                if (memberOwners.TryGetValue(member, out var owner) && owner != root)
                    blockers.Add("Selected sets share a member that would be dispatched twice: " + member);
                else memberOwners[member] = root;
                AddMembers(member, root, rootMembers);
            }
        }
        foreach (var id in selected.Where(s => byId[s].Family == "applicationSet")) AddMembers(id, id, new(StringComparer.Ordinal));
        if (selected.Any(setMembers.Contains)) blockers.Add("A selected set and its member would be dispatched twice.");
        var ordered = required.Select(id => byId[id]).ToArray();
        var plan = new TestSuiteRunPlan("", executionMode, selected.ToArray(), required.ToArray(), excluded.ToArray(), ordered,
            blockers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        return plan with { PlanSha256 = Fingerprint(plan) };
    }

    internal static bool Text(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Any(char.IsControl)) return false;
        try { XmlConvert.VerifyXmlChars(value); return true; } catch (XmlException) { return false; }
    }

    public static string Fingerprint(TestSuiteRunPlan plan) => CanonicalJson.Sha256(JsonSerializer.SerializeToNode(plan with { PlanSha256 = "" }, BridgeProtocol.Json)!);
    public static void Validate(TestSuiteRunPlan plan)
    {
        if (plan is null || plan.Definitions is null || plan.Definitions.Length is < 1 or > 1000 || plan.RequiredDefinitionIds is null ||
            plan.DispatchDefinitionIds is null || plan.DispatchDefinitionIds.Length is < 1 or > 1000 || plan.ExcludedDefinitionIds is null || plan.ExcludedDefinitionIds.Length > 1000 ||
            plan.Blockers is null || plan.Blockers.Length > 10000 || plan.Blockers.Any(b => !Text(b, 4000)) || plan.Definitions.Any(d => d is null) ||
            plan.RequiredDefinitionIds.Length != plan.Definitions.Length || plan.RequiredDefinitionIds.Distinct(StringComparer.Ordinal).Count() != plan.RequiredDefinitionIds.Length ||
            !plan.RequiredDefinitionIds.SequenceEqual(plan.Definitions.Select(d => d.Id)) || plan.DispatchDefinitionIds.Any(id => !plan.RequiredDefinitionIds.Contains(id, StringComparer.Ordinal)))
            throw new BridgeException("InvalidRunPlan", "Missing, duplicated or inconsistent required definition closure.");
        if (plan.NativeExecutionAuthorized || plan.PlanSha256 != Fingerprint(plan))
            throw new BridgeException("StaleRunPlan", "Run plan changed or incorrectly claims native authorization.");
        var rebuilt = Build(plan.Definitions, plan.DispatchDefinitionIds, plan.ExcludedDefinitionIds, plan.ExecutionMode);
        if (plan.PlanSha256 != rebuilt.PlanSha256)
            throw new BridgeException("InvalidRunPlan", "Closure, blockers or dispatch differ from independently rebuilt plan semantics.");
    }
}
