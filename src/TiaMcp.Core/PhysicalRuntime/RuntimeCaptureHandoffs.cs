using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Security;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.PhysicalRuntime;

public sealed record RuntimeCaptureSession(string HostId, WebRuntimeContext Context, string SubjectId,
    string SecurityMode, CredentialReference Credential, bool PeerVerified);
public sealed record RuntimeCaptureHandoff(string SchemaVersion, string RecordRef, string ReviewId,
    RuntimeCaptureSession Session, string DeclarationState, bool DeviceIdentityVerified, bool ProgramMatchVerified,
    bool EffectiveRightsVerified, bool SourceAuthenticityVerified, bool OperationAdmitted, bool AssertionsAdmitted);

/// <summary>Links supplied handoff evidence to an owned capture context; never grants runtime authority.</summary>
public static class RuntimeCaptureHandoffs
{
    public static void ValidateRequest(RuntimeCaptureRequest request)
    {
        if (request.HandoffRef is null) return;
        Reference(request.HandoffRef);
        Require(request.ReviewId is not null);
    }

    public static RuntimeCaptureHandoff? Resolve(string artifactsRoot, RuntimeCaptureRequest request,
        RuntimeCaptureSession session, RuntimeCaptureSignal[] signals)
    {
        ValidateRequest(request);
        if (request.HandoffRef is null) return null;
        var retained = new RuntimeCaptureHandoff("runtime-capture-handoff.v1", request.HandoffRef, request.ReviewId!,
            session, "consistentDeclarations", false, false, false, false, false, false);
        ValidateRetained(request, signals, retained);
        ValidateEvidence(artifactsRoot, retained);
        return retained;
    }

    public static void ValidateRead(string artifactsRoot, RuntimeCaptureHandoff handoff, RuntimeCaptureSession session)
    {
        Require(handoff.Session == session);
        ValidateEvidence(artifactsRoot, handoff);
    }

    public static void ValidateEvidence(string artifactsRoot, RuntimeCaptureHandoff handoff)
    {
        Shape(handoff);
        JsonObject report;
        try { report = new RuntimeHandoffWorkflowStore(artifactsRoot).ReadVerifiedReport(handoff.RecordRef); }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { throw new BridgeException("RuntimeCaptureHandoffUnavailable", "Original handoff evidence is missing, changed or invalid. Capture does not authorize a runtime operation."); }
        Require((string?)report["reviewId"] == handoff.ReviewId &&
            (string?)report["summary"]?["state"] == handoff.DeclarationState);
        var expected = report["expected"]!; var session = handoff.Session;
        Require((string?)expected["hostId"] == session.HostId && (string?)expected["subjectId"] == session.SubjectId &&
            (string?)expected["securityMode"] == session.SecurityMode &&
            JsonNode.DeepEquals(expected["context"], JsonSerializer.SerializeToNode(session.Context, BridgeProtocol.Json)) &&
            JsonNode.DeepEquals(expected["credential"], JsonSerializer.SerializeToNode(session.Credential, BridgeProtocol.Json)));
    }

    public static void ValidateRetained(RuntimeCapturePlan plan) => ValidateRetained(plan.Request, plan.Signals, plan.Handoff);

    internal static void ValidateRetained(RuntimeCaptureRequest request, RuntimeCaptureSignal[] signals, RuntimeCaptureHandoff? handoff)
    {
        if (request.HandoffRef is null) { Require(handoff is null); return; }
        Require(handoff is not null);
        Shape(handoff!);
        Require(handoff!.RecordRef == request.HandoffRef && handoff.ReviewId == request.ReviewId &&
            handoff.Session.Context.ConnectionId == request.AttemptId &&
            signals.All(signal => signal.Selection.Context == handoff.Session.Context));
    }

    private static void Shape(RuntimeCaptureHandoff handoff)
    {
        Require(handoff is not null && handoff.SchemaVersion == "runtime-capture-handoff.v1" &&
            handoff.DeclarationState == "consistentDeclarations" && !handoff.DeviceIdentityVerified &&
            !handoff.ProgramMatchVerified && !handoff.EffectiveRightsVerified && !handoff.SourceAuthenticityVerified &&
            !handoff.OperationAdmitted && !handoff.AssertionsAdmitted && handoff.Session is not null);
        Reference(handoff!.RecordRef);
        Require(Hex(handoff.ReviewId, 64));
        var session = handoff.Session;
        Require(Hex(session.HostId, 32) && session.Context is not null && session.PeerVerified &&
            !string.IsNullOrWhiteSpace(session.SubjectId) && session.SubjectId.Length <= 512 &&
            session.SecurityMode is "local" or "static" or "disabled" or "umc");
        EngineeringIdentityGuard.Text(session.SubjectId, "subjectId", 512);
        WebRuntimeProtocol.Validate(session.Context);
        ProtectedCredentialGuard.ValidateReference(session.Credential);
    }

    private static void Reference(string value)
    {
        var parts = value?.Split(':');
        Require(parts is { Length: 3 } && parts[0] == "runtime-handoff" && Hex(parts[1], 32) && Hex(parts[2], 64));
    }
    private static bool Hex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Require([DoesNotReturnIf(false)] bool condition)
    { if (!condition) throw new BridgeException("RuntimeCaptureHandoffMismatch", "Handoff declarations, retained review or owned session context do not match. No target/program identity or operation authority is inferred."); }
}
