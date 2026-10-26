"""Retrieve a failed source's retained checkpoint into a fresh owned project and prove it is the exact pre-change state.

This complements a refused change_restore (unknown after-state fingerprint); it is not a change_restore qualification.
The original failed project is never opened, edited or released.
"""
import argparse
import hashlib
import json
import pathlib

from block_test_runtime import utc_now
from qualification_owned_scratch import assert_snapshot, require, snapshot
from run_block_tests import save_json
from smoke_mcp import ROOT
from typed_graphical_owned_session import TypedOwnedSession


def refusal(capture):
    """The failed capture must show an uncertain after-state and a refused restore before any retrieval."""
    status = capture.get("failureStatus") or {}
    require(capture.get("status") == "blocked" and capture.get("restoreDispatched") is True, "The capture did not dispatch a restore for a failed source.")
    require(status.get("state") == "recovery_required" and status.get("afterFingerprint") is None, "The failed change has a known after-state.")
    require(str(capture.get("failureRecoveryError", "")).startswith("change_restore: "), "change_restore was not refused.")
    return status


def run(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    capture_path = args.capture.resolve()
    capture = json.loads(capture_path.read_text(encoding="utf-8"))
    status = refusal(capture)
    archive = pathlib.Path(status["recoveryArchive"]).resolve()
    require(archive.is_relative_to((ROOT / "artifacts").resolve()), "The checkpoint must be under repository artifacts.")
    require(hashlib.sha256(archive.read_bytes()).hexdigest() == status["recoveryArchiveSha256"], "The retained checkpoint hash differs.")
    # The failing source's own pre-change snapshot, written by the capture before it prepared that source.
    folder = capture_path.parent / ("source-" + str(len(capture["fixtures"]) - 1).zfill(2)) / "before"
    expected = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    require(all(hashlib.sha256(pathlib.Path(row["file"]).read_bytes()).hexdigest() == row["sha256"] for row in expected["objects"].values()),
            "The retained pre-change snapshot changed.")
    report = {"schemaVersion": 1, "startedAtUtc": utc_now(), "completed": False, "capture": str(capture_path),
              "captureSha256": hashlib.sha256(capture_path.read_bytes()).hexdigest(), "failedChange": status["changeId"],
              "refusal": {"state": status["state"], "afterFingerprint": None, "changeRestore": capture["failureRecoveryError"]},
              "checkpoint": {"path": str(archive), "sha256": status["recoveryArchiveSha256"]}, "steps": []}
    def persist():
        report["calls"] = [call for lifetime in report.get("sessions", []) for call in lifetime["calls"]]
        save_json(output / "result.json", report)
    session = TypedOwnedSession(args.host, args.worker, output, report, persist, timeout=args.timeout)
    try:
        session.invoke("tia_start")
        retrieved = session.invoke("project_retrieve", {"archivePath": str(archive), "expectedSha256": status["recoveryArchiveSha256"]})
        report["retrieved"] = retrieved["project"]
        actual = snapshot(session.invoke, output / "retrieved")
        assert_snapshot(expected, actual)
        report["steps"].append({"name": "checkpoint retrieved into a fresh owned project equals the pre-change snapshot ("
                                + str(len(expected["objects"])) + " blocks/types/tag tables)"})
        report["completed"] = True
    except Exception as error:
        report["failure"] = {"type": type(error).__name__, "message": str(error)}
    finally:
        try:
            session.close()
        except Exception as error:
            report.update(completed=False, cleanupFailure=str(error))
        report["completedAtUtc"] = utc_now()
        report["toolCalls"] = [{"name": call["tool"], "isError": call.get("status") != "passed"} for call in report.get("calls", [])]
        persist()
    return 0 if report["completed"] else 2


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", type=pathlib.Path, required=True, help="Blocked capture report whose last source failed.")
    for name in ("host", "worker", "output"):
        parser.add_argument("--" + name, type=pathlib.Path, required=True)
    parser.add_argument("--timeout", type=int, default=300)
    raise SystemExit(run(parser.parse_args()))
