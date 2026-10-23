using System.Text.Json.Nodes;

namespace TiaMcp.Core.Engineering;

/// <summary>Version 3 supplied references; consistency never grants native eligibility.</summary>
internal static class CompositeEngineeringTyped
{
    internal static string EncoderState(JsonNode row, JsonNode report)
    {
        if (row["contradictions"]!.AsArray().Count != 0) return "contradictoryDeclaration";
        if ((string?)row["encoder"]!["state"] == "disconnected") return "disconnected";
        if ((string?)row["encoder"]!["state"] != "connected" || (string?)report["encoderCoverage"]?["ownerResolution"] != "capturedObject") return "unresolved";
        var evidence = row["targetEvidence"]!;
        if ((string?)evidence["state"] != "suppliedTarget") return "unresolved";
        bool resolved = (string?)row["encoder"]!["route"] switch
        {
            "module" or "channel" => (string?)evidence["moduleResolution"] == "capturedObject",
            "splitModules" => (string?)evidence["inputModuleResolution"] == "capturedObject" && (string?)evidence["outputModuleResolution"] == "capturedObject",
            "addresses" => (string?)evidence["plcResolution"] == "capturedObject",
            "dbMember" => (string?)evidence["blockResolution"] == "capturedObject",
            "startdriveTelegram" => (string?)evidence["telegramResolution"] == "retainedDeclaration" && (string?)evidence["driveHardwareResolution"] == "capturedObject",
            _ => false
        };
        return resolved ? "resolvedDeclaration" : "unresolved";
    }

    internal static IEnumerable<(string Role, JsonNode Selector)> Targets(JsonNode row)
    {
        var target = row["encoder"]!["target"]; if (target is null) yield break;
        switch ((string?)row["encoder"]!["route"])
        {
            case "module": yield return ("module", target); break;
            case "splitModules": foreach (string field in new[] { "inputModule", "outputModule" }) yield return (field, target[field]!); break;
            case "channel": yield return ("module", target["module"]!); break;
            case "addresses": yield return ("plc", target["plc"]!); break;
            case "dbMember": yield return ("block", target["block"]!); break;
        }
    }

    internal static string? Mapping(string purpose, JsonNode source, JsonNode? sourceEvidence, JsonNode? sourceProfile,
        JsonNode target, JsonNode? targetEvidence, JsonNode? targetProfile, JsonArray constraints)
    {
        string? from = (string?)source["kind"], to = (string?)target["kind"];
        bool Typed(string? kind) => kind is "driveTelegram" or "driveTelegramAddress" or "technologyEncoder";
        bool Domain(string? kind) => Typed(kind) || kind is "model" or "connectionEndpoint" or "driveParameter" or "technologyRelationship";
        void Constraint(string kind, string state) => constraints.Add(new JsonObject { ["kind"] = kind, ["state"] = state });
        if (purpose == "dependent" && Domain(from) && Domain(to) && (Typed(from) || Typed(to))) return "declaredAssociation";
        if (purpose == "telegram" && to is "driveTelegram" or "driveTelegramAddress" &&
            (from == "amlAttribute" || from == "amlObject" && (string?)sourceEvidence?["scope"] == "instance" &&
                (string?)sourceEvidence?["kind"] is "InternalElement" or "ExternalInterface"))
        {
            Constraint("nativeEligibility", "notVerified"); return "declaredAssociation";
        }
        if (purpose != "encoder" || to != "technologyEncoder" || from is not ("driveTelegram" or "model")) return null;
        Constraint("nativeEligibility", "notVerified");
        if (targetEvidence is null || sourceEvidence is null) return "unresolvedSelection";
        var encoder = targetEvidence["encoder"]!; var original = encoder["target"]; bool exact;
        if (original is null) return "unresolvedSelection";
        if (from == "driveTelegram")
        {
            if ((string?)encoder["route"] != "startdriveTelegram") return "unsupportedSemantics";
            exact = original is not null && (string?)original["recordId"] == (string?)source["recordRef"] &&
                (string?)original["phase"] == (string?)source["phase"] && (string?)original["telegramId"] == (string?)source["id"];
            Constraint("originalTelegramReference", exact ? "exactReference" : "referenceMismatch");
            bool domain = JsonNode.DeepEquals(sourceProfile?["valueDomain"], targetProfile?["valueDomain"]);
            Constraint("valueDomain", domain ? "equalDeclarations" : "differentDeclarations");
            string number = (string)targetEvidence["targetEvidence"]!["externalEncoderNumberCheck"]!;
            Constraint("externalEncoderNumber", number);
            return exact && domain && number != "differentNumber" ? "declaredAssociation" : "constraintMismatch";
        }
        if ((string?)encoder["route"] is "startdriveTelegram" or "unknown") return "unsupportedSemantics";
        exact = Targets(targetEvidence).Any(t => (string?)t.Selector["nodeId"] == (string?)source["nodeId"] && (string?)t.Selector["objectPath"] == (string?)source["objectPath"]);
        Constraint("originalObjectReference", exact ? "exactReference" : "referenceMismatch");
        return exact ? "declaredAssociation" : "constraintMismatch";
    }
}
