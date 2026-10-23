using System.Collections.ObjectModel;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Host;

internal sealed partial class CredentialVault
{
    private static string CanonicalRoot(string path) => EngineeringResourceKey.ForPath("workspace", path)["workspace:".Length..];
    private static CredentialStatus Status(StoredCredential record, Lifecycle state, bool verified) =>
        new(record.Reference, record.Binding, state.State, verified) { OperationReferences = state.Pins };

    // Vault first, journal second. Journal callbacks use the supplied frozen record, never a recursive journal read.
    internal EngineeringOperationRecord AcquireOperation(string artifactRoot, string id, int revision)
    {
        using var held = Lock(); var header = Header();
        string location = CanonicalRoot(artifactRoot); var store = new EngineeringOperationStore(location);
        var operation = store.Read(id);
        if (operation.Revision != revision || operation.State != "prepared") throw Error("CredentialAdmissionRequired");
        var records = OperationRecords(operation, header);
        var pin = new CredentialOperationReference(location, id, operation.PlanSha256);
        foreach (var record in records.DistinctBy(r => r.Reference.CredentialId))
        {
            var state = ReadLifecycle(record.Reference);
            if (state.Pins?.Contains(pin) == true) continue;
            var pins = (state.Pins ?? []).Append(pin).ToArray();
            if (pins.Length > 64) throw Error("CredentialCapacity");
            Publish(record.Reference.CredentialId + ".state.json", state with { SchemaVersion = "protected-credential-lifecycle.v2", Pins = pins }, replace: true);
        }
        return store.Acquire(id, revision, r => VerifyPins(r, location, header));
    }

    internal EngineeringOperationRecord BeginOperationCheckpoint(string artifactRoot, string id, int revision,
        Func<NativeTargetObservation> reacquire)
    {
        using var held = Lock(); var header = Header(); string location = CanonicalRoot(artifactRoot);
        var store = new EngineeringOperationStore(location);
        if (store.Read(id).Plan.Credentials is null) throw Error("CredentialAdmissionRequired");
        return store.BeginCheckpoint(id, revision, reacquire, r => VerifyPins(r, location, header));
    }

    /// <summary>One synchronous trusted provider invocation, only after existing durable dispatch intent. Never marks success.</summary>
    internal EngineeringOperationRecord DispatchOperation(string artifactRoot, string id, int revision,
        Func<NativeTargetObservation> reacquire, Action<IReadOnlyDictionary<string, byte[]>> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        using var held = Lock(); var header = Header(); string location = CanonicalRoot(artifactRoot);
        var store = new EngineeringOperationStore(location);
        if (store.Read(id).Plan.Credentials is null) throw Error("CredentialAdmissionRequired");
        var dispatched = store.Dispatch(id, revision, reacquire, r => VerifyPins(r, location, header));
        return ConsumeOperation(store, dispatched, header, "dispatch", consumer);
    }

    /// <summary>Uses only recovery slots once, after durable fresh-context admission; reconciliation remains independent.</summary>
    internal EngineeringOperationRecord RecoverOperation(string artifactRoot, string id, int revision,
        IReadOnlyList<EngineeringRecoveryDestination> destinations, NativeTargetObservation reviewedTarget,
        Func<NativeTargetObservation> reacquire, Action<IReadOnlyDictionary<string, byte[]>> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        using var held = Lock(); var header = Header(); string location = CanonicalRoot(artifactRoot);
        var store = new EngineeringOperationStore(location);
        if (store.Read(id).Plan.Credentials?.Any(c => c.UsePhase == "recovery") != true) throw Error("CredentialAdmissionRequired");
        var admitted = store.ReserveRecovery(id, revision, destinations, r => VerifyPins(r, location, header, "recovery"), reviewedTarget, reacquire);
        return ConsumeOperation(store, admitted, header, "recovery", consumer);
    }

    private EngineeringOperationRecord ConsumeOperation(EngineeringOperationStore store, EngineeringOperationRecord admitted,
        VaultHeader header, string phase, Action<IReadOnlyDictionary<string, byte[]>> consumer)
    {
        var requirements = admitted.Plan.Credentials!.Where(c => c.UsePhase == phase).ToArray();
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Consume(int index)
        {
            if (index == requirements.Length) { consumer(new ReadOnlyDictionary<string, byte[]>(bytes)); return; }
            var item = requirements[index]; var record = Read(item.Reference, item.Binding, header);
            Decrypt(record, header, clear => { bytes.Add(item.Name, clear); Consume(index + 1); });
        }
        try { Consume(0); }
        catch
        {
            // Intent alone retains ownership even if recording the unknown outcome fails.
            try { store.MarkUnknown(admitted.Id, admitted.Revision); } catch { }
            throw Error("CredentialConsumerFailed");
        }
        finally { bytes.Clear(); }
        return admitted;
    }

    private StoredCredential[] OperationRecords(EngineeringOperationRecord operation, VaultHeader header)
    {
        if (operation.Plan.Credentials is null) throw Error("CredentialAdmissionRequired");
        return operation.Plan.Credentials.Select(c =>
        {
            var record = Read(c.Reference, c.Binding, header);
            if (ReadLifecycle(record.Reference).State == "retired") throw Error("CredentialRetired");
            Decrypt(record, header, _ => { });
            return record;
        }).ToArray();
    }
    private void VerifyPins(EngineeringOperationRecord operation, string location, VaultHeader header, string? phase = null)
    {
        if (operation.Plan.Credentials is null) throw Error("CredentialAdmissionRequired");
        var expected = new CredentialOperationReference(location, operation.Id, operation.PlanSha256);
        foreach (var item in operation.Plan.Credentials)
        {
            if (item.Reference.VaultId != header.VaultId) throw Error("CredentialContextMismatch");
            var lifecycle = ReadLifecycle(item.Reference);
            if (lifecycle.State == "retired") throw Error("CredentialRetired");
            if (lifecycle.Pins?.Contains(expected) != true) throw Error("CredentialAdmissionRequired");
            // A damaged dispatch-only ciphertext must not disable a separately readable recovery generation.
            if (phase is null || item.UsePhase == phase) Decrypt(Read(item.Reference, item.Binding, header), header, _ => { });
        }
    }
    private static void VerifyReleased(Lifecycle lifecycle)
    {
        foreach (var pin in lifecycle.Pins ?? [])
        {
            EngineeringOperationRecord operation;
            try
            {
                if (!Directory.Exists(Path.Combine(pin.ArtifactRoot, "engineering-operations"))) throw Error("CredentialReservationUnresolved");
                operation = new EngineeringOperationStore(pin.ArtifactRoot).Read(pin.OperationId);
                if (operation.PlanSha256 != pin.PlanSha256 || operation.Plan.Credentials?.Any(c => c.Reference == lifecycle.Reference) != true)
                    throw Error("CredentialReservationUnresolved");
            }
            catch { throw Error("CredentialReservationUnresolved"); }
            if (operation.State is not ("stopped" or "succeeded" or "reconciled")) throw Error("CredentialInUse");
        }
    }
}
