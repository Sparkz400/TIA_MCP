"""Independent retained UA report checks, shared by live-loopback and replay qualifications."""
import base64
import copy
import hashlib
import json
from pathlib import Path
from jsonschema import Draft202012Validator
from referencing import Registry, Resource


def validator(name="diagnostic-ua-observation", fragment=""):
    root = Path(__file__).resolve().parents[1] / "contracts"
    schemas = [json.loads(p.read_text(encoding="utf-8")) for p in root.glob("*.json")]
    registry = Registry().with_resources((s["$id"], Resource.from_contents(s)) for s in schemas if "$id" in s and "$schema" in s)
    return Draft202012Validator({"$ref": "https://tia-mcp.local/contracts/" + name + ".v1.schema.json" + fragment}, registry=registry)


def qualify(call, check, reference, root, output, label, exact_value=True):
    receipt = call("diagnostic_ua_observe", dict(snapshotRef=reference))
    validator(fragment="#/$defs/receipt").validate(receipt)

    def download(receipt):
        artifact, raw = receipt["artifact"], bytearray()
        while len(raw) < artifact["bytes"]:
            chunk = call("artifact_read", dict(path=artifact["path"], offset=len(raw)))
            data = base64.b64decode(chunk["data"], validate=True)
            check(bool(data), "Artifact read makes progress")
            raw.extend(data)
        check(len(raw) == artifact["bytes"] and hashlib.sha256(raw).hexdigest() == receipt["id"], "Exact artifact download " + label)
        return bytes(raw)

    original = download(receipt)
    document = json.loads(original)
    validator().validate(document)
    source = document["captureSource"]
    rows, previous, total = [], None, 0
    for i, file in enumerate(source["originalFiles"]):
        raw = base64.b64decode(file["base64"], validate=True)
        check(hashlib.sha256(raw).hexdigest() == file["sha256"] and len(raw) == file["bytes"], "Original source bytes and hash " + label)
        check((root / file["relativePath"]).read_bytes() == raw, "Embedded source equals separately opened original " + label)
        decoded = json.loads(raw); total += len(raw)
        if i == 0:
            check(decoded == source["plan"], "Decoded original plan retained")
        else:
            check(decoded["previousSha256"] == previous and decoded["index"] == i, "Chain continuity retained")
            rows.extend(decoded["rows"])
            if i == source["segmentCount"]:
                check(decoded["progress"] == source["recordedProgress"], "Exact last progress retained")
        previous = file["sha256"]
    check(rows == source["rows"] and total == source["originalByteCount"] and previous == reference.split(":")[-1], "Complete selected original prefix retained")
    check(all(h["verdict"] == "unresolved" for h in document["hypotheses"]), "No hypothesis promoted by observations")
    if exact_value:
        check(document["signals"][1]["firstObservation"]["value"]["value"] == "18446744073709551615", "Exact UInt64 retained in report")
    if len(document["signals"]) == 6 and exact_value:
        signals = document["signals"]
        check(signals[2]["bad"] > 0 and signals[2]["goodValues"] == 0, "Bad quality never becomes a usable value")
        check(signals[3]["uncertain"] > 0 and signals[3]["goodValues"] == 0, "Uncertain quality remains explicit")
        check(signals[4]["missingSourceTimestamps"] > 0 and signals[4]["missingServerTimestamps"] > 0, "Missing timestamps remain explicit")
        check(signals[5]["unsupported"] > 0 and signals[5]["goodValues"] == 0, "Unsupported array remains explicit")
    check(document["signals"][1]["savedDatatype"] == "UInt" and not document["datatypeMatchVerified"], "Datatype mismatch cannot become authority")
    output.mkdir(parents=True, exist_ok=True)
    (output / (label + "-observation.json")).write_bytes(original)
    for fmt, extension in (("json", "json"), ("markdown", "md"), ("html", "html")):
        export = call("diagnostic_ua_report_export", dict(reportId=receipt["id"], format=fmt))
        validator("diagnostic-ua-observation-export").validate(export)
        raw = download(export)
        if fmt == "json":
            check(raw == original, "JSON export preserves exact report bytes")
        else:
            check((not exact_value or b"18446744073709551615" in raw) and b"unresolved" in raw, "Readable export retains exact values and unresolved evidence")
            if fmt == "html":
                check(b"<script>" not in raw and b"default-src 'none'" in raw, "HTML export is inert and self-contained")
        (output / (label + "-observation." + extension)).write_bytes(raw)
    for field in ("signals", "hypotheses", "sourceCompletenessVerified"):
        changed = copy.deepcopy(document)
        if field == "signals": changed[field][0]["goodValues"] = 999
        elif field == "hypotheses": changed[field][0]["verdict"] = "supported"
        else: changed[field] = True
        raw = json.dumps(changed, ensure_ascii=False).encode()
        digest = hashlib.sha256(raw).hexdigest()
        (root / "trace-diagnostics" / (digest + ".json")).write_bytes(raw)
        call("diagnostic_ua_report_export", dict(reportId=digest, format="json"), "UaDiagnosticReportMismatch")
    # Pinning does not trust a source file merely because an embedded copy survives.
    path = root / source["originalFiles"][-1]["relativePath"]
    raw = path.read_bytes()
    try:
        path.write_bytes(raw + b" ")
        call("diagnostic_ua_report_export", dict(reportId=receipt["id"], format="json"),
             "RuntimeCaptureIntegrityFailure" if document["acquisitionMode"] == "polling" else "UaSubscriptionIntegrityFailure")
    finally:
        path.write_bytes(raw)
    check(call("diagnostic_ua_observe", dict(snapshotRef=reference))["id"] == receipt["id"], "Repeat observation remains content identical")
    return receipt


def main():
    """Reopen genuinely older producer files in a separate copy, with no UA server."""
    import argparse
    import shutil
    from smoke_mcp import Client
    parser=argparse.ArgumentParser(description=main.__doc__)
    for name in ("host","prior","output"): parser.add_argument("--"+name,type=Path,required=True)
    args=parser.parse_args(); output=args.output.resolve(); output.mkdir(parents=True,exist_ok=False)
    prior=args.prior.resolve(); root=output/"store"
    def inventory(folder):return {str(p.relative_to(folder)):hashlib.sha256(p.read_bytes()).hexdigest() for p in folder.rglob('*') if p.is_file()}
    before=inventory(prior);shutil.copytree(prior/"store",root)
    report=dict(status="running",checks=[],responses=[],nativeStarted=False,physicalQualified=False,priorHashes=before)
    client=None
    def check(value,label):
        if not value: raise AssertionError(label)
        report["checks"].append(label)
    def call(name,arguments,code=None):
        raw=client.request("tools/call",dict(name=name,arguments=arguments))
        report["responses"].append(dict(name=name,arguments=arguments,response=raw))
        value=raw.get("structuredContent") or json.loads(raw["content"][0]["text"])
        check(bool(raw.get("isError")) == (code is not None),name+" outcome")
        if code:check(value["error"]["code"]==code,name+" refuses "+code)
        return value
    try:
        source=json.loads((prior/"result.json").read_text());check(source["status"]=="passed","Prior producer qualification passed")
        snapshots={}
        for entry in source["responses"]:
            if entry["name"] not in ("runtime_ua_capture_status","runtime_ua_subscription_status"):continue
            raw=entry["response"];value=raw.get("structuredContent") or json.loads(raw["content"][0]["text"])
            if value.get("state") == "running":continue
            kind="capture" if entry["name"]=="runtime_ua_capture_status" else "subscription"
            request=value["plan"]["uaContext"]["request"] if kind=="capture" else value["plan"]["request"]
            if request.get("diagnosticBindingId"):snapshots[kind]=value["snapshotRef"]
        check(set(snapshots)=={"capture","subscription"},"Actual prior bound polling and subscription prefixes found")
        client=Client(args.host.resolve(),30,["--no-native-workers","--artifacts",str(root)])
        client.request("initialize",dict(protocolVersion="2025-11-25",capabilities={},clientInfo=dict(name="ua-report-replay",version="1")))
        client.notify("notifications/initialized")
        for kind,reference in snapshots.items():qualify(call,check,reference,root,output/"observations",kind)
        check(inventory(prior)==before,"All original previous-producer bytes unchanged")
        report["status"]="passed"
    except BaseException as e:
        report["status"]="failed";report["error"]=repr(e);raise
    finally:
        if client:
            receipt=client.close();report["cleanup"]=receipt
            if receipt["hostExitCode"]!=0 or receipt["forcedHostStop"]:report["status"]="failed"
        (output/"result.json").write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
    print(json.dumps(dict(status=report["status"],checks=len(report["checks"]))))
    if report["status"]!="passed":raise RuntimeError("Replay cleanup failed")


if __name__ == "__main__":main()
