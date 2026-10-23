"""Qualify preview/revision/idempotency guards in a new copy of a known scratch archive."""
import hashlib
import json
import pathlib
import uuid

from smoke_mcp import Client, ROOT


def main():
    checkpoint_journal = ROOT / "artifacts/changes/change-85a07d54ead94b2a8a257a5dde0fe2a5/journal.json"
    checkpoint = json.loads(checkpoint_journal.read_text(encoding="utf-8"))
    archive = pathlib.Path(checkpoint["recoveryArchive"]).resolve()
    assert archive.is_relative_to((ROOT / "artifacts").resolve()) and archive.suffix == ".zap21"
    assert pathlib.Path(checkpoint["projectPath"]).name.startswith("TiaMcpScratch_")
    expected_hash = checkpoint["recoveryArchiveSha256"]
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == expected_hash
    report_path = ROOT / "artifacts/write-guards-final.json"
    report = {"completed": False, "steps": [], "failures": [], "scope": "New archive-retrieved copy of a known task scratch project; no physical operations"}
    client = Client(ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe", 300)
    started = False

    def persist():
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    def call(name, arguments=None, label=None, expected_error=None):
        raw = client.request("tools/call", {"name": name, "arguments": arguments or {}})
        value = raw.get("structuredContent") or json.loads(raw["content"][0]["text"])
        failed = bool(raw.get("isError"))
        report["steps"].append({"name": label or name, "arguments": arguments or {}, "isError": failed, "expectedError": expected_error, "result": value})
        persist()
        if expected_error:
            assert failed and value.get("error", {}).get("code") == expected_error, value
        elif failed:
            raise RuntimeError(f"{name}: {value}")
        print("PASS " + (label or name), flush=True)
        return value

    def names():
        inventory = call("project_inventory", {"maxItems": 2000})
        coverage = inventory.get("completeness", {})
        assert coverage.get("scopeComplete") is True and coverage.get("truncated") is False, coverage
        return sorted((item["path"], item["name"], item["number"], item["programmingLanguage"]) for item in inventory["blocks"])

    def clean_prepared(prepared):
        status = call("change_status", {"changeId": prepared["changeId"]})
        assert status["state"] == "prepared" and status["stages"] == [] and status["recoveryArchive"] is None, status
        journal_path = pathlib.Path(status["journalPath"])
        assert not (journal_path.parent / "before.zap21").exists()
        disk = json.loads(journal_path.read_text(encoding="utf-8"))
        assert disk["idempotencyKey"] is None and disk["state"] == "prepared" and disk["stages"] == []

    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-write-guard-smoke", "version": "0.2.0"}})
        client.notify("notifications/initialized")
        start = call("tia_start")
        started = True
        report["ownedProcessId"] = start["processId"]
        retrieved = call("project_retrieve", {"archivePath": str(archive), "expectedSha256": expected_hash})
        report["project"] = retrieved["project"]
        initial_names = names()
        suffix = uuid.uuid4().hex[:8]
        plans = []
        for role in ("Applied", "Stale"):
            fixture = call("fixture_sources", {"language": "SCL", "blockName": "TiaMcp_Guard_" + role + "_" + suffix})
            plans.append(call("change_prepare", {"kind": fixture["kind"], "payload": fixture["payload"]}, "prepare " + role))
        first, second = plans
        assert first["preview"]["baseFingerprint"] == second["preview"]["baseFingerprint"]
        arguments = {"changeId": first["changeId"], "previewHash": first["previewHash"], "idempotencyKey": uuid.uuid4().hex,
                     "checkpointSave": True, "compile": True, "save": True}
        wrong_hash = "0" * 64 if first["previewHash"] != "0" * 64 else "1" * 64
        call("change_apply", {**arguments, "previewHash": wrong_hash}, "wrong preview hash rejected", "PreviewMismatch")
        clean_prepared(first)
        assert names() == initial_names
        applied = call("change_apply", arguments, "apply first prepared fixture")
        assert applied["state"] == "applied" and applied["compile"]["errorCount"] == 0
        applied_names = names()
        assert len(applied_names) == len(initial_names) + 1
        journal_path = pathlib.Path(applied["journalPath"])
        journal_before_replay = journal_path.read_bytes()
        journal_mtime_before_replay = journal_path.stat().st_mtime_ns
        replay = call("change_apply", arguments, "same idempotency key replays recorded result")
        for field in ("changeId", "state", "previewHash", "afterFingerprint", "stages", "compile", "recoveryArchive", "recoveryArchiveSha256"):
            assert replay[field] == applied[field], (field, replay[field], applied[field])
        assert journal_path.read_bytes() == journal_before_replay and journal_path.stat().st_mtime_ns == journal_mtime_before_replay
        assert names() == applied_names
        call("change_apply", {**arguments, "idempotencyKey": uuid.uuid4().hex}, "different idempotency key rejected", "IdempotencyConflict")
        assert journal_path.read_bytes() == journal_before_replay
        second_args = {**arguments, "changeId": second["changeId"], "previewHash": second["previewHash"], "idempotencyKey": uuid.uuid4().hex}
        call("change_apply", second_args, "stale second preview rejected before checkpoint", "StaleRevision")
        clean_prepared(second)
        assert names() == applied_names
        recovery = pathlib.Path(applied["recoveryArchive"])
        assert hashlib.sha256(recovery.read_bytes()).hexdigest() == applied["recoveryArchiveSha256"]
        report["verification"] = {"wrongHashRejectedWithoutCheckpointOrMutation": True, "sameBaselinePrepared": True,
            "firstChangeCompiledAndSaved": True, "sameKeyRecordedReplayWithoutJournalWrite": True,
            "differentKeyRejected": True, "staleRevisionRejectedWithoutCheckpointOrMutation": True,
            "successfulChangeArchiveHashVerified": True}
        report["completed"] = True
    except Exception as error:
        report["failures"].append(str(error))
        raise
    finally:
        if started:
            try:
                call("tia_detach")
            except Exception as error:
                report["detachFailure"] = str(error)
        client.close()
        report["hostExitCode"] = client.process.returncode
        report["hostLogs"] = client.errors
        persist()
        print("Report: " + str(report_path), flush=True)


if __name__ == "__main__":
    main()
