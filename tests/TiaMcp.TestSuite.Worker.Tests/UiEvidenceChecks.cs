using System;
using System.IO;
using Newtonsoft.Json.Linq;
using TiaMcp.TestSuite.V21;

internal static class UiEvidenceChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        string root = Path.Combine(Path.GetTempPath(), "ts-ui-fake-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        // Deliberate fake image header tests transport integrity, never native visual correctness.
        var image = new byte[2048]; image[0] = 255; image[1] = 216; image[2] = 255;
        File.WriteAllBytes(Path.Combine(root, "authored-fake.jpg"), image);
        var now = DateTime.UtcNow;
        var challenge = new JObject { ["nonce"] = Guid.NewGuid().ToString("N"), ["issuedUtc"] = now.ToString("o"),
            ["projectPath"] = "owned", ["processId"] = 123, ["sessionEpoch"] = "epoch", ["ruleName"] = "rule", ["manifestSha256"] = "manifest",
            ["requireLicenseCheck"] = true, ["selectedProgramBlocks"] = new JArray("Main", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB", "TiaMcp_IsEnabled"),
            ["excludedBranches"] = new JArray("Software Units", "PLC-Variablen", "PLC-Datentypen") };
        var response = new JObject { ["method"] = "operator-read-native-scope-editor", ["challenge"] = challenge.DeepClone(),
            ["selectedProgramBlocks"] = challenge["selectedProgramBlocks"].DeepClone(), ["excludedBranches"] = challenge["excludedBranches"].DeepClone(),
            ["allLeavesVisible"] = true, ["checkboxesChanged"] = false, ["objectSelector"] = "CodeBlock_FB_FC_OB", ["ruleEnabled"] = true,
            ["screenshots"] = new JArray(new JObject { ["file"] = "authored-fake.jpg", ["sha256"] = StyleUiEvidence.Hash(image), ["purpose"] = "scope" },
                new JObject { ["file"] = "authored-fake.jpg", ["sha256"] = StyleUiEvidence.Hash(image), ["purpose"] = "license" }),
            ["license"] = new JObject { ["product"] = "TIA Portal Test Suite Advanced V21", ["method"] = "Automation License Manager Check", ["validKeys"] = 1, ["defectiveKeys"] = 0 } };
        check(JToken.DeepEquals(response, StyleUiEvidence.Validate(challenge, response, root, now)), "UI transport binds the exact current challenge; fake image does not qualify native scope");
        foreach (string field in new[] { "nonce", "projectPath", "processId", "sessionEpoch", "ruleName", "manifestSha256" })
        {
            var changed = (JObject)response.DeepClone(); changed["challenge"][field] = "different";
            reject(() => StyleUiEvidence.Validate(challenge, changed, root, now), "exact current challenge");
        }
        reject(() => StyleUiEvidence.Validate(challenge, response, root, now.AddMinutes(16)), "expired");
        reject(() => StyleUiEvidence.Validate(challenge, response, root, now.AddSeconds(-1)), "expired");
        foreach (string field in new[] { "selectedProgramBlocks", "excludedBranches" })
        {
            var changed = (JObject)response.DeepClone(); ((JArray)changed[field]).RemoveAt(0);
            reject(() => StyleUiEvidence.Validate(challenge, changed, root, now), "incomplete");
        }
        foreach (string field in new[] { "allLeavesVisible", "checkboxesChanged", "ruleEnabled" })
        {
            var changed = (JObject)response.DeepClone(); changed[field] = !changed.Value<bool>(field);
            reject(() => StyleUiEvidence.Validate(challenge, changed, root, now), "TestSuiteScopeIncomplete");
        }
        var noLicense = (JObject)response.DeepClone(); noLicense.Remove("license");
        reject(() => StyleUiEvidence.Validate(challenge, noLicense, root, now), "valid-key check");
        var defective = (JObject)response.DeepClone(); defective["license"]["defectiveKeys"] = 1;
        reject(() => StyleUiEvidence.Validate(challenge, defective, root, now), "valid-key check");
        var escape = (JObject)response.DeepClone(); escape["screenshots"][0]["file"] = "../outside.jpg";
        reject(() => StyleUiEvidence.Validate(challenge, escape, root, now), "local challenge artifact");
        image[9] = 1; File.WriteAllBytes(Path.Combine(root, "authored-fake.jpg"), image);
        reject(() => StyleUiEvidence.Validate(challenge, response, root, now), "bytes changed");
        string duplicate = Path.Combine(root, "duplicate.json"); File.WriteAllText(duplicate, "{\"nonce\":1,\"nonce\":2}");
        reject(() => StyleUiEvidence.Read(duplicate), "already exists");
    }
}
