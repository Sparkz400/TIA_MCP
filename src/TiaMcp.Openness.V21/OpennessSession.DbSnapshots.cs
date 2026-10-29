using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Blocks.Interface;

namespace TiaMcp.Openness.V21
{
    // DB snapshots: read-only export of the snapshot stored on one exact DB, and two prepared online changes limited to a
    // ticket-bound owned PLCSIM instance through the plc_online_compare route. CreateSnapshot stores the instance's actual
    // values in the offline DB (a project change); LoadSnapshotAsActualValues writes the stored snapshot into the instance.
    internal sealed partial class OpennessSession
    {
        private JObject DbSnapshotExport(JObject args)
        {
            var target = DbSnapshotTarget(args, args);
            string path = Path.Combine(ArtifactDirectory(args), "db-snapshot-" + Guid.NewGuid().ToString("N") + ".xml");
            DbSnapshotExportTo(target, path);
            var result = Envelope();
            result["identity"] = target.Identity.DeepClone();
            result["artifact"] = ChangeArtifact(path);
            result["valueDomain"] = "storedSnapshotValues";
            result["note"] = "InterfaceSnapshot.Export of the snapshot stored on the offline DB; it is not a current CPU read and the project is unchanged.";
            return result;
        }

        private NativeObjectTarget DbSnapshotTarget(JObject selector, JObject payload)
        {
            var target = ResolveObjectTarget(new JObject { ["projectName"] = selector["projectName"], ["plcPath"] = selector["plcPath"], ["kind"] = "block",
                ["objectPath"] = ChangeText(payload, "blockPath", 4096), ["projectPath"] = payload["projectPath"] });
            if (!(target.Block.Item is DataBlock)) throw new BridgeException("UnsupportedObjectKind", "Select an exact GlobalDB, InstanceDB or ArrayDB by blockPath.");
            return target;
        }

        private static void DbSnapshotExportTo(NativeObjectTarget target, string path)
        {
            // The documented accessor is the block; the interface fallback costs one lookup if V21 moved it.
            var snapshot = target.Block.Item.GetService<InterfaceSnapshot>() ?? ((DataBlock)target.Block.Item).Interface?.GetService<InterfaceSnapshot>()
                ?? throw new BridgeException("UnsupportedCapability", "The selected DB exposes no InterfaceSnapshot service.");
            try { snapshot.Export(new FileInfo(path), ExportOptions.WithDefaults); }
            catch (EngineeringException ex) { throw new BridgeException("DbSnapshotUnavailable", "The selected DB has no exportable stored snapshot: " + ex.Message); }
            if (!File.Exists(path)) throw new BridgeException("DbSnapshotUnavailable", "InterfaceSnapshot.Export produced no file.");
        }

        private void PrepareDbSnapshotChange(EngineeringChange change, JObject preview)
        {
            if (!_ownsPortal) throw new BridgeException("ScratchIsolationRequired", "DB snapshot capture and actual-value loads require a worker-owned TIA instance started with tia_start.");
            if (string.IsNullOrWhiteSpace(change.Payload.Value<string>("downloadTicket")))
                throw new BridgeException("PhysicalPlcRefused", "DB snapshot capture and actual-value loads are limited to a ticket-bound owned PLCSIM Advanced instance (downloadTicket from sim_create); physical PLCs are refused.");
            var login = OwnedSimulationLogin(change.Payload, OwnedSimulationMarker(change.Payload));
            if (login != null) preview["login"] = login.Evidence;
            var target = DbSnapshotTarget(change.Selector, change.Payload);
            EnsureEditableBlock(target.Block.Item, false);
            if (target.Block.NativeSystemGroup) throw new BridgeException("GeneratedObject", "Generated system-group DBs are excluded.");
            OwnedSimulationHardware(target.Plc);
            if ((target.Block.Item as DataBlock).Interface?.GetService<ValueService>() == null)
                throw new BridgeException("UnsupportedCapability", "The selected DB interface exposes no ValueService.");
            var effects = (JArray)preview["effects"]; var warnings = (JArray)preview["warnings"];
            if (change.Kind == "db_snapshot_create")
            {
                string before = Path.Combine(change.Directory, "before-snapshot.xml");
                try { DbSnapshotExportTo(target, before); change.Backups.Add(new JObject { ["role"] = "previous-snapshot", ["artifact"] = ChangeArtifact(before) }); }
                catch (BridgeException ex) when (ex.Code == "DbSnapshotUnavailable") { }
                effects.Add(new JObject { ["action"] = "createSnapshot", ["blockPath"] = target.Identity["objectPath"].DeepClone(), ["previousSnapshotExported"] = change.Backups.Count > 0 });
                warnings.Add("Goes online to the owned PLCSIM instance, stores its current actual values as the DB snapshot in the project (replacing any previous snapshot) and goes offline. Recovery uses the project checkpoint archive.");
                return;
            }
            RequireChangeBool(change.Payload, "acknowledgeActualValueWrite");
            string expected = ChangeText(change.Payload, "snapshotSha256", 64);
            string current = Path.Combine(change.Directory, "snapshot-to-load.xml");
            DbSnapshotExportTo(target, current);
            if (ChangeFileHash(current) != expected) throw new BridgeException("DbSnapshotChanged", "The DB's stored snapshot differs from the reviewed capture; capture and review it again.");
            change.Backups.Add(new JObject { ["role"] = "snapshot-to-load", ["artifact"] = ChangeArtifact(current) });
            effects.Add(new JObject { ["action"] = "loadSnapshotAsActualValues", ["blockPath"] = target.Identity["objectPath"].DeepClone(),
                ["snapshotRecordId"] = change.Payload["snapshotRecordId"]?.DeepClone(), ["snapshotSha256"] = expected });
            warnings.Add("Writes the stored snapshot values as actual values into the owned PLCSIM instance's DB. This is a CPU write: the project checkpoint cannot undo it and no public API reads the values back.");
        }

        private void ApplyDbSnapshotChange(EngineeringChange change)
        {
            bool create = change.Kind == "db_snapshot_create";
            var marker = OwnedSimulationMarker(change.Payload);
            var target = DbSnapshotTarget(change.Selector, change.Payload);
            EnsureEditableBlock(target.Block.Item, false);
            if (!create)
            {
                string current = Path.Combine(change.Directory, "snapshot-before-load.xml");
                DbSnapshotExportTo(target, current);
                if (ChangeFileHash(current) != change.Payload.Value<string>("snapshotSha256"))
                    throw new BridgeException("DbSnapshotChanged", "The DB's stored snapshot changed since preparation; nothing was loaded.");
            }
            var values = ((DataBlock)target.Block.Item).Interface?.GetService<ValueService>() ?? throw new BridgeException("UnsupportedCapability", "The selected DB interface exposes no ValueService.");
            var report = OwnedSimulationReport(marker, create ? "DB snapshot capture" : "DB snapshot actual-value load");
            change.Readbacks.Add(new JObject { ["role"] = "owned-simulation-online", ["report"] = report });
            // Planned during shared admission, like the compare route's observer project.
            string observer = SimulationDiscoveryObserverDirectory(_sharedMutationArguments);
            var failure = OwnedSimulationOnline(marker, target.Plc, OwnedSimulationHardware(target.Plc), observer, report, create ? "DbSnapshotCreateFailed" : "DbSnapshotLoadFailed", null, challenges =>
            {
                report["stage"] = create ? "createSnapshot" : "loadSnapshotAsActualValues";
                if (create) values.CreateSnapshot(); else values.LoadSnapshotAsActualValues();
                if (challenges.Failure != null) throw challenges.Failure;
            }, OwnedSimulationLogin(change.Payload, marker));
            if (failure != null) throw failure;
            report["completed"] = true;
            if (!create)
            {
                change.Readbacks.Add(new JObject { ["role"] = "actual-values", ["verified"] = false, ["reason"] = "No public V21 API reads DB actual values back; the load is reported, not verified." });
                return;
            }
            string after = Path.Combine(change.Directory, "after-snapshot.xml");
            DbSnapshotExportTo(target, after);
            var readback = ChangeArtifact(after);
            change.Readbacks.Add(new JObject { ["role"] = "db-snapshot-readback", ["artifact"] = readback });
            if (change.Backups.Any(backup => backup.Value<string>("role") == "previous-snapshot" && backup["artifact"].Value<string>("sha256") == readback.Value<string>("sha256")))
                throw new BridgeException("MutationVerificationFailed", "The exported DB snapshot is unchanged after CreateSnapshot.");
        }
    }
}
