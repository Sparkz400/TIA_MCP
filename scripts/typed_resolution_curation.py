"""Aggregate typed resolution reports into the report shape scripts/curate_evidence.py reads.

Each --run is <result.json>=<expectation>: "passed", or text that the retained native
rejection must contain. A rejection only counts with the original project verified
unchanged, so an expected native boundary is evidence, never a relabeled pass.
"""
import argparse
import json
import pathlib


def rejection(report):
    """Native failure text retained by the resolution and tag-ambiguity qualifiers."""
    setup = report.get("nativeDuplicateSetup") or {}
    probe = report.get("namespaceReferenceProbe") or {}
    texts = [json.dumps(setup.get("result", {}).get("nativeRejection")), json.dumps(report.get("failureRestore"))]
    preserved = (setup.get("fullOriginalXmlPreserved") is True and setup.get("outcome") == "native-rejected"
                 or probe.get("status") == "native-attempt-failed" and report.get("failureOriginalXmlVerified") is True)
    return " ".join(texts), preserved


def curation_report(runs):
    steps, calls, completed = [], [], bool(runs)
    for path, expectation in runs:
        report = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
        calls.extend({"name": call["tool"], "isError": call["status"] != "passed"}
                     for session in report.get("sessions", []) for call in session.get("calls", []))
        name = pathlib.Path(path).parent.name
        if expectation == "passed":
            ok = report.get("status") == "passed" and all(case.get("status") == "passed" for case in report.get("cases", []))
            steps.extend({"name": f"{name}/{case.get('id')}: {case.get('status')}"} for case in report.get("cases", []))
        else:
            text, preserved = rejection(report)
            ok = report.get("status") == "blocked" and expectation in text and preserved
            steps.append({"name": f"{name}: native rejection {'observed, original preserved' if ok else 'not as expected'}"})
        completed = completed and ok
    return {"completed": completed, "steps": steps, "toolCalls": calls}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", action="append", required=True, help="<result.json>=<passed|expected rejection text>")
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    runs = [tuple(item.split("=", 1)) for item in args.run]
    args.output.write_text(json.dumps(curation_report(runs), indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"completed": curation_report(runs)["completed"], "runs": len(runs)}))
