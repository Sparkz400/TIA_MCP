using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core.Diagnostics;

namespace TiaMcp.Core.PhysicalRuntime;

/// <summary>Datatype is the caller's declared PLC type; it must fit the typed PLCSIM read and is what diagnostic mappings compare.</summary>
public sealed record SimulatorCaptureSignal(string Symbol, string Type, string Datatype);
public sealed record SimulatorCaptureSource(int InstanceId, string InstanceName, SimulatorCaptureSignal[] Signals, bool CycleSynchronous = false);
public sealed record SimulatorSyncPoint(long Count, long TimeSinceSameSyncPointNanoseconds, long TimeSinceAnySyncPointNanoseconds);
public sealed record SimulatorCaptureRead(int InstanceId, string InstanceName, string Symbol, string Type, string Status, JsonElement? Value,
    string? ProviderErrorCode, DateTimeOffset RequestStartedUtc, DateTimeOffset ResponseReceivedUtc, SimulatorSyncPoint? SyncPoint)
{
    public string SchemaVersion => "plcsim-runtime-read.v1";
    public string Clock => "simulatorWorkerUtc";
    public bool HostClockWentBackwards => ResponseReceivedUtc < RequestStartedUtc;
    public bool ProgramMatchVerified => false;
    public bool NativeQualified => false;
}

/// <summary>PLCSIM source admission and retained-row validation; the capture journal and poller are shared with the Web source.</summary>
public static class SimulatorCaptureContracts
{
    private static readonly Dictionary<string, string[]> Datatypes = new(StringComparer.Ordinal) {
        ["bool"] = ["Bool"], ["int8"] = ["SInt"], ["uint8"] = ["USInt", "Byte"], ["int16"] = ["Int"], ["uint16"] = ["UInt", "Word"],
        ["int32"] = ["DInt"], ["uint32"] = ["UDInt", "DWord"], ["int64"] = ["LInt"], ["uint64"] = ["ULInt", "LWord"],
        ["float"] = ["Real"], ["double"] = ["LReal"], ["string"] = ["String"], ["wstring"] = ["WString"] };
    private static readonly JsonSerializerOptions Strict = new(BridgeProtocol.Json) {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    public static readonly string[] ReadErrors = ["PlcsimCaptureReadFailed", "PlcsimResponseInvalid", "SimulationNotSelected",
        "SimulationTargetMismatch", "SimulationOwnershipUnverified", "SimulationCaptureModeInactive", "SimulationSyncPointTimeout",
        "NativeWorkersDisabled", "BridgeFaulted", "TimeoutOutcomeUnknown", "WorkerDisconnected"];

    public static void ValidateRequest(RuntimeCaptureRequest request)
    {
        if (request.Source is null && request.Simulator is null) return;
        var source = request.Simulator;
        // attemptId is the owned instance's creationId; the Web session handoff and raw representation do not apply.
        Require(request.Source == "plcsim" && source is not null && request.HandoffRef is null && request.Representation == "simple" &&
            source.InstanceId >= 0 && Text(source.InstanceName, 128) && source.Signals is { } signals &&
            signals.Length == request.SelectionIds.Length && signals.All(s => s is not null) &&
            signals.Select(s => s.Symbol).Distinct(StringComparer.Ordinal).Count() == signals.Length);
        foreach (var signal in source!.Signals)
            Require(Text(signal.Symbol, 512) && signal.Type is not null && Datatypes.TryGetValue(signal.Type, out var allowed) &&
                allowed.Contains(signal.Datatype, StringComparer.OrdinalIgnoreCase));
    }

    public static RuntimeCaptureSignal[] Signals(RuntimeCaptureRequest request) =>
        request.Simulator!.Signals.Select((s, i) => new RuntimeCaptureSignal(request.SelectionIds[i], null,
            JsonSerializer.SerializeToElement(new { name = s.Symbol, datatype = s.Datatype, simulatorType = s.Type }, BridgeProtocol.Json))).ToArray();

    /// <summary>One worker argument shape for begin, sample and end.</summary>
    public static object Arguments(RuntimeCaptureRequest request) => new { instanceId = request.Simulator!.InstanceId,
        instanceName = request.Simulator.InstanceName, creationId = request.AttemptId, cycleSynchronous = request.Simulator.CycleSynchronous,
        signals = request.Simulator.Signals.Select(s => new { symbol = s.Symbol, type = s.Type }) };

    public static void ValidatePlan(RuntimeCapturePlan plan) =>
        Require(plan.UaContext is null && plan.Handoff is null && JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(plan.Signals, BridgeProtocol.Json), JsonSerializer.SerializeToElement(Signals(plan.Request), BridgeProtocol.Json)));

    public static void ValidateRead(RuntimeCaptureRequest request, string selectionId, string? errorCode, string? json)
    {
        if (errorCode is not null) { Require(ReadErrors.Contains(errorCode, StringComparer.Ordinal)); return; }
        var source = request.Simulator!; int index = Array.IndexOf(request.SelectionIds, selectionId);
        Require(index >= 0 && json is not null);
        try
        {
            using var original = JsonDocument.Parse(json!);
            var read = original.RootElement.Deserialize<SimulatorCaptureRead>(Strict);
            var signal = source.Signals[index];
            Require(read is not null && read.InstanceId == source.InstanceId && read.InstanceName == source.InstanceName &&
                read.Symbol == signal.Symbol && read.Type == signal.Type && (read.SyncPoint is not null) == source.CycleSynchronous &&
                read.SyncPoint is null or { Count: >= 0, TimeSinceSameSyncPointNanoseconds: >= 0, TimeSinceAnySyncPointNanoseconds: >= 0 } &&
                JsonElement.DeepEquals(original.RootElement, JsonSerializer.SerializeToElement(read, BridgeProtocol.Json)));
            bool missing = read!.Value is null or { ValueKind: JsonValueKind.Null };
            if (read.Status == "read") Require(read.ProviderErrorCode is null && !missing && Value(signal, read.Value!.Value));
            else Require(read.Status == "readFailed" && missing && read.ProviderErrorCode is { Length: >= 1 and <= 64 } code && code.All(char.IsAsciiLetterOrDigit));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { Require(false); }
    }

    /// <summary>Maps one worker sample to per-signal exact read JSON. An unusable response yields explicit error rows, never values.</summary>
    public static (string? Result, string? Error)[] Reads(RuntimeCaptureRequest request, JsonElement response)
    {
        var source = request.Simulator!;
        try
        {
            var values = response.GetProperty("values");
            Require(response.GetProperty("instanceId").GetInt32() == source.InstanceId && response.GetProperty("instanceName").GetString() == source.InstanceName &&
                values.GetArrayLength() == source.Signals.Length);
            var sync = response.TryGetProperty("syncPoint", out var point) && point.ValueKind != JsonValueKind.Null ? point.Deserialize<SimulatorSyncPoint>(Strict) : null;
            return source.Signals.Select((signal, i) =>
            {
                var value = values[i]; bool good = value.GetProperty("quality").GetString() == "good";
                Require(value.GetProperty("symbol").GetString() == signal.Symbol && value.GetProperty("type").GetString() == signal.Type);
                var read = new SimulatorCaptureRead(source.InstanceId, source.InstanceName, signal.Symbol, signal.Type, good ? "read" : "readFailed",
                    good ? value.GetProperty("value").Clone() : null, good ? null : value.GetProperty("errorCode").GetString(),
                    value.GetProperty("observedAtUtc").GetDateTimeOffset(), value.GetProperty("completedAtUtc").GetDateTimeOffset(), sync);
                string json = JsonSerializer.Serialize(read, BridgeProtocol.Json);
                ValidateRead(request, request.SelectionIds[i], null, json);
                return ((string?)json, (string?)null);
            }).ToArray();
        }
        catch (Exception error) when (error is BridgeException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException)
        { return source.Signals.Select(_ => ((string?)null, (string?)"PlcsimResponseInvalid")).ToArray(); }
    }

    public static string ReadError(Exception error) =>
        error is BridgeException e && ReadErrors.Contains(e.Code, StringComparer.Ordinal) ? e.Code : "PlcsimCaptureReadFailed";

    internal static bool Value(SimulatorCaptureSignal signal, JsonElement value) => signal.Type switch {
        "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "string" or "wstring" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 16384,
        "float" or "double" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double real) && double.IsFinite(real),
        _ => value.ValueKind == JsonValueKind.Number && Integer(value.GetRawText(), signal.Datatype) };
    private static bool Integer(string text, string datatype)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) return false;
        try { TraceMeasurements.ValidateNumericSample(number, datatype); return true; } catch (BridgeException) { return false; }
    }
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition)
    { if (!condition) throw new BridgeException("PlcsimCaptureInvalid", "Invalid PLCSIM capture source or retained evidence. Only an exact owned simulator instance is read; no PLC values are written."); }
}
