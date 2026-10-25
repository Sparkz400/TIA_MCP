"""Derive a curate_evidence.py input from raw qualify_hmi.py bridge reports.

The raw reports stay unchanged; the derived file binds each one by SHA-256 and lists
every profile gate outcome as a step and the MCP client's tool outcomes as toolCalls.
"""
import argparse
import hashlib
import json
from pathlib import Path


def derive(paths):
    sources, steps, tools = [], [], []
    for path in paths:
        raw = Path(path).read_bytes(); report = json.loads(raw.decode("utf-8"))
        sources.append({"path": str(Path(path).resolve()), "sha256": hashlib.sha256(raw).hexdigest(),
                        "completed": report.get("completed") is True, "cleanupVerified": report.get("cleanupVerified") is True})
        for profile in report.get("profiles", []):
            steps += [f"{profile['profile']}: {name}={value['status']}" for name, value in profile.get("gates", {}).items()]
        tools += (report.get("launcherShutdown") or {}).get("toolCalls", [])
    return {"completed": bool(sources) and all(s["completed"] and s["cleanupVerified"] for s in sources),
            "sourceReports": sources, "steps": [{"name": step} for step in steps], "toolCalls": tools}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps(derive(args.report), indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
