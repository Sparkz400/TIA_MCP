"""Derive a curate_evidence.py input from raw component qualification reports.

The raw installation/runtime reports keep their own format and stay unchanged;
the derived file binds each one by SHA-256 and lists its acceptance flags,
stages and MCP tool outcomes in the fields curate_evidence.py reads.
"""
import argparse
import hashlib
import json
from pathlib import Path

FLAGS = ("explicitSourceAttributesVerified", "sourceContentPolicyVerified", "preservationVerified", "freshReopenVerified",
         "firstCheckpointRecoveryVerified", "installedProjectVerified", "obCallVerified", "runtimeVerified")


def calls(report):
    for session in report.get("sessions", []):
        yield from session.get("calls", [])
    yield from report.get("calls", [])


def derive(paths):
    sources, steps, tools = [], [], []
    for path in paths:
        raw = Path(path).read_bytes(); report = json.loads(raw.decode("utf-8"))
        sources.append({"path": str(Path(path).resolve()), "sha256": hashlib.sha256(raw).hexdigest(), "status": report.get("status")})
        name = report.get("kind") or report.get("sourceMode") or "component"
        steps += [f"{name}: {flag}={report[flag]}" for flag in FLAGS if flag in report]
        steps += [f"{name}: {event['event']}={event['value'].get('state')}" for event in report.get("events", []) if isinstance(event.get("value"), dict) and "state" in event["value"]]
        tools += [{"name": call["tool"], "isError": call.get("status") != "passed"} for call in calls(report)]
    return {"completed": bool(sources) and all(s["status"] == "passed" for s in sources),
            "sourceReports": sources, "steps": [{"name": step} for step in steps], "toolCalls": tools}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps(derive(args.report), indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
