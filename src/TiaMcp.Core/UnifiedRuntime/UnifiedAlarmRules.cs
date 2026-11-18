using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.Core.UnifiedRuntime;

/// <summary>
/// Requests and result shapes for WinCC Unified PC Runtime alarms through its GraphQL server (WinCC Unified GraphQL System
/// Manual 11/2025, sections 2, 5.4, 5.5.1 and 5.6). Pure rules: no network. Field selections follow the server's own schema
/// (introspected at connect), alarms are acknowledged by their fully qualified path, and no request falls back to en-US:
/// the server defaults languages and filterLanguage to en-US, which a runtime without that language rejects or fails on.
/// </summary>
public static partial class UnifiedAlarmRules
{
    public const int MaxAlarms = 1000;
    public const int MaxAcknowledge = 64;

    /// <summary>Fields asked for when the server's ActiveAlarm type has them; path and instanceID identify an alarm for acknowledgement.</summary>
    public static readonly string[] ActiveFields =
    [
        "name", "path", "instanceID", "state", "stateText", "eventText", "alarmText1", "alarmClassName", "alarmClassID", "priority",
        "raiseTime", "acknowledgmentTime", "clearTime", "resetTime", "modificationTime", "origin", "area", "connectionName", "userName",
        "hostName", "languages",
    ];

    /// <summary>LoggedAlarm has no path (manual 5.6.7); it adds hasComments.</summary>
    public static readonly string[] LoggedFields =
    [
        "name", "instanceID", "state", "stateText", "eventText", "alarmText1", "alarmClassName", "alarmClassID", "priority",
        "raiseTime", "acknowledgmentTime", "clearTime", "resetTime", "modificationTime", "origin", "area", "userName", "hostName",
        "hasComments", "languages",
    ];

    private static readonly HashSet<string> Translatable = new(StringComparer.Ordinal) { "stateText", "eventText", "alarmText1" };

    [GeneratedRegex("^[a-z]{2,3}-[A-Z]{2}$")] private static partial Regex LanguagePattern();

    /// <summary>The canonical GraphQL endpoint https://host[:port]/graphql/. The manual forbids localhost and 127.0.0.1 (use the host
    /// name the Runtime web server certificate names).</summary>
    public static Uri Endpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath is not ("/graphql/" or "/graphql") ||
            uri.HostNameType is UriHostNameType.Unknown)
            throw Error("UnifiedEndpointInvalid", "Use https://<host name>/graphql/ of the WinCC Unified Runtime web server.");
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw Error("UnifiedEndpointInvalid", "WinCC Unified GraphQL refuses localhost and 127.0.0.1; use the host name in the Runtime web server certificate.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + "/graphql/");
    }

    public static string[] Languages(string[]? requested, string? userLanguage)
    {
        string[] languages = requested is { Length: > 0 } ? requested : string.IsNullOrWhiteSpace(userLanguage) ? [] : [userLanguage!];
        if (languages.Length == 0)
            throw Error("UnifiedLanguageRequired", "The runtime user has no UMC language and none was given. Pass languages with the runtime's language (for example [\"de-DE\"]); the server's en-US default fails on runtimes without English.");
        if (languages.Length > 8 || languages.Distinct(StringComparer.Ordinal).Count() != languages.Length || languages.Any(l => l is null || !LanguagePattern().IsMatch(l)))
            throw Error("UnifiedLanguageInvalid", "languages takes 1..8 distinct ISO codes in exact case, such as de-DE or en-US.");
        return languages;
    }

    public static JsonObject ActiveRequest(IReadOnlyCollection<string> available, string[] languages, string? filterString, string? filterLanguage) =>
        Request("query($languages:[String],$filterString:String,$filterLanguage:String){activeAlarms(languages:$languages,filterString:$filterString,filterLanguage:$filterLanguage){"
            + Selection(ActiveFields, available) + "}}", Variables(languages, filterString, filterLanguage));

    public static JsonObject LoggedRequest(IReadOnlyCollection<string> available, string[] languages, string? filterString, string? filterLanguage,
        DateTimeOffset? startTime, DateTimeOffset? endTime, int maxItems)
    {
        var variables = Variables(languages, filterString, filterLanguage);
        variables["startTime"] = startTime?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        variables["endTime"] = endTime?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        variables["maxNumberOfResults"] = maxItems;
        return Request("query($languages:[String],$filterString:String,$filterLanguage:String,$startTime:Timestamp,$endTime:Timestamp,$maxNumberOfResults:Int){loggedAlarms(languages:$languages,filterString:$filterString,filterLanguage:$filterLanguage,startTime:$startTime,endTime:$endTime,maxNumberOfResults:$maxNumberOfResults){"
            + Selection(LoggedFields, available) + "}}", variables);
    }

    /// <summary>acknowledgeAlarms takes the fully qualified alarm name, which active alarms report as path
    /// (System::Tag:Alarm or System::Tag.Element:Alarm); name alone does not resolve (error 2).</summary>
    public static JsonObject AcknowledgeRequest(IReadOnlyCollection<string> available, IReadOnlyList<UnifiedAlarmIdentifier> alarms)
    {
        if (alarms is null || alarms.Count is < 1 or > MaxAcknowledge) throw Error("InvalidRequest", "Acknowledge 1.." + MaxAcknowledge + " alarms per call.");
        foreach (var alarm in alarms)
        {
            if (string.IsNullOrWhiteSpace(alarm.Path) || alarm.Path.Length > 1024 || !QualifiedPath(alarm.Path))
                throw Error("UnifiedAlarmPathRequired", "Acknowledge by path (System::Tag:Alarm) from runtime_unified_alarms, not by name. Got: " + Bounded(alarm.Path ?? ""));
            if (alarm.InstanceId is < 0) throw Error("InvalidRequest", "instanceID is 0 or more; 0 acknowledges every active instance of the alarm.");
        }
        if (alarms.Select(a => (a.Path, a.InstanceId ?? 0)).Distinct().Count() != alarms.Count) throw Error("InvalidRequest", "Each alarm instance is acknowledged once per call.");
        var input = new JsonArray(alarms.Select(a => (JsonNode)new JsonObject { ["name"] = a.Path, ["instanceID"] = a.InstanceId ?? 0 }).ToArray());
        string[] wanted = ["name", "path", "instanceID", "state", "acknowledgmentTime", "error"];
        return Request("mutation($input:[AlarmIdentifierInput]){acknowledgeAlarms(input:$input){" + Selection(wanted, available, errorSelection: true) + "}}",
            new JsonObject { ["input"] = input });
    }

    public static bool QualifiedPath(string path)
    {
        int system = path.IndexOf("::", StringComparison.Ordinal);
        return system > 0 && path.LastIndexOf(':') > system + 2 && !path.EndsWith(':');
    }

    /// <summary>The fields of one schema type from an introspection result; empty when the type is unknown.</summary>
    public static string[] TypeFields(JsonElement data, string alias) =>
        data.TryGetProperty(alias, out var type) && type.ValueKind == JsonValueKind.Object && type.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array
            ? fields.EnumerateArray().Where(f => f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String).Select(f => f.GetProperty("name").GetString()!).ToArray()
            : [];

    public const string IntrospectionQuery = "query{active:__type(name:\"ActiveAlarm\"){fields{name}} logged:__type(name:\"LoggedAlarm\"){fields{name}}}";

    /// <summary>Rows with the identifiers first, translatable texts keyed by language and, for active alarms, the exact acknowledge input.</summary>
    public static JsonArray Rows(JsonElement alarms, string[] languages, bool active, int maxItems, out int total)
    {
        var rows = new JsonArray(); total = 0;
        if (alarms.ValueKind != JsonValueKind.Array) return rows;
        foreach (var alarm in alarms.EnumerateArray())
        {
            total++;
            if (rows.Count >= maxItems || alarm.ValueKind != JsonValueKind.Object) continue;
            var row = new JsonObject();
            string[] order = alarm.TryGetProperty("languages", out var returned) && returned.ValueKind == JsonValueKind.Array
                ? returned.EnumerateArray().Select(l => l.GetString() ?? "").ToArray() : languages;
            foreach (var property in alarm.EnumerateObject())
            {
                if (property.Name == "languages") continue;
                if (Translatable.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Array)
                {
                    var texts = new JsonObject(); int i = 0;
                    foreach (var text in property.Value.EnumerateArray())
                    {
                        string key = i < order.Length && !string.IsNullOrEmpty(order[i]) ? order[i] : "#" + i;
                        if (!texts.ContainsKey(key)) texts[key] = JsonNode.Parse(text.GetRawText());
                        i++;
                    }
                    row[property.Name] = texts;
                }
                else row[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
            if (active && row["path"] is JsonValue path && path.TryGetValue<string>(out string? qualified))
                row["acknowledge"] = new JsonObject { ["path"] = qualified, ["instanceID"] = row["instanceID"]?.DeepClone() };
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>GraphQL top-level errors as a bounded list; the request failed as a whole when there are any.</summary>
    public static JsonArray TopLevelErrors(JsonElement response)
    {
        var errors = new JsonArray();
        if (!response.TryGetProperty("errors", out var list) || list.ValueKind != JsonValueKind.Array) return errors;
        foreach (var error in list.EnumerateArray().Take(10))
            errors.Add(new JsonObject
            {
                ["message"] = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? Bounded(m.GetString()!) : null,
                ["code"] = error.TryGetProperty("extensions", out var x) && x.ValueKind == JsonValueKind.Object && x.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? Bounded(c.GetString()!) : null,
            });
        return errors;
    }

    private static string Selection(IEnumerable<string> wanted, IReadOnlyCollection<string> available, bool errorSelection = false)
    {
        var fields = wanted.Where(available.Contains).Select(f => f == "error" && errorSelection ? "error{code description}" : f).ToArray();
        if (!fields.Any(f => f is "name" or "path")) throw Error("UnifiedSchemaUnexpected", "The server's alarm type lacks name and path; the GraphQL schema differs from the System Manual.");
        return string.Join(' ', fields);
    }

    private static JsonObject Variables(string[] languages, string? filterString, string? filterLanguage)
    {
        if (filterString is { Length: > 4096 }) throw Error("InvalidRequest", "filterString is limited to 4096 characters.");
        if (filterLanguage is not null && !LanguagePattern().IsMatch(filterLanguage)) throw Error("UnifiedLanguageInvalid", "filterLanguage is one ISO code in exact case.");
        // filterLanguage defaults to en-US on the server: pass the first requested language whenever there is a filter.
        return new JsonObject
        {
            ["languages"] = new JsonArray(languages.Select(l => (JsonNode)l).ToArray()),
            ["filterString"] = string.IsNullOrWhiteSpace(filterString) ? null : filterString,
            ["filterLanguage"] = string.IsNullOrWhiteSpace(filterString) ? null : filterLanguage ?? languages[0],
        };
    }

    private static JsonObject Request(string query, JsonObject variables) => new() { ["query"] = query, ["variables"] = variables };
    private static string Bounded(string value) => value.Length > 300 ? value[..300] + "…" : value;
    internal static BridgeException Error(string code, string message) => new(code, message);
}

public sealed record UnifiedAlarmIdentifier(string Path, int? InstanceId);
