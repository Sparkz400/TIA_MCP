"""Actual Host registration/option boundary checks; never dispatch a native call."""
import argparse
import subprocess
from pathlib import Path
from smoke_mcp import Client


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", type=Path, required=True)
    args = parser.parse_args()
    watch = {"qualification_watch_fixture_seed", "qualification_watch_compete",
             "qualification_watch_authoring_source_negative", "qualification_watch_authoring_source_reconcile"}
    component = {"qualification_component_window"}
    wanted = watch | component
    for extra, expected in (([], set()), (["--component-qualification"], component),
                            (["--component-qualification", "--watch-qualification", "none"], wanted)):
        client = Client(args.host.resolve(), 10, ["--worker", "D:/nonexistent-qualification-worker.exe", *extra])
        try:
            client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "qualification-boundary", "version": "1"}})
            client.notify("notifications/initialized")
            names = {t["name"] for t in client.request("tools/list")["tools"]}
            assert names & wanted == expected
            assert {"watch_table_generate", "watch_table_prepare_authoring", "watch_table_apply", "watch_table_recover"} <= names
        finally:
            close = client.close()
            assert close["hostExitCode"] == 0 and not close["workerExits"] and not close["forcedHostStop"]
    for extra in (["--watch-qualification", "none"], ["--component-qualification", "--watch-qualification", "typo"]):
        result = subprocess.run([str(args.host.resolve()), *extra], capture_output=True, text=True, timeout=10)
        assert result.returncode != 0
        assert "requires" in result.stderr or "Unknown watch" in result.stderr
    print("PASS five actual Host qualification boundaries; no native worker launched")


if __name__ == "__main__": main()
