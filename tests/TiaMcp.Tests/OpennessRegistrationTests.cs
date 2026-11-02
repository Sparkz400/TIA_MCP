using TiaMcp.Core;

namespace TiaMcp.Tests;

internal static class OpennessRegistrationTests
{
    private static readonly OpennessWorkerIdentity Worker = new(@"C:\TIA MCP\workers\openness\TiaMcp.Openness.V21.exe", "2026/11/02 12:00:00.123", "approved-hash");
    private static OpennessAllowListEntry Current(string name = "Entry") => new(name, Worker.Path, Worker.DateModified, Worker.FileHash);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static Task MissingAndOtherInstallations()
    {
        var absent = OpennessRegistration.Evaluate(Worker, []);
        var relocated = OpennessRegistration.Evaluate(Worker, [Current() with { Path = @"D:\Other\TiaMcp.Openness.V21.exe" }]);
        Require(absent.Status == "missing" && relocated.Status == "path_not_registered", "Missing and different installations must be distinguished.");
        Require(relocated.WorkerPath == Worker.Path && relocated.Mismatches.SequenceEqual(["Path"]) && relocated.RegistryEntry is null, "Do not attribute another installation's key to this worker.");
        return Task.CompletedTask;
    }

    public static Task TimestampAndHashAreIndependent()
    {
        foreach (var (entry, expected) in new[] {
            (Current() with { DateModified = "old-time" }, new[] { "DateModified" }),
            (Current() with { FileHash = "old-hash" }, new[] { "FileHash" }),
            (Current() with { DateModified = null, FileHash = null }, new[] { "DateModified", "FileHash" }) })
        {
            var report = OpennessRegistration.Evaluate(Worker, [entry]);
            Require(report.Status == "stale" && report.Mismatches.SequenceEqual(expected), "Stale reason must identify exactly the changed/malformed fields.");
            Require(report.Message.Contains(Worker.Path) && report.RegistryEntry!.EndsWith(@"\Entry"), "Diagnostic must identify executable and saved entry.");
        }
        return Task.CompletedTask;
    }

    public static Task ValidDuplicateWins()
    {
        var entries = new[] { Current() with { FileHash = "stale" }, Current("Entry (8)") with { Path = Worker.Path.ToUpperInvariant() } };
        foreach (var order in new[] { entries, entries.Reverse().ToArray() })
        {
            var report = OpennessRegistration.Evaluate(Worker, order);
            Require(report.Status == "registered" && report.RegistryEntry!.EndsWith(@"\Entry (8)"), "An obsolete duplicate must not hide a valid saved identity; path comparison is case-insensitive.");
        }
        return Task.CompletedTask;
    }

    public static Task MalformedPathIsNotTrusted()
    {
        var report = OpennessRegistration.Evaluate(Worker, [Current() with { Path = null }]);
        Require(report.Status == "path_not_registered", "A missing/non-string path must not authorize the worker.");
        return Task.CompletedTask;
    }

    public static Task MissingWorkerDoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "TiaMcp.Openness.V21.exe");
        var report = OpennessRegistration.Inspect(path);
        Require(report.Status == (OperatingSystem.IsWindows() ? "worker_missing" : "unsupported_platform"), "Read-only startup diagnosis must handle an absent worker without throwing.");
        Require(!Directory.Exists(Path.GetDirectoryName(path)), "Inspection created a directory.");
        return Task.CompletedTask;
    }
}
