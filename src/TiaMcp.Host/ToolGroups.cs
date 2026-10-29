namespace TiaMcp.Host;

/// <summary>Named tool groups for --toolsets. Dependency-free so option parsing and its tests need no MCP SDK.</summary>
public static class ToolGroups
{
    // Always advertised: catalog discovery and artifact transfer serve every other group.
    public const string Base = "base";
    // Registered only by explicit qualification flags; --toolsets never hides them.
    public const string Qualification = "qualification";

    // First matching prefix wins, so longer prefixes precede the shorter ones they would otherwise fall under.
    private static readonly (string Prefix, string Group)[] Rules =
    [
        ("qualification_", Qualification),
        ("feature_catalog", Base), ("artifact_", Base),
        ("project_model", "explorer"), ("project_snapshot_", "explorer"),
        ("plc_online_compare", "simulation"), ("plc_station_upload", "simulation"), ("sim_", "simulation"), ("simulation_", "simulation"), ("db_snapshot_", "simulation"),
        ("tia_", "core"), ("project_", "core"), ("target_capabilities", "core"), ("object_resolve", "core"), ("fixture_sources", "core"),
        ("change_", "core"), ("engineering_", "core"), ("block_", "core"), ("call_graph", "core"), ("instance_lookup", "core"),
        ("symbol_trace", "core"), ("tag_table_", "core"), ("type_read", "core"), ("xref_query", "core"), ("software_inspect", "core"),
        ("technology_read", "core"), ("hardware_", "core"), ("plc_", "core"),
        ("runtime_", "runtime"), ("device_", "runtime"),
        ("library_", "library"), ("hmi_", "hmi"), ("watch_", "watch"),
        ("diagnostic_", "diagnostics"), ("trace_", "diagnostics"),
        ("testsuite_", "testsuite"), ("translation_", "translation"), ("opcua_", "opcua"), ("vci_", "vci"),
        ("automationml_", "cax"), ("cax_", "cax"), ("graph_", "graph"),
        ("access_", "reviews"), ("addin_", "reviews"), ("composite_", "reviews"), ("connection_", "reviews"), ("drive_", "reviews"),
        ("multiuser_", "reviews"), ("public_certificate_", "reviews"), ("certificate_", "reviews"), ("safety_", "reviews"),
        ("software_unit_", "reviews"), ("technology_profile_", "reviews"), ("teamcenter_", "reviews"), ("web_", "reviews"),
    ];

    /// <summary>Groups a user may name, in documentation order.</summary>
    public static readonly IReadOnlyList<string> Selectable = Rules.Select(rule => rule.Group)
        .Where(group => group is not (Base or Qualification)).Distinct().ToArray();

    public static string? Of(string tool) => Rules.FirstOrDefault(rule => tool.StartsWith(rule.Prefix, StringComparison.Ordinal)).Group;

    /// <summary>Parses a comma-separated --toolsets value; null means every group.</summary>
    public static IReadOnlySet<string>? Parse(string value)
    {
        var names = value.Split(',', StringSplitOptions.TrimEntries);
        if (names.Any(name => name.Length == 0)) throw new ArgumentException("--toolsets requires comma-separated group names without empty entries.");
        if (names.Contains("all", StringComparer.Ordinal))
            return names.Length == 1 ? null : throw new ArgumentException("--toolsets all cannot be combined with other groups.");
        var unknown = names.Where(name => !Selectable.Contains(name, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException("Unknown --toolsets group " + string.Join(", ", unknown) + ". Valid groups: all, " + string.Join(", ", Selectable) + ".");
        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    public static bool Advertised(string tool, IReadOnlySet<string>? selected) => Of(tool) switch
    {
        null => throw new InvalidOperationException("Tool " + tool + " belongs to no --toolsets group; add its prefix to ToolGroups."),
        Base or Qualification => true,
        var group => selected is null || selected.Contains(group),
    };
}
