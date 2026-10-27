"""Prepare-only native probe of the combined PLC/UDT/HMI recipe on a `plchmi` HmiSeeder fixture.

The seeder (raw Openness) authors MotorStatus 1.0.0/1.0.1, MotorDrive 1.0.0, MotorDB and the
Unified tags bound to MotorDB.status.*. Through the production bridge this probe only prepares:
an install plan into PLC_B (apply must stay refused), and a field-changing update plan for PLC_A
that must be refused with an exact impact report. Inventories must be unchanged afterwards.
"""
import json
from urllib.parse import unquote


def _require(condition, message):
    if not condition:
        raise AssertionError(message)


def _call(client, name, arguments):
    raw = client.request("tools/call", {"name": name, "arguments": arguments})
    if raw.get("structuredContent"):
        return raw.get("isError") is True, raw["structuredContent"]
    text = "\n".join(i["text"] for i in raw.get("content", []) if i.get("type") == "text")
    try:
        return raw.get("isError") is True, json.loads(text)
    except ValueError:  # SDK argument errors are plain text
        return raw.get("isError") is True, {"error": {"code": None, "message": text}}


def _pin(version):
    return {"typeGuid": version["typeGuid"], "versionGuid": version["versionGuid"]}


def probe_plc_hmi_recipe(client, profile, inspections, proofs):
    fixture = profile.get("plcHmiFixture") or {}
    _require(fixture.get("status") == "created", "The native report has no created plchmi fixture")
    _require(len(proofs) == 2 and all("declaration" in p for p in proofs), "Recipe probe needs both passed connection/declaration proofs")
    a = next(p for p in proofs if p["hmiSoftware"] == fixture["hmiA"]); b = next(p for p in proofs if p is not a)
    owner = lambda proof: next(e["target"] for e in inspections if e["target"]["name"] == proof["hmiSoftware"] and e["section"] == "tags")
    rows = lambda target, section: [i for e in inspections if e["target"]["objectPath"] == target["objectPath"] and e["section"] == section for i in e["result"]["items"]]
    hmi_b = owner(b)
    connection_b = next(r["name"] for r in rows(hmi_b, "connections") if r["identity"]["objectPath"] == b["connection"]["sourcePath"])
    table_b = unquote(b["declaration"]["sourcePath"].split("/tagTables/", 1)[1].split("/tags/", 1)[0])
    screen_b = rows(hmi_b, "screens")[0]["identity"]["objectPath"]
    status = client.tool("tia_session_status")
    _require(len(status.get("projects", [])) == 1, "Exactly one retrieved fixture project must be open")
    project = status["projects"][0]
    project_name = project.get("name") or project["path"].replace("\\", "/").rsplit("/", 1)[-1].rsplit(".", 1)[0]
    library = client.tool("library_open", {"request": {"kind": "project"}})["libraryId"]
    source = {"libraryId": library, "revision": next(x for x in client.tool("library_list")["libraries"] if x["libraryId"] == library)["revision"]}
    snapshot = lambda: (client.tool("library_query", {"request": {"libraryId": library, "maxItems": 200}})["items"],
                        [sorted(i["identity"]["objectPath"] for i in client.tool("hmi_inspect", {"request": {"schemaVersion": 1, "section": "tags", "target": owner(p), "limit": 200}})["items"]) for p in proofs])
    before = snapshot()
    outcomes = {"plc": {"errors": 0, "maxWarnings": 0}, "hmi": {"errors": 0, "maxWarnings": 0}}
    plc_b = b["plcOwner"]["objectPath"]
    install = {"schemaVersion": 1, "action": "plc_hmi_component_plan", "planOnly": True, "source": source,
               "selection": [_pin({**fixture["motorDrive"], **fixture["motorDrive"]["v1"]})],
               "target": {"projectName": project_name, "plcPath": plc_b},
               "integration": {"dependencyPins": [_pin({**fixture["motorStatus"], **fixture["motorStatus"]["v1"]})],
                               "instanceDb": {"name": "MotorDrive_1", "numberPolicy": "firstAvailable"},
                               "caller": {"mode": "createStandaloneFc", "name": "MotorCaller", "language": "FBD", "numberPolicy": "firstAvailable"},
                               "mappings": [{"parameter": p, "direction": d, "dataType": "Bool", "tagPath": plc_b + "/tags/QualificationTags/tags/" + t}
                                            for p, d, t in (("Enable", "Input", "MotorEnable"), ("Active", "Output", "MotorActive"))]},
               "hmi": {"family": "unified", "profile": profile["profile"], "hmiPath": hmi_b["hmiPath"], "connection": connection_b, "tagTable": table_b,
                       "tags": [{"name": "MotorSpeedB", "dataType": "Real", "source": {"kind": "instanceMember", "members": ["status", "speed"]}},
                                {"name": "MotorEnableB", "dataType": "Bool", "source": {"kind": "plcTag", "tagPath": plc_b + "/tags/QualificationTags/tags/MotorEnable"}}],
                       "screenItems": [{"screenPath": screen_b, "itemName": "MotorSpeedField", "rectangle": {"left": 10, "top": 10, "width": 100, "height": 20}, "tag": "MotorSpeedB"}]},
               "expectedCompilerOutcomes": outcomes}
    error, prepared = _call(client, "change_prepare", {"kind": "library_integrate", "payload": install})
    _require(not error, "Combined install plan must prepare: " + json.dumps(prepared, ensure_ascii=False)[:2000])
    plan = prepared["preview"]["integrationPlan"]
    codes = {b["code"] for b in plan["blockers"]}
    _require(plan["status"] == "planned_only" and plan["applySupported"] is False
             and {"HmiMemberMappingUnqualified", "HmiScreenItemCreationUnavailable", "HmiPreservationGateClosed"} <= codes
             and [s["kind"] for s in plan["steps"]] == ["library_instantiate", "graphical_typed_edit", "hmi_engineering"]
             and plan["hmi"]["tags"][0]["plcSymbol"] == "MotorDrive_1.status.speed"
             and plan["hmi"]["target"]["connection"]["plcPath"] == plc_b, "Combined install plan differs from the native fixture")
    refused, applied = _call(client, "change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": "plchmi-probe-install",
                                              "checkpointSave": False, "compile": False, "save": False})
    _require(refused and applied.get("error", {}).get("code") == "PlcIntegrationNotQualified", "Combined apply must stay refused: " + json.dumps(applied, ensure_ascii=False)[:2000])
    update = {"schemaVersion": 1, "action": "plc_hmi_update_plan", "planOnly": True, "source": source,
              "selection": [_pin({**fixture["motorStatus"], **fixture["motorStatus"]["v2"]})],
              "target": {"projectName": project_name, "plcPath": a["plcOwner"]["objectPath"]},
              "hmi": {"family": "unified", "profile": profile["profile"]}, "expectedCompilerOutcomes": outcomes}
    error, broken = _call(client, "change_prepare", {"kind": "library_integrate", "payload": update})
    report = broken.get("error", {}).get("details", {})
    rows_by_tag = {r["tagName"]: r for r in report.get("bindings", [])}
    speed, running = rows_by_tag.get("MotorSpeed", {}), rows_by_tag.get("MotorRunning", {})
    _require(error and broken["error"]["code"] == "HmiBindingWouldBreak" and report.get("breakingCount") == 1
             and speed.get("status") == "typeChanged" and speed.get("beforeDataType") == "Real" and speed.get("afterDataType") == "LReal"
             and running.get("status") == "unchanged" and report.get("unresolved") == []
             and any(c["itemPath"].endswith("/MotorSpeedConsumer") and c["property"] == "Left" for c in speed.get("consumers", [])),
             "Field-changing update must be refused with the exact impact report: " + json.dumps(broken, ensure_ascii=False)[:4000])
    classic = dict(update, hmi={"family": "classic", "profile": profile["profile"]})
    error, wrong = _call(client, "change_prepare", {"kind": "library_integrate", "payload": classic})
    _require(error and wrong.get("error", {}).get("code") == "HmiFamilyNotQualified", "Classic combined recipe must be refused before native work")
    _require(snapshot() == before, "Plan-only probe changed the project library or HMI tag inventories")
    return {"status": "passed", "reason": "Prepare-only: combined install plan with blocked HMI steps and refused apply; field-changing UDT update refused with exact binding/consumer impact; Classic refused; library and HMI tag inventories unchanged. No apply, compile or save.",
            "installPlan": plan, "applyRefusal": applied.get("error"), "updateRefusal": broken.get("error"), "applyQualified": False}
