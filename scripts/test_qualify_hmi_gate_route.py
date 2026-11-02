"""Offline checks of scripts/qualify_hmi_gate_route.py: fixture resolution, the Classic document edits and readback rule, the
intended-delta oracle, and whole runs against a fake Host that keeps an HMI model and enforces the worker's refusals."""
import copy
import hashlib
import itertools
import json
import pathlib
import re
import shutil
import tempfile
import types
import unittest
from unittest import mock

import qualify_hmi_gate_route as route

SCREEN = ('<?xml version="1.0" encoding="utf-8"?>\n<Document>\n  <Engineering version="V21" />\n  <DocumentInfo><Created>{created}</Created></DocumentInfo>\n'
          '  <{root} ID="0">\n    <AttributeList>\n      <BackColor>{back}</BackColor>\n      <GridColor>{grid}</GridColor>\n      <Name>{name}</Name>\n    </AttributeList>\n'
          '    <ObjectList>\n      <Item ID="1"><AttributeList><Name>Rectangle</Name><BackColor>128, 128, 128</BackColor></AttributeList></Item>\n    </ObjectList>\n  </{root}>\n</Document>\n')
TABLE = ('<?xml version="1.0" encoding="utf-8"?>\n<Document>\n  <Engineering version="V21" />\n  <DocumentInfo><Created>{created}</Created></DocumentInfo>\n'
         '  <Hmi.Tag.TagTable ID="0">\n    <AttributeList><Name>QualificationTags</Name></AttributeList>\n    <ObjectList>\n'
         '      <Hmi.Tag.Tag ID="1" CompositionName="Tags"><AttributeList><Length>2</Length><Name>SharedValue</Name></AttributeList>'
         '<LinkList><DataType TargetID="@OpenLink"><Name>Int</Name></DataType></LinkList>'
         '<ObjectList><MultilingualText ID="2" CompositionName="Comment" /></ObjectList></Hmi.Tag.Tag>\n    </ObjectList>\n  </Hmi.Tag.TagTable>\n</Document>\n')
ROLES = {"Overview_A": ("regular", "Hmi.Screen.Screen", "/screens/Overview_A"), "Overview_B": ("regular", "Hmi.Screen.Screen", "/screens/Overview_B"),
         "Template_1": ("template", "Hmi.Screen.ScreenTemplate", "/screenTemplates/Template_1"), "Popup_1": ("popup", "Hmi.Screen.ScreenPopup", "/screenPopups/Popup_1"),
         "Global": ("globalElements", "Hmi.Screen.ScreenGlobalElements", "/screenGlobalElements")}


def screen(name, back="204, 204, 204", grid="0, 0, 0", created="2026-11-01T00:00:00Z"):
    return SCREEN.format(root=ROLES[name][1], name=name, back=back, grid=grid, created=created)


class Refused(Exception):
    def __init__(self, code, details=None):
        super().__init__(code)
        self.code, self.details = code, details or {}


class FakeHost:
    """One TIA with project copies by path. A connection is a Host; extra holds its qualification profile."""

    def __init__(self, directory, family, profile, errors=0, leak=None, stale_readback=False, scope=None, receipt=None, read_only=False):
        self.directory, self.family, self.profile, self.errors = directory, family, profile, errors
        self.leak, self.stale_readback = leak, stale_readback
        # scope(hmiPath) overrides the compiled target; receipt overrides a Host's close receipt; read_only the reopen.
        self.scope, self.receipt, self.read_only = scope, receipt, read_only
        self.counter, self.changes, self.applies = itertools.count(1), {}, 0
        self.worlds, self.active = {}, None
        self.original = str(directory / "p" / "Fixture.ap21")
        self.recovered = str(directory / "r" / "Fixture.ap21")

    # The fixture model, keyed by HMI path then section then objectPath.
    def seed(self):
        world = {}
        for index, name in enumerate(route.HMIS, 1):
            hmi = "/devices/HmiDevice_%s/items/%s/hmi/%s" % ("AB"[index - 1], name, name)
            if self.family == "classic":
                # The seeder authors no popup on KTP700 Basic PN.
                exports = {hmi + ROLES[s][2]: screen(s) for s in ROLES if not (self.profile == "classic-basic" and s == "Popup_1")}
                exports[hmi + "/tagTables/QualificationTags"] = TABLE.format(created="x")
                world[hmi] = {"exports": exports, "plc": None}
            else:
                plc = "/devices/PlcDevice_%d/items/PLC_%s/plc/PLC_%s" % (index - 1, "AB"[index - 1], "AB"[index - 1])
                table = hmi + "/tagTables/Default%20tag%20table"
                tags = {table: {"kind": "hmiTagTable", "name": "Default tag table", "properties": {}}}
                for tag, spelling in (("LinkedSignal", "SharedSignal"), ("LinkedDbValue", "QualificationDB.Value")):
                    tags[table + "/tags/" + tag] = {"kind": "hmiTag", "name": tag, "properties": {"Connection": "HMI_Connection_1", "PlcTag": spelling, "DataType": "Int", "AccessMode": "SymbolicAccess"}}
                tags[table + "/tags/SharedValue"] = {"kind": "hmiTag", "name": "SharedValue", "properties": {"Connection": "", "PlcTag": "", "DataType": "Int", "AccessMode": "SymbolicAccess"}}
                world[hmi] = {"plc": plc, "tags": tags, "connections": {hmi + "/connections/HMI_Connection_1": {"kind": "hmiConnection", "name": "HMI_Connection_1", "properties": {"Partner": "PLC_" + "AB"[index - 1]}}},
                              "screens": {hmi + "/screens/" + s: {"kind": "hmiScreen", "name": s, "properties": {"Width": 800, "Height": 480},
                                                                  "eventHandlers": [{"properties": {"EventType": "Loaded"}, "script": {"scriptCode": "seeded"}}] if s == "Overview_A" else []}
                                          for s in ("Overview_A", "Overview_B")},
                              "items": {hmi + "/screens/%s/items/RepeatedItem" % s: {"kind": "hmiScreenItem", "name": "RepeatedItem", "screen": hmi + "/screens/" + s,
                                        "nativeType": "Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle", "properties": {"Left": 30, "Top": 40, "Width": 120, "Height": 60}}
                                        for s in ("Overview_A", "Overview_B")},
                              "model": {path: {"kind": row["kind"], "name": row["name"], "properties": dict(row["properties"])} for path, row in tags.items()}}
        return world

    def connect(self, store, extra, calls):
        return FakeClient(self, extra[1] if extra else None, calls)

    def world(self):
        return self.worlds[self.active]

    def hash(self):
        return hashlib.sha256(json.dumps(self.world(), sort_keys=True).encode()).hexdigest()

    def identity(self, hmi, path, kind, name):
        return {"schemaVersion": 2, "projectPath": self.active, "projectCopyId": "copy-" + self.active[-20:], "sessionGeneration": "g",
                "family": self.family, "hmiPath": hmi, "objectPath": path, "kind": kind, "name": name}

    def classic_rows(self, hmi, section, screen_path=None):
        exports = self.world()[hmi]["exports"]
        if section == "screens":
            return [dict(self.row(hmi, path, "hmiScreen", name), screenRole=ROLES[name][0], properties={"BackColor": route.root_attribute(exports[path], "BackColor")})
                    for name in ROLES for path in [hmi + ROLES[name][2]] if path in exports]
        if section == "items":
            return [self.row(hmi, screen_path + "/items/Rectangle", "hmiScreenItem", "Rectangle", properties={"BackColor": "128, 128, 128"})]
        if section == "tags":
            table = hmi + "/tagTables/QualificationTags"
            names = [t.findtext("AttributeList/Name") for t in route.root_object(exports[table]).iter("Hmi.Tag.Tag")]
            return [self.row(hmi, table, "hmiTagTable", "QualificationTags")] + [self.row(hmi, table + "/tags/" + n, "hmiTag", n) for n in names]
        return []

    def row(self, hmi, path, kind, name, properties=None):
        return {"identity": self.identity(hmi, path, kind, name), "name": name, "properties": properties or {}}

    def inspect(self, request):
        section = request.get("section", "devices")
        if section == "devices":
            return {"items": [{"name": name, "family": self.family, "hmiPath": hmi, "profile": self.profile, "identity": self.identity(hmi, hmi, "hmiDevice", name)}
                              for name, hmi in zip(route.HMIS, self.world())], "coverage": {"status": "complete"}}
        hmi = request["target"]["hmiPath"]
        artifacts = []
        if self.family == "classic":
            items = self.classic_rows(hmi, section, request.get("screenPath"))
            exported = request.get("screenPath") if section == "items" else request.get("objectPath") if request.get("includeSource") else None
            if exported:
                artifacts.append(self.export(hmi, exported))
        else:
            exact = request.get("objectPath") if section == "screens" else None
            items = [dict(self.row(hmi, path, row["kind"], row["name"], row["properties"]), **{k: row[k] for k in ("nativeType", "dynamizations") if k in row},
                          **({"eventHandlers": row.get("eventHandlers", [])} if section == "items" or path == exact else {}),
                          **({"behaviorCoverage": {c: {"status": "observed", "count": len(row.get(c, []))} for c in ("dynamizations", "eventHandlers")}} if section == "items" else {}))
                     for path, row in self.world()[hmi].get(section, {}).items() if section != "items" or row["screen"] == request.get("screenPath")]
        if request.get("objectPath") and section != "items":
            items = [r for r in items if r["identity"]["objectPath"] == request["objectPath"]]
        offset, limit = request.get("offset", 0), request.get("limit", 100)
        more = offset + limit < len(items)
        return {"items": items[offset:offset + limit], "nextOffset": offset + limit if more else None, "coverage": {"status": "complete"},
                "metadataRevision": "r", "projectPath": self.active, "artifacts": artifacts}

    def export(self, hmi, path):
        text = self.world()[hmi]["exports"][path]
        # Every export has its own creation time and its own ID numbering.
        text = re.sub(r"<Created>[^<]*</Created>", "<Created>%d</Created>" % next(self.counter), text)
        file = self.directory / ("export-%d.xml" % next(self.counter))
        file.write_bytes(b"\xef\xbb\xbf" + text.encode("utf-8"))
        return {"path": str(file), "sha256": hashlib.sha256(file.read_bytes()).hexdigest()}

    def references(self, request):
        edges = self.edges(request)
        offset, limit = request.get("offset", 0), request.get("limit", 100)
        return {"relationships": edges[offset:offset + limit], "nextOffset": offset + limit if offset + limit < len(edges) else None}

    def edges(self, request):
        hmi = request["target"]["hmiPath"]
        plc, table = self.world()[hmi]["plc"], hmi + "/tagTables/Default%20tag%20table"
        base = {"projectPath": self.active, "plcPath": plc, "scopePath": plc}
        # 199 unrelated edges first, so the item edges land on the second page as on the native fixture (run hb3).
        return [{"relation": "hmiNativePayload", "sourcePath": hmi + "/filler/%d" % i, "resolution": "unresolved"} for i in range(199)] + [
            {"relation": "hmiPlcDeclaration", "sourcePath": table + "/tags/LinkedSignal", "resolution": "resolved",
             "target": {**base, "kind": "tag", "objectPath": plc + "/tags/QualificationTags/tags/SharedSignal", "name": "SharedSignal", "dataType": "Int"}},
            {"relation": "hmiPlcDeclaration", "sourcePath": table + "/tags/LinkedDbValue", "resolution": "resolved",
             "target": {**base, "kind": "plcDeclaration", "objectPath": plc + "/blocks/QualificationDB/interface/Static/members/Value", "name": "Value",
                        "blockPath": plc + "/blocks/QualificationDB", "pathComponents": ["Value"], "xmlLine": 7}}] + [
            {"relation": "hmiScreenItemTag", "sourcePath": path, "sourceProperty": "Dynamizations/%s/Tag" % d["property"], "resolution": "resolved",
             "target": {"objectPath": table + "/tags/" + d["properties"]["Tag"]}}
            for path, row in self.world()[hmi].get("items", {}).items() for d in row.get("dynamizations", []) if row["screen"] == request.get("screenPath")]

    # The worker's refusals, in its order: request rules, kind, body, gate, StaleRevision.
    def check(self, kind, payload, admitted):
        operation, target = payload["operation"], payload["target"]
        hmi, path = target["hmiPath"], target["objectPath"]
        if kind != "hmi_engineering":
            family, operations = {"hmi_classic_import": ("classic", {"classic_xml_import", "scalar_property_set"}), "hmi_external_tag_create": ("unified", {"external_tag_create"}),
                                  "hmi_screen_create": ("unified", {"screen_create", "item_create"}), "hmi_dynamization_create": ("unified", {"dynamization_set"}),
                                  "hmi_event_handler_create": ("unified", {"event_handler_set"})}[kind]
            if target["family"] != family or operation not in operations:
                return "InvalidRequest"
        if operation == "screen_create":
            screens = self.world()[hmi]["screens"].values()
            if set(payload["properties"]) != {"Width", "Height"}:
                return "InvalidRequest"
            if any(r["name"] == payload["name"] for r in screens):
                return "HmiNameConflict"
            if not any(r["properties"] == payload["properties"] for r in screens):
                return "DependencyUnresolved"
        if operation == "event_handler_set":
            script, event = payload["script"], payload["eventType"]
            if event == "None" or not script.strip() or len(script) > 16384 or any(ord(c) < 32 and c not in "\t\r\n" for c in script):
                return "InvalidRequest"
            owner = self.world()[hmi]["screens"].get(path) or self.world()[hmi]["items"][path]
            allowed = ["Loaded", "Cleared"] if owner["kind"] == "hmiScreen" else ["Down", "Up", "Tapped"]
            if event not in allowed:
                raise Refused("InvalidRequest", {"eventType": event, "allowed": allowed})
            if any(h["properties"]["EventType"] == event for h in owner.get("eventHandlers", [])):
                return "HmiNameConflict"
        if operation == "dynamization_set":
            binding, item = payload["bindings"][0], self.world()[hmi]["items"][path]
            tag = self.world()[hmi]["tags"].get(binding["tagIdentity"]["objectPath"]) if binding["tagIdentity"]["hmiPath"] == hmi else None
            if binding["property"] not in ("ProcessValue", "Visible", "Enabled") or binding["property"] != "ProcessValue" and binding["dataType"] != "Bool" or tag is None:
                return "InvalidRequest"
            if not item["nativeType"].endswith(("HmiIOField", "HmiBar", "HmiGauge")):
                return "HmiAttributeUnavailable"
            if any(d["property"] == binding["property"] for d in item.get("dynamizations", [])):
                return "HmiNameConflict"
            if binding["dataType"] != tag["properties"]["DataType"]:
                return "InvalidRequest"
        if operation == "item_create":
            place, screen = payload["placement"], self.world()[hmi]["screens"].get(path)
            if payload["itemType"] not in ("HmiRectangle", "HmiText", "HmiButton", "HmiIOField", "HmiBar", "HmiGauge", "HmiSlider", "HmiToggleSwitch"):
                return "InvalidRequest"
            if path + "/items/" + payload["name"] in self.world()[hmi]["items"]:
                return "HmiNameConflict"
            if place["left"] + place["width"] > screen["properties"]["Width"] or place["top"] + place["height"] > screen["properties"]["Height"]:
                return "InvalidRequest"
        if operation == "scalar_property_set" and not re.fullmatch(r"\d{1,3}, \d{1,3}, \d{1,3}", payload["properties"]["BackColor"]):
            return "InvalidRequest"
        if operation == "classic_xml_import":
            if "<!DOCTYPE" in payload["xml"]:
                return "InvalidRequest"
            try:
                supplied = route.root_object(payload["xml"])
            except AssertionError:
                return "InvalidRequest"
            current = route.root_object(self.world()[hmi]["exports"][path])
            if supplied.tag != current.tag or supplied.findtext("AttributeList/Name") != current.findtext("AttributeList/Name"):
                return "XmlScopeMismatch"
            if current.tag not in ("Hmi.Screen.Screen", "Hmi.Tag.TagTable"):
                return "HmiPreservationUnavailable"
        if operation == "external_tag_create":
            tag = payload["plcTag"]
            if payload["connection"]["hmiPath"] != hmi or tag.get("scopePath", tag["plcPath"]) != tag["plcPath"] or set(tag) - {*route.PLC_KEYS, "memberPath"}:
                return "InvalidRequest"
            if tag["kind"] == "block" and not all(re.fullmatch(r"[A-Za-z_]\w*", m) for m in tag["memberPath"]):
                return "InvalidRequest"
            if path + "/tags/" + payload["name"] in self.world()[hmi]["tags"]:
                return "HmiNameConflict"
            if tag["plcPath"] != self.world()[hmi]["plc"]:
                return "DependencyUnresolved"
            if payload["dataType"] != "Int":
                return "TargetIncompatible"
        if kind == "hmi_engineering" or not admitted:
            return "HmiPreservationUnavailable"
        return None

    def apply(self, payload):
        world, operation = self.world(), payload["operation"]
        hmi, path = payload["target"]["hmiPath"], payload["target"]["objectPath"]
        if operation == "scalar_property_set" and not self.stale_readback:
            world[hmi]["exports"][path] = route.set_root_attribute(world[hmi]["exports"][path], "BackColor", payload["properties"]["BackColor"])
        elif operation == "classic_xml_import":
            # A re-export renumbers export-local IDs in document order.
            ids = itertools.count()
            world[hmi]["exports"][path] = re.sub(r' ID="\d+"', lambda _: ' ID="%d"' % next(ids), payload["xml"])
        elif operation == "external_tag_create":
            tag = payload["plcTag"]
            spelling = tag["name"] if tag["kind"] == "tag" else tag["objectPath"].rsplit("/", 1)[1] + "." + ".".join(tag["memberPath"])
            properties = {"Connection": payload["connection"]["name"], "PlcTag": spelling, "DataType": payload["dataType"], "AccessMode": "SymbolicAccess"}
            created = path + "/tags/" + payload["name"]
            world[hmi]["tags"][created] = {"kind": "hmiTag", "name": payload["name"], "properties": properties}
            world[hmi]["model"][created] = {"kind": "hmiTag", "name": payload["name"], "properties": dict(properties)}
        elif operation == "screen_create":
            created = hmi + "/screens/" + payload["name"]
            world[hmi]["screens"][created] = {"kind": "hmiScreen", "name": payload["name"], "properties": dict(payload["properties"])}
            world[hmi]["model"][created] = {"kind": "hmiModelNode", "name": payload["name"], "properties": dict(payload["properties"])}
        elif operation == "item_create":
            created, place = path + "/items/" + payload["name"], payload["placement"]
            properties = {"Left": place["left"], "Top": place["top"], "Width": place["width"], "Height": place["height"]}
            world[hmi]["items"][created] = {"kind": "hmiScreenItem", "name": payload["name"], "screen": path, "properties": properties,
                                            "nativeType": "Siemens.Engineering.HmiUnified.UI.Widgets." + payload["itemType"]}
            world[hmi]["model"][created] = {"kind": "hmiModelNode", "name": payload["name"], "properties": dict(properties)}
        elif operation == "event_handler_set":
            owner = world[hmi]["screens"].get(path) or world[hmi]["items"][path]
            owner.setdefault("eventHandlers", []).append({"properties": {"EventType": payload["eventType"]}, "script": {
                "scriptCode": payload["script"].replace("\r\n", "\n").replace("\n", "\r\n"), "scriptCodeSha256": hashlib.sha256(payload["script"].replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8")).hexdigest(), "scriptsExecuted": False}})
            world[hmi]["model"][path + "/eventHandlers/" + payload["eventType"]] = {"kind": "hmiModelNode", "name": payload["eventType"], "properties": {}}
        elif operation == "dynamization_set":
            binding = payload["bindings"][0]
            tag = binding["tagIdentity"]["objectPath"].rsplit("/", 1)[1]
            values = {"Tag": tag, "DynamizationType": "Tag", "UseIndirectAddressing": False}
            world[hmi]["items"][path].setdefault("dynamizations", []).append({"property": binding["property"], "properties": values})
            world[hmi]["model"][path + "/dynamizations/" + binding["property"]] = {"kind": "hmiModelNode", "name": binding["property"], "properties": dict(values)}
        if self.leak:
            self.leak(world)


class FakeClient:
    def __init__(self, host, admitted, calls):
        self.host, self.admitted, self.calls = host, admitted, calls
        self.process = types.SimpleNamespace(returncode=0)

    def initialize(self):
        pass

    def receipt_close(self):
        receipt = {"hostExitCode": 0, "forcedHostStop": False, "stderrEofObserved": True, "toolCalls": [],
                   "workerExits": [{"processExitVerified": True, "exitCode": 0, "forcedTermination": False}]}
        return {**receipt, **(self.host.receipt or {})}

    def tool(self, name, arguments=None):
        raw = self.raw_tool(name, arguments)
        if raw["isError"]:
            raise RuntimeError(name + ": " + json.dumps(raw["structuredContent"])[:300])
        return raw["structuredContent"]

    def raw_tool(self, name, arguments=None):
        try:
            result = self.call(name, arguments or {})
            error = False
        except Refused as refused:
            result, error = {"error": {"code": refused.code, "message": refused.code, "details": refused.details}}, True
        self.calls.append({"name": name, "isError": error})
        return {"structuredContent": result, "isError": error}

    def call(self, name, args):
        host = self.host
        if name == "tia_start":
            return {"processId": next(host.counter)}
        if name == "project_retrieve":
            host.worlds[host.original] = host.seed()
            host.active = host.original
            return {"project": {"path": host.original}}
        if name == "project_open":
            host.active = args["path"]
            return {"project": {"path": args["path"]}, "opened": True, "readOnly": host.read_only}
        if name == "tia_detach":
            return {}
        if name == "hmi_inspect":
            return host.inspect(args["request"])
        if name == "hmi_references":
            return host.references(args["request"])
        if name == "change_prepare":
            return self.prepare(args["kind"], args["payload"])
        if name == "change_apply":
            return self.apply(args)
        if name == "change_restore":
            change = host.changes[args["changeId"]]
            host.worlds[host.recovered] = change["before"]
            host.active = host.recovered
            return {"changeId": change["id"], "state": "restored", "stages": change["stages"] + ["hmi_recovered_content_equality_verified"],
                    "recoveredProject": {"path": host.recovered}}
        raise AssertionError("Unexpected tool " + name)

    def prepare(self, kind, payload):
        host = self.host
        # The three profiles are production-admitted for the 1 November kinds; a qualification Host does not change that.
        admission = route.admission(kind, host.profile)
        code = host.check(kind, payload, admission == "production" or self.admitted == host.profile)
        gate = {"available": code is None, "admission": admission if code is None else None, "targetProfile": host.profile, "reasonCode": code}
        if code:
            raise Refused(code, {"gate": gate})
        if payload["expectedContentHash"] != host.hash():
            raise Refused("StaleRevision", {"gate": gate, "baseline": {"contentHash": host.hash()}})
        number = next(host.counter)
        change = {"id": "c%d" % number, "kind": kind, "payload": payload, "state": "prepared", "key": None, "hash": host.hash(), "stages": [],
                  "journal": host.directory / ("journal-%d.json" % number)}
        host.changes[change["id"]] = change
        self.write(change)
        return {**self.view(change), "preview": {"hmiEvidence": {"gate": gate}, "project": {"path": host.active}}}

    def apply(self, args):
        host, change = self.host, self.host.changes[args["changeId"]]
        if change["key"] is not None:
            if change["key"] == args["idempotencyKey"] and change["state"] == "applied":
                return self.view(change)
            raise Refused("RecoveryRequired")
        if not args["compile"] or not args["save"]:
            raise Refused("InvalidRequest")
        if host.hash() != change["hash"]:
            raise Refused("StaleRevision")
        change["key"], change["before"] = args["idempotencyKey"], copy.deepcopy(host.world())
        host.apply(change["payload"])
        change.update(state="applied", stages=list(route.APPLIED_STAGES))
        self.write(change)
        return self.view(change)

    def view(self, change):
        rows = [{"depth": 0, "state": "Error", "description": "Password %d" % i, "path": ""} for i in range(self.host.errors)]
        rows.append({"depth": 0, "state": "Error" if self.host.errors else "Success", "description": "Compiling finished (errors: %d; warnings: 0)" % self.host.errors, "path": ""})
        hmi = change["payload"]["target"]["hmiPath"]
        kind, target = route.compile_scope({"hmiPath": hmi}, self.host.family)
        compiled = {"errorCount": self.host.errors, "warningCount": 0, "compileScope": kind,
                    "targets": [{"target": self.host.scope(hmi) if self.host.scope else target, "messages": rows, "messagesTruncated": False}]}
        return {"changeId": change["id"], "previewHash": "h" + change["id"], "state": change["state"], "stages": change["stages"], "journalPath": str(change["journal"]),
                "compile": compiled if change["state"] == "applied" else None,
                "hmiEvidence": {"baselineCompile": compiled, "baselineCompileChangedCapture": False}}

    def write(self, change):
        change["journal"].write_text(json.dumps({"state": change["state"], "stages": change["stages"]}), encoding="utf-8")


class CompileProblemTests(unittest.TestCase):
    def test_the_rows_decide_not_the_summary_counts(self):
        # Route run hr4: the first compile after a retrieve lists both password errors at depth 0 yet ends "errors: 0".
        first = {"targets": [{"messagesTruncated": False, "messages": [
            {"depth": 0, "state": "Error", "description": "The entered password confirmation does not match the password. "},
            {"depth": 0, "state": "Error", "description": "The entered password does not comply with the password security concept"},
            {"depth": 0, "state": "Success", "description": "Compiling finished (errors: 0; warnings: 0)"}]}]}
        later = {"targets": [{"messagesTruncated": False, "messages": [
            {"depth": 0, "state": "Error", "description": ""}, {"depth": 1, "state": "Error", "description": ""},
            {"depth": 3, "state": "Error", "description": "The entered password confirmation does not match the password. "},
            {"depth": 3, "state": "Error", "description": "The entered password does not comply with the password security concept"},
            {"depth": 0, "state": "Error", "description": "Compiling finished (errors: 2; warnings: 0)"}]}]}
        self.assertEqual(route.problems(first), route.problems(later))
        added = json.loads(json.dumps(later))
        added["targets"][0]["messages"].insert(2, {"depth": 3, "state": "Warning", "description": "New warning"})
        self.assertNotEqual(route.problems(first), route.problems(added))
        self.assertIsNone(route.problems({"targets": [{"messagesTruncated": True, "messages": []}]}))


class GateRouteRunTests(unittest.TestCase):
    def setUp(self):
        self.root = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.root)
        (self.root / "p").mkdir()
        (self.root / "r").mkdir()
        fits = mock.patch.object(route, "retrieve_fits", lambda _: True)
        fits.start()
        self.addCleanup(fits.stop)

    def configured(self, profile):
        archive = self.root / "prep" / "native" / "configured.zap21"
        archive.parent.mkdir(parents=True)
        archive.write_bytes(b"configured")
        (self.root / "prep" / "result.json").write_text(json.dumps({"completed": True, "nativeResult": {"completed": True, "savedConfiguredArchiveCaptured": True, "profile": profile,
            "archive": {"path": str(archive), "sha256": hashlib.sha256(b"configured").hexdigest()}}}), encoding="utf-8")
        return archive

    def seeded(self, sha=None, completed=True, profile="unified-pc"):
        archive = self.root / "hps" / profile / "baseline.zap21"
        archive.parent.mkdir(parents=True, exist_ok=True)
        archive.write_bytes(b"seeded")
        report = self.root / "hps" / "native-report.json"
        report.write_text(json.dumps({"completed": completed, "fixtureMode": "positive", "profiles": [{"profile": profile, "archiveRetrievalVerified": True,
            "connectionFixture": {"links": [{"isValid": True}]}, "archive": {"path": str(archive), "sha256": sha or hashlib.sha256(b"seeded").hexdigest()}}]}), encoding="utf-8")
        return report

    def run_route(self, profile, family, kind=None, **fake):
        host = FakeHost(self.root, family, profile, **fake)
        arguments = types.SimpleNamespace(profile=profile, base=self.root, fixture=None, seeded_report=None, kind=kind)
        if profile in route.SEEDED:
            arguments.seeded_report = self.seeded(profile=profile)
        else:
            arguments.fixture = self.configured(profile)
        with mock.patch("builtins.print"):
            return route.run(arguments, host.connect, lambda pid: types.SimpleNamespace(wait_for_exit=lambda timeout: {"processId": pid, "processExitVerified": True}))

    def test_classic_route_passes_against_a_faithful_host(self):
        report = self.run_route("classic-comfort", "classic")
        self.assertEqual([], report["failures"])
        self.assertTrue(report["completed"])
        self.assertEqual(11, sum(c.startswith("Refused before any write") for c in report["checks"]))
        self.assertEqual(6, sum(c.startswith("Applied with every stage") for c in report["checks"]))
        self.assertEqual(6, len(report["replays"]))
        self.assertIn("A production Host admits the kind with admission production", report["checks"])

    def test_classic_route_runs_on_the_seeded_rt_advanced_and_basic_baselines(self):
        for profile, refusals in (("classic-rt-advanced", 11), ("classic-basic", 10)):
            with self.subTest(profile=profile):
                shutil.rmtree(self.root / "hps", ignore_errors=True)
                report = self.run_route(profile, "classic", errors=1)
                self.assertEqual([], report["failures"])
                self.assertEqual("seeded", report["fixture"]["mode"])
                self.assertEqual(refusals, sum(c.startswith("Refused before any write") for c in report["checks"]))
                self.assertEqual(6, sum(c.startswith("Gate admits this kind and profile (qualification)") for c in report["checks"]))
                self.assertIn("A production Host refuses the kind before its production admission", report["checks"])

    def test_unified_routes_pass_on_configured_and_seeded_fixtures(self):
        for profile, errors in (("unified-comfort", 0), ("unified-pc", 2)):
            with self.subTest(profile=profile):
                shutil.rmtree(self.root / "hps", ignore_errors=True)
                shutil.rmtree(self.root / "prep", ignore_errors=True)
                report = self.run_route(profile, "unified", errors=errors)
                self.assertEqual([], report["failures"])
                self.assertEqual("seeded" if profile == "unified-pc" else "configured", report["fixture"]["mode"])
                self.assertEqual(9, sum(c.startswith("Refused before any write") for c in report["checks"]))
                self.assertEqual(3, sum(c.startswith("Independent readback") for c in report["checks"]))

    def test_an_unrelated_change_and_a_missing_readback_fail(self):
        def leak(world):
            hmi = sorted(world)[1]
            world[hmi]["exports"][hmi + "/screens/Overview_B"] = screen("Overview_B", back="1, 1, 1")
        failures = self.run_route("classic-comfort", "classic", leak=leak)["failures"]
        self.assertTrue(any(f.startswith("hmi_inspect of every HMI unchanged outside the intended delta: BackColor of HMI_RT_1") for f in failures), failures)
        failures = self.run_route_fresh("classic-comfort", "classic", stale_readback=True)
        self.assertTrue(any(f.startswith("Independent readback of the target: BackColor of HMI_RT_1") for f in failures), failures)

    def run_route_fresh(self, profile, family, **fake):
        shutil.rmtree(self.root / "prep", ignore_errors=True)
        return self.run_route(profile, family, **fake)["failures"]

    def test_screen_route_passes_on_both_unified_profiles(self):
        for profile, errors in (("unified-comfort", 0), ("unified-pc", 2)):
            with self.subTest(profile=profile):
                shutil.rmtree(self.root / "hps", ignore_errors=True)
                shutil.rmtree(self.root / "prep", ignore_errors=True)
                report = self.run_route(profile, "unified", kind="hmi_screen_create", errors=errors)
                self.assertEqual([], report["failures"])
                self.assertEqual("hmi_screen_create", report["kind"])
                self.assertEqual(9, sum(c.startswith("Refused before any write") for c in report["checks"]))
                self.assertEqual(3, sum(c.startswith("Independent readback") for c in report["checks"]))
                self.assertEqual(3, sum(c.startswith("Gate admits this kind and profile (production)") for c in report["checks"]))
                self.assertIn("A production Host admits the kind with admission production", report["checks"])

    def test_binding_route_creates_its_items_then_binds_three_tags(self):
        for profile, errors in (("unified-comfort", 0), ("unified-pc", 2)):
            with self.subTest(profile=profile):
                shutil.rmtree(self.root / "hps", ignore_errors=True)
                shutil.rmtree(self.root / "prep", ignore_errors=True)
                report = self.run_route(profile, "unified", kind="hmi_dynamization_create", errors=errors)
                self.assertEqual([], report["failures"])
                self.assertEqual(4, sum(c.startswith("Setup applied") for c in report["checks"]))
                self.assertEqual(7, sum(c.startswith("Refused before any write") for c in report["checks"]))
                self.assertEqual(3, sum(c.startswith("Independent readback") for c in report["checks"]))
                self.assertIn("A production Host refuses the kind before its production admission", report["checks"])

    def test_a_binding_that_changes_another_item_field_fails(self):
        def leak(world):
            for hmi in world:
                for row in world[hmi]["items"].values():
                    if row.get("dynamizations"):
                        row["properties"]["Bound"] = True
        failures = self.run_route("unified-comfort", "unified", kind="hmi_dynamization_create", leak=leak)["failures"]
        self.assertTrue(any("unchanged outside the intended delta: ProcessValue of HmiIOField QualificationField on HMI_RT_1" in f for f in failures), failures)

    def test_handler_route_adds_scripts_to_a_screen_a_rectangle_and_a_button(self):
        for profile, errors in (("unified-comfort", 0), ("unified-pc", 2)):
            with self.subTest(profile=profile):
                shutil.rmtree(self.root / "hps", ignore_errors=True)
                shutil.rmtree(self.root / "prep", ignore_errors=True)
                report = self.run_route(profile, "unified", kind="hmi_event_handler_create", errors=errors)
                self.assertEqual([], report["failures"])
                self.assertEqual(1, sum(c.startswith("Setup applied") for c in report["checks"]))
                self.assertEqual(9, sum(c.startswith("Refused before any write") for c in report["checks"]))
                self.assertIn("Refused before any write: an event that already has a handler (Loaded) (HmiNameConflict)", report["checks"])
                self.assertEqual(["Cleared handler of screen Overview_A on HMI_RT_1", "Tapped handler of item RepeatedItem on HMI_RT_1", "Tapped handler of item QualificationButton on HMI_RT_2"],
                                 [r["label"] for r in report["replays"]])

    def test_a_handler_that_changes_its_script_fails_the_readback(self):
        def leak(world):
            for hmi in world:
                for owner in list(world[hmi]["screens"].values()) + list(world[hmi]["items"].values()):
                    for handler in owner.get("eventHandlers", []):
                        handler["script"]["scriptCode"] = handler["script"]["scriptCode"].strip()
        failures = self.run_route("unified-comfort", "unified", kind="hmi_event_handler_create", leak=leak)["failures"]
        self.assertTrue(any(f.startswith("Independent readback of the target: Cleared handler of screen Overview_A") for f in failures), failures)

    def test_a_screen_row_change_outside_the_created_item_fails(self):
        def leak(world):
            for hmi in world:
                world[hmi]["screens"][hmi + "/screens/Overview_B"]["properties"]["Items"] = len(world[hmi]["items"])
        failures = self.run_route("unified-comfort", "unified", kind="hmi_screen_create", leak=leak)["failures"]
        self.assertTrue(any("unchanged outside the intended delta: HmiButton on HMI_RT_2 Overview_B" in f for f in failures), failures)
        shutil.rmtree(self.root / "prep")
        with self.assertRaises(AssertionError):
            self.run_route("classic-comfort", "classic", kind="hmi_screen_create")

    def test_lifecycle_lock_release_and_compile_scope_are_gated(self):
        report = self.run_route("unified-comfort", "unified")
        for label in ("Normal lifecycle of the qualification Host", "Normal lifecycle of the production Host", "Independent lock release",
                      "Compiler scope: the baseline and the apply compile exactly owningDeviceHardwareAndSoftware HmiDevice_B: root tag on HMI_RT_2"):
            self.assertTrue(any(c.startswith(label) for c in report["checks"]), label)
        self.assertEqual(["qualification", "production"], [l["host"] for l in report["lifetimes"]])
        cases = (({"receipt": {"forcedHostStop": True}}, "Normal lifecycle of the qualification Host"),
                 ({"receipt": {"workerExits": []}}, "Normal lifecycle of the production Host"),
                 ({"read_only": True}, "Independent lock release"),
                 ({"scope": lambda hmi: hmi.replace("HmiDevice_A", "HmiDevice_B")}, "Compiler scope: the baseline and the apply compile exactly owningDeviceHardwareAndSoftware HmiDevice_A"))
        for fake, label in cases:
            with self.subTest(label=label):
                shutil.rmtree(self.root / "prep", ignore_errors=True)
                failures = self.run_route("unified-comfort", "unified", **fake)["failures"]
                self.assertTrue(any(f.startswith(label) for f in failures), failures)

    def test_a_unified_table_row_change_is_outside_the_delta(self):
        def leak(world):
            for hmi in world:
                world[hmi]["tags"][hmi + "/tagTables/Default%20tag%20table"]["properties"]["Count"] = len(world[hmi]["tags"])
        failures = self.run_route("unified-comfort", "unified", leak=leak)["failures"]
        self.assertTrue(any("unchanged outside the intended delta: root tag on HMI_RT_1" in f for f in failures), failures)
        # A compile error the configured fixture does not have fails the clean-compile rule.
        shutil.rmtree(self.root / "prep")
        failures = self.run_route("unified-comfort", "unified", errors=1)["failures"]
        self.assertTrue(any(f.startswith("Compiler errors and warnings as the baseline compile found them (none)") for f in failures), failures)


class GateRouteHelperTests(unittest.TestCase):
    def setUp(self):
        self.root = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.root)

    def test_seeded_fixture_is_unified_pc_only_and_hash_checked(self):
        archive = self.root / "baseline.zap21"
        archive.write_bytes(b"x")
        def report(**row):
            path = self.root / "native-report.json"
            path.write_text(json.dumps({"completed": True, "fixtureMode": "positive", "profiles": [{"profile": "unified-pc", "archiveRetrievalVerified": True,
                "connectionFixture": {"links": [{"isValid": True}]}, "archive": {"path": str(archive), "sha256": hashlib.sha256(b"x").hexdigest()}, **row}]}), encoding="utf-8")
            return path
        found = route.resolve_fixture("unified-pc", seeded_report=report())
        self.assertEqual(("seeded", str(archive.resolve())), (found["mode"], found["path"]))
        for bad in ({"archive": {"path": str(archive), "sha256": "0" * 64}}, {"archiveRetrievalVerified": False}, {"connectionFixture": {"links": [{"isValid": False}]}}):
            with self.assertRaises(AssertionError):
                route.resolve_fixture("unified-pc", seeded_report=report(**bad))
        for profile in ("unified-comfort", "classic-comfort"):
            with self.assertRaises(AssertionError):
                route.resolve_fixture(profile, seeded_report=report())
        with self.assertRaises(AssertionError):
            route.resolve_fixture("unified-pc")
        with self.assertRaises(AssertionError):
            route.resolve_fixture("unified-pc", fixture=archive, seeded_report=report())

    def test_readback_rule_ignores_document_info_ids_and_formatting_only(self):
        a = screen("Overview_A")
        b = re.sub(r"\s*\n\s*", "", a.replace("2026-11-01T00:00:00Z", "later")).replace('ID="1"', 'ID="7"')
        self.assertEqual(route.normalized(a), route.normalized(b))
        self.assertNotEqual(route.normalized(a), route.normalized(screen("Overview_A", back="1, 2, 3")))
        self.assertNotEqual(route.normalized(a), route.normalized(a.replace('<Name>Rectangle</Name>', '<Name>Other</Name>')))

    def test_root_attribute_edit_changes_only_the_root_scalar(self):
        edited = route.set_root_attribute(screen("Overview_A"), "BackColor", "1, 2, 3")
        self.assertEqual("1, 2, 3", route.root_attribute(edited, "BackColor"))
        self.assertIn("<BackColor>128, 128, 128</BackColor>", edited)
        self.assertEqual(route.normalized(edited), route.normalized(screen("Overview_A", back="1, 2, 3")))
        with self.assertRaises(AssertionError):
            route.set_root_attribute(screen("Overview_A"), "BackColor", "204, 204, 204")
        # An equal element before the root's (here in DocumentInfo) would be edited instead; the edit refuses.
        shadowed = screen("Overview_A").replace("<Created>", "<BackColor>204, 204, 204</BackColor><Created>")
        with self.assertRaises(AssertionError):
            route.set_root_attribute(shadowed, "BackColor", "1, 2, 3")

    def test_added_tag_is_a_unique_copy_with_fresh_ids(self):
        edited = route.add_tag(TABLE.format(created="x"), "SharedValueAdded")
        tags = list(route.root_object(edited).iter("Hmi.Tag.Tag"))
        self.assertEqual(["SharedValue", "SharedValueAdded"], [t.findtext("AttributeList/Name") for t in tags])
        ids = re.findall(r' ID="(\d+)"', edited)
        self.assertEqual(len(ids), len(set(ids)))
        with self.assertRaises(AssertionError):
            route.add_tag(edited.replace("SharedValueAdded", "Other"), "SharedValue")

    def test_refusal_documents(self):
        self.assertIn("<!DOCTYPE", route.with_dtd(screen("Overview_A")))
        self.assertTrue(route.with_dtd(screen("Overview_A")).startswith('<?xml version="1.0" encoding="utf-8"?><!DOCTYPE'))
        with self.assertRaises(AssertionError):
            route.root_object(route.with_second_root(screen("Overview_A")))

    def test_intended_delta(self):
        classic = ["screens /h/screens/A", "items /h/screens/A", "items /h/screens/A /h/screens/A/items/R", "tags /h/tagTables/T/tags/N"]
        self.assertEqual(["tags /h/tagTables/T/tags/N"], route.outside(classic, "classic", ["/h/screens/A"]))
        self.assertEqual(["screens /h/screens/AB", "tags"], route.outside(["screens /h/screens/AB", "tags"], "classic", ["/h/screens/A"]))
        unified = ["tags /h/t/tags/New", "model /h/t/tags/New", "model /h/t/tags/New/x", "tags /h/t", "connections /h/t/tags/New", "model /h/t/tags/Newer"]
        self.assertEqual(["tags /h/t", "connections /h/t/tags/New", "model /h/t/tags/Newer"], route.outside(unified, "unified", ["/h/t/tags/New"]))


if __name__ == "__main__":
    unittest.main()
