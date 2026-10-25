"""Independent original-result oracle for the synthetic real UA server.

These expectations are protocol-fixture constants, not imported production
serializers. Altered copies must fail without changing original evidence.
"""
import copy
import datetime as dt
import json
from pathlib import Path
from jsonschema import Draft202012Validator, ValidationError

ALLOWED = {"GetEndpoints", "CreateSession", "ActivateSession", "Read", "Browse",
           "BrowseNext", "Continuation", "CloseSession", "FixtureTransportReadFailure"}
NS = "urn:tiamcp:qualification:synthetic"
UA = "http://opcfoundation.org/UA/"


def require(condition, name):
    if not condition:
        raise ValueError(name)


def verify_values(packet):
    schema = json.loads((Path(__file__).resolve().parents[1] / "contracts/opcua-runtime.v1.schema.json").read_text())
    Draft202012Validator({**schema, "$ref": "#/$defs/read"}).validate(packet)
    items = {x["node"]["identifier"]: x for x in packet["items"]}
    require(len(items) == len(packet["items"]), "duplicate item identity")
    for name, scalar, kind, dtype, encoding in [
        ("int64", "-9223372036854775808", "Int64", "8", "decimalInteger"),
        ("uint64", "18446744073709551615", "UInt64", "9", "decimalInteger"),
        ("double", "0.125", "Double", "11", "roundTripDouble"),
        ("bool", True, "Boolean", "1", "boolean"),
        ("text", "synthetic local value", "String", "12", "string"),
        ("byteString", "AQID", "ByteString", "15", "base64"),
        ("minimumDate", "0001-01-01T00:00:00.0000000Z", "DateTime", "13", "utcDateTime"),
    ]:
        item = items[name]
        require(item["node"] == dict(namespaceUri=NS, identifierType="string", identifier=name), "exact namespace identity")
        require(item["value"] == scalar and type(item["value"]) is type(scalar), "exact scalar original bytes")
        require(item["builtInType"] == kind and item["dataType"] == dict(namespaceUri=UA, identifierType="numeric", identifier=dtype), "exact type")
        require(item["encoding"] == encoding and item["valueRank"] == -1 and item["arrayDimensions"] is None, "scalar representation and rank")
        require(item["quality"] == "Good" and item["statusCode"] == "0x00000000" and item["outcome"] == "value", "good scalar status")
        require(dt.datetime.fromisoformat(item["sourceTimestamp"]) == dt.datetime(2026, 10, 19, 12, 34, 56, tzinfo=dt.timezone.utc), "source timestamp")
        require(dt.datetime.fromisoformat(item["serverTimestamp"]) == dt.datetime(2026, 10, 19, 12, 34, 56, 123000, tzinfo=dt.timezone.utc), "server timestamp")
        require(item["sourcePicoseconds"] == 12 and item["serverPicoseconds"] == 34, "original picoseconds")
    require(items["bad"]["statusCode"] == "0x808c0000" and items["bad"]["quality"] == "Bad" and items["bad"]["value"] is None, "bad value suppression")
    require(items["uncertain"]["statusCode"] == "0x40920000" and items["uncertain"]["quality"] == "Uncertain" and items["uncertain"]["value"] == "42", "uncertain not upgraded")
    require(items["missingTimes"]["sourceTimestamp"] is None and items["missingTimes"]["serverTimestamp"] is None, "missing timestamps remain missing")
    require(items["array"]["outcome"] == "value" and items["array"]["encoding"] == "array" and items["array"]["value"] == dict(dimensions=[3], elementEncoding="decimalInteger", elements=["1", "2", "3"]) and items["array"]["valueRank"] == 1 and items["array"]["arrayDimensions"] == [3], "array exact values and metadata")
    for name, dimensions, encoding, elements in [
        ("uint64Array", [3], "decimalInteger", ["0", "9007199254740993", "18446744073709551615"]),
        ("int64Matrix", [2, 3], "decimalInteger", ["-9223372036854775808", "-1", "0", "9223372036854775807", "2", "3"]),
        ("emptyArray", [0], "decimalInteger", []),
        ("stringArray", [4], "string", ["Größe <tag>", None, "", '\"\n']),
        ("byteStrings", [3], "base64", ["AP8=", None, ""]),
        ("doubleMatrix", [2, 2], "roundTripDouble", ["0.125", "-2.5", "3", "4"]),
    ]:
        if name in items:
            item = items[name]
            require(item["outcome"] == "value" and item["encoding"] == "array" and
                    item["value"] == dict(dimensions=dimensions, elementEncoding=encoding, elements=elements),
                    "exact array dimensions, element order and values: " + name)
    require(items["structure"]["outcome"] == "unsupported" and items["structure"]["value"] is None, "structure unsupported")
    require(items["byteArray"]["encoding"] == "array" and items["byteArray"]["value"] == dict(dimensions=[3], elementEncoding="decimalInteger", elements=["1", "2", "3"]), "byte array retained")
    require(items["byteString"]["outcome"] == "value" and items["byteString"]["value"] == "AQID", "scalar byte string retained")
    require(isinstance(items["minimumDate"]["value"], str), "minimum scalar date not null")
    require(items["denied"]["quality"] == "Bad" and items["denied"]["value"] is None, "partial access refusal")


def verify_audit(audit, healthy=False):
    require(bool(audit), "actual service audit required")
    ops = [x["operation"] for x in audit]
    require(set(ops) <= ALLOWED, "read-only service allowlist")
    times = [dt.datetime.fromisoformat(x["at"]) for x in audit]
    require(times == sorted(times), "service chronology")
    if healthy:
        require(ops[:3] == ["GetEndpoints", "CreateSession", "ActivateSession"], "explicit discovery/session activation order")
        require("CloseSession" in ops, "confirmed owned close service")
        end = ops.index("CloseSession")
        require("Read" in ops[3:end] and "Browse" in ops[3:end], "real values and browse before close")
        require(any(x["operation"] == "Continuation" and x.get("release") is True for x in audit[:end]), "continuation released")


def controls(packet, audit):
    rejected = []
    mutations = [
        ("roundedUInt64", lambda p: p["items"][1].update(value="18446744073709552000")),
        ("upgradedQuality", lambda p: next(x for x in p["items"] if x["node"]["identifier"] == "uncertain").update(quality="Good")),
        ("fabricatedTimestamp", lambda p: next(x for x in p["items"] if x["node"]["identifier"] == "missingTimes").update(sourceTimestamp="2026-10-19T00:00:00Z")),
        ("namespaceAlias", lambda p: p["items"][0]["node"].update(namespaceUri="urn:wrong")),
        ("byteArrayDefault", lambda p: next(x for x in p["items"] if x["node"]["identifier"] == "byteArray").update(outcome="value", value="AQID")),
        ("integerEncoding", lambda p: next(x for x in p["items"] if x["node"]["identifier"] == "int64").update(encoding="string")),
        ("textValue", lambda p: next(x for x in p["items"] if x["node"]["identifier"] == "text").update(value="changed")),
        ("minimumDateValue", lambda p: next(x for x in p["items"] if x["node"]["identifier"] == "minimumDate").update(value="changed")),
        ("byteStringType", lambda p: next(x for x in p["items"] if x["node"]["identifier"] == "byteString").update(
            builtInType="String", dataType=dict(namespaceUri=UA, identifierType="numeric", identifier="12"), encoding="string")),
    ]
    for name, mutate in mutations:
        altered = copy.deepcopy(packet); mutate(altered)
        try: verify_values(altered)
        except (ValueError, KeyError, TypeError, ValidationError): rejected.append(name)
        else: raise ValueError("mutation accepted: " + name)
    for name, altered in [
        ("writeService", audit + [dict(operation="Write", at=audit[-1]["at"])]),
        ("missingClose", [x for x in audit if x["operation"] != "CloseSession"]),
        ("activationOrder", [audit[0], audit[2], audit[1], *audit[3:]]),
    ]:
        try: verify_audit(altered, healthy=True)
        except (ValueError, KeyError, TypeError, ValidationError): rejected.append(name)
        else: raise ValueError("mutation accepted: " + name)
    return rejected


def audit_managed(directory):
    root = Path(directory)
    packet = json.loads((root / "values.json").read_text())
    audit = json.loads((root / "server-audit-0.json").read_text())
    result = json.loads((root / "result.json").read_text())
    require(result["status"] == "passed", "managed suite passed")
    verify_values(packet); verify_audit(audit, healthy=True)
    for path in root.glob("server-audit-*.json"):
        verify_audit(json.loads(path.read_text()))
    crash = json.loads((root / "crash-process.json").read_text())
    require(crash["exitCode"] == 73 and crash["originalHandleWaited"] and not crash["forcedTermination"], "original owned crash lifetime")
    cases = {x["name"]: x for x in json.loads((root / "scenarios.json").read_text())}
    schema = json.loads((Path(__file__).resolve().parents[1] / "contracts/opcua-runtime.v1.schema.json").read_text())
    for case in cases.values():
        if "status" in case:
            Draft202012Validator(schema).validate(case["status"])
    for name in ["wrongPin", "wrongUri", "wrongHost", "expired", "wrongApplication"]:
        require(cases[name]["status"]["state"] == "notConnected" and not cases[name]["status"]["peerVerified"], "certificate/profile refusal " + name)
        require([x["operation"] for x in cases[name]["audit"]] == ["GetEndpoints"], "no session before trust admission")
    require(cases["rotation"]["status"]["state"] != "connected" and not cases["rotation"]["status"]["peerVerified"], "actual peer rotation not verified")
    return dict(status="passed", negativeControls=controls(packet, audit), realLoopback=True, physicalQualified=False)


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--managed", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = audit_managed(args.managed)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)
