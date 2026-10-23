using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.Security;

namespace TiaMcp.Core.PhysicalRuntime;

/// <summary>Reproducible comparisons of separately retained declarations; never an authorization token.</summary>
public static class RuntimeHandoffReview
{
    public const int InputLimit = 128 << 10, ObservationLimit = 256 << 10, ReportLimit = 2 << 20;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(BridgeProtocol.Json) {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true };

    public static JsonObject Build(string artifactsRoot, byte[] bytes)
    {
        var request = ReadRequest(bytes);
        Fields(request, "schemaVersion reviewId accessRecordId accessPhase owner expected operations baseline current");
        Require(Text(request, "schemaVersion") == "runtime-handoff-request.v1", "Unsupported handoff schema.");
        string reviewId = Text(request, "reviewId"), accessId = Text(request, "accessRecordId"), phase = Text(request, "accessPhase");
        EngineeringIdentityGuard.Sha256(reviewId, "reviewId"); EngineeringIdentityGuard.Sha256(accessId, "accessRecordId");
        Require(phase is "before" or "after", "Select an exact access phase.");
        long charged = bytes.Length;
        void Charge(int size) { charged += size; Require(charged <= 64L << 20, "Referenced evidence exceeds 64 MiB."); }
        var accessEnvelope = new AccessWorkflowStore(artifactsRoot).ReadVerified(accessId, Charge);
        var access = accessEnvelope["report"]!.AsObject();
        Require(Text(access, "reviewId") == reviewId, "Access evidence belongs to another saved review.");
        var owner = Object(request["owner"]); Fields(owner, "nodeId objectPath");
        Text(owner, "nodeId"); Text(owner, "objectPath");
        Require(JsonNode.DeepEquals(owner, access["owner"]), "Select the exact access owner.");
        var review = new ProjectReviewStore(artifactsRoot).Read(reviewId);
        Charge(ProjectReviewStore.ReadArtifact(artifactsRoot, new JsonObject {
            ["path"] = "project-reviews/" + reviewId + ".json", ["sha256"] = reviewId }).Length);
        var nodes = review["model"]!["nodes"]!.AsArray();
        Require(nodes.Count(n => JsonNode.DeepEquals(n?["nodeId"], owner["nodeId"])) == 1 &&
            nodes.Count(n => JsonNode.DeepEquals(n?["objectPath"], owner["objectPath"])) == 1,
            "Owner ID and path must each be unique in the saved model.");

        var expected = Object(request["expected"]);
        Fields(expected, "hostId context targetIdentity programIdentity subjectId clientId credential securityDomain securityMode");
        Id(expected, "hostId"); Context(expected["context"]); Identity(expected["targetIdentity"]); Identity(expected["programIdentity"]);
        foreach (string key in new[] { "subjectId", "clientId", "securityDomain", "securityMode" }) Text(expected, key);
        Credential(expected["credential"]);
        Require(JsonNode.DeepEquals(expected["subjectId"], access["subjectId"]) &&
            JsonNode.DeepEquals(expected["securityDomain"], access["identity"]?["domain"]) &&
            JsonNode.DeepEquals(expected["securityMode"], access["identity"]?["securityMode"]),
            "Expected subject, security domain and mode must match the selected public access review.");
        Require(Text(expected, "securityDomain") is "cpuRuntime" or "legacyWeb", "This handoff uses the Web runtime context only.");
        var operations = Rows(request, "operations", 64); Require(operations.Count > 0, "Select at least one scoped operation.");
        var operationKeys = new HashSet<(string, string)>();
        foreach (var item in operations)
        {
            var operation = Object(item); Fields(operation, "id scope requiredOutcome");
            Require(operationKeys.Add((Text(operation, "id"), Text(operation, "scope"))), "Duplicate operation and scope.");
            Require(Text(operation, "requiredOutcome") is "allowed" or "denied", "Require an explicit allowed or denied observation.");
        }

        var baseline = Load("baseline"); var current = Load("current");
        var constraints = new JsonArray();
        void Constraint(string stage, string name, string state) => constraints.Add(new JsonObject {
            ["stage"] = stage, ["kind"] = name, ["state"] = state });
        void Match(string stage, string name, JsonNode? wanted, JsonNode? observed) =>
            Constraint(stage, name, wanted is null || observed is null ? "unknown" : JsonNode.DeepEquals(wanted, observed) ? "equalDeclarations" : "mismatch");
        foreach (var (stage, evidence) in new[] { ("baseline", baseline), ("current", current) })
        {
            foreach (string role in new[] { "target", "program", "session" })
            {
                var row = evidence[role];
                Match(stage, role + ".host", expected["hostId"], row["hostId"]);
                foreach (string key in new[] { "endpoint", "connectionId", "epoch", "peerCertificateSha256" })
                    Match(stage, role + "." + key, expected["context"]![key], row["context"]![key]);
                Constraint(stage, role + ".freshness", Text(row, "freshness") == "current" ? "declaredCurrent" : Text(row, "freshness"));
            }
            Match(stage, "targetIdentity", expected["targetIdentity"], evidence["target"]["identity"]);
            Match(stage, "programIdentity", expected["programIdentity"], evidence["program"]["identity"]);
            var session = evidence["session"];
            foreach (string key in new[] { "subjectId", "clientId", "credential", "securityDomain", "securityMode" })
                Match(stage, key, expected[key], session[key]);
            Constraint(stage, "sessionState", Text(session, "state") == "connected" ? "declaredConnected" : "unavailable");
            foreach (string key in new[] { "peerVerified", "authenticated", "permissionsComplete" })
                Constraint(stage, key, Boolean(session, key) ? "declaredTrue" : "unknown");
        }

        foreach (string role in new[] { "target", "program", "session" })
        {
            var old = baseline[role]; var next = current[role];
            Constraint("transition", role + ".sequence", Number(next, "sequence") < Number(old, "sequence") ? "regressed" : "nondecreasingDeclaration");
            Match("transition", role + ".sourceKind", old["sourceKind"], next["sourceKind"]);
            Match("transition", role + ".host", old["hostId"], next["hostId"]);
            Match("transition", role + ".context", old["context"], next["context"]);
            if (role != "session") Match("transition", role + ".identity", old["identity"], next["identity"]);
            // A changed observation cannot reuse a sequence, even if the new declaration satisfies the expectation.
            if (Number(next, "sequence") == Number(old, "sequence"))
                Constraint("transition", role + ".sameSequence", JsonNode.DeepEquals(old, next) ? "equalDeclarations" : "mismatch");
        }
        foreach (string key in new[] { "subjectId", "clientId", "credential", "securityDomain", "securityMode", "permissionsComplete" })
            Match("transition", "session." + key, baseline["session"][key], current["session"][key]);
        var oldPermissions = Permissions(baseline["session"]); var newPermissions = Permissions(current["session"]);
        Constraint("transition", "permissions", oldPermissions.Count == newPermissions.Count &&
            oldPermissions.All(p => newPermissions.TryGetValue(p.Key, out string? value) && p.Value == value) ? "equalDeclarations" : "mismatch");
        var permissions = new JsonArray();
        var assessment = access["assessments"]!.AsArray().Single(a => (string?)a!["phase"] == phase)!;
        foreach (var item in operations)
        {
            var operation = Object(item); var key = (Text(operation, "id"), Text(operation, "scope"));
            string wanted = Text(operation, "requiredOutcome");
            string Outcome(Dictionary<(string, string), string> values) => values.GetValueOrDefault(key, "unknown");
            string before = Outcome(oldPermissions), after = Outcome(newPermissions);
            permissions.Add(new JsonObject { ["operation"] = operation.DeepClone(), ["baselineOutcome"] = before,
                ["currentOutcome"] = after, ["state"] = before == "unknown" || after == "unknown" ? "unknown" :
                    before == wanted && after == wanted ? "matchesDeclaredExpectation" : "mismatch",
                ["publicGrantDeclared"] = assessment["potentialRights"]!.AsArray().Any(r =>
                    (string?)r!["id"] == key.Item1 && (string?)r["scope"] == key.Item2), ["operationAdmitted"] = false });
        }
        bool invalid = constraints.Any(c => (string?)c!["state"] is "unknown" or "mismatch" or "expired" or "unavailable" or "regressed") ||
            permissions.Any(p => (string?)p!["state"] != "matchesDeclaredExpectation");
        var report = new JsonObject {
            ["schemaVersion"] = "runtime-handoff-review.v1", ["reviewId"] = reviewId, ["accessRecordId"] = accessId,
            ["accessPhase"] = phase, ["owner"] = owner.DeepClone(), ["expected"] = expected.DeepClone(),
            ["evidence"] = new JsonObject { ["baseline"] = request["baseline"]!.DeepClone(), ["current"] = request["current"]!.DeepClone() },
            ["observations"] = new JsonObject { ["baseline"] = ObservationRows(baseline), ["current"] = ObservationRows(current) },
            ["constraints"] = constraints, ["permissions"] = permissions,
            ["summary"] = new JsonObject { ["state"] = invalid ? "requiresFreshEvidence" : "consistentDeclarations",
                ["requiresFreshEvidence"] = invalid, ["constraintCount"] = constraints.Count, ["operationCount"] = permissions.Count,
                ["publicAccessPhaseKind"] = phase == "before" ? "observation" : access["afterKind"]!.DeepClone(),
                ["publicClosureComplete"] = assessment["suppliedClosureComplete"]!.DeepClone(), ["referencedBytes"] = charged },
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false,
            ["liveFreshnessVerified"] = false, ["deviceIdentityVerified"] = false, ["programMatchVerified"] = false,
            ["effectiveRightsVerified"] = false, ["operationAdmitted"] = false, ["assertionsAdmitted"] = false,
            ["admission"] = "unavailableWithoutQualifiedNativeEvidence",
            ["mutationRecovery"] = "unknownOutcomeRequiresIndependentReconciliationNoAutomaticReplay",
            ["limits"] = "Supplied or synthetic public observations only. Separate artifact references do not authenticate independent producers. Sequence and freshness are declarations. Neither certificates, metadata hashes, successful reads nor public grants prove CPU/program identity or effective rights. Replay reproduces a review only; it never creates a session or admits an operation. Capture quality and source-to-runtime signal association require separate checks." };
        Require(JsonSerializer.SerializeToUtf8Bytes(report, Json).Length <= ReportLimit, "Report exceeds 2 MiB.");
        return report;

        Dictionary<string, JsonObject> Load(string stage)
        {
            var refs = Object(request[stage]); Fields(refs, "target program session");
            var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (string role in new[] { "target", "program", "session" })
            {
                var reference = Object(refs[role]); Fields(reference, "path sha256");
                Text(reference, "path", 4096); EngineeringIdentityGuard.Sha256(Text(reference, "sha256"), "observation.sha256");
                byte[] source = ProjectReviewStore.ReadArtifact(artifactsRoot, reference, ObservationLimit); Charge(source.Length);
                result.Add(role, ReadObservation(role, source));
            }
            return result;
        }
    }

    internal static JsonObject ReadRequest(byte[] bytes) => Parse(bytes, InputLimit);

    internal static JsonObject ReadObservation(string role, byte[] source)
    {
        Require(role is "target" or "program" or "session", "Select target, program or session evidence.");
        var row = Parse(source, ObservationLimit);
        Fields(row, "schemaVersion sourceKind hostId context sequence freshness " + (role == "session" ?
            "subjectId clientId credential securityDomain securityMode peerVerified authenticated state permissionsComplete permissions" : "identity"));
        Require(Text(row, "schemaVersion") == "runtime-handoff-" + role + ".v1", "Observation has the wrong role or schema.");
        Require(Text(row, "sourceKind") is "supplied" or "synthetic", "Production evidence admission is unavailable.");
        Id(row, "hostId"); Context(row["context"]); Number(row, "sequence");
        Require(Text(row, "freshness") is "current" or "expired" or "unknown", "Unknown freshness declaration.");
        if (role == "session")
        {
            foreach (string key in new[] { "subjectId", "clientId", "securityDomain", "securityMode" }) Text(row, key);
            Credential(row["credential"]);
            foreach (string key in new[] { "peerVerified", "authenticated", "permissionsComplete" }) Boolean(row, key);
            Require(Text(row, "state") is "connected" or "closed" or "expired" or "unknown", "Unknown session state.");
            Permissions(row);
        }
        else Identity(row["identity"]);
        return row;
    }

    private static JsonObject ObservationRows(Dictionary<string, JsonObject> rows) => new(rows.Select(p =>
        new KeyValuePair<string, JsonNode?>(p.Key, p.Value.DeepClone())));
    private static Dictionary<(string, string), string> Permissions(JsonObject row)
    {
        var result = new Dictionary<(string, string), string>();
        foreach (var item in Rows(row, "permissions", 256))
        {
            var permission = Object(item); Fields(permission, "id scope outcome");
            string outcome = Text(permission, "outcome");
            Require(outcome is "allowed" or "denied" or "unknown", "Unknown permission outcome.");
            Require(result.TryAdd((Text(permission, "id"), Text(permission, "scope")), outcome), "Duplicate scoped permission.");
        }
        return result;
    }
    private static void Identity(JsonNode? node)
    {
        if (node is null) return;
        var value = Object(node); Fields(value, "scheme value"); Text(value, "scheme"); Text(value, "value");
    }
    private static void Credential(JsonNode? node)
    {
        if (node is null) return;
        var value = Object(node); Fields(value, "schemaVersion vaultId credentialId bindingSha256 cipherSha256");
        ProtectedCredentialGuard.ValidateReference(value.Deserialize<CredentialReference>(Json)!);
    }
    private static void Context(JsonNode? node)
    {
        var value = Object(node); Fields(value, "endpoint connectionId epoch peerCertificateSha256");
        var context = value.Deserialize<WebRuntimeContext>(Json) ?? throw Error("Expected runtime context.");
        WebRuntimeProtocol.Validate(context);
    }
    private static JsonObject Parse(byte[] bytes, int limit)
    {
        Require(bytes.Length is > 0 && bytes.Length <= limit, "Input exceeds its byte bound.");
        using var doc = JsonDocument.Parse(Utf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 24 });
        void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in element.EnumerateObject()) { Require(names.Add(field.Name), "Duplicate JSON field."); Unique(field.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
            else if (element.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(element.GetString()!);
        }
        Unique(doc.RootElement); return Object(JsonNode.Parse(doc.RootElement.GetRawText()));
    }
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Error("Expected object.");
    private static JsonArray Rows(JsonObject node, string key, int maximum) => node[key] is JsonArray rows && rows.Count <= maximum ? rows : throw Error("Invalid collection or bound.");
    private static void Fields(JsonObject node, string fields)
    { string[] keys = fields.Split(' '); Require(node.Count == keys.Length && keys.All(node.ContainsKey), "Missing or unknown public fields."); }
    private static string Text(JsonObject node, string key, int maximum = 512)
    {
        Require(node[key] is JsonValue value && value.TryGetValue<string>(out _), "Expected public text.");
        string text = node[key]!.GetValue<string>();
        Require(!string.IsNullOrWhiteSpace(text) && text.Length <= maximum && !text.Any(char.IsControl), "Invalid public text.");
        _ = Utf8.GetByteCount(text); return text;
    }
    private static void Id(JsonObject node, string key) => Require(ProtectedCredentialGuard.IsId(Text(node, key)), "Expected exact identifier.");
    private static bool Boolean(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<bool>(out bool b) ? b : throw Error("Expected Boolean.");
    private static long Number(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<long>(out long n) && n >= 0 ? n : throw Error("Expected nonnegative Int64 sequence.");
    private static void Require(bool condition, string message) { if (!condition) throw Error(message); }
    private static BridgeException Error(string message) => new("InvalidRuntimeHandoff", message);
}
