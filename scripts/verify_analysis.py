"""Read-only native call-graph regression on the runner-owned conformance project."""
import argparse
import json
import pathlib

from generate_conformance import ROOT, canonical
from run_conformance import native_call
from smoke_mcp import Client


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus-report", type=pathlib.Path, default=ROOT / "artifacts/conformance/live-first/index.json")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/analysis-regression-v03.json")
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    args = parser.parse_args()
    source = json.loads(args.corpus_report.read_text(encoding="utf-8"))
    project = pathlib.Path(source["scratch"]["project"]["path"]).resolve()
    artifacts = (ROOT / "artifacts/scratch").resolve()
    if not project.is_relative_to(artifacts) or not project.name.startswith("TiaMcpScratch_") or project.suffix != ".ap21":
        raise ValueError("Regression is restricted to the generated repository scratch project.")
    report = {"schemaVersion": 1, "completed": False, "project": str(project), "steps": [],
              "limitations": ["Read-only graph regression; no source import, compile, runtime execution or project save.",
                              "Namespace/duplicate-member and truncated-index hardening has pure tests; this root-scope project does not qualify those native profiles."]}
    client = None
    def persist():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(canonical(report), encoding="utf-8")
    def call(tool, arguments=None):
        result = native_call(client, tool, arguments)
        report["steps"].append({"tool": tool, "result": result})
        persist()
        print("PASS " + tool, flush=True)
        return result
    try:
        client = Client(args.host, 300)
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                     "clientInfo": {"name": "tia-analysis-regression", "version": "0.3.0"}})
        client.notify("notifications/initialized")
        call("tia_start")
        call("project_open", {"path": str(project)})
        graph = call("call_graph", {"maxItems": 2000, "includeXmlEvidence": False})
        prefix = graph["plcPath"] + "/blocks/"
        expected = [("Tcf_Caller", "Tcf_Child"), ("Tcf_MultiInstance", "Tcf_MemoryCell"),
                    ("Main", "TiaMcp_EnableGate"), ("TiaMcp_EnableGate", "TiaMcp_IsEnabled")]
        verified = []
        for caller, callee in expected:
            matches = [edge for edge in graph["edges"] if edge["callerPath"] == prefix + caller and edge["calleePath"] == prefix + callee]
            if not matches:
                raise AssertionError(f"Missing exact call edge {caller} -> {callee}")
            verified.append({"callerPath": prefix + caller, "calleePath": prefix + callee, "edgeCount": len(matches),
                             "instancePaths": [edge.get("instancePath") for edge in matches],
                             "resolution": [edge.get("instanceResolution") for edge in matches]})
        report["verifiedEdges"] = verified
        report["instanceDbResolutionObserved"] = any(edge.get("instancePath") for edge in graph["edges"])
        if not report["instanceDbResolutionObserved"]:
            report["limitations"].append("Native calls resolve directly to FB identities in this project; no InstanceDB-to-FB fallback branch was exercised.")
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
    finally:
        if client:
            try:
                report["detach"] = native_call(client, "tia_detach")
            except Exception as error:
                report["completed"] = False
                report["detachError"] = str(error)
            client.close()
            report["hostExitCode"] = client.process.returncode
            if client.process.returncode != 0:
                report["completed"] = False
        persist()
    print("Report: " + str(args.report), flush=True)
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
