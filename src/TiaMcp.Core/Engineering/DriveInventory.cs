using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.Core.Engineering;

/// <summary>#213: one read-only view joining Startdrive drive objects and telegrams, PLC tags at the telegrams' addresses and
/// technology objects connected to the drives. Every join uses native identities or addresses, never names. Coverage is
/// reported per source, so an unavailable or partial read can never look like "no drives".</summary>
public static partial class DriveInventory
{
    public static async Task<JsonObject> Capture(IBridgeClient bridge, string? projectName, CancellationToken cancellationToken)
    {
        var coverage = new JsonObject(); var blockers = new JsonArray();
        var (observed, driveError) = await Native("drive_observe", new() { ["projectName"] = projectName, ["maxItems"] = 2000, ["parameterNumbers"] = new JsonArray(1) });
        var drives = new JsonArray(); bool drivesComplete = false;
        if (driveError is not null)
        {
            coverage["drives"] = new JsonObject { ["status"] = driveError.Code == "UnsupportedCapability" ? "unavailable" : "failed", ["errorCode"] = driveError.Code, ["message"] = driveError.Message };
            blockers.Add(Blocker("driveCoverage", null, "Drives could not be read (" + driveError.Code + "); the project may still contain drives."));
        }
        else
        {
            var completeness = observed!["completeness"] as JsonObject ?? new JsonObject();
            bool truncated = completeness["truncated"]?.GetValue<bool>() == true;
            // An unreadable drive object number only switches that drive to index handles; every other warning leaves the read partial.
            var warnings = observed["warnings"]?.AsArray() ?? new JsonArray();
            bool scope = completeness["scopeComplete"]?.GetValue<bool>() == true
                || !truncated && warnings.Count > 0 && warnings.All(w => w is JsonObject o && Text(o["path"])?.EndsWith("/driveObjectNumber", StringComparison.Ordinal) == true);
            var incomplete = new JsonArray();
            foreach (var drive in observed["drives"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var objects = new JsonArray();
                foreach (var item in drive["driveObjects"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    var snapshot = item["snapshot"] as JsonObject ?? new JsonObject();
                    string driveId = Text(snapshot["identity"]?["driveId"]) ?? "?";
                    if (snapshot["telegramsComplete"]?.GetValue<bool>() != true) incomplete.Add(Text(drive["hardwarePath"]) + "#" + driveId);
                    var telegrams = new JsonArray(snapshot["telegrams"]?.AsArray().OfType<JsonObject>().Select(t => (JsonNode)new JsonObject {
                        ["id"] = t["id"]?.DeepClone(), ["type"] = t["type"]?.DeepClone(), ["number"] = t["number"]?.DeepClone(),
                        ["inputBytes"] = t["inputBytes"]?.DeepClone(), ["outputBytes"] = t["outputBytes"]?.DeepClone(), ["addressesComplete"] = t["addressesComplete"]?.DeepClone(),
                        ["addresses"] = new JsonArray(t["addresses"]?.AsArray().OfType<JsonObject>().Select(a => (JsonNode)new JsonObject {
                            ["direction"] = a["direction"]?.DeepClone(), ["startAddress"] = a["startAddress"]?.DeepClone(), ["lengthBits"] = a["length"]?.DeepClone(),
                            ["unknownReason"] = a["unknownReason"]?.DeepClone(), ["tags"] = new JsonArray() }).ToArray() ?? []) }).ToArray() ?? []);
                    foreach (var telegram in telegrams.OfType<JsonObject>().Where(t => t["addressesComplete"]?.GetValue<bool>() != true))
                        incomplete.Add(Text(drive["hardwarePath"]) + "#" + driveId + "/" + Text(telegram["id"]));
                    objects.Add(new JsonObject { ["driveId"] = driveId, ["driveObjectNumber"] = item["driveObjectNumber"]?.DeepClone(),
                        ["driveObjectIndex"] = item["driveObjectIndex"]?.DeepClone(), ["telegrams"] = telegrams });
                }
                drives.Add(new JsonObject { ["hardwarePath"] = drive["hardwarePath"]?.DeepClone(), ["name"] = drive["name"]?.DeepClone(),
                    ["typeIdentifier"] = drive["typeIdentifier"]?.DeepClone(), ["driveObjects"] = objects, ["technologyObjects"] = new JsonArray() });
            }
            drivesComplete = scope && !truncated && incomplete.Count == 0;
            coverage["drives"] = new JsonObject { ["status"] = drivesComplete ? "complete" : "partial", ["scopeComplete"] = scope, ["truncated"] = truncated,
                ["incompleteTelegrams"] = incomplete, ["warnings"] = observed["warnings"]?.DeepClone() ?? new JsonArray() };
            if (!drivesComplete) blockers.Add(Blocker("driveCoverage", null, "The drive read is partial (" + (truncated ? "truncated" : !scope ? "scope incomplete" : incomplete.Count + " telegram(s) without complete addresses") + ")."));
        }

        var (inventory, inventoryError) = await Native("project_inventory", new() { ["projectName"] = projectName, ["maxItems"] = 2000 });
        if (inventoryError is not null) throw inventoryError;
        var plcs = inventory!["plcs"]?.AsArray().OfType<JsonObject>().Select(p => Text(p["path"])).OfType<string>().ToArray() ?? [];
        var tags = new List<(string Plc, string Table, JsonObject Tag, Address Address)>();
        var tagCoverage = new JsonArray(); var technologyCoverage = new JsonArray(); var technology = new JsonArray();
        foreach (string plc in plcs)
        {
            var (software, softwareError) = await Native("software_inspect", new() { ["projectName"] = projectName, ["plcPath"] = plc, ["maxItems"] = 2000 });
            bool tagsComplete = softwareError is null && software!["completeness"]?["truncated"]?.GetValue<bool>() != true;
            tagCoverage.Add(new JsonObject { ["plcPath"] = plc, ["status"] = tagsComplete ? "complete" : softwareError is null ? "partial" : "failed", ["errorCode"] = softwareError?.Code });
            if (!tagsComplete) blockers.Add(Blocker("tagCoverage", plc, "PLC tags of " + plc + " could not be read completely; telegram symbols may be missing."));
            foreach (var table in software?["tagTables"]?.AsArray().OfType<JsonObject>() ?? [])
                foreach (var tag in table["tags"]?.AsArray().OfType<JsonObject>() ?? [])
                    if (Parse(Text(tag["logicalAddress"])) is Address address) tags.Add((plc, Text(table["path"]) ?? "", tag, address));

            var (objects, objectsError) = await Native("technology_read", new() { ["projectName"] = projectName, ["plcPath"] = plc, ["maxItems"] = 2000 });
            var row = new JsonObject { ["plcPath"] = plc, ["status"] = objectsError is null ? "complete" : "failed", ["errorCode"] = objectsError?.Code, ["unreadObjects"] = new JsonArray() };
            technologyCoverage.Add(row);
            if (objectsError is not null) { blockers.Add(Blocker("technologyCoverage", plc, "Technology objects of " + plc + " could not be read (" + objectsError.Code + ").")); continue; }
            foreach (var info in objects!["technologyObjects"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                string path = Text(info["path"]) ?? "";
                var (read, readError) = await Native("technology_read", new() { ["projectName"] = projectName, ["technologyPath"] = path, ["maxItems"] = 2000 });
                var targets = readError is null ? HardwareTargets(read!) : [];
                bool complete = readError is null && read!["hardwareConnectionCoverage"]?["scopeComplete"]?.GetValue<bool>() == true;
                if (!complete) { row["status"] = "partial"; row["unreadObjects"]!.AsArray().Add(path); }
                technology.Add(new JsonObject { ["plcPath"] = plc, ["path"] = path, ["name"] = info["name"]?.DeepClone(), ["type"] = info["systemLibraryType"]?.DeepClone(),
                    ["connectionsComplete"] = complete, ["hardwareTargets"] = new JsonArray(targets.Select(t => (JsonNode)t).ToArray()), ["drives"] = new JsonArray() });
            }
        }
        coverage["tags"] = tagCoverage; coverage["technology"] = technologyCoverage;
        if (technologyCoverage.OfType<JsonObject>().Any(r => Text(r["status"]) == "partial"))
            blockers.Add(Blocker("technologyCoverage", null, "Some technology objects' connections were not read completely; a drive may have an unseen technology object."));

        var reconciliation = Reconcile(drives, technology, tags, blockers);
        return new JsonObject {
            ["schemaVersion"] = "drive-inventory.v1", ["project"] = observed?["project"]?.DeepClone() ?? inventory["project"]?.DeepClone(),
            ["drives"] = drives, ["technologyObjects"] = technology, ["reconciliation"] = reconciliation, ["coverage"] = coverage,
            ["noDrivesConfirmed"] = drivesComplete && drives.Count == 0, ["blockers"] = blockers, ["writeReady"] = blockers.Count == 0,
            ["note"] = "Tags join telegrams by address overlap and technology objects join drives by resolved hardware references; names are never used. " +
                       "Startdrive does not report which PLC owns a telegram, so a tag matches when its address overlaps; matches in several PLCs are ambiguous." };

        async Task<(JsonObject? Response, BridgeOperationException? Error)> Native(string operation, JsonObject arguments)
        {
            try { return (JsonNode.Parse((await bridge.CallAsync(operation, arguments, cancellationToken)).GetRawText())!.AsObject(), null); }
            // A worker refusal is reported per source; lost transport stays terminal.
            catch (BridgeOperationException error) { return (null, error); }
        }
    }

    private static JsonObject Reconcile(JsonArray drives, JsonArray technology, List<(string Plc, string Table, JsonObject Tag, Address Address)> tags, JsonArray blockers)
    {
        var withoutTags = new JsonArray(); var ambiguous = new JsonArray(); var spanning = new JsonArray();
        var owners = new Dictionary<JsonObject, int>(ReferenceEqualityComparer.Instance);
        foreach (var drive in drives.OfType<JsonObject>())
            foreach (var item in drive["driveObjects"]!.AsArray().OfType<JsonObject>())
                foreach (var telegram in item["telegrams"]!.AsArray().OfType<JsonObject>())
                    foreach (var range in telegram["addresses"]!.AsArray().OfType<JsonObject>())
                    {
                        string where = Text(drive["hardwarePath"]) + "#" + Text(item["driveId"]) + "/" + Text(telegram["id"]) + "/" + Text(range["direction"]);
                        if (range["startAddress"] is not JsonValue s || !s.TryGetValue<int>(out int start) || range["lengthBits"] is not JsonValue l || !l.TryGetValue<int>(out int bits)) continue;
                        bool input = Text(range["direction"])?.StartsWith("In", StringComparison.OrdinalIgnoreCase) == true;
                        var matches = tags.Where(t => t.Address.Input == input && t.Address.Start < start + (bits + 7) / 8 && start < t.Address.Start + t.Address.Bytes).ToList();
                        foreach (var match in matches)
                        {
                            owners[match.Tag] = owners.TryGetValue(match.Tag, out int n) ? n + 1 : 1;
                            range["tags"]!.AsArray().Add(new JsonObject { ["plcPath"] = match.Plc, ["tablePath"] = match.Table, ["name"] = match.Tag["name"]?.DeepClone(),
                                ["dataType"] = match.Tag["dataType"]?.DeepClone(), ["logicalAddress"] = match.Tag["logicalAddress"]?.DeepClone(), ["isSafety"] = match.Tag["isSafety"]?.DeepClone() });
                        }
                        if (matches.Count == 0) withoutTags.Add(where);
                        var plcs = matches.Select(m => m.Plc).Distinct().ToArray();
                        if (plcs.Length > 1)
                        {
                            ambiguous.Add(new JsonObject { ["telegram"] = where, ["plcPaths"] = new JsonArray(plcs.Select(p => (JsonNode)p!).ToArray()) });
                            blockers.Add(Blocker("ambiguousTagOwnership", where, "Tags in several PLCs overlap this telegram range; select the owning PLC explicitly."));
                        }
                    }
        foreach (var (tag, count) in owners.Where(o => o.Value > 1))
        {
            spanning.Add(new JsonObject { ["name"] = tag["name"]?.DeepClone(), ["logicalAddress"] = tag["logicalAddress"]?.DeepClone(), ["telegramRanges"] = count });
            blockers.Add(Blocker("addressOverlap", Text(tag["name"]), "Tag " + Text(tag["name"]) + " overlaps " + count + " telegram ranges."));
        }

        var withoutTechnology = new JsonArray(); var severalTechnology = new JsonArray(); var technologyWithoutDrive = new JsonArray();
        foreach (var to in technology.OfType<JsonObject>())
        {
            var targets = to["hardwareTargets"]!.AsArray().Select(t => Text(t)).OfType<string>().ToArray();
            var linked = drives.OfType<JsonObject>().Where(d => Text(d["hardwarePath"]) is string p && targets.Any(t => t == p || t.StartsWith(p + "/", StringComparison.Ordinal) || p.StartsWith(t + "/", StringComparison.Ordinal))).ToList();
            foreach (var drive in linked)
            {
                to["drives"]!.AsArray().Add(drive["hardwarePath"]!.DeepClone());
                drive["technologyObjects"]!.AsArray().Add(new JsonObject { ["plcPath"] = to["plcPath"]!.DeepClone(), ["path"] = to["path"]!.DeepClone() });
            }
            if (linked.Count > 1) blockers.Add(Blocker("ambiguousTechnologyLink", Text(to["path"]), "Technology object " + Text(to["path"]) + " resolves to " + linked.Count + " drives."));
            if (linked.Count == 0 && targets.Length > 0) technologyWithoutDrive.Add(to["path"]!.DeepClone());
        }
        foreach (var drive in drives.OfType<JsonObject>())
        {
            int count = drive["technologyObjects"]!.AsArray().Count;
            if (count == 0) withoutTechnology.Add(drive["hardwarePath"]!.DeepClone());
            if (count > 1)
            {
                severalTechnology.Add(drive["hardwarePath"]!.DeepClone());
                blockers.Add(Blocker("duplicateAxisAssociation", Text(drive["hardwarePath"]), "Several technology objects connect to drive " + Text(drive["hardwarePath"]) + "."));
            }
        }
        var duplicateNames = new JsonArray(drives.OfType<JsonObject>().GroupBy(d => Text(d["name"]) ?? "").Where(g => g.Count() > 1).Select(g => (JsonNode)g.Key!).ToArray());
        return new JsonObject { ["telegramRangesWithoutTags"] = withoutTags, ["ambiguousTagOwnership"] = ambiguous, ["tagsSpanningTelegrams"] = spanning,
            ["drivesWithoutTechnologyObject"] = withoutTechnology, ["drivesWithSeveralTechnologyObjects"] = severalTechnology,
            ["technologyObjectsWithoutDiscoveredDrive"] = technologyWithoutDrive, ["duplicateDriveNames"] = duplicateNames };
    }

    // Resolved hardware or channel references of a technology object's connections, as hardware paths.
    private static string[] HardwareTargets(JsonObject read) =>
        (read["hardwareConnections"]?.AsArray().OfType<JsonObject>() ?? []).Where(r => Text(r["status"]) != "notExposed")
            .SelectMany(r => r.Select(p => p.Value).Concat((r["attributes"] as JsonArray ?? []).Select(a => a?["value"])))
            .OfType<JsonObject>().Where(r => Text(r["valueSemantics"]) == "engineeringReference" && Text(r["resolution"]) == "resolved" && Text(r["kind"]) is "hardware" or "channel")
            .Select(r => Text(Text(r["kind"]) == "channel" ? r["parent"]?["hardwarePath"] : r["hardwarePath"])).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray();

    private static JsonObject Blocker(string kind, string? target, string message) => new() { ["kind"] = kind, ["target"] = target, ["message"] = message };

    internal sealed record Address(bool Input, int Start, int Bytes);

    [GeneratedRegex(@"^%(?<area>[IEQA])(?<size>[XBWD])?(?<byte>\d+)(\.(?<bit>[0-7]))?(:P)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddressPattern();

    /// <summary>A PLC tag address as an input/output byte range; memory, DB and peripheral-less symbolic tags return null.</summary>
    internal static Address? Parse(string? text)
    {
        var match = AddressPattern().Match(text ?? "");
        if (!match.Success) return null;
        string size = match.Groups["size"].Value.ToUpperInvariant();
        // Byte, word and double word take no bit; a bit address (X or no size) needs one.
        if ((size is "B" or "W" or "D") == match.Groups["bit"].Success) return null;
        bool input = match.Groups["area"].Value.ToUpperInvariant() is "I" or "E";
        return new Address(input, int.Parse(match.Groups["byte"].Value), size == "W" ? 2 : size == "D" ? 4 : 1);
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out string? s) ? s : null;
}
