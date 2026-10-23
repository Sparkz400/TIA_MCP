using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static string RepairArtifact(JToken reference)
    {
        if (!(reference is JObject) || string.IsNullOrWhiteSpace(reference.Value<string>("path")))
            throw new InvalidOperationException("An exact retained artifact reference is required.");
        string path = Path.GetFullPath(reference.Value<string>("path"));
        if (!File.Exists(path) || Hash(path) != reference.Value<string>("sha256"))
            throw new InvalidOperationException("Repair admission artifact changed: " + path);
        return path;
    }

    internal sealed class RepairPlan
    {
        internal JObject Profile;
        internal string Archive, SourcePath, Output, ReviewPath;
        internal string[] Owners;
        internal int Minutes;
    }

    internal static bool RepairUiCaptureRequested(JObject command, string reviewHash, int processId, string projectPath)
    {
        string action = command.Value<string>("command");
        if ((action != "capture-saved-close" && action != "abort-close") ||
            command.Value<string>("reviewSha256") != reviewHash || command.Value<int?>("processId") != processId ||
            command.Value<string>("projectPath") != projectPath)
            throw new InvalidOperationException("The command is not bound to this exact owned editor and review.");
        return action == "capture-saved-close";
    }

    // File-only admission. Linked into pure tests without Siemens references.
    internal static RepairPlan ValidateRepairUiAdmission(JObject admission, string helperPath)
    {
        if (admission.Value<bool?>("ownedFixture") != true || admission.Value<bool?>("sequentialNativeAdmission") != true ||
            admission.Value<bool?>("checkpointRetrievalGatePassed") != true || admission.Value<bool?>("automaticReplay") != false ||
            admission.Value<bool?>("runtimeAllowed") != false || admission.Value<string>("helperSha256") != Hash(helperPath))
            throw new InvalidOperationException("Exact sequential fixture-only repair admission is required.");
        string archive = RepairArtifact(admission["archive"]), sourcePath = RepairArtifact(admission["sourceReport"]);
        string recoveryPath = RepairArtifact(admission["recoveryGate"]);
        string windowPath = RepairArtifact(admission["storeWindowIntent"]), reviewPath = RepairArtifact(admission["review"]);
        var source = JObject.Parse(File.ReadAllText(sourcePath));
        var profile = ((JArray)source["profiles"]).OfType<JObject>().Single(row =>
            string.Equals(row["archive"]?.Value<string>("path"), archive, StringComparison.OrdinalIgnoreCase) &&
            row["archive"].Value<string>("sha256") == Hash(archive));
        string selectedProfile = profile.Value<string>("profile");
        if (!new[] { "classic-comfort", "unified-comfort", "unified-pc" }.Contains(selectedProfile, StringComparer.Ordinal) ||
            source.Value<bool?>("completed") != true || profile.Value<string>("fixtureMode") != "positive" ||
            profile.Value<bool?>("archiveRetrievalVerified") != true ||
            profile.Value<bool?>("deliberateInvalidReferences") != false ||
            !Path.GetFileName(profile.Value<string>("projectPath")).StartsWith("TiaMcpHmi_", StringComparison.Ordinal))
            throw new InvalidOperationException("Only an unchanged independently authored positive fixture archive is admitted.");
        var recovery = JObject.Parse(File.ReadAllText(recoveryPath));
        var sessions = recovery["sessions"] as JArray;
        if (recovery.Value<bool?>("completed") != true ||
            recovery["preservation"]?.Value<bool?>("checkpointRecovery") != true ||
            recovery["preservation"]?.Value<bool?>("recoveredFreshReopen") != true ||
            sessions == null || sessions.Count == 0 ||
            sessions.Any(session => session["cleanup"]?.Value<bool?>("completed") != true))
            throw new InvalidOperationException("Completed checkpoint recovery, fresh reopen and every owned cleanup are required.");
        string output = Path.GetFullPath(admission.Value<string>("outputDirectory"));
        string root = Path.GetFullPath(admission.Value<string>("artifactRoot")).TrimEnd(Path.DirectorySeparatorChar);
        if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(output) || File.Exists(output))
            throw new InvalidOperationException("A new exact output below the admitted artifact root is required.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(output)); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse ancestry is not admitted.");
        var window = JObject.Parse(File.ReadAllText(windowPath));
        if (window.Value<string>("kind") != "held-coordinator-store-window" ||
            window.Value<string>("mode") != "isolatedQualificationMutation" || window.Value<bool?>("readOnly") != false ||
            window.Value<bool?>("sameHandlesHeldThroughDispatch") != true || window.Value<bool?>("automaticReplay") != false ||
            !((JArray)window["resources"]).OfType<JObject>().Any(r => string.Equals(r.Value<string>("path"), output, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The exact output resource must be included in the held coordinator window.");
        var review = JObject.Parse(File.ReadAllText(reviewPath));
        string[] owners = ((JArray)profile["nativeBefore"]).Select(row => row.Value<string>("name")).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (owners.Length == 0 || owners.Distinct(StringComparer.Ordinal).Count() != owners.Length || owners.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Distinct nonempty HMI owners are required.");
        if (review.Value<bool?>("approved") != true || review.Value<string>("sourceArchiveSha256") != Hash(archive) ||
            !owners.SequenceEqual(((JArray)review["owners"]).Values<string>().OrderBy(n => n, StringComparer.Ordinal)) ||
            review.Value<string>("allowedEdit") != (profile.Value<string>("profile").StartsWith("unified-", StringComparison.Ordinal)
                ? "configure-encrypted-transfer-password-and-confirmation" : "configure-existing-start-screen") ||
            review.Value<bool?>("disableSecurityAllowed") != false || review.Value<bool?>("executeScriptsAllowed") != false)
            throw new InvalidOperationException("Exact owner and prerequisite-only review is required.");
        int minutes = admission.Value<int?>("uiTimeoutMinutes") ?? 30;
        if (minutes < 1 || minutes > 60) throw new InvalidOperationException("UI wait must be bounded to 1..60 minutes.");
        return new RepairPlan { Profile = profile, Archive = archive, SourcePath = sourcePath,
            Output = output, ReviewPath = reviewPath, Owners = owners, Minutes = minutes };
    }
}
