"""Write a committable evidence summary from local qualification reports.

Raw reports stay in the ignored artifacts/ tree. The summary under evidence/
keeps what a reviewer needs: source commit, binary tree hashes, each raw report's
hash, its completion and the MCP tools that answered without error.
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import platform
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
SCHEMA = "tia-mcp.evidence-summary.v1"


def file_sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def tree(path):
    """Hash a binary file or every file under a build output directory."""
    files = [path] if path.is_file() else sorted(p for p in path.rglob("*") if p.is_file())
    if not files:
        raise ValueError(f"No binaries under {path}")
    lines = "".join(f"{p.relative_to(path).as_posix() if p != path else p.name}\t{file_sha256(p)}\n" for p in files)
    return {"name": path.name, "files": len(files), "sha256": hashlib.sha256(lines.encode()).hexdigest()}


def run(path):
    report = json.loads(path.read_text(encoding="utf-8"))
    # Qualifiers that log each MCP call as a step (tool + isError) have no separate toolCalls list.
    calls = report.get("toolCalls") or [{"name": st["tool"], "isError": st["isError"]}
                                        for st in report.get("steps", []) if "tool" in st and "isError" in st]
    return {"report": path.name, "sha256": file_sha256(path), "bytes": path.stat().st_size,
            "completed": report.get("completed") is True,
            "steps": [step.get("name") or step.get("tool") for step in report.get("steps", [])],
            "toolsPassed": sorted({c["name"] for c in calls if not c["isError"]}),
            "toolsRejected": sorted({c["name"] for c in calls if c["isError"]})}


def git(*arguments):
    return subprocess.run(["git", *arguments], cwd=ROOT, check=True, capture_output=True, text=True).stdout.strip()


def summarize(evidence_id, reports, binaries, scope, limitations):
    runs = [run(p) for p in reports]
    return {"schema": SCHEMA, "evidenceId": evidence_id,
            "outcome": "passed" if runs and all(r["completed"] for r in runs) else "failed",
            "recordedAtUtc": datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat(),
            "source": {"commit": git("rev-parse", "HEAD"), "dirty": bool(git("status", "--porcelain", "--", "src", "scripts"))},
            "environment": {"machine": platform.node(), "os": platform.platform(), "tiaVersion": 21},
            "scope": scope, "limitations": limitations,
            "binaries": [tree(p) for p in binaries], "runs": runs}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--id", required=True, help="Catalog evidence ID; the summary is written to evidence/<id>.json.")
    parser.add_argument("--report", type=pathlib.Path, action="append", required=True)
    parser.add_argument("--binary", type=pathlib.Path, action="append", required=True, help="Build output directory or file actually run.")
    parser.add_argument("--scope", required=True)
    parser.add_argument("--limitation", action="append", required=True)
    args = parser.parse_args()
    summary = summarize(args.id, args.report, args.binary, args.scope, args.limitation)
    destination = ROOT / "evidence" / (args.id + ".json")
    destination.parent.mkdir(exist_ok=True)
    destination.write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"{summary['outcome'].upper()} {destination.relative_to(ROOT).as_posix()}")
    return 0 if summary["outcome"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
