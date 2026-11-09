namespace TiaMcp.Host;

/// <summary>
/// What a Host started with --tia-version 19 advertises: the base tools and the read tools the V19 adapter implements,
/// each mapped to its worker operation (scripts/test_tia_v19_surface.py keeps this map and V19Session.Operations equal).
/// Every other tool is removed from the surface, so it is neither listed nor callable.
/// </summary>
public static class TiaVersionSurface
{
    public static readonly IReadOnlyDictionary<string, string> V19Tools = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tia_discover"] = "discover",
        ["tia_capabilities"] = "bridge_info",
        ["tia_attach"] = "attach",
        ["tia_session_status"] = "session_status",
        ["tia_start"] = "start",
        ["tia_detach"] = "detach",
        ["project_open"] = "project_open",
        ["project_inventory"] = "project_inventory",
        ["software_inspect"] = "software_inspect",
        ["block_read"] = "block_read",
        ["type_read"] = "type_read",
        ["tag_table_read"] = "tag_table_read",
    };

    // The V21 descriptions name V21 behavior (.ap21, change routes, export formats); a V19 Host states its own.
    internal static readonly IReadOnlyDictionary<string, string> V19Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tia_discover"] = "List running TIA Portal V19 instances. Does not start TIA or connect to a PLC.",
        ["tia_capabilities"] = "Report the read-only V19 adapter: installation, implemented operations and limits. Installation evidence does not verify licenses or project-specific support.",
        ["tia_attach"] = "Attach to an explicitly selected running V19 process. Siemens may display its own Openness access dialog. Does not change, save or close its projects.",
        ["tia_session_status"] = "Read the V19 worker's current TIA attachment and open project identities.",
        ["tia_start"] = "Start a separate V19 instance without its graphical editor, owned by this worker, for opening a project file. Must be detached first.",
        ["tia_detach"] = "Release the V19 worker's TIA connection. Does not close a user-owned TIA process; a worker-owned instance is disposed without saving.",
        ["project_open"] = "Open an exact existing .ap19 project in an empty worker-owned V19 instance started with tia_start. Never upgrades or saves it; other versions are refused (ProjectVersionUnsupported). readOnly=true opens it as a secondary project, on which TIA also refuses exports.",
        ["project_inventory"] = "Inspect the open V19 project: devices with configured address ranges, PLCs and blocks including software units. Paths use the same scheme as the V21 Host. Bounded, with explicit completeness. Does not connect to a PLC. The groupPath, namePrefix and includeHardware=false filters work only on the V21 Host and are refused here (UnsupportedOnTiaVersion).",
        ["software_inspect"] = "Inspect a V19 PLC's software scopes (the PLC and its software and safety units): blocks, tag tables with tags and constants, and PLC data types, in the V21 Host's shape without technology objects, group paths and unit relations. Read an object for its native XML. The groupPath and namePrefix filters work only on the V21 Host and are refused here (UnsupportedOnTiaVersion).",
        ["block_read"] = "Export a V19 block as unchanged native SimaticML XML (V19 schema) and report its metadata. Use paths from project_inventory. Only native XML: the format, render and culture parameters are refused (UnsupportedOnTiaVersion).",
        ["type_read"] = "Export a V19 PLC data type (UDT) as unchanged native SimaticML XML. Select typeName or typePath from software_inspect. Only native XML: the format parameter is refused (UnsupportedOnTiaVersion).",
        ["tag_table_read"] = "Export a V19 tag table as unchanged native SimaticML XML including constants and multilingual comments. Select tagTableName or tagTablePath from software_inspect.",
    };

    public static bool Advertised(string tool, HostOptions options) =>
        options.TiaVersion != 19 || ToolGroups.Of(tool) == ToolGroups.Base || V19Tools.ContainsKey(tool);

    internal static string? Description(string tool, HostOptions options) =>
        options.TiaVersion == 19 && V19Descriptions.TryGetValue(tool, out var text) ? text : null;

    internal static string Instructions(HostOptions options) => options.TiaVersion != 19 ? "" :
        "\nThis Host runs the read-only TIA Portal V19 adapter. It opens .ap19 projects or attaches to a running V19 Portal and reads inventory, software scopes, blocks, PLC data types and tag tables as native V19 XML. " +
        "It never changes, compiles, saves or archives a project, and advertises no change, library, HMI, simulation, runtime or review tools. " +
        "To edit a V19 project, upgrade a copy into V21 with project_upgrade on a V21 Host; object paths name the same objects in both.";
}
