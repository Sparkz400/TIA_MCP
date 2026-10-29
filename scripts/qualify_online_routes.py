"""Qualify the D09 online routes (plc_online_compare, plc_station_upload, db_snapshot_capture and the change kinds
db_snapshot_create and db_snapshot_load_actual) on one owned PLCSIM Advanced instance, logged in only as the instance's
generated scratch runtime user.

The fixture is an owned scratch CPU 1511-1 PN with simulation support and a global DB OnDb (Count : Int := 5), added with
checkpoint, compile and save, and a saved review of it. The program is downloaded to a fresh owned PLCSIM Advanced V8
instance; after the generated-state save and protection-credential provisioning the CPU runs. Typed refusals come first and
change nothing: an unknown ticket, the CPU's protection credential, runtime-user metadata naming another project or CPU, a
copy outside scratch-credentials, no runtime credential (online, TIA's login challenge stays unanswered), an upload into an
open project, a capture of a DB without a stored snapshot, a create without a ticket and a load without acknowledgement.
With runtimeCredentialMetadataPath plc_online_compare logs in once and finds online and offline identical. Count is set to
42 through PLCSIM; db_snapshot_create stores it as the DB snapshot, db_snapshot_capture reviews it, Count is set to 7 and
db_snapshot_load_actual loads the snapshot, read back as 42. Each preview names the login, each online session answers
exactly one project-user login, and the complete project state stays unchanged; the create is restored from its
checkpoint, whose DB has no snapshot again. plc_station_upload then uploads the station into a new project in an empty
owned Portal, whose PLC has the downloaded blocks. The instance is stopped, switched off and released, and the TIA
processes that ran before the run are unchanged.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import uuid

from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_download, verify_memory_card, verify_owned_instance
from library_native_harness import Harness, require
from qualify_hardware_changes import Run
from qualify_plc_object_mutations import diff

DB = "OnDb"
SOURCE = '''DATA_BLOCK "OnDb"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
   VAR
      Count : Int := 5;
   END_VAR

BEGIN

END_DATA_BLOCK

'''
SAME = ("ObjectsIdentical", "FolderContentsIdentical", "CompareIrrelevant")
CRITERIA = {
    "plc_online_compare": {
        "AC-identity": "An unknown ticket, the CPU's protection credential, runtime-user metadata naming another project or CPU, a copy outside scratch-credentials and no credential are refused with typed codes; without a credential TIA's login challenge stays unanswered. None changes the project.",
        "AC-completeness": "With the runtime user the untruncated compare tree is identical in every row right after the download, OnDb included.",
        "AC-preserve": "Exactly one project-user login is answered, the instance stays in RUN and the complete project state (hardware, device inventory, program) is unchanged.",
        "AC-evidence": "This report."},
    "plc_station_upload": {
        "AC-identity": "Refused with ProjectIsolationRequired while a project is open; the upload logs in once as the ticket-bound instance's runtime user.",
        "AC-completeness": "The uploaded station's PLC has exactly the downloaded blocks (name, kind, number).",
        "AC-preserve": "A new project in an empty owned Portal; the instance stays in RUN.",
        "AC-recovery": "The new project and its station-upload.json are saved.",
        "AC-evidence": "This report."},
    "db_snapshot_capture": {
        "AC-identity": "A DB without a stored snapshot refuses with DbSnapshotUnavailable (V21 exports empty SnapshotValues dated 0001-01-01); the record names the exact project and DB.",
        "AC-completeness": "The review holds the value db_snapshot_create stored (Count = 42).",
        "AC-preserve": "The complete project state is unchanged.",
        "AC-evidence": "This report."},
    "change.db_snapshot_create": {
        "AC-identity": "Without a download ticket the preparation refuses with PhysicalPlcRefused.",
        "AC-preview": "The preview names the runtime-user login and createSnapshot of OnDb without a previous snapshot.",
        "AC-native": "Applied with checkpoint, compile and save, online with one project-user login; the stored snapshot holds the CPU value 42.",
        "AC-preserve": "The complete project state is otherwise unchanged.",
        "AC-recovery": "change_restore into a new directory gives the pre-change state, whose DB has no stored snapshot again.",
        "AC-evidence": "This report."},
    "change.db_snapshot_load_actual": {
        "AC-identity": "Without acknowledgeActualValueWrite the preparation refuses with InvalidRequest.",
        "AC-preview": "The preview names the runtime-user login, the reviewed record and its snapshot hash.",
        "AC-native": "Applied without compile and save, online with one project-user login; PLCSIM reads Count back as 42 after it was set to 7.",
        "AC-preserve": "The complete project state is unchanged.",
        "AC-recovery": "change_status retains the checkpoint; a project checkpoint cannot undo the CPU write (catalog limitation).",
        "AC-evidence": "This report."},
}
LIMITATIONS = [
    "One owned PLCSIM Advanced V8 CPU 1511 instance on Softbus of an owned scratch CPU 1511-1 PN V4.1. Power-on needs the provisioned protection credential, provisioned with "
    "first-use TLS trust under the owned-empty-CPU policy; the online routes themselves only log in as the generated runtime user and never trust a certificate.",
    "One global DB with one Int; arrays, structures, instance DBs and retain values are not exercised.",
    "The other-project and other-CPU refusals use edited copies of the runtime user's nonsecret metadata; the encrypted secret is never copied.",
    "Online compare is checked identical right after the download only; no online/offline difference is produced. The fingerprint is recorded, not asserted.",
    "The compared project state is what the read tools expose (hardware, device inventory, program inventory); snapshot values are read through db_snapshot_capture and PLCSIM.",
]


def nodes(tree):
    yield tree
    for child in tree["children"]:
        yield from nodes(child)


def differences(tree):
    """Compare rows whose CompareResultState is neither identical nor irrelevant."""
    return [{k: node[k] for k in ("left", "right", "state")} for node in nodes(tree) if node["state"] not in SAME]


def online_session(report, username, reused=False, pin=None, unauthenticated=False):
    """An online report that answered exactly one login challenge, the project-user login as username, kept the instance in
    RUN and completed every cleanup. reused: the same Portal logged in before, and TIA reuses that login without a challenge
    (observed 29 October 2026), so no answered challenge is also accepted. pin / unauthenticated: the station upload's TLS
    trust, its first-use certificate pin or (maintainer decision of 29 October 2026) a challenge offering no certificate; only
    then are TLS challenges answered Trusted, each with that pin or without any certificate."""
    def allowed(c):
        return c["type"] == "TlsVerificationConfiguration" and bool(
            pin and c.get("tlsAuthentication") == "firstUsePinnedCertificate" and c.get("pinnedCertificateSha256") == pin
            or unauthenticated and c.get("tlsAuthentication") == "unauthenticated" and c.get("certificates") == [])
    trusted = [c for c in report["onlineChallenges"] if c["decision"] == "Trusted"]
    require(all(allowed(c) for c in trusted), "TLS was trusted outside the upload's pin %s or certificate-less trust: %s" % (pin, trusted))
    answered = [(c["type"], c["decision"], c.get("username")) for c in report["onlineChallenges"] if c["decision"] not in ("unanswered", "Trusted")]
    require(answered == [("OnlineAuthenticationConfiguration", "ProjectUser", username)] or reused and answered == [],
            "Expected one project-user login as %s: %s" % (username, report["onlineChallenges"]))
    require(report.get("completed") is True and "failure" not in report and report["login"]["username"] == username and report["login"]["secretReturned"] is False,
            "The online session did not complete with the runtime-user login: %s" % {k: report.get(k) for k in ("completed", "failure", "login")})
    require(report["operatingStateBefore"] == report["operatingStateAfter"] == "Run", "The instance left RUN: %s" % [report["operatingStateBefore"], report["operatingStateAfter"]])
    require(all(c.get("completed") is True for c in report["cleanup"]), "Online cleanup incomplete: %s" % report["cleanup"])
    return {"onlineChallenges": report["onlineChallenges"], "login": report["login"], "cleanup": [c.get("action") for c in report["cleanup"]]}


def edited_copy(source, directory, **changes):
    """A copy of nonsecret credential metadata under its original file name; the encrypted secret is never copied."""
    directory.mkdir(parents=True)
    target = directory / Path(source).name
    target.write_text(json.dumps({**json.loads(Path(source).read_text(encoding="utf-8")), **changes}, indent=2), encoding="utf-8")
    return str(target)


def program(software):
    """Each PLC block by name with its kind and number; an upload names its project and station afresh."""
    require(software["completeness"]["truncated"] is False, "software_inspect is truncated")
    return {block["name"]: [block["kind"], block["number"]] for block in software["blocks"]}


def fixture(h, plc, kind, payload):
    prepared = h.stage("prepare " + kind, lambda: h.tool("change_prepare", {"kind": kind, "payload": payload, "plcPath": plc}))
    return h.apply_prepared(kind, prepared)


class Lab:
    def __init__(self, h, run):
        self.h, self.run = h, run

    def unchanged(self, label, operation):
        """A stage whose operation must leave the complete project state as it was after the download."""
        def check():
            result = operation()
            after = self.run.state()
            require(after == self.run.current, "%s changed the project: %s" % (label, diff(self.run.current, after)))
            return result
        return self.h.stage(label, check)

    def refuse(self, label, name, arguments, code, text=""):
        def check():
            error = self.h.call_error(name, arguments)
            require(error.get("code") == code and text in (error.get("message") or ""), "Expected %s (%s), observed %s: %s" % (code, text, error.get("code"), error.get("message")))
            return error
        return self.unchanged("refuse %s: %s" % (name, label), check)

    def resolve(self):
        """Refresh PLCSIM's tag list through sim_tags and resolve OnDb.Count. After db_snapshot_load_actual PLCSIM reads the
        old list as NotUpToDate (-47, observed 29 October 2026)."""
        tags = self.h.tool("sim_tags", {"instanceId": self.instance["id"], "filter": DB, "maxItems": 2000})["tags"]
        found = [t["symbol"] for t in tags if t["symbol"].replace('"', "") == DB + ".Count"]
        require(len(found) == 1, "PLCSIM symbol %s.Count is missing or ambiguous: %s" % (DB, [t["symbol"] for t in tags]))
        self.symbol = found[0]
        return self.symbol

    def count(self, value=None):
        """Count read through PLCSIM; with a value, written first and read back."""
        signal = {"symbol": self.symbol, "type": "int16"}
        if value is not None:
            self.h.tool("sim_write", {"instanceId": self.instance["id"], "writes": [{**signal, "value": value}]})
        row = self.h.tool("sim_read", {"instanceId": self.instance["id"], "signals": [signal]})["values"][0]
        require(row["quality"] == "good" and value in (None, row["value"]), "PLCSIM reads %s as %s" % (self.symbol, row))
        return row["value"]

    def prepare(self, kind, payload):
        payload = {**payload, "downloadTicket": self.ticket, "runtimeCredentialMetadataPath": self.login}
        prepared = self.h.stage("prepare " + kind, lambda: self.h.tool("change_prepare", {"kind": kind, "payload": payload, "plcPath": self.run.plc}))
        preview, login = prepared["preview"], prepared["preview"].get("login") or {}
        require(prepared["state"] == "prepared" and preview["kind"] == kind and preview["project"]["path"] == self.run.project
                and Path(login.get("runtimeCredentialMetadataPath", "")).resolve() == Path(self.login).resolve()
                and login.get("username") == self.username and login.get("secretReturned") is False,
                "The %s preview lacks the runtime-user login: %s" % (kind, {k: preview.get(k) for k in ("kind", "project", "login")}))
        return prepared

    def apply(self, kind, prepared, **flags):
        """Harness.apply_prepared with explicit flags (the actual-value load refuses compile and save), the online report
        of its one login and change_status's retained checkpoint."""
        h = self.h
        row = {"kind": kind, "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
        h.report["mutations"].append(row)
        def run():
            applied = h.tool("change_apply", {**h.apply_args(prepared), **flags})
            require(applied["state"] == "applied" and applied["recoveryArchiveSha256"], "%s did not apply with a checkpoint" % kind)
            row["state"] = "applied"
            online = [r["report"] for r in applied["readbacks"] if r["role"] == "owned-simulation-online"]
            require(len(online) == 1, "Expected one owned-simulation-online readback: %s" % applied["readbacks"])
            online_session(online[0], self.username, reused=True)  # The compare logged this Portal in before.
            status = h.tool("change_status", {"changeId": prepared["changeId"]})
            require(status["state"] == "applied" and status["recoveryArchiveSha256"] == applied["recoveryArchiveSha256"], "change_status differs from the apply result")
            return applied
        return self.unchanged("apply " + kind, run)


def body(h):
    before = h.stage("TIA processes before the run", lambda: {str(p["processId"]): p.get("projectPath") for p in h.tool("tia_discover")["processes"]})
    run = Run(h)
    run.plc = h.owned_scratch("online routes")
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    h.report["criteria"] = CRITERIA
    lab = Lab(h, run)
    cpus = h.scratch["scratchSecurity"]["cpus"]
    require(len(cpus) == 1 and cpus[0]["runtimeUser"].get("supported") is True, "Exactly one scratch CPU with a generated runtime user is required")
    secret = cpus[0]["credentialMetadataPath"]
    lab.login, lab.username = cpus[0]["runtimeUser"]["credentialMetadataPath"], cpus[0]["runtimeUser"]["username"]
    metadata = json.loads(Path(lab.login).read_text(encoding="utf-8"))
    db = run.plc + "/blocks/" + DB
    h.apply("project_simulation_enable", {"enabled": True})
    fixture(h, run.plc, "source_upsert", {"language": "SCL", "source": SOURCE, "expectedBlockNames": [DB]})
    review = h.stage("project_snapshot_capture", lambda: h.tool("project_snapshot_capture", {"request": {"plcPath": run.plc, "blockPaths": [db]}}))["reviewId"]

    h.stage("discovery baseline", lambda: h.tool("simulation_discovery_baseline", {})["baseline"])
    created = h.stage("sim_create", lambda: h.tool("sim_create", {"cpuType": "CPU1511", "creationId": uuid.uuid4().hex}))
    lab.instance = verify_owned_instance(created["instance"])
    try:
        require(lab.instance.get("operatingState") == "Off", "A fresh owned OFF instance is required")
        # The ticket expires 15 minutes after sim_create; every online step below must start before then.
        lab.ticket = created["downloadTicket"]
        card = {"downloadTicket": lab.ticket, "credentialMetadataPath": secret}
        download = h.stage("simulation_download", lambda: h.tool("simulation_download", card))
        verify_download(download, lab.instance)
        h.report["memoryCard"] = verify_memory_card(download)
        h.report["generatedProjectSave"] = save_generated_scratch_project(h.tool, download)
        # A protected card powers on only with the provisioned protection credential (SimulationConfigurationPasswordRequired otherwise).
        provisioned = h.stage("simulation_provision_scratch_credential",
                              lambda: h.tool("simulation_provision_scratch_credential", {**card, "trustOwnedEmptyCpuCertificate": True}))
        h.report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
        lab.provisioning_pin = provisioned["tlsPinnedCertificateSha256"]
        h.report["restoredMemoryCard"] = verify_memory_card(download)
        h.stage("power on", lambda: h.tool("sim_state", {"instanceId": lab.instance["id"], "action": "powerOn", "timeoutMs": 60000}))
        running = h.stage("run", lambda: h.tool("sim_state", {"instanceId": lab.instance["id"], "action": "run", "timeoutMs": 30000}))
        require(running["instance"]["operatingState"] == "Run", "The instance did not enter RUN")
        def resolve():
            lab.resolve()
            require(lab.count() == 5, "Count does not start at its start value 5")
            return lab.symbol
        h.stage("resolve OnDb.Count in PLCSIM", resolve)
        def baseline():
            run.current, lab.downloaded = run.state(), program(h.tool("software_inspect", {"plcPath": run.plc, "maxItems": 2000}))
            return {"stateKeys": len(run.current), "blocks": lab.downloaded}
        h.stage("project state after the download", baseline)

        online = {"downloadTicket": lab.ticket, "plcPath": run.plc, "maxItems": 2000}
        copies = h.directory / "credential-copies"
        lab.refuse("an unknown ticket", "plc_online_compare", {**online, "downloadTicket": "not-a-ticket", "runtimeCredentialMetadataPath": lab.login}, "InvalidSimulationTicket")
        lab.refuse("the CPU's protection credential", "plc_online_compare", {**online, "runtimeCredentialMetadataPath": secret}, "InvalidSimulationCredential")
        other = str(Path(metadata["projectPath"]).with_name("TiaMcpScratch_20000101_000000_00000000.ap21"))
        lab.refuse("runtime-user metadata naming another scratch project", "plc_online_compare",
                   {**online, "runtimeCredentialMetadataPath": edited_copy(lab.login, copies / "project" / "scratch-credentials", projectPath=other)}, "SimulationLoginTargetMismatch")
        lab.refuse("runtime-user metadata naming another CPU", "plc_online_compare",
                   {**online, "runtimeCredentialMetadataPath": edited_copy(lab.login, copies / "cpu" / "scratch-credentials", cpuName=metadata["cpuName"] + "Other")},
                   "SimulationLoginTargetMismatch")
        lab.refuse("a runtime-user metadata copy outside scratch-credentials", "plc_online_compare",
                   {**online, "runtimeCredentialMetadataPath": edited_copy(lab.login, copies / "elsewhere")}, "InvalidSimulationCredential")
        def unauthenticated():
            error = h.call_error("plc_online_compare", online)
            details = error.get("details") or {}
            challenges = details.get("onlineChallenges", [])
            require(error.get("code") == "SimulationOnlineAuthenticationRequired" and "login" not in details and challenges
                    and challenges[0]["type"] == "OnlineAuthenticationConfiguration" and "ProjectUser" in challenges[0].get("offered", [])
                    and all(c["decision"] == "unanswered" for c in challenges),
                    "Expected TIA's unanswered project-user login challenge: %s %s" % (error.get("code"), challenges))
            return error
        lab.unchanged("refuse plc_online_compare: no runtime credential", unauthenticated)
        lab.refuse("an open project", "plc_station_upload", {"downloadTicket": lab.ticket, "runtimeCredentialMetadataPath": lab.login}, "ProjectIsolationRequired")
        # V21 exports empty SnapshotValues for a DB without a stored snapshot (observed 29 October 2026).
        lab.refuse("a DB without a stored snapshot", "db_snapshot_capture", {"reviewId": review, "objectPath": db}, "DbSnapshotUnavailable", "no stored snapshot")
        lab.refuse("db_snapshot_create without a download ticket", "change_prepare", {"kind": "db_snapshot_create", "payload": {"blockPath": db}, "plcPath": run.plc}, "PhysicalPlcRefused")

        def compare():
            report = h.tool("plc_online_compare", {**online, "runtimeCredentialMetadataPath": lab.login})
            session = online_session(report, lab.username)
            require(report["compareTruncated"] is False and not differences(report["compare"]),
                    "Online and offline differ right after the download: %s" % differences(report["compare"]))
            names = [node["left"] for node in nodes(report["compare"])]
            require(any(DB in (name or "") for name in names), "The compare tree has no %s row: %s" % (DB, names))
            return {**session, "rows": names, "fingerprint": report["fingerprint"], "projectModified": [report["projectModifiedBefore"], report["projectModifiedAfter"]]}
        lab.unchanged("plc_online_compare with the runtime user", compare)

        if not h.args.skip_snapshots:
            h.stage("set Count to 42 through PLCSIM", lambda: lab.count(42))
            create = lab.prepare("db_snapshot_create", {"blockPath": db})
            require(create["preview"]["effects"] == [{"action": "createSnapshot", "blockPath": db, "previousSnapshotExported": False}],
                    "Unexpected db_snapshot_create effects: %s" % create["preview"]["effects"])
            readback = [r["artifact"] for r in lab.apply("db_snapshot_create", create)["readbacks"] if r["role"] == "db-snapshot-readback"]
            require(len(readback) == 1, "db_snapshot_create returned no snapshot readback")
            def capture():
                record = h.tool("db_snapshot_capture", {"reviewId": review, "objectPath": db})
                source = record["inputSource"]
                require(source["objectPath"] == db and Path(source["projectPath"]).resolve() == Path(run.project).resolve(), "The capture names another DB or project: %s" % source)
                rows = [r["value"] for r in h.tool("db_snapshot_review_page", {"recordId": record["recordId"], "section": "beforeValues", "count": 200})["rows"]]
                require([r["text"] for r in rows if r["path"].replace('"', "").split(".")[-1] == "Count"] == ["42"], "The captured snapshot does not hold Count = 42: %s" % rows)
                return {"recordId": record["recordId"], "snapshotSha256": record["input"]["sha256"], "values": rows,
                        "equalsCreateReadback": record["input"]["sha256"] == readback[0]["sha256"]}
            captured = lab.unchanged("db_snapshot_capture of " + DB, capture)

            h.stage("set Count to 7 through PLCSIM", lambda: lab.count(7))
            lab.refuse("db_snapshot_load_actual without acknowledgeActualValueWrite", "change_prepare", {"kind": "db_snapshot_load_actual", "plcPath": run.plc,
                       "payload": {"snapshotRecordId": captured["recordId"], "downloadTicket": lab.ticket, "runtimeCredentialMetadataPath": lab.login}},
                       "InvalidRequest", "acknowledgeActualValueWrite")
            load = lab.prepare("db_snapshot_load_actual", {"snapshotRecordId": captured["recordId"], "acknowledgeActualValueWrite": True})
            require(load["preview"]["effects"] == [{"action": "loadSnapshotAsActualValues", "blockPath": db, "snapshotRecordId": captured["recordId"],
                                                    "snapshotSha256": captured["snapshotSha256"]}], "Unexpected db_snapshot_load_actual effects: %s" % load["preview"]["effects"])
            lab.apply("db_snapshot_load_actual", load, compile=False, save=False)
            def loaded():
                lab.resolve()
                require(lab.count() == 42, "Count is not 42 after the load")
                return 42
            h.stage("PLCSIM reads back the loaded snapshot", loaded)

            def restore():
                recovered = h.tool("change_restore", {"changeId": create["changeId"], "previewHash": create["previewHash"]})
                require(recovered["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in recovered["stages"]
                        and Path(recovered["recoveredProject"]["path"]).resolve() != Path(run.project).resolve(), "Checkpoint recovery was not verified: %s" % recovered["stages"])
                after = run.state()
                require(after == run.current, "The recovered state differs: %s" % diff(run.current, after))
                error = h.call_error("db_snapshot_capture", {"reviewId": review, "objectPath": db})
                require(error.get("code") == "DbSnapshotUnavailable" and "no stored snapshot" in (error.get("message") or ""), "The recovered DB still has a snapshot: %s" % error)
                return {"recoveredProject": recovered["recoveredProject"], "stages": recovered["stages"], "capture": error.get("code")}
            h.stage("restore db_snapshot_create from its checkpoint", restore)

        # In an empty Portal the upload meets TLS verification (29 October 2026); on explicit authorization it trusts the owned
        # CPU's certificate pinned for the upload attempt, or, when the challenge offers none, the owned instance unauthenticated.
        if not h.args.skip_upload:
            h.stage("close the scratch before the upload", lambda: h.tool("tia_detach"))
            h.owned_portal("for the station upload")
            def upload():
                report = h.tool("plc_station_upload", {"downloadTicket": lab.ticket, "runtimeCredentialMetadataPath": lab.login, "trustOwnedSimulationCertificate": True})
                pin, unauthenticated = report["tlsPinnedCertificateSha256"], report["tlsUnauthenticated"] is True
                require(report["trustOwnedSimulationCertificate"] is True and report["tlsCertificatePinScope"] == "currentUploadAttemptOnly"
                        and (pin or unauthenticated) and any(c["decision"] == "Trusted" for c in report["onlineChallenges"]),
                        "The upload trusted no owned-instance TLS challenge: %s" % report["onlineChallenges"])
                session = online_session(report, lab.username, pin=pin, unauthenticated=unauthenticated)
                require(report["saved"] is True and report["uploadedStation"] and Path(report["project"]["path"]).is_file() and Path(report["reportPath"]).is_file(),
                        "The upload saved no station: %s" % {k: report.get(k) for k in ("saved", "uploadedStation", "project", "reportPath")})
                plcs = h.tool("project_inventory", {"maxItems": 1000})["plcs"]
                require(len(plcs) == 1, "Expected one uploaded PLC: %s" % plcs)
                uploaded = program(h.tool("software_inspect", {"plcPath": plcs[0]["path"], "maxItems": 2000}))
                require(uploaded == lab.downloaded, "The uploaded blocks differ: downloaded %s, uploaded %s" % (lab.downloaded, uploaded))
                return {**session, "tlsPinnedCertificateSha256": pin, "tlsUnauthenticated": unauthenticated, "provisioningPinnedCertificateSha256": lab.provisioning_pin, "project": report["project"], "uploadedStation": report["uploadedStation"], "devices": report["devices"],
                        "upload": {k: report["upload"][k] for k in ("state", "errors", "warnings")}, "blocks": uploaded}
            h.stage("plc_station_upload with the runtime user", upload)

        stopped = h.stage("stop", lambda: h.tool("sim_state", {"instanceId": lab.instance["id"], "action": "stop", "timeoutMs": 30000}))
        require(stopped["instance"]["operatingState"] == "Stop", "The instance did not stop")
        off = h.stage("power off", lambda: h.tool("sim_state", {"instanceId": lab.instance["id"], "action": "powerOff", "timeoutMs": 60000}))
        require(off["instance"]["operatingState"] == "Off", "The instance did not switch off")
    finally:
        released = h.stage("sim_release", lambda: h.tool("sim_release", {"instanceId": lab.instance["id"], "deleteOwned": True}))
        require(released.get("unregistered") is True, "The owned instance was not released")
    h.stage("tia_detach", lambda: h.tool("tia_detach"))
    def preserved():
        after = {str(p["processId"]): p.get("projectPath") for p in h.tool("tia_discover")["processes"]}
        require(all(after.get(pid) == path for pid, path in before.items()), "A preexisting TIA process or its project changed: before %s, after %s" % (before, after))
        return after
    h.stage("preexisting TIA processes are unchanged", preserved)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--sim-worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    parser.add_argument("--skip-snapshots", action="store_true", help="Run the compare and the station upload without the DB-snapshot steps.")
    parser.add_argument("--skip-upload", action="store_true", help="Leave out the station upload.")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "or_", "D09 online routes on one owned PLCSIM Advanced V8 instance, logged in as its generated runtime user", LIMITATIONS, []).run(body)
