using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    internal sealed class ConfiguredVerifyPlan
    {
        internal string ConfiguredResult, Project, Archive, Output, Phase;
        internal string[] Owners;
    }

    private static void VerifyConfiguredTerminal(JObject wrapper, JToken terminalReference, string wrapperPath)
    {
        var proofReference = wrapper["processProof"]?["jobProof"];
        var proof = JObject.Parse(File.ReadAllText(RepairArtifact(proofReference)));
        var terminal = JObject.Parse(File.ReadAllText(RepairArtifact(terminalReference)));
        var intent = JObject.Parse(File.ReadAllText(RepairArtifact(terminal["intent"])));
        string source = RepairArtifact(intent["source"]);
        if (!new[] { "activeProcesses", "totalProcesses", "limitTerminatedProcesses", "limitFlags" }.All(k => proof[k]?.Type == JTokenType.Integer) ||
            proof.Value<string>("schemaVersion") != "owned-job-terminal.v1" || proof.Value<bool?>("createdAtomicallyInJob") != true ||
            proof.Value<bool?>("helperHandleSignaled") != true || proof.Value<int?>("activeProcesses") != 0 ||
            proof.Value<int?>("totalProcesses") < 1 || proof.Value<int?>("totalProcesses") == null ||
            proof.Value<int?>("limitTerminatedProcesses") != 0 || proof.Value<int?>("limitFlags") != 0 ||
            proof.Value<bool?>("automaticTermination") != false || terminal.Value<string>("schemaVersion") != "native-dispatch-terminal.v1" ||
            terminal.Value<bool?>("originalAttemptPromoted") != false || !JToken.DeepEquals(terminal["jobEvidence"], proofReference) ||
            intent.Value<string>("schemaVersion") != "native-dispatch-intent.v1" || intent.Value<bool?>("containmentRequiredFromCreation") != true ||
            string.IsNullOrWhiteSpace(proof.Value<string>("jobName")) || !proof.Value<string>("jobName").StartsWith("Local\\TiaMcp-Owned-", StringComparison.Ordinal) ||
            intent.Value<string>("jobName") != proof.Value<string>("jobName") ||
            !string.Equals(source, Path.Combine(Path.GetDirectoryName(wrapperPath), "admission.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Original terminal job must bind the exact configured dispatch intent.");
    }

    internal static ConfiguredVerifyPlan ValidateConfiguredVerifyAdmission(JObject admission, string helperPath)
    {
        if (admission.Value<bool?>("ownedFixture") != true || admission.Value<bool?>("sequentialNativeAdmission") != true ||
            admission.Value<bool?>("automaticReplay") != false || admission.Value<bool?>("runtimeAllowed") != false ||
            admission.Value<string>("helperSha256") != Hash(helperPath))
            throw new InvalidOperationException("Exact owned configured-verification admission is required.");
        string sourcePath = RepairArtifact(admission["configuredResult"]), archive = RepairArtifact(admission["archive"]);
        var source = JObject.Parse(File.ReadAllText(sourcePath));
        VerifyConfiguredTerminal(source, admission["configuredTerminal"], sourcePath);
        var native = source["nativeResult"] as JObject;
        if (source.Value<bool?>("completed") != true || source.Value<int?>("helperExitCode") != 0 ||
            source["processProof"]?.Value<bool?>("completed") != true || native == null || native.Value<bool?>("completed") != true ||
            native.Value<bool?>("savedConfiguredArchiveCaptured") != true || native.Value<bool?>("explicitOwnedSdkDispose") != true ||
            native.Value<bool?>("retainedHandleExitVerified") != true || native.Value<bool?>("sourceArchiveUnchanged") != true ||
            !JToken.DeepEquals(native["archive"], admission["archive"]))
            throw new InvalidOperationException("Completed configured fixture and its original archive are required.");
        string rawPath = Path.Combine(Path.GetDirectoryName(sourcePath), "native", "result.json");
        if (!File.Exists(rawPath) || !JToken.DeepEquals(native, JObject.Parse(File.ReadAllText(rawPath))))
            throw new InvalidOperationException("Original raw configured native result differs.");
        string phase = admission.Value<string>("phase");
        if (phase != "reopen-retrieve" && phase != "recovered-reopen") throw new InvalidOperationException("Exact qualification phase required.");
        string project = native.Value<string>("projectPath");
        if (phase == "recovered-reopen")
        {
            string priorPath = RepairArtifact(admission["priorResult"]);
            var prior = JObject.Parse(File.ReadAllText(priorPath));
            VerifyConfiguredTerminal(prior, admission["priorTerminal"], priorPath);
            if (prior.Value<bool?>("completed") != true || prior.Value<int?>("helperExitCode") != 0 || prior["processProof"]?.Value<bool?>("completed") != true ||
                prior["nativeResult"]?.Value<bool?>("completed") != true ||
                prior["nativeResult"]?.Value<string>("phase") != "reopen-retrieve" ||
                !JToken.DeepEquals(prior["nativeResult"]?["configuredResult"], admission["configuredResult"]))
                throw new InvalidOperationException("Exact completed first recovery lifetime required.");
            if (!JToken.DeepEquals(prior["nativeResult"], JObject.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(priorPath), "native", "result.json")))))
                throw new InvalidOperationException("Original raw recovery native result differs.");
            project = prior["nativeResult"].Value<string>("recoveredProjectPath");
            string recoveryRoot = Path.Combine(Path.GetDirectoryName(priorPath), "native", "recovered") + Path.DirectorySeparatorChar;
            if (project == null || !Path.GetFullPath(project).StartsWith(recoveryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Recovered project escapes the original owned recovery root.");
        }
        string configuredRoot = Path.Combine(Path.GetDirectoryName(sourcePath), "native") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(native.Value<string>("projectPath")).StartsWith(configuredRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configured project escapes the original owned root.");
        if (!File.Exists(project) || !Path.GetFileName(project).StartsWith("TiaMcpHmi_", StringComparison.Ordinal) ||
            Path.GetExtension(project) != ".ap21" || !string.Equals(Path.GetFullPath(project), admission.Value<string>("projectPath"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Exact existing owned project identity required.");
        string output = Path.GetFullPath(admission.Value<string>("outputDirectory"));
        string root = Path.GetFullPath(admission.Value<string>("artifactRoot")).TrimEnd(Path.DirectorySeparatorChar);
        if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(output) || File.Exists(output) ||
            output.StartsWith(Path.GetDirectoryName(project) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A new nonoverlapping verification output is required.");
        foreach (string path in new[] { output, project, sourcePath, archive })
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)); parent != null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Reparse ancestry is not admitted.");
        var window = JObject.Parse(File.ReadAllText(RepairArtifact(admission["storeWindowIntent"])));
        var resources = window["resources"] as JArray;
        if (window.Value<string>("kind") != "held-coordinator-store-window" || window.Value<bool?>("readOnly") != false ||
            window.Value<string>("mode") != "isolatedQualificationMutation" || window.Value<bool?>("sameHandlesHeldThroughDispatch") != true ||
            window.Value<bool?>("automaticReplay") != false || resources == null ||
            !resources.OfType<JObject>().Any(r => r.Value<string>("kind") == "workspace" && string.Equals(r.Value<string>("path"), output, StringComparison.OrdinalIgnoreCase)) ||
            !resources.OfType<JObject>().Any(r => r.Value<string>("kind") == "project" && string.Equals(r.Value<string>("path"), project, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Exact held project and output resources are required.");
        var owners = ((JArray)native["after"]).Select(row => row.Value<string>("name")).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (owners.Length == 0 || owners.Any(string.IsNullOrWhiteSpace) || owners.Distinct(StringComparer.Ordinal).Count() != owners.Length)
            throw new InvalidOperationException("Exact nonempty distinct configured owners required.");
        return new ConfiguredVerifyPlan { ConfiguredResult = sourcePath, Archive = archive, Project = project, Output = output, Phase = phase, Owners = owners };
    }
}
