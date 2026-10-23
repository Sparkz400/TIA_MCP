using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.TestSuite;

public sealed record TestSuiteSystemScope(string ServerAddress, string FolderPath, string ServerInterface);
/// <summary>Public typed scope properties captured with the exact accompanying native export.</summary>
public sealed record TestSuiteSystemScopeReadback(string ExportSha256, string EvidenceSha256, string ProjectFilePath,
    string ProjectOpenEpoch, TestSuiteSystemScope Scope);
public sealed record TestSuiteSystemScopeDifference(string Stage, string Property, string Expected, string Observed);
public sealed record TestSuiteSystemScopePreservationReport(string ReviewedSha256, string ExportSha256,
    string? ReopenedExportSha256, TestSuiteSystemScopeDifference[] Differences, string[] EvidenceSha256,
    bool ExportScopePreserved, bool NativeReadbackVerified, bool FreshReopenVerified)
{
    public bool ScopePreservationPassed => ExportScopePreserved && NativeReadbackVerified && FreshReopenVerified;
    public bool NativeExecutionAuthorized => false;
    public bool FullDefinitionPreservationVerified => false;
}

/// <summary>Read-only guard for the retained V21 TST property profile; never repairs or executes a definition.</summary>
public static partial class TestSuiteSystemScopePreservation
{
    private static readonly string[] Properties = ["AUTHOR", "VERSION", "COMMENT", "SERVER_ADDRESS", "FOLDER_PATH", "SELECTED_SERVER_INTERFACE"];

    public static TestSuiteSystemScope ReadScope(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 1 or > 16 * 1024 * 1024)
            throw Invalid("A nonempty bounded retained TST artifact is required.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw Invalid("The retained TST profile requires valid UTF-8."); }
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > 100000 || lines.Any(l => l.Length > 8192 || l.Any(c => char.IsControl(c) && c != '\t')))
            throw Invalid("TST line bounds or characters exceed the retained profile.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int section = 0, cases = 0, ends = 0;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith("TEST_CASE ", StringComparison.Ordinal)) { cases++; continue; }
            if (line == "END_TEST_CASE") { ends++; continue; }
            if (line == "PROPERTY")
            {
                if (section != 0 || cases != 1 || ends != 0) throw Invalid("Exactly one property section inside one test case is supported.");
                section = 1; continue;
            }
            if (line == "END_PROPERTY")
            {
                if (section != 1) throw Invalid("Unmatched property section.");
                section = 2; continue;
            }
            if (section != 1 || line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var match = PropertyLine().Match(line);
            if (!match.Success || !Properties.Contains(match.Groups[1].Value, StringComparer.Ordinal) ||
                !values.TryAdd(match.Groups[1].Value, match.Groups[2].Value))
                throw Invalid("Unrecognized, escaped or duplicate property syntax requires a separately qualified profile.");
        }
        if (section != 2 || cases != 1 || ends != 1 || Properties.Any(p => !values.ContainsKey(p)))
            throw Invalid("The retained profile requires one complete test case and all six explicit properties.");
        // Only the genuine Standard_SIMATIC spelling is qualified by the retained fixture. Other interface
        // enum spellings must come from a new native fixture, not an inferred mapping from the typed API.
        if (values["SELECTED_SERVER_INTERFACE"] != "Standard_SIMATIC")
            throw Invalid("Only the retained Standard_SIMATIC textual profile is qualified for scope comparison.");
        return new(values["SERVER_ADDRESS"], values["FOLDER_PATH"], values["SELECTED_SERVER_INTERFACE"]);
    }

    public static TestSuiteSystemScopePreservationReport Compare(byte[] reviewed, byte[] exported,
        TestSuiteSystemScopeReadback? readback = null, byte[]? reopenedExport = null,
        TestSuiteSystemScopeReadback? reopenedReadback = null)
    {
        var expected = ReadScope(reviewed);
        var actual = ReadScope(exported);
        string reviewedHash = Hash(reviewed), exportHash = Hash(exported);
        var differences = new List<TestSuiteSystemScopeDifference>();
        var evidence = new List<string>();
        CompareScope("export", expected, actual, differences);
        bool exportPreserved = differences.Count == 0;
        bool verified = CheckReadback("readback", expected, actual, exportHash, readback, differences, evidence);
        string? reopenHash = null;
        bool reopened = false;
        if (reopenedExport is not null)
        {
            var reopenScope = ReadScope(reopenedExport); reopenHash = Hash(reopenedExport);
            int before = differences.Count;
            CompareScope("freshReopenExport", expected, reopenScope, differences);
            reopened = CheckReadback("freshReopenReadback", expected, reopenScope, reopenHash, reopenedReadback, differences, evidence)
                && before == differences.Count;
            if (reopenedReadback is not null && readback is not null)
            {
                if (EngineeringJobs.EngineeringResourceKey.ForPath("project", reopenedReadback.ProjectFilePath) !=
                    EngineeringJobs.EngineeringResourceKey.ForPath("project", readback.ProjectFilePath) ||
                    reopenedReadback.ProjectOpenEpoch == readback.ProjectOpenEpoch || reopenedReadback.EvidenceSha256 == readback.EvidenceSha256)
                    throw new BridgeException("StaleSystemScopeReadback", "Fresh reopen requires the same project, a different open epoch and independent retained evidence.");
            }
            else reopened = false;
        }
        else if (reopenedReadback is not null) throw Invalid("Fresh-reopen readback requires its retained export.");
        return new(reviewedHash, exportHash, reopenHash, differences.ToArray(), evidence.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            exportPreserved, verified, reopened);
    }

    private static bool CheckReadback(string stage, TestSuiteSystemScope expected, TestSuiteSystemScope exported,
        string hash, TestSuiteSystemScopeReadback? readback, List<TestSuiteSystemScopeDifference> differences, List<string> evidence)
    {
        if (readback is null) return false;
        EngineeringIdentityGuard.Sha256(readback.ExportSha256, "exportSha256");
        EngineeringIdentityGuard.Sha256(readback.EvidenceSha256, "evidenceSha256");
        EngineeringJobs.EngineeringResourceKey.ForPath("project", readback.ProjectFilePath);
        if (!TestSuiteRunPlanner.Text(readback.ProjectOpenEpoch, 256))
            throw Invalid("The native project-open observation epoch must be explicit and bounded.");
        if (readback.ExportSha256 != hash) throw new BridgeException("StaleSystemScopeReadback", "Scope readback belongs to different retained export bytes.");
        if (readback.Scope is null || readback.Scope.ServerAddress is null || readback.Scope.FolderPath is null || readback.Scope.ServerInterface is null)
            throw Invalid("All three public scope values must be explicitly retained, including an empty folder path.");
        evidence.Add(readback.EvidenceSha256);
        int before = differences.Count;
        CompareScope(stage, expected, readback.Scope, differences);
        CompareScope(stage + "AgainstExport", exported, readback.Scope, differences);
        return before == differences.Count;
    }

    private static void CompareScope(string stage, TestSuiteSystemScope expected, TestSuiteSystemScope observed,
        List<TestSuiteSystemScopeDifference> differences)
    {
        if (expected.ServerAddress != observed.ServerAddress) differences.Add(new(stage, "SERVER_ADDRESS", expected.ServerAddress, observed.ServerAddress));
        if (expected.FolderPath != observed.FolderPath) differences.Add(new(stage, "FOLDER_PATH", expected.FolderPath, observed.FolderPath));
        if (expected.ServerInterface != observed.ServerInterface) differences.Add(new(stage, "SELECTED_SERVER_INTERFACE", expected.ServerInterface, observed.ServerInterface));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static BridgeException Invalid(string message) => new("UnsupportedSystemScopeProfile", message);
    [GeneratedRegex("^([A-Z_]+)[ \\t]*:[ \\t]*\"([^\"]*)\"[ \\t]*(?://.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyLine();
}
