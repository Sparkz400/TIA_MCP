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
    texts = [json.dumps(setup.get("result", {}).get(key)) for key in ("nativeRejection", "compileMessages")] + [json.dumps(report.get("failureRestore"))]
    preserved = (setup.get("fullOriginalXmlPreserved") is True and setup.get("outcome") in {"native-rejected", "compile-rejected"}
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


def capture_curation_report(path):
    """A capture_typed_routes_owned.py report: completed only with every input captured, saved reopen verified and cleanup clean."""
    report = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    fixtures = report.get("fixtures", [])
    completed = (report.get("status") == "passed" and report.get("completed") is True and report.get("persistenceVerified") is True
                 and not report.get("remainingSources") and not report.get("cleanupFailure") and bool(fixtures)
                 and all(entry.get("engineeringVerified") is True for entry in fixtures))
    return {"completed": completed,
            "steps": [{"name": pathlib.Path(entry["sourcePath"]).name + ": " + ("captured" if entry.get("engineeringVerified") else "not captured")} for entry in fixtures]
                     + [{"name": "saved reopen: " + ("verified" if report.get("persistenceVerified") else "not verified")}],
            "toolCalls": [{"name": call["tool"], "isError": call["status"] != "passed"} for call in report.get("calls", [])]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", action="append", default=[], help="<result.json>=<passed|expected rejection text>")
    parser.add_argument("--capture", type=pathlib.Path, help="A capture_typed_routes_owned.py capture.json instead of --run reports.")
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    if bool(args.run) == bool(args.capture):
        parser.error("Select --run reports or one --capture report.")
    result = capture_curation_report(args.capture) if args.capture else curation_report([tuple(item.split("=", 1)) for item in args.run])
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"completed": result["completed"], "steps": len(result["steps"])}))
