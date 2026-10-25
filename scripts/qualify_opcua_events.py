"""Actual Host event capture on the encrypted fixture; shared by packaged qualification."""
import base64
import csv
import io
import json
import time
import uuid

from jsonschema import Draft202012Validator


def qualify(c, call, fixture, ended, download, schema, attempt, nodes, output, check):
    capture = uuid.uuid4().hex
    request = dict(attemptId=attempt, nodes=nodes[:1], durationSeconds=8, maximumNotifications=3,
                   events=dict(notifier=dict(nodes[0], identifier="fixture"), minimumSeverity=500))
    baseline = fixture("audit")["audit"]
    call(c, "subscription_start", dict(captureId=capture, request=request))
    deadline = time.monotonic() + 10
    while call(c, "subscription_status", dict(captureId=capture))["recordedProgress"]["rows"] == 0:
        check(time.monotonic() < deadline, "Bounded event source setup")
        time.sleep(0.05)
    for number, severity in ((1, 100), (2, 700), (3, 900)):
        fixture("emitEvent", number=number, severity=severity)
    status = ended(capture)
    reference = status["snapshotRef"]
    rows = call(c, "subscription_page", dict(snapshotRef=reference, count=200))["rows"]
    events = [n for r in rows for n in r["event"].get("events", [])]
    check(len(events) == 2, "Actual severity filter excludes low event on protected session")
    for number, event in zip((2, 3), events):
        fields = event["fields"]
        check(fields[0]["text"] == base64.b64encode(f"fixture-event-{number}".encode()).decode(), "Exact original event bytes")
        check(fields[2]["node"] == request["events"]["notifier"], "Original exact source identity")
        check(fields[6]["locale"] == "de-DE" and fields[6]["text"] == f"Übertemperatur <script>alert(1)</script> {number}", "Original localized hostile message")
    check(status["recordedProgress"]["notifications"] == 3 and status["recordedProgress"]["remoteCleanupConfirmed"], "Signal and event bound with confirmed cleanup")
    for fmt in ("csv", "html", "markdown"):
        export = call(c, "subscription_export", dict(snapshotRef=reference, format=fmt))
        exact = json.loads(download(export["artifacts"][0]))
        text = download(export["artifacts"][1]).decode("utf-8")
        Draft202012Validator({**schema, "$ref": "#/$defs/export"}).validate(exact)
        check(exact["rows"] == rows, "Exact event companion JSON " + fmt)
        if fmt == "csv":
            check([json.loads(r["event_json"]) for r in csv.DictReader(io.StringIO(text))] == [r["event"] for r in rows], "CSV preserves complete original event fields")
        else:
            check("&lt;script&gt;" in text and "<script>" not in text and "EventId" in text and "ReceiveTime" in text, "Readable event timeline escapes hostile content " + fmt)
        (output / ("events." + ("md" if fmt == "markdown" else fmt))).write_text(text, encoding="utf-8")
    audit = fixture("audit")["audit"][len(baseline):]
    filters = [x for x in audit if x["operation"] == "FixtureEventFilter"]
    check(len(filters) == 1 and filters[0]["minimumSeverity"] == 500 and filters[0]["operators"] == ["GreaterThanOrEqual"], "Server received exact bounded severity filter")
    check([x["path"] for x in filters[0]["fields"]] == [[name] for name in ("EventId", "EventType", "SourceNode", "SourceName", "Time", "ReceiveTime", "Message", "Severity")], "Server received exact standard event field order")
    (output / "events-original-export.json").write_text(json.dumps(exact, indent=2) + "\n", encoding="utf-8")
    plain = uuid.uuid4().hex
    call(c, "subscription_start", dict(captureId=plain, request=dict(request, events=None, maximumNotifications=1)))
    check("events" not in ended(plain)["plan"]["request"], "Explicit null event option preserves existing signal-only intent")
    for mode in ("eventNullMessage", "eventSecretEcho"):
        fixture("mode", value=mode)
        selected = uuid.uuid4().hex
        call(c, "subscription_start", dict(captureId=selected, request=dict(request, nodes=[], maximumNotifications=1)))
        deadline = time.monotonic() + 10
        while call(c, "subscription_status", dict(captureId=selected))["recordedProgress"]["rows"] == 0:
            check(time.monotonic() < deadline, "Bounded event fixture setup " + mode)
            time.sleep(0.05)
        fixture("emitEvent", number=4, severity=700)
        result = ended(selected)
        original = call(c, "subscription_page", dict(snapshotRef=result["snapshotRef"], count=200))["rows"]
        if mode == "eventSecretEcho":
            check(result["recordedProgress"]["notifications"] == 0 and any(r["event"]["errorCode"] == "UaSecretEchoRejected" for r in original), "Credential echoed in event field refuses before journal commit")
        else:
            message = next(n["fields"][6] for r in original for n in r["event"].get("events", []))
            check(message["kind"] == "value" and message["text"] is None and message["locale"] == "de-DE", "Protected event preserves localized null text")
    fixture("mode", value="normal")
    return capture, request, reference, rows
