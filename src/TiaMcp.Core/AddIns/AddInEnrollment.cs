using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace TiaMcp.Core.AddIns;

/// <summary>Explicit opt-in producer enrollment for the published V21 companion: OS lineage plus pinned package hashes, not attestation.</summary>
internal sealed record AddInEnrollment(string AssemblySha256, string ConfigurationSha256)
{
    // TIA V21 runs Add-Ins in this loader below <install>\Bin; taken from the installed layout, not natively qualified.
    internal const string LoaderImage = "Siemens.Engineering.AddIn.Loader.x64.exe";
    private sealed record OwnerContext(string? State, bool OsProcessIdentityVerified, bool NativeProcessIdMatched, AddInHandoffPeer? Process,
        int WorkerProcessId, string? WorkerInstanceId, string? SessionGeneration);

    internal static AddInEnrollment Read(string path)
    {
        using var input = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(input.Length is > 0 and <= 4096, "InvalidAddInEnrollment");
        byte[] bytes = new byte[input.Length]; input.ReadExactly(bytes);
        JsonObject value;
        try { value = AddInSelection.Parse(bytes); }
        catch (Exception error) when (error is BridgeException or JsonException or ArgumentException) { Require(false, "InvalidAddInEnrollment"); throw; }
        Require(value.Count == 3 && value["schemaVersion"] is JsonValue version && version.TryGetValue<string>(out var text) && text == "addin-enrollment.v1", "InvalidAddInEnrollment");
        return new(Digest(value, "assemblySha256"), Digest(value, "configurationSha256"));
    }

    internal Func<AddInHandoffPeer, string, string, CancellationToken, Task<AddInHandoffBinding>> Authority(IBridgeClient bridge, string artifacts, Func<int, int>? parentOf = null)
    {
        parentOf ??= ParentProcessId;
        return async (peer, reviewId, _, _) =>
        {
            // Cancelling an admitted bridge call faults the shared engineering session; the handoff deadline abandons this wait instead.
            var owner = (await bridge.CallAsync("addin_owner_context", new { }, CancellationToken.None)).Deserialize<OwnerContext>(BridgeProtocol.Json);
            Require(owner is { State: "available", OsProcessIdentityVerified: true, NativeProcessIdMatched: true, Process: not null }, "AddInOwnerUnavailable");
            var tia = owner!.Process!;
            string loader = Path.Combine(Path.GetDirectoryName(tia.ExecutablePath) ?? "", "PublicAPI", "AddIn", "Loader", LoaderImage);
            Require(peer.WindowsSessionId == tia.WindowsSessionId && string.Equals(peer.ExecutablePath, loader, StringComparison.OrdinalIgnoreCase) &&
                Utc(peer.StartedAtUtc) >= Utc(tia.StartedAtUtc) && parentOf(peer.ProcessId) == tia.ProcessId, "AddInProducerNotEnrolled");
            // Project path and snapshot come from the saved review; the live checks are owner generation, host and peer lineage.
            var model = new ProjectReviewStore(artifacts).Read(reviewId)["model"]!;
            string evidence = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { owner, peer, enrollment = this }, BridgeProtocol.Json)));
            // The Add-In runs in the loader child, so that verified peer is the bound TIA-side process; the portal identity is in the evidence.
            return new(owner.WorkerProcessId, owner.WorkerInstanceId!, owner.SessionGeneration!, peer, Environment.MachineName,
                (string)model["project"]!["projectPath"]!, (string)model["snapshotId"]!, AssemblySha256, ConfigurationSha256, evidence, false);
        };
    }

    private static DateTime Utc(string value) => DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) && time.Kind == DateTimeKind.Utc
        ? time : throw new BridgeException("AddInProducerNotEnrolled", "Process start time is not an exact UTC observation.");
    private static string Digest(JsonObject value, string key)
    {
        string? text = value[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        Require(text is { Length: 64 } && text.All(char.IsAsciiHexDigitLower), "InvalidAddInEnrollment"); return text!;
    }
    private static void Require(bool value, string code)
    {
        if (!value) throw new BridgeException(code, code switch {
            "InvalidAddInEnrollment" => "Enrollment must be exactly {schemaVersion: addin-enrollment.v1, assemblySha256, configurationSha256} as written by Publish-AddIn.ps1.",
            "AddInOwnerUnavailable" => "No attached worker owns a verified TIA process; attach this Host to the TIA instance first. Nothing was started or attached.",
            _ => "Add-In producer refused: it must run in TIA's Add-In loader below the TIA process owned by the attached worker." });
    }

    internal static int ParentProcessId(int id)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var process = Process.GetProcessById(id);
        if (NtQueryInformationProcess(process.SafeHandle, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
            throw new BridgeException("AddInProducerNotEnrolled", "Producer parent process is unavailable.");
        return checked((int)info.InheritedFromUniqueProcessId);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, out ProcessBasicInformation information, int length, out int returned);
}
