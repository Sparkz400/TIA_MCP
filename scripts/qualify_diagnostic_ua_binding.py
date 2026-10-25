"""Prebound diagnostic intent on real loopback UA polling/subscriptions; no Siemens access."""
import argparse
import base64
import copy
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
import uuid

from smoke_mcp import Client
from test_diagnostic_ua_binding_contract import validator
from qualify_diagnostic_ua_observation import qualify as qualify_observation
from qualify_diagnostic_ua_comparison import qualify as qualify_comparison


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("host", "server", "samples", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    out = args.output.resolve(); out.mkdir(parents=True, exist_ok=False)
    root = out / "store"
    sample = json.loads(args.samples.read_text(encoding="utf-8"))["uaBindingFixture"]
    if sample["authority"] != "syntheticManagedProducer" or sample["nativeQualification"] is not False:
        raise ValueError("Expected explicit synthetic fixture")
    for relative, encoded in sample["files"].items():
        path = (root / relative).resolve()
        if not path.is_relative_to(root):
            raise ValueError("Fixture escaped output root")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(base64.b64decode(encoded, validate=True))
    report = dict(status="failed", checks=[], responses=[], cleanups=[], nativeStarted=False, physicalQualified=False)
    client = server = None
    lines = queue.Queue(); errors = []

    def check(value, label):
        if not value:
            raise AssertionError(label)
        report["checks"].append(label)

    def hashes():
        return {str(p): hashlib.sha256(p.read_bytes()).hexdigest()
                for folder in {args.host.resolve().parent, args.server.resolve().parent}
                for p in folder.iterdir() if p.is_file() and p.suffix in (".dll", ".exe", ".json")}

    def fixture(operation, **arguments):
        server.stdin.write(json.dumps(dict(operation=operation, **arguments)) + "\n"); server.stdin.flush()
        return json.loads(lines.get(timeout=20))

    def start_host():
        instance = Client(args.host.resolve(), 30, ["--no-native-workers", "--artifacts", str(root)])
        instance.request("initialize", dict(protocolVersion="2025-11-25", capabilities={}, clientInfo=dict(name="ua-binding", version="1")))
        instance.notify("notifications/initialized")
        return instance

    def close_host():
        nonlocal client
        receipt = client.close(); report["cleanups"].append(receipt); client = None
        check(receipt["hostExitCode"] == 0 and not receipt["forcedHostStop"], "Original Host exits cleanly")

    def call(name, arguments, code=None):
        raw = client.request("tools/call", dict(name=name, arguments=arguments))
        report["responses"].append(dict(name=name, arguments=arguments, response=raw))
        value = raw.get("structuredContent")
        if value is None:
            value = json.loads("\n".join(c["text"] for c in raw["content"] if c.get("type") == "text"))
        check(bool(raw.get("isError")) == (code is not None), name + " outcome")
        if code:
            check(value["error"]["code"] == code, name + " refuses " + code)
        return value

    def complete(kind, capture):
        deadline = time.monotonic() + 25
        while True:
            result = call("runtime_ua_" + kind + "_status", dict(captureId=capture))
            if result["state"] != "running":
                return result
            check(time.monotonic() < deadline, "Capture terminates within bounded deadline")
            time.sleep(0.1)

    report["inputHashes"] = hashes()
    try:
        server = subprocess.Popen(["dotnet", str(args.server.resolve()), "--serve", "--output", str(out / "server")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8",
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        def read_lines():
            for line in server.stdout:
                lines.put(line)
        def read_errors():
            errors.extend(server.stderr.read().splitlines())
        threading.Thread(target=read_lines, daemon=True).start()
        threading.Thread(target=read_errors, daemon=True).start()
        check(json.loads(lines.get(timeout=30)).get("ready"), "Owned fixture server ready")
        ready = json.loads((out / "server/ready.json").read_text())
        client = start_host()
        tools = {t["name"]: t for t in client.request("tools/list")["tools"]}
        check("diagnostic_ua_binding_plan" in tools, "New offline tool is registered")
        feature = call("feature_catalog", dict(featureId="diagnostic_ua_binding_plan"))
        check(feature["features"][0]["mcpTools"] == ["diagnostic_ua_binding_plan"] and not feature["targetAvailabilityProbed"],
              "Bundled feature catalog exposes the exact offline tool without target authority")
        attempt = uuid.uuid4().hex
        nodes = [dict(namespaceUri=ready["namespaceUri"], identifierType="string", identifier=n) for n in ("bool", "uint64", "bad", "uncertain", "missingTimes", "array")]
        request = copy.deepcopy(sample["request"]); request["attemptId"] = attempt
        request["reason"] = "Observation </pre><script>fetch('https://invalid.example/')</script> ``` # evidence"
        for signal, node in zip(("run", "final", "feedback", "permit"), nodes[2:]):
            request["mappings"].append(dict(signalId=signal,node=node,unit="caller-selected",reason="Quality fixture; datatype equivalence unverified"))
        for mapping, node in zip(request["mappings"], nodes):
            mapping["node"] = node
        receipt = call("diagnostic_ua_binding_plan", dict(experimentId=sample["experimentId"], request=request))
        validator("#/$defs/receipt").validate(receipt)
        binding = receipt["binding"]
        check(binding["mappings"][1]["declaration"]["datatype"] == "UInt" and not binding["datatypeMatchVerified"],
              "Saved UInt declaration does not become equivalence with fixture UInt64")
        check(fixture("audit")["sessions"] == 0, "Offline preparation creates no remote session")
        connected = call("runtime_ua_connect", dict(attemptId=attempt, selection=ready["selection"]))
        check(connected["state"] == "connected" and connected["peerVerified"], "Known healthy pinned encrypted session")
        retained = []
        observations = []
        for kind in ("capture", "subscription"):
            req = dict(attemptId=attempt, nodes=nodes, diagnosticBindingId=receipt["id"], durationSeconds=2)
            if kind == "capture":
                req.update(intervalMilliseconds=100, maximumSamples=len(nodes))
            else:
                req.update(maximumNotifications=len(nodes))
            baseline = fixture("audit")
            for mutation in (dict(attemptId=uuid.uuid4().hex), dict(nodes=list(reversed(nodes)))):
                rejected = uuid.uuid4().hex
                call("runtime_ua_" + kind + "_start", dict(captureId=rejected, request={**req, **mutation}), "UaDiagnosticBindingMismatch")
                store_dir = root / ("runtime-ua-acquisition/runtime-captures" if kind == "capture" else "runtime-ua-subscriptions")
                check(not (store_dir / rejected).exists(), "Refusal precedes retained capture intent")
            check(fixture("audit") == baseline, "Binding refusals precede all UA services and session leasing")
            capture = uuid.uuid4().hex
            call("runtime_ua_" + kind + "_start", dict(captureId=capture, request=req))
            status = complete(kind, capture)
            original_request = status["plan"]["uaContext"]["request"] if kind == "capture" else status["plan"]["request"]
            check(original_request["diagnosticBindingId"] == receipt["id"], "Exact binding retained before " + kind)
            page = call("runtime_ua_" + kind + "_page", dict(snapshotRef=status["snapshotRef"], count=200))
            check(len(page["rows"]) > 0, "Actual acquired rows retained for " + kind)
            if kind == "capture":
                values = [json.loads(row["readResultJson"])["items"][0] for row in page["rows"] if row["kind"] == "read"]
                check(values[1]["builtInType"] == "UInt64" and values[1]["value"] == "18446744073709551615", "Original UInt64 remains exact despite saved UInt declaration")
            else:
                check(status["recordedProgress"]["remoteCleanupConfirmed"], "Subscription cleanup independently recorded")
            call("runtime_ua_" + kind + "_export", dict(snapshotRef=status["snapshotRef"], format="json"))
            baseline = fixture("audit")
            observations.append(qualify_observation(call, check, status["snapshotRef"], root, out / "observations", kind))
            check(fixture("audit") == baseline, "Observation and readable exports perform no UA services")
            changed = {**req, "diagnosticBindingId": None}
            call("runtime_ua_" + kind + "_start", dict(captureId=capture, request=changed),
                 "UaCaptureRequestChanged" if kind == "capture" else "UaSubscriptionRequestChanged")
            retained.append((kind, capture, req, status["snapshotRef"]))
            unbound = {key: value for key, value in req.items() if key != "diagnosticBindingId"}
            old_capture = uuid.uuid4().hex
            call("runtime_ua_" + kind + "_start", dict(captureId=old_capture, request=unbound))
            old_status = complete(kind, old_capture)
            old_request = old_status["plan"]["uaContext"]["request"] if kind == "capture" else old_status["plan"]["request"]
            call("diagnostic_ua_observe", dict(snapshotRef=old_status["snapshotRef"]), "UaDiagnosticBindingRequired")
            check("diagnosticBindingId" not in old_request, "Unbound normalized request preserves original fields")
            baseline = fixture("audit")
            call("runtime_ua_" + kind + "_start", dict(captureId=old_capture, request=req),
                 "UaCaptureRequestChanged" if kind == "capture" else "UaSubscriptionRequestChanged")
            check(fixture("audit") == baseline, "Retroactive binding refuses without remote services")
            retained.append((kind, old_capture, unbound, old_status["snapshotRef"]))
        fixture("mode", value="normal")
        capture=uuid.uuid4().hex
        req=dict(attemptId=attempt,nodes=nodes,diagnosticBindingId=receipt["id"],durationSeconds=8,maximumNotifications=len(nodes)+2,
                 events=dict(notifier=dict(nodes[0],identifier="fixture"),minimumSeverity=500))
        call("runtime_ua_subscription_start",dict(captureId=capture,request=req))
        deadline=time.monotonic()+10
        while call("runtime_ua_subscription_status",dict(captureId=capture))["recordedProgress"]["rows"]==0:
            check(time.monotonic()<deadline,"Bounded mixed-event setup");time.sleep(0.05)
        fixture("emitEvent",number=2,severity=700);fixture("emitEvent",number=2,severity=700)
        status=complete("subscription",capture)
        baseline=fixture("audit")
        observed=qualify_observation(call,check,status["snapshotRef"],root,out/"observations","events")
        document=json.loads(Path(observed["artifact"]["path"]).read_bytes())
        check(sum(len(r["event"].get("events",[])) for r in document["captureSource"]["rows"])==2,"Prebound diagnostic report retains original repeated events alongside signals")
        check(fixture("audit")==baseline,"Event timeline composition has no remote services")
        observations.append(observed);retained.append(("subscription",capture,req,status["snapshotRef"]))
        first_events = status['snapshotRef']
        capture=uuid.uuid4().hex
        req={**req,'maximumNotifications':len(nodes)+1}
        call('runtime_ua_subscription_start',dict(captureId=capture,request=req))
        deadline=time.monotonic()+10
        while call('runtime_ua_subscription_status',dict(captureId=capture))['recordedProgress']['rows']==0:
            check(time.monotonic()<deadline,'Second event capture starts within deadline');time.sleep(0.05)
        fixture('emitEvent',number=3,severity=900)
        second_events=complete('subscription',capture)['snapshotRef']
        before_comparison=fixture('audit')
        comparisons=[qualify_comparison(call,check,first_events,second_events,out/'comparisons','events'),
                     qualify_comparison(call,check,observations[0]['snapshotRef'],observations[1]['snapshotRef'],out/'comparisons','polling-subscription')]
        check(fixture('audit')==before_comparison,'Comparisons, guards and exports perform no UA services')
        for mode in ("subscriptionDuplicate", "subscriptionGap", "subscriptionOverflow", "subscriptionRepublishUnavailable", "subscriptionCreateItemsDenied"):
            fixture("mode", value=mode)
            capture = uuid.uuid4().hex
            req = dict(attemptId=attempt,nodes=nodes,diagnosticBindingId=receipt["id"],durationSeconds=2,keepAliveCount=1,maximumNotifications=100)
            call("runtime_ua_subscription_start", dict(captureId=capture,request=req))
            status = complete("subscription", capture)
            baseline = fixture("audit")
            observed = qualify_observation(call, check, status["snapshotRef"], root, out / "observations", mode, exact_value=mode != "subscriptionCreateItemsDenied")
            doc = json.loads(Path(observed["artifact"]["path"]).read_bytes())
            field = {"subscriptionDuplicate":"duplicateMessages","subscriptionGap":"gapRows","subscriptionOverflow":"overflowNotifications","subscriptionRepublishUnavailable":"failureEvents","subscriptionCreateItemsDenied":"failureEvents"}[mode]
            check(doc["events"][field] > 0, "Report preserves " + mode)
            check(fixture("audit") == baseline, "Adverse report processing performs no remote services")
            observations.append(observed)
            before_comparison=fixture('audit')
            comparisons.append(qualify_comparison(call,check,first_events,status['snapshotRef'],out/'comparisons',mode))
            check(fixture('audit')==before_comparison,'Adverse comparison performs no UA service')
            retained.append(("subscription",capture,req,status["snapshotRef"]))
        fixture("mode", value="normal")
        call("runtime_ua_disconnect", dict(attemptId=attempt))
        check(fixture("audit")["sessions"] == 0, "Independent server confirms zero sessions")
        close_host()
        # Raw evidence remains readable if a diagnostic source is absent, while a new binding must revalidate it.
        path = root / "trace-diagnostics" / (receipt["id"] + ".json")
        original = path.read_bytes(); path.unlink()
        before = fixture("audit")
        client = start_host()
        for kind, capture, req, reference in retained:
            check(call("runtime_ua_" + kind + "_status", dict(captureId=capture))["snapshotRef"] == reference, "Restart retains original " + kind + " prefix")
            call("runtime_ua_" + kind + "_start", dict(captureId=capture, request=req))
            call("runtime_ua_" + kind + "_export", dict(snapshotRef=reference))
        for observation in observations:
            call("diagnostic_ua_observe", dict(snapshotRef=observation["snapshotRef"]), "ArtifactUnavailable")
            call("diagnostic_ua_report_export", dict(reportId=observation["id"], format="json"), "ArtifactUnavailable")
        for comparison in comparisons:
            call('diagnostic_ua_report_export',dict(reportId=comparison['id'],format='json'),'ArtifactUnavailable')
        check(fixture("audit") == before, "Restart observations never replay services or require missing binding")
        path.write_bytes(original)
        for observation in observations:
            check(call("diagnostic_ua_observe", dict(snapshotRef=observation["snapshotRef"]))["id"] == observation["id"], "Restart produces identical observation from originals")
            call("diagnostic_ua_report_export", dict(reportId=observation["id"], format="html"))
        for comparison in comparisons:
            check(call('diagnostic_ua_compare',comparison['arguments'])['id']==comparison['id'],'Second Host derives the identical comparison')
            call('diagnostic_ua_report_export',dict(reportId=comparison['id'],format='html'))
        check(fixture("audit") == before, "Restart report revalidation performs no UA service")
        report["status"] = "passed"
    except BaseException as error:
        report["error"] = repr(error)
        raise
    finally:
        try:
            if client is not None:
                close_host()
            if server is not None and server.poll() is None:
                check(fixture("audit")["sessions"] == 0, "Final server session cleanup")
                server.stdin.write('{"operation":"stop"}\n'); server.stdin.flush()
                server.stdin.close(); server.wait(timeout=30)
                check(server.returncode == 0, "Original owned server exits cleanly")
            check(hashes() == report["inputHashes"], "Exact binaries unchanged during qualification")
        except BaseException as error:
            report["status"] = "failed"; report["cleanupError"] = repr(error)
            if server is not None and server.poll() is None:
                server.kill(); server.wait(timeout=10)
            raise
        finally:
            (out / "result.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            (out / "server-stderr.txt").write_text("\n".join(errors), encoding="utf-8")
    print(json.dumps(dict(status=report["status"], checks=len(report["checks"]))))


if __name__ == "__main__":
    main()
