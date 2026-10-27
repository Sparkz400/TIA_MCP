using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TiaMcp.Core.PhysicalRuntime;

/// <summary>An owned PLCSIM Advanced instance as the simulator worker observed it. Never a physical PLC or a caller claim.</summary>
public sealed record PlcsimTargetBinding(string SchemaVersion, int InstanceId, string InstanceName, string CreationId,
    string StoragePath, string MarkerSha256, string[] ControllerAddresses, string? ProgramFingerprintSha256, DateTimeOffset ObservedUtc);

/// <summary>Retained, content-addressed owned-PLCSIM bindings. Runtime writes and control require one that covers the session endpoint.</summary>
public static class PlcsimTargetBindings
{
    public const string Folder = "runtime-plcsim-bindings";
    public const int ByteLimit = 16 << 10;
    private static readonly JsonSerializerOptions Strict = new(BridgeProtocol.Json) {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };

    /// <summary>Maps one sim_identity response; it must describe the exact requested owned instance with at least one usable IPv4 address.</summary>
    public static PlcsimTargetBinding Observed(JsonElement identity, int instanceId, string instanceName, string creationId, DateTimeOffset observedUtc)
    {
        try
        {
            var instance = identity.GetProperty("instance");
            Require(instance.GetProperty("owned").GetBoolean() && instance.GetProperty("id").GetInt32() == instanceId &&
                instance.GetProperty("name").GetString() == instanceName && identity.GetProperty("creationId").GetString() == creationId);
            // Zero or placeholder addresses cannot be reached; they never bind an endpoint.
            var addresses = identity.GetProperty("controllerAddresses").EnumerateArray().Select(a => a.GetString()!)
                .Where(a => a != "0.0.0.0").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var binding = new PlcsimTargetBinding("plcsim-target-binding.v1", instanceId, instanceName, creationId,
                instance.GetProperty("storagePath").GetString()!, identity.GetProperty("markerSha256").GetString()!, addresses, null, observedUtc);
            Validate(binding); return binding;
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentNullException) { throw Error(); }
    }

    /// <summary>The fingerprint of the engineering worker's plc_online_compare of this instance at one of its addresses; null when none was exposed.</summary>
    public static string? Fingerprint(PlcsimTargetBinding observed, JsonElement compare)
    {
        try
        {
            Require(compare.GetProperty("instanceId").GetInt32() == observed.InstanceId && compare.GetProperty("instanceName").GetString() == observed.InstanceName);
            var fingerprint = compare.GetProperty("fingerprint");
            if (!fingerprint.GetProperty("available").GetBoolean() || fingerprint.GetProperty("sha256").ValueKind == JsonValueKind.Null) return null;
            Require(observed.ControllerAddresses.Contains(fingerprint.GetProperty("address").GetString(), StringComparer.Ordinal));
            string sha256 = fingerprint.GetProperty("sha256").GetString()!; Require(Hex(sha256, 64)); return sha256;
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or FormatException) { throw Error(); }
    }

    public static JsonObject Reference(string sha256) => new() { ["path"] = Folder + "/" + sha256 + ".json", ["sha256"] = sha256 };

    public static JsonObject Retain(string artifactsRoot, PlcsimTargetBinding binding)
    {
        Validate(binding);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(binding, BridgeProtocol.Json);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)), folder = Path.Combine(Path.GetFullPath(artifactsRoot), Folder);
        PackageStorage.EnsureNoLinks(folder); Directory.CreateDirectory(folder);
        string path = PackageStorage.Under(folder, hash + ".json");
        if (!File.Exists(path))
        {
            string temporary = PackageStorage.Under(folder, hash + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temporary, bytes);
            try { File.Move(temporary, path); } catch (IOException) when (File.Exists(path)) { File.Delete(temporary); }
        }
        Require(ProjectReviewStore.ReadArtifact(artifactsRoot, Reference(hash), ByteLimit).AsSpan().SequenceEqual(bytes));
        return Reference(hash);
    }

    public static PlcsimTargetBinding Read(string artifactsRoot, string sha256)
    {
        Require(Hex(sha256, 64));
        return Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, Reference(sha256), ByteLimit));
    }

    public static PlcsimTargetBinding Parse(byte[] bytes)
    {
        try
        {
            var binding = JsonSerializer.Deserialize<PlcsimTargetBinding>(bytes, Strict);
            Require(binding is not null); Validate(binding!);
            Require(JsonSerializer.SerializeToUtf8Bytes(binding, BridgeProtocol.Json).AsSpan().SequenceEqual(bytes));
            return binding!;
        }
        catch (JsonException) { throw Error(); }
    }

    /// <summary>Only a literal controller address of the bound instance qualifies; host names, other addresses and physical PLCs stay unbound.</summary>
    public static bool Covers(PlcsimTargetBinding binding, string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        uri.HostNameType == UriHostNameType.IPv4 && binding.ControllerAddresses.Contains(uri.Host, StringComparer.Ordinal);

    public static void RequireEndpoint(PlcsimTargetBinding binding, string endpoint)
    {
        if (!Covers(binding, endpoint)) throw new BridgeException("RuntimeTargetNotOwnedPlcsim",
            "The connected endpoint is not a controller address of the bound owned PLCSIM instance. Writes and control are refused for physical PLCs and unbound targets; nothing was sent.");
    }

    public static void RequireSameInstance(PlcsimTargetBinding recorded, PlcsimTargetBinding current)
    {
        if (recorded.InstanceId != current.InstanceId || recorded.InstanceName != current.InstanceName || recorded.CreationId != current.CreationId ||
            recorded.StoragePath != current.StoragePath || recorded.MarkerSha256 != current.MarkerSha256 ||
            !recorded.ControllerAddresses.SequenceEqual(current.ControllerAddresses, StringComparer.Ordinal))
            throw new BridgeException("PlcsimTargetBindingStale", "The owned PLCSIM instance no longer matches its binding. Bind it again; nothing was sent.");
    }

    public static JsonObject TargetIdentity(PlcsimTargetBinding binding) =>
        new() { ["scheme"] = "plcsim-owned-instance", ["value"] = binding.InstanceName + "#" + binding.MarkerSha256 };
    public static JsonObject? ProgramIdentity(PlcsimTargetBinding binding) => binding.ProgramFingerprintSha256 is null ? null :
        new() { ["scheme"] = "plcsim-online-fingerprint-sha256", ["value"] = binding.ProgramFingerprintSha256 };

    private static void Validate(PlcsimTargetBinding binding) => Require(binding is { SchemaVersion: "plcsim-target-binding.v1", InstanceId: >= 0 } &&
        Text(binding.InstanceName, 128) && Hex(binding.CreationId, 32) && Text(binding.StoragePath, 1024) && Hex(binding.MarkerSha256, 64) &&
        binding.ControllerAddresses is { Length: >= 1 and <= 16 } && binding.ControllerAddresses.All(a => IPAddress.TryParse(a, out var ip) &&
            ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() == a && a != "0.0.0.0") &&
        binding.ControllerAddresses.SequenceEqual(binding.ControllerAddresses.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) &&
        (binding.ProgramFingerprintSha256 is null || Hex(binding.ProgramFingerprintSha256, 64)));
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool Hex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw Error(); }
    private static BridgeException Error() => new("PlcsimTargetBindingInvalid",
        "The owned PLCSIM binding is missing, changed or does not describe the exact owned instance with a usable controller address. No target is bound.");
}
