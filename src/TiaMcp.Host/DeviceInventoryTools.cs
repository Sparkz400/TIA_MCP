using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.Inventory;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class DeviceInventoryTools(IBridgeClient bridge, HostOptions options, InventoryStore store, IInventoryIdentityFactory identityFactory)
{
    [McpServerTool(Name = "device_inventory", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Capture or page an immutable offline device/module inventory, including BMK fields, MLFB, configured firmware, slot, IP and project paths. Omit snapshotId to capture; optional exact devicePath narrows large projects to one device. Continue with returned snapshotId/cursor. 1..200 rows and 256 KiB per page; maxVisited 100..50000. Explicit traversal and field gaps. Four bounded snapshots retained per host; export before eviction/restart. No online access.")]
    public async Task<CallToolResult> Inventory(CancellationToken cancellationToken, string? projectName = null,
        string? snapshotId = null, string? cursor = null, int pageSize = 100, int maxVisited = 50000, string? devicePath = null)
    {
        try
        {
            if (pageSize is < 1 or > 200 || maxVisited is < 100 or > 50000) throw new BridgeException("InvalidRequest", "pageSize must be 1..200 and maxVisited 100..50000.");
            if (snapshotId == null)
            {
                if (cursor != null) throw new BridgeException("InvalidRequest", "cursor requires its snapshotId.");
                var result = await bridge.CallAsync("device_inventory_capture", new { projectName, maxVisited, devicePath,
                    artifactDirectory = Path.Combine(options.ArtifactsDirectory, "inventory") }, cancellationToken);
                var artifact = result.GetProperty("artifact");
                string path = artifact.GetProperty("path").GetString()!;
                // Reuse the artifact-store path/junction boundary before opening the native spool.
                new ArtifactStore(options.ArtifactsDirectory).Read(path, 0, 1);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 48L * 1024 * 1024) throw new BridgeException("InventoryLimit", "Native inventory exceeds the spool limit.");
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(hash, artifact.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("InventoryIntegrityFailure", "Native inventory changed before import.");
                stream.Position = 0;
                using var reader = new StreamReader(stream);
                var rows = new List<InventoryRow>(); string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    if (line.Length > 128 * 1024 || rows.Count >= 50000) throw new BridgeException("InventoryLimit", "Invalid native row bounds.");
                    rows.Add(JsonSerializer.Deserialize<InventoryRow>(line, InventoryStore.Json) ?? throw new JsonException());
                }
                if (rows.Count != result.GetProperty("rowCount").GetInt32()) throw new BridgeException("InventoryIntegrityFailure", "Native row count differs from capture metadata.");
                var coverage = result.GetProperty("coverage").Deserialize<InventoryCoverage>(InventoryStore.Json)!;
                string projectPath = result.GetProperty("project").GetProperty("path").GetString()!;
                var snapshot = store.Capture(AggregateNetworkAddresses(rows), projectPath, coverage,
                    result.GetProperty("capturedAtUtc").GetDateTimeOffset());
                snapshotId = snapshot.Metadata.SnapshotId;
            }
            else if (projectName != null || devicePath != null) throw new BridgeException("InvalidRequest", "Continuation uses the snapshot's captured scope; omit projectName and devicePath.");
            return Format(store.Page(snapshotId, pageSize, cursor));
        }
        catch (Exception error) when (IsExpected(error)) { return Failure(error); }
    }

    [McpServerTool(Name = "device_online_browse", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Read Siemens Web API module IDs from one explicit HTTPS controller. Optional hardwareId selects a subtree; no network scan. Requires advertised Modules.Browse and Modules.ReadIdentificationMaintenance, trusted TLS and read_diagnostics. Optional sessionEnvironmentVariable names a target-bound host-local token record; never pass credentials. Returns online configuration identifiers for explicit inventory mapping, not actual identities.")]
    public async Task<CallToolResult> Browse(string endpoint, CancellationToken cancellationToken, uint? hardwareId = null, string? sessionEnvironmentVariable = null)
    {
        try
        {
            using var provider = identityFactory.Create(endpoint, ReadSession(endpoint, sessionEnvironmentVariable));
            await provider.ProbeAsync(cancellationToken);
            var result = await provider.BrowseAsync(hardwareId, cancellationToken);
            return Format(new { provider = SiemensWebIdentity.Provider, endpoint = provider.Endpoint,
                observedAtUtc = DateTimeOffset.UtcNow, source = "onlineControllerConfiguration", result });
        }
        catch (Exception error) when (IsExpected(error)) { return Failure(error); }
    }

    [McpServerTool(Name = "device_identify", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Read actual I&M0 MLFB, serial number, hardware revision and firmware for 1..32 explicitly mapped rows through a Siemens HTTPS controller. Requires snapshotId, controllerRowId/controllerHardwareId and bindings [{rowId,hardwareId}] from device_online_browse. Endpoint IP must match the configured controller; online names must match. Writes a new snapshot, keeping configured values separate and partial failures explicit. Supports only controllers advertising the required Web API methods and modules exposing I&M0; hardware qualification remains unverified. Optional target-bound host token record via sessionEnvironmentVariable.")]
    public async Task<CallToolResult> Identify(string snapshotId, string endpoint, string controllerRowId, uint controllerHardwareId,
        InventoryBinding[] bindings, CancellationToken cancellationToken, string? sessionEnvironmentVariable = null)
    {
        try
        {
            if (bindings == null || bindings.Length is < 1 or > 32 || bindings.Any(b => b == null || string.IsNullOrEmpty(b.RowId)) ||
                bindings.Select(b => b.RowId).Distinct().Count() != bindings.Length ||
                bindings.Select(b => b.HardwareId).Distinct().Count() != bindings.Length)
                throw new BridgeException("InvalidRequest", "Provide 1..32 distinct row and hardware-ID bindings.");
            var snapshot = store.Get(snapshotId); var rows = snapshot.ReadRows().ToArray();
            var byId = rows.ToDictionary(r => r.RowId, StringComparer.Ordinal);
            if (!byId.TryGetValue(controllerRowId, out var controller) || controller.Kind != "cpu" || bindings.Any(b => !byId.ContainsKey(b.RowId)))
                throw new BridgeException("InvalidRequest", "The controller must be a CPU and all rows must belong to this snapshot.");
            var uri = SiemensWebIdentity.ValidateEndpoint(endpoint);
            if (!IPAddress.TryParse(uri.Host, out var selectedIp) || controller.Configured["ip"].State != "available" ||
                !controller.Configured["ip"].Value!.Split(';', StringSplitOptions.TrimEntries).Any(s => IPAddress.TryParse(s, out var ip) && ip.Equals(selectedIp)))
                throw new BridgeException("IdentityMismatch", "The HTTPS endpoint IP must match a configured interface under the selected CPU. DNS and inferred station IPs are not used for association.");
            if (bindings.Any(b => (b.RowId == controllerRowId) != (b.HardwareId == controllerHardwareId)))
                throw new BridgeException("IdentityMismatch", "The controller row and controllerHardwareId must identify the same binding.");
            using var provider = identityFactory.Create(endpoint, ReadSession(endpoint, sessionEnvironmentVariable));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            BridgeException? commonFailure = null;
            try
            {
                await provider.ProbeAsync(deadline.Token);
                var onlineController = await provider.NodeAsync(controllerHardwareId, deadline.Token);
                if (!onlineController.TryGetProperty("name", out var onlineName) || onlineName.ValueKind != JsonValueKind.String || onlineName.GetString() != controller.Name ||
                    !onlineController.TryGetProperty("subtype", out var subtype) || subtype.ValueKind != JsonValueKind.String || subtype.GetString() != "cpu")
                    throw new BridgeException("IdentityMismatch", "Online controller name/type does not match the selected CPU.");
            }
            catch (BridgeException error) { commonFailure = error; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { commonFailure = Deadline(); }
            foreach (var binding in bindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = byId[binding.RowId];
                try
                {
                    if (commonFailure != null) throw commonFailure;
                    byId[row.RowId] = await provider.IdentifyAsync(row, binding.HardwareId, deadline.Token);
                }
                catch (BridgeException error) { byId[row.RowId] = SiemensWebIdentity.Failure(row, provider.Endpoint, binding.HardwareId, error); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { commonFailure = Deadline(); byId[row.RowId] = SiemensWebIdentity.Failure(row, provider.Endpoint, binding.HardwareId, commonFailure); }
            }
            var enriched = store.Capture(rows.Select(r => byId[r.RowId]), snapshot.Metadata.ProjectPath,
                snapshot.Metadata.Coverage, snapshot.Metadata.CapturedAtUtc, snapshotId);
            return Format(new { snapshot = enriched.Metadata, selectedRows = bindings.Select(b => byId[b.RowId]),
                onlineScopeComplete = bindings.All(b => byId[b.RowId].Online.Status == "read"),
                note = "Actual observations have per-row timestamps. Unselected rows retain their previous observation state. Mismatch means differing captured values, not an assessment of firmware compatibility." });
        }
        catch (Exception error) when (IsExpected(error)) { return Failure(error); }
    }

    [McpServerTool(Name = "device_inventory_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export all rows of one immutable device inventory snapshot to UTF-8 CSV or Excel XLSX in the host artifact directory. Includes configured/actual values, missing-field states/reasons, provenance and mismatches, plus metadata. Export includes the whole captured snapshot, not just the current page; incomplete captures remain labeled. Retrieve files with artifact_read.")]
    public CallToolResult Export(string snapshotId, string format = "csv")
    {
        try { return Format(InventoryExport.Write(store.Get(snapshotId), Path.Combine(options.ArtifactsDirectory, "inventory"), format)); }
        catch (Exception error) when (IsExpected(error)) { return Failure(error); }
    }

    internal static IEnumerable<InventoryRow> AggregateNetworkAddresses(List<InventoryRow> rows)
    {
        var byPath = rows.ToDictionary(r => r.Path, StringComparer.Ordinal);
        var inherited = new Dictionary<string, List<InventoryField>>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.Configured["ip"].State == "available"))
        {
            var parent = row.ParentPath;
            for (int depth = 0; parent != null && depth < 65 && byPath.TryGetValue(parent, out var ancestor); depth++, parent = ancestor.ParentPath)
            {
                if (!inherited.TryGetValue(parent, out var fields)) inherited[parent] = fields = [];
                fields.Add(row.Configured["ip"]);
            }
        }
        foreach (var row in rows)
        {
            if (row.Configured["ip"].State == "notExposed" && inherited.TryGetValue(row.Path, out var fields))
            {
                var value = string.Join("; ", fields.SelectMany(f => f.Value!.Split(';', StringSplitOptions.TrimEntries)).Distinct());
                var source = "Configured descendant interfaces: " + string.Join("; ", fields.Select(f => f.Source).Distinct());
                row.Configured["ip"] = value.Length <= 4096 && source.Length <= 8192 ? InventoryField.Read(value, source) :
                    InventoryField.Missing("readError", "Aggregated interface data exceeds field bounds; inspect individual interface rows.");
            }
            yield return row;
        }
    }

    private static string? ReadSession(string endpoint, string? variable)
    {
        if (variable == null) return null;
        if (!Regex.IsMatch(variable, "^TIA_INVENTORY_[A-Z0-9_]{1,64}$")) throw new BridgeException("InvalidRequest", "Use a host environment variable named TIA_INVENTORY_<target> containing a target-bound token record.");
        var value = Environment.GetEnvironmentVariable(variable);
        if (value == null || value.Length > 16384) throw new BridgeException("AuthenticationRequired", "The target-bound Web API session record is absent or invalid on the host.");
        using var document = JsonDocument.Parse(value);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("endpoint", out var storedEndpoint) || !root.TryGetProperty("token", out var storedToken) ||
            storedEndpoint.ValueKind != JsonValueKind.String || storedToken.ValueKind != JsonValueKind.String ||
            SiemensWebIdentity.ValidateEndpoint(storedEndpoint.GetString()!) != SiemensWebIdentity.ValidateEndpoint(endpoint))
            throw new BridgeException("AuthenticationRequired", "The Web API session record must identify this exact HTTPS endpoint.");
        var token = storedToken.GetString();
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || token.Any(char.IsControl))
            throw new BridgeException("AuthenticationRequired", "The target-bound Web API token is invalid.");
        return token;
    }

    private static BridgeException Deadline() => new("OnlineTimeout", "The 60-second identification budget expired; remaining selected rows were not read.");
    private static bool IsExpected(Exception error) => error is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException;
    private static CallToolResult Failure(Exception error) => Format(new { error = new { code = (error as BridgeException)?.Code ?? "InventoryFailure",
        message = error is BridgeException ? error.Message : "Inventory input or artifact could not be processed." } }, true);
    private static CallToolResult Format(object value, bool error = false)
    {
        var element = JsonSerializer.SerializeToElement(value, InventoryStore.Json);
        return new() { IsError = error, StructuredContent = element, Content = [new TextContentBlock { Text = element.GetRawText() }] };
    }
}
