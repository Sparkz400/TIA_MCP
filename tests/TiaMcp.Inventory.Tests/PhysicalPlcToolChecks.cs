using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Host;

/// <summary>
/// plc_physical_* wrappers against a recording bridge: no worker, no PLC. The wrappers hold no gate of their own. The
/// launch option decides whether they are registered (Program.cs) and the worker refuses with PhysicalOnlineDisabled;
/// the acknowledgement rules are the worker's (PhysicalDownloadRules, covered in TiaMcp.Tests). What is checked here is
/// what the Host owns: argument validation before any bridge call, the exact arguments sent, and how a refusal returns.
/// </summary>
internal static class PhysicalPlcToolChecks
{
    private sealed class RecordingBridge : IBridgeClient
    {
        public List<(string Operation, object? Arguments, CancellationToken Token)> Calls { get; } = [];
        public BridgeException? Refusal;
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((operation, arguments, cancellationToken));
            return Refusal is null ? Task.FromResult(JsonSerializer.SerializeToElement(new { operation, done = true })) : Task.FromException<JsonElement>(Refusal);
        }
    }

    // The argument object as the wrapper built it: property names in declaration order with their values, independent of
    // the bridge serializer.
    private static (string Name, object? Value)[] Sent(object? arguments) =>
        arguments!.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (p.Name, p.GetValue(arguments))).ToArray();

    private static bool Same((string Name, object? Value)[] actual, params (string Name, object? Value)[] expected) =>
        actual.Length == expected.Length && actual.Zip(expected).All(pair => pair.First.Name == pair.Second.Name && Equals(pair.First.Value, pair.Second.Value));

    private static string? Code(CallToolResult result) =>
        result.StructuredContent is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;

    internal static async Task Run(Action<bool, string> check, string artifacts)
    {
        var options = new HostOptions("", artifacts, null, 60, false, "", null) { PhysicalPlcOnline = true };
        var bridge = new RecordingBridge();
        var tools = new PhysicalPlcTools(bridge, options);
        using var source = new CancellationTokenSource();
        var token = source.Token;

        // The launch option and the tool group.
        check(!HostOptions.Parse([]).PhysicalPlcOnline, "Online access to real PLCs is off unless the Host is started with --physical-plc-online");
        check(HostOptions.Parse(["--physical-plc-online"]).PhysicalPlcOnline, "--physical-plc-online turns the option on");
        var declared = typeof(PhysicalPlcTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => (Method: method, Tool: method.GetCustomAttribute<McpServerToolAttribute>())).Where(m => m.Tool is not null)
            .ToDictionary(m => m.Tool!.Name!, m => (m.Tool!, m.Method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? ""), StringComparer.Ordinal);
        check(declared.Keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "plc_physical_download", "plc_physical_online_compare", "plc_physical_station_upload", "plc_physical_targets" }),
            "PhysicalPlcTools declares exactly the four plc_physical_* tools");
        check(declared.Keys.All(name => ToolGroups.Of(name) == "physical"), "Every plc_physical_* tool is in the physical tool group");
        check(declared["plc_physical_download"].Item1 is { Destructive: true, ReadOnly: false, OpenWorld: true }, "plc_physical_download is annotated destructive and open-world");
        check(declared["plc_physical_targets"].Item1 is { ReadOnly: true, Destructive: false, OpenWorld: false } && declared["plc_physical_online_compare"].Item1 is { ReadOnly: true, Destructive: false, OpenWorld: true },
            "plc_physical_targets and plc_physical_online_compare are annotated read-only");
        check(declared["plc_physical_station_upload"].Item1 is { Destructive: false, ReadOnly: false }, "plc_physical_station_upload writes a new project and is not annotated read-only");
        check(declared.Values.All(tool => tool.Item2.Contains("Not qualified on a physical PLC yet", StringComparison.Ordinal)), "Every plc_physical_* description says it is not qualified on a physical PLC");

        // plc_physical_targets
        var result = await tools.Targets(token);
        check(result.IsError != true && bridge.Calls.Count == 1 && bridge.Calls[0].Operation == "plc_physical_targets" && bridge.Calls[0].Token == token &&
            Same(Sent(bridge.Calls[0].Arguments), ("projectName", null), ("plcPath", null)), "plc_physical_targets sends projectName and plcPath only, both unset by default, with the request's cancellation token");
        check(result.StructuredContent!.Value.GetProperty("operation").GetString() == "plc_physical_targets" && ((TextContentBlock)result.Content[0]).Text == result.StructuredContent.Value.GetRawText(),
            "The worker's response is returned as structured content and as the same text");
        await tools.Targets(token, "Project1", "/plc/CPU");
        check(Same(Sent(bridge.Calls[^1].Arguments), ("projectName", "Project1"), ("plcPath", "/plc/CPU")), "plc_physical_targets forwards its selection");

        // plc_physical_online_compare
        bridge.Calls.Clear();
        await tools.OnlineCompare("Intel(R) Ethernet", "192.168.0.1", token);
        check(bridge.Calls.Count == 1 && bridge.Calls[0].Operation == "plc_physical_online_compare" && Same(Sent(bridge.Calls[0].Arguments), ("projectName", null), ("plcPath", null),
            ("pcInterface", "Intel(R) Ethernet"), ("pcInterfaceNumber", 1), ("targetInterface", null), ("mode", "PN/IE"), ("expectedAddress", "192.168.0.1"), ("trustedCertificateSha256", null), ("maxItems", 500)),
            "plc_physical_online_compare defaults: interface number 1, mode PN/IE, no target interface, no trusted certificate, 500 items");
        await tools.OnlineCompare("PC", "10.0.0.2", token, "P", "/plc", 2, "X1", "MPI", new string('a', 64), 2000);
        check(Same(Sent(bridge.Calls[^1].Arguments), ("projectName", "P"), ("plcPath", "/plc"), ("pcInterface", "PC"), ("pcInterfaceNumber", 2), ("targetInterface", "X1"), ("mode", "MPI"),
            ("expectedAddress", "10.0.0.2"), ("trustedCertificateSha256", new string('a', 64)), ("maxItems", 2000)), "plc_physical_online_compare forwards every argument unchanged");
        await tools.OnlineCompare("PC", "10.0.0.2", token, maxItems: 1);
        check((int)Sent(bridge.Calls[^1].Arguments).Single(p => p.Name == "maxItems").Value! == 1, "maxItems 1 is the lower bound");
        bridge.Calls.Clear();
        foreach (int maxItems in new[] { 0, -1, 2001, int.MaxValue, int.MinValue })
        {
            bool refused = false;
            try { _ = tools.OnlineCompare("PC", "10.0.0.2", token, maxItems: maxItems); } catch (McpException error) { refused = error.Message == "maxItems must be between 1 and 2000."; }
            check(refused && bridge.Calls.Count == 0, "plc_physical_online_compare refuses maxItems " + maxItems + " before any bridge call");
        }

        // plc_physical_station_upload
        await tools.StationUpload("PC", "10.0.0.3", token);
        check(bridge.Calls.Count == 1 && bridge.Calls[0].Operation == "plc_physical_station_upload" && Same(Sent(bridge.Calls[0].Arguments), ("pcInterface", "PC"), ("pcInterfaceNumber", 1), ("mode", "PN/IE"),
            ("expectedAddress", "10.0.0.3"), ("trustedCertificateSha256", null), ("artifactDirectory", Path.Combine(artifacts, "uploads"))),
            "plc_physical_station_upload sends the route and the Host's uploads directory, and no project, PLC or target interface");
        check(!Directory.Exists(Path.Combine(artifacts, "uploads")), "The Host does not create the uploads directory itself");
        await tools.StationUpload("PC", "10.0.0.3", token, 3, "TCP/IP", new string('b', 64));
        check(Same(Sent(bridge.Calls[^1].Arguments), ("pcInterface", "PC"), ("pcInterfaceNumber", 3), ("mode", "TCP/IP"), ("expectedAddress", "10.0.0.3"), ("trustedCertificateSha256", new string('b', 64)),
            ("artifactDirectory", Path.Combine(artifacts, "uploads"))), "plc_physical_station_upload forwards every argument unchanged");

        // plc_physical_download
        bridge.Calls.Clear();
        await tools.Download("PC", "10.0.0.4", "PLC_1", true, token);
        check(bridge.Calls.Count == 1 && bridge.Calls[0].Operation == "plc_physical_download" && Same(Sent(bridge.Calls[0].Arguments), ("projectName", null), ("plcPath", null), ("pcInterface", "PC"),
            ("pcInterfaceNumber", 1), ("targetInterface", null), ("mode", "PN/IE"), ("expectedAddress", "10.0.0.4"), ("expectedPlcName", "PLC_1"), ("acknowledgeLiveTarget", true), ("softwareScope", "changes"),
            ("hardware", false), ("acknowledgeStop", false), ("acknowledgeReinitialize", false), ("startAfterDownload", false), ("trustedCertificateSha256", null)),
            "plc_physical_download defaults: changes only, no hardware, no stop, no reinitialization, no start, no trusted certificate");
        await tools.Download("PC", "10.0.0.4", "PLC_1", true, token, "P", "/plc", 2, "X1", "MPI", "all", true, true, true, true, new string('c', 64));
        check(Same(Sent(bridge.Calls[^1].Arguments), ("projectName", "P"), ("plcPath", "/plc"), ("pcInterface", "PC"), ("pcInterfaceNumber", 2), ("targetInterface", "X1"), ("mode", "MPI"),
            ("expectedAddress", "10.0.0.4"), ("expectedPlcName", "PLC_1"), ("acknowledgeLiveTarget", true), ("softwareScope", "all"), ("hardware", true), ("acknowledgeStop", true),
            ("acknowledgeReinitialize", true), ("startAfterDownload", true), ("trustedCertificateSha256", new string('c', 64))), "plc_physical_download forwards every argument and acknowledgement unchanged");
        // Each acknowledgement is its own argument: none is derived from another or from softwareScope/hardware.
        foreach (var (stop, reinitialize, start) in new[] { (true, false, false), (false, true, false), (false, false, true) })
        {
            await tools.Download("PC", "10.0.0.4", "PLC_1", true, token, softwareScope: "all", hardware: true, acknowledgeStop: stop, acknowledgeReinitialize: reinitialize, startAfterDownload: start);
            var sent = Sent(bridge.Calls[^1].Arguments).ToDictionary(p => p.Name, p => p.Value);
            check(Equals(sent["acknowledgeStop"], stop) && Equals(sent["acknowledgeReinitialize"], reinitialize) && Equals(sent["startAfterDownload"], start),
                $"plc_physical_download never infers an acknowledgement (stop {stop}, reinitialize {reinitialize}, start {start})");
        }
        // Without acknowledgeLiveTarget the Host either refuses itself or leaves the refusal to the worker; it never sends true.
        bridge.Calls.Clear();
        var unacknowledged = await tools.Download("PC", "10.0.0.4", "PLC_1", false, token);
        check(bridge.Calls.Count == 0 ? unacknowledged.IsError == true : Equals(Sent(bridge.Calls[0].Arguments).Single(p => p.Name == "acknowledgeLiveTarget").Value, false),
            "plc_physical_download without acknowledgeLiveTarget is never sent as acknowledged");
        bridge.Calls.Clear();
        foreach (string scope in new[] { "", "Changes", "ALL", "hardware", "changes ", "all,changes" })
        {
            bool refused = false;
            try { _ = tools.Download("PC", "10.0.0.4", "PLC_1", true, token, softwareScope: scope); } catch (McpException error) { refused = error.Message == "softwareScope must be changes or all."; }
            check(refused && bridge.Calls.Count == 0, "plc_physical_download refuses softwareScope '" + scope + "' before any bridge call");
        }

        // A worker refusal is a coded tool error for every tool; the worker is asked once and never again.
        foreach (var (name, call) in new (string, Func<Task<CallToolResult>>)[]
        {
            ("plc_physical_targets", () => tools.Targets(token)), ("plc_physical_online_compare", () => tools.OnlineCompare("PC", "10.0.0.2", token)),
            ("plc_physical_station_upload", () => tools.StationUpload("PC", "10.0.0.3", token)), ("plc_physical_download", () => tools.Download("PC", "10.0.0.4", "PLC_1", true, token)),
        })
        {
            bridge.Calls.Clear();
            bridge.Refusal = new BridgeException("PhysicalOnlineDisabled", "Online access to real PLCs is off. The Host must be started with --physical-plc-online.");
            var refusal = await call();
            check(refusal.IsError == true && Code(refusal) == "PhysicalOnlineDisabled" && bridge.Calls.Count == 1 && bridge.Calls[0].Operation == name &&
                refusal.StructuredContent!.Value.GetProperty("error").GetProperty("message").GetString()!.Contains("--physical-plc-online", StringComparison.Ordinal),
                name + " returns the worker's PhysicalOnlineDisabled refusal as a coded error after one call");
            bridge.Refusal = new BridgeOperationException("PhysicalAuthenticationRequired", "The PLC asks for a login.", JsonSerializer.SerializeToElement(new { challenge = "login" }));
            var detailed = await call();
            check(detailed.IsError == true && Code(detailed) == "PhysicalAuthenticationRequired" &&
                detailed.StructuredContent!.Value.GetProperty("error").GetProperty("details").GetProperty("challenge").GetString() == "login", name + " keeps the details of a worker refusal");
            bridge.Refusal = null;
        }
    }
}
