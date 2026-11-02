using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace TiaMcp.Core;

public sealed record OpennessWorkerIdentity(string Path, string DateModified, string FileHash);
public sealed record OpennessAllowListEntry(string Name, string? Path, string? DateModified, string? FileHash);
public sealed record OpennessRegistrationReport(string Status, string WorkerPath, string? RegistryEntry,
    IReadOnlyList<string> Mismatches, string Message);

/// <summary>Read-only application authorization diagnostics. Never opens Siemens APIs or writes registry values.</summary>
public static class OpennessRegistration
{
    public const string AllowListPath = @"SOFTWARE\Siemens\Automation\Openness\AllowList";
    private const string Action = "Run scripts/Setup-TiaMcp.ps1 for a verified bundle at its final path, with the approved release hash; " +
        "for a source build, explicitly register that build or approve it in TIA. Runtime startup does not change trust.";

    public static OpennessRegistrationReport Inspect(string workerPath)
    {
        if (!OperatingSystem.IsWindows())
            return new("unsupported_platform", workerPath, null, [], "Openness registration inspection requires Windows.");
        try
        {
            var path = System.IO.Path.GetFullPath(workerPath);
            // Hold against replacement/writes while capturing identity, just as setup does.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var identity = new OpennessWorkerIdentity(path,
                File.GetLastWriteTimeUtc(path).ToString("yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                Convert.ToBase64String(SHA256.HashData(stream)));
            return Evaluate(identity, ReadEntries(path));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new("worker_missing", workerPath, null, [], $"Openness worker was not found: {workerPath}. Check the configured --worker path.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return new("unavailable", workerPath, null, [], $"Cannot read the worker identity or Registry64 AllowList for {workerPath}. Registration is unknown; check file/registry read access. {Action}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<OpennessAllowListEntry> ReadEntries(string workerPath)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var app = machine.OpenSubKey(AllowListPath + @"\" + System.IO.Path.GetFileName(workerPath), writable: false);
        if (app is null) return [];
        var result = new List<OpennessAllowListEntry>();
        foreach (var name in app.GetSubKeyNames().Order(StringComparer.Ordinal))
        {
            using var entry = app.OpenSubKey(name, writable: false);
            if (entry is null) continue;
            string? ReadString(string value) => entry.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string text &&
                entry.GetValueKind(value) == RegistryValueKind.String ? text : null;
            result.Add(new(name, ReadString("Path"), ReadString("DateModified"), ReadString("FileHash")));
        }
        return result;
    }

    // Pure comparison also covers duplicate saved identities and malformed/non-string values.
    public static OpennessRegistrationReport Evaluate(OpennessWorkerIdentity worker, IReadOnlyList<OpennessAllowListEntry> entries)
    {
        var matchingPath = entries.Where(entry => string.Equals(entry.Path, worker.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingPath.Length == 0)
            return new(entries.Count == 0 ? "missing" : "path_not_registered", worker.Path, null, ["Path"],
                $"No saved Openness identity matches worker {worker.Path}. {Action}");
        OpennessRegistrationReport? closest = null;
        foreach (var entry in matchingPath)
        {
            var mismatches = new List<string>();
            if (!string.Equals(entry.DateModified, worker.DateModified, StringComparison.Ordinal)) mismatches.Add("DateModified");
            if (!string.Equals(entry.FileHash, worker.FileHash, StringComparison.Ordinal)) mismatches.Add("FileHash");
            var key = @"HKEY_LOCAL_MACHINE\" + AllowListPath + @"\" + System.IO.Path.GetFileName(worker.Path) + @"\" + entry.Name;
            if (mismatches.Count == 0)
                return new("registered", worker.Path, key, [], $"Saved Openness identity matches {worker.Path}; path, UTC modification time and SHA-256 agree. Group membership and TIA access remain separate prerequisites.");
            var report = new OpennessRegistrationReport("stale", worker.Path, key, mismatches,
                $"Saved Openness identity for {worker.Path} is stale in {entry.Name}: {string.Join(", ", mismatches)} differs. {Action}");
            if (closest is null || mismatches.Count < closest.Mismatches.Count) closest = report;
        }
        return closest!;
    }
}
