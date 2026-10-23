using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.OpcUa;

// Local files only; the offline library never fetches dependencies or connects to a target.
try
{
    JsonObject result;
    string output;
    int exitCode = 0;
    if (args.Length == 3 && args[0] == "inspect")
    {
        result = OpcUaModelReview.Inspect(ReadBounded(args[1], OpcUaModelReview.MaxBytes)); output = args[2];
    }
    else if (args.Length is 4 or 5 && args[0] == "compare")
    {
        var consumers = args.Length == 5 ? JsonNode.Parse(ReadBounded(args[4], 1024 * 1024)) as JsonObject
            ?? throw new BridgeException("OpcUaInvalidConsumers", "Consumer manifest must be an object.") : null;
        result = OpcUaModelReview.Compare(ReadBounded(args[1], OpcUaModelReview.MaxBytes),
            ReadBounded(args[2], OpcUaModelReview.MaxBytes), consumers); output = args[3];
    }
    else if (args.Length >= 3 && args[0] == "dependencies")
    {
        output = args[1];
        result = OpcUaDependencyAnalysis.Analyze(ReadSources(args[2..]));
        if (!result["selectedDependenciesResolved"]!.GetValue<bool>()) exitCode = 3;
    }
    else if (args.Length >= 2 && args[0] == "compare-set")
    {
        output = args[1];
        var before = new List<string>();
        var after = new List<string>();
        string? consumerPath = null;
        for (int i = 2; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length) throw new BridgeException("OpcUaUsage", "Each compare-set option requires a file path.");
            switch (args[i])
            {
                case "--before": before.Add(args[i + 1]); break;
                case "--after": after.Add(args[i + 1]); break;
                case "--consumers" when consumerPath is null: consumerPath = args[i + 1]; break;
                default: throw new BridgeException("OpcUaUsage", "Unknown or repeated compare-set option: " + args[i]);
            }
        }
        if (before.Count == 0 || after.Count == 0) throw new BridgeException("OpcUaUsage", "compare-set requires at least one --before and --after file.");
        var consumers = consumerPath is null ? null : JsonNode.Parse(ReadBounded(consumerPath, 1024 * 1024)) as JsonObject
            ?? throw new BridgeException("OpcUaInvalidConsumers", "Consumer manifest must be an object.");
        result = OpcUaDependencyAnalysis.Compare(ReadSources(before), ReadSources(after), consumers);
        if (!result["before"]!["selectedDependenciesResolved"]!.GetValue<bool>() ||
            !result["after"]!["selectedDependenciesResolved"]!.GetValue<bool>()) exitCode = 3;
    }
    else
    {
        Console.Error.WriteLine("Offline only. Usage:\n  inspect model.xml report.json\n  compare before.xml after.xml report.json [consumers.json]\n  dependencies report.json model.xml [dependency.xml ...]\n  compare-set report.json --before model.xml [--before dependency.xml ...] --after model.xml [--after dependency.xml ...] [--consumers consumers-v2.json]\nExit codes: 0 completed, 1 invalid input/I/O, 2 usage, 3 report written with unresolved dependencies.");
        return args.Length == 1 && args[0] is "--help" or "-h" ? 0 : 2;
    }
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions { WriteIndented = true });
    if (bytes.Length > OpcUaModelReview.MaxReportBytes) throw new BridgeException("OpcUaCapacity", "Formatted report exceeds 32 MiB.");
    // Never replace input, evidence or another session's output.
    using (var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) destination.Write(bytes);
    Console.WriteLine(Path.GetFullPath(output));
    Console.WriteLine("Offline review only; no target, PLC mapping, import or runtime qualification.");
    return exitCode;
}
catch (Exception ex) when (ex is BridgeException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or ArgumentException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { code = ex is BridgeException bridge ? bridge.Code : "OpcUaReviewFailed", message = ex.Message }));
    return ex is BridgeException { Code: "OpcUaUsage" } ? 2 : 1;
}

static OpcUaNodeSetSource[] ReadSources(IReadOnlyList<string> paths)
{
    if (paths.Count is < 1 or > OpcUaDependencyAnalysis.MaxFiles)
        throw new BridgeException("OpcUaCapacity", "Supply 1 to 32 NodeSet files per snapshot.");
    var sources = new List<OpcUaNodeSetSource>();
    var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    int total = 0;
    foreach (string path in paths)
    {
        string id = Path.GetFullPath(path);
        if (!seen.Add(id)) throw new BridgeException("OpcUaInvalidSources", "A NodeSet path was supplied more than once: " + id);
        var bytes = ReadBounded(id, Math.Min(OpcUaModelReview.MaxBytes, OpcUaDependencyAnalysis.MaxTotalBytes - total));
        total += bytes.Length;
        sources.Add(new(id, bytes));
    }
    return sources.ToArray();
}

static byte[] ReadBounded(string path, int limit)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (stream.Length == 0 || stream.Length > limit) throw new BridgeException("OpcUaCapacity", "Input is empty or exceeds its byte limit.");
    var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
    if (stream.ReadByte() != -1) throw new BridgeException("ArtifactChanged", "Input grew during read.");
    return bytes;
}
