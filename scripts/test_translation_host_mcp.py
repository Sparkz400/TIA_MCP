"""Actual production MCP, exclusively offline tools and deliberately absent native workers."""
import argparse
import base64
import hashlib
import io
import json
import pathlib
import zipfile
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT
from jsonschema import Draft202012Validator
from referencing import Registry, Resource


TOOLS = {
    "translation_capture", "translation_inspect", "translation_prepare", "translation_review_page",
    "translation_approve", "translation_candidate", "translation_reconcile", "translation_reopen",
    "translation_list", "translation_artifact_read",
}
ALLOWED = TOOLS | {"artifact_upload_begin", "artifact_upload_chunk", "artifact_upload_commit"}


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    output = args.output.resolve()
    require(not output.exists(), "Use a new isolated output directory")
    output.mkdir(parents=True)
    missing = output / "intentionally-absent-native-worker.exe"
    require(not missing.exists(), "Worker path must be absent")
    report = {"completed": False, "nativeExecuted": False, "nativeImportAllowed": False, "steps": [], "hostExits": [],
              "harnessSha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
              "managedBinaryHashes": {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest()
                                      for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll', 'TiaMcp.Translations.dll')}}

    def start():
        nonlocal client
        client = Client(args.host.resolve(), 30, ["--worker", str(missing), "--sim-worker", str(missing),
                                                 "--artifacts", str(output / "host-artifacts")])
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                      "clientInfo": {"name": "translation-offline-contract", "version": "1"}})
        client.notify("notifications/initialized")
        return client

    def call(client, name, arguments):
        require(name in ALLOWED, "Test attempted a non-offline tool: " + name)
        return client.tool(name, arguments)

    def upload(client, name, data):
        status = call(client, "artifact_upload_begin", {"fileName": name, "expectedBytes": len(data),
                                                       "sha256": hashlib.sha256(data).hexdigest()})
        for offset in range(0, len(data), 65536):
            call(client, "artifact_upload_chunk", {"uploadId": status["uploadId"], "offset": offset,
                                                   "data": base64.b64encode(data[offset:offset + 65536]).decode("ascii")})
        return call(client, "artifact_upload_commit", {"uploadId": status["uploadId"]})["artifactRef"]

    def artifact(record, name):
        return next(a["artifactRef"] for a in record["artifacts"] if a["artifactRef"].endswith(":" + name))

    def download(client, reference):
        data = bytearray()
        while True:
            page = call(client, "translation_artifact_read", {"artifactRef": reference, "offset": len(data)})
            data.extend(base64.b64decode(page["data"], validate=True))
            if page["complete"]:
                require(hashlib.sha256(data).hexdigest() == page["sha256"], "Artifact hash differs")
                return bytes(data)

    client = None
    try:
        client = start()
        tools = {t["name"]: t for t in client.request("tools/list")["tools"]}
        require(TOOLS <= tools.keys(), "Translation tool registration incomplete")
        require(not any(name.startswith('qualification_translation_') for name in tools), "Production Host exposed native qualification tools")
        for name in TOOLS:
            schema = tools[name]["inputSchema"]
            require(schema["type"] == "object" and "path" not in schema.get("properties", {}), "Unexpected path input")
            require(tools[name]["annotations"]["openWorldHint"] is False, "Tool must be offline")
        report["steps"].append("All 10 production translation tools registered with offline schemas")

        fixture = ROOT / "fixtures/translations/synthetic-bilingual"
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED) as workbook:
            for path in sorted(fixture.rglob("*")):
                if path.is_file() and path.suffix != ".json":
                    data = path.read_bytes()
                    if path.name == 'sheet1.xml':
                        data = data.replace(b'<row r="3">', b'<row r="0003">').replace(
                            b'</m:opaque>', b'<row r="2" vendor="keep"/></m:opaque>')
                    workbook.writestr(path.relative_to(fixture).as_posix(), data)
        original = stream.getvalue()
        project = {"object": {"projectPath": "D:\\synthetic\\McpTranslation.ap21", "plcPath": None, "scopePath": None,
                              "objectPath": "/project/McpTranslation", "kind": "project"},
                   "snapshot": {"observationId": "mcp-offline-1", "sourceSha256": "a" * 64, "tiaVersion": 21},
                   "contentSha256": "b" * 64}
        capture = call(client, "translation_capture", {
            "workbookUploadRef": upload(client, "original.xlsx", original),
            "profileUploadRef": upload(client, "profile.json", (fixture / "profile.json").read_bytes()),
            "projectUploadRef": upload(client, "project.json", json.dumps(project).encode()),
        })
        inspection = call(client, "translation_inspect", {"captureRef": capture["recordRef"], "pageSize": 3})
        row = next(r for r in inspection["rows"] if r["nativeKey"] == "K-002")
        require(row["target"]["state"] == "missing", "Missing cell lost")
        resumed = call(client, "translation_inspect", {"captureRef": capture["recordRef"], "pageSize": 3,
                                                        "cursor": inspection["nextCursor"]})
        require(resumed["offset"] == 3, "MCP cursor did not roundtrip")
        batch = {"edits": [{"nativeKey": row["nativeKey"], "culture": "de-DE", "kind": "set", "value": "Kühlung 東京",
                             "expectedSourceSha256": row["sourceSha256"], "expectedTargetSha256": row["targetSha256"],
                             "approved": False, "reviewer": None, "rationale": "MCP test"}], "options": None}
        review = call(client, "translation_prepare", {"captureRef": capture["recordRef"],
                                                       "proposalsArtifactRef": upload(client, "proposals.json", json.dumps(batch, ensure_ascii=False).encode())})
        page = call(client, "translation_review_page", {"recordRef": review["recordRef"]})
        require(page["rows"][0]["after"] == "Kühlung 東京" and not page["rows"][0]["edit"]["approved"], "Review changed suggestions")
        blocked = client.request("tools/call", {"name": "translation_candidate", "arguments": {
            "reviewRef": review["recordRef"], "exactReviewSha256": review["metadata"]["reviewSha256"], "approvalSha256": "0" * 64}})
        require(blocked.get("isError") and blocked["structuredContent"]["error"]["code"] == "ExplicitTranslationApprovalRequired", "Unapproved candidate accepted")
        approval = call(client, "translation_approve", {"reviewRef": review["recordRef"], "exactReviewSha256": review["metadata"]["reviewSha256"],
                                                        "reviewer": "MCP test reviewer", "approveEveryChange": True})
        candidate = call(client, "translation_candidate", {"reviewRef": review["recordRef"], "exactReviewSha256": review["metadata"]["reviewSha256"],
                                                           "approvalSha256": approval["recordSha256"]})
        require(candidate["state"] == "complete" and not candidate["nativeImportAllowed"], "Candidate state incorrect")
        result = download(client, artifact(candidate, "candidate.xlsx"))
        with zipfile.ZipFile(io.BytesIO(original)) as before, zipfile.ZipFile(io.BytesIO(result)) as after:
            require(set(before.namelist()) == set(after.namelist()), "Package part set changed")
            for name in before.namelist():
                if name != "xl/worksheets/sheet1.xml":
                    require(before.read(name) == after.read(name), "Unrelated part changed: " + name)
            ns = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
            old_sheet = ET.fromstring(before.read('xl/worksheets/sheet1.xml'))
            new_sheet = ET.fromstring(after.read('xl/worksheets/sheet1.xml'))
            require(ET.tostring(old_sheet.find(ns+'extLst')) == ET.tostring(new_sheet.find(ns+'extLst')), 'Opaque row-like extension changed')
            require(new_sheet.find(ns+'sheetData/'+ns+'row[@r="0003"]/'+ns+'c[@r="C3"]') is not None, 'Cell was not added to the exact original row')
        report["steps"].append("Committed uploads, capture, cursor, unapproved review, explicit approval and preserving candidate through actual MCP")
        report['hostExits'].append(client.close())
        require(client.process.returncode == 0, "First Host did not shut down cleanly")
        client = start()
        reopened = call(client, "translation_reopen", {"recordRef": review["recordRef"]})
        require(reopened["candidate"]["recordSha256"] == candidate["recordSha256"], "Restart lost exact candidate")
        require(download(client, artifact(reopened["candidate"], "candidate.xlsx")) == result, "Restart changed candidate bytes")
        listed = call(client, "translation_list", {"kind": "review"})
        require(listed["rows"][0]["recordRef"] == review["recordRef"], "Restart discovery failed")
        reconciliation = call(client, "translation_reconcile", {"baselineCaptureRef": capture["recordRef"],
            "editedWorkbookArtifactRef": artifact(candidate, "candidate.xlsx"), "currentCaptureRef": capture["recordRef"]})
        require(not reconciliation["metadata"]["approved"] and reconciliation["metadata"]["proposals"] == 1, "Reconciliation reused approval")
        rows = call(client, "translation_review_page", {"recordRef": reconciliation["recordRef"], "section": "rows"})
        require(rows["rows"][0]["status"] == "proposed", "Reconciliation page incorrect")
        report["steps"].append("Real Host restart, exact artifact retrieval and fresh unapproved reconciliation")
        def changed_workbook(old, new, source_bytes=original):
            changed = io.BytesIO()
            with zipfile.ZipFile(io.BytesIO(source_bytes)) as source, zipfile.ZipFile(changed, 'w', zipfile.ZIP_DEFLATED) as target:
                for name in source.namelist():
                    data = source.read(name)
                    if name == 'xl/worksheets/sheet1.xml': require(old in data, 'Adversarial worksheet marker missing')
                    target.writestr(name, data.replace(old, new) if name == 'xl/worksheets/sheet1.xml' else data)
            return changed.getvalue()

        profile_ref = upload(client, 'profile.json', (fixture / 'profile.json').read_bytes())
        project_ref = upload(client, 'project.json', json.dumps(project).encode())
        for old, new in ((b'</sheetData>', b'</sheetData><sheetData/>'),
                         (b'<t>Neu erstelltes Objekt</t>', b'<t><m:opaque>Neu</m:opaque> erstelltes Objekt</t>')):
            refused = client.request('tools/call', {'name':'translation_capture', 'arguments':{
                'workbookUploadRef':upload(client, 'unsupported.xlsx', changed_workbook(old,new)),
                'profileUploadRef':profile_ref, 'projectUploadRef':project_ref}})
            require(refused.get('isError') and refused['structuredContent']['error']['code']=='UnsupportedWorkbookProfile', 'Unsupported workbook was admitted')
        opaque_capture = call(client, 'translation_capture', {
            'workbookUploadRef':upload(client, 'annotated.xlsx', changed_workbook(b'<t>Neu erstelltes Objekt</t>', b'<t><!--retain-->Neu erstelltes Objekt</t>')),
            'profileUploadRef':profile_ref, 'projectUploadRef':project_ref})
        opaque_rows = call(client, 'translation_inspect', {'captureRef':opaque_capture['recordRef']})['rows']
        opaque_row = next(row for row in opaque_rows if row['nativeKey']=='K-008-current')
        blocked_batch = {'edits':[dict(nativeKey=opaque_row['nativeKey'],culture='de-DE',kind='set',value='Neu',
            expectedSourceSha256=opaque_row['sourceSha256'],expectedTargetSha256=opaque_row['targetSha256'],approved=False)]}
        blocked_review = call(client, 'translation_prepare', {'captureRef':opaque_capture['recordRef'],
            'proposalsArtifactRef':upload(client,'proposals.json',json.dumps(blocked_batch).encode())})
        issues = call(client,'translation_review_page',{'recordRef':blocked_review['recordRef'],'section':'issues'})['rows']
        require(any(issue['code']=='UnsupportedWorkbookProfile' for issue in issues), 'Opaque edit lacks an explicit preservation issue')
        refused = client.request('tools/call', {'name':'translation_approve','arguments':{
            'reviewRef':blocked_review['recordRef'],'exactReviewSha256':blocked_review['metadata']['reviewSha256'],
            'reviewer':'MCP test reviewer','approveEveryChange':True}})
        require(refused.get('isError') and refused['structuredContent']['error']['code']=='TranslationReviewBlocked', 'Caller approval bypassed opaque preservation')
        report['steps'].append('Noncanonical row and opaque extension preservation; malformed capture and annotated-cell approval refusal')
        marker = b'<c r="C3" t="inlineStr">'
        for replacement in (b'<c r="C3" t="inlineStr" s="999">', marker+b'<!--unreviewed-->'):
            refused = client.request('tools/call', {'name':'translation_reconcile','arguments':{
                'baselineCaptureRef':capture['recordRef'],'currentCaptureRef':capture['recordRef'],
                'editedWorkbookArtifactRef':upload(client,'edited.xlsx',changed_workbook(marker,replacement,result))}})
            require(refused.get('isError') and refused['structuredContent']['error']['code']=='TranslationPreservationFailure', 'Reconciliation accepted unreviewed new-cell metadata')
        report['steps'].append('External edited workbook cannot add unreviewed attributes or annotations to a newly created cell')
        marker_text = "###Inherited from type###"
        alarm_profile = json.loads((fixture / 'profile.json').read_text(encoding='utf-8'))
        alarm_profile['rows'][0]['alarmTextPolicy'] = 'instance-inheritance-v1'
        for target in alarm_profile['rows'][0]['targets']:
            target['maxUtf16Length'] = None
        alarm_original = changed_workbook(b'<c r="B2" t="inlineStr"><is><t>Ready</t></is></c>',
            ('<c r="B2" t="inlineStr"><is><t>'+marker_text+'</t></is></c>').encode())
        alarm_original = changed_workbook('Prêt — untouched'.encode(), marker_text.encode(), alarm_original)
        registry = Registry()
        for path in (ROOT / 'contracts').glob('*.schema.json'):
            schema = json.loads(path.read_text(encoding='utf-8'))
            if '$id' in schema and '$schema' in schema:
                resource = Resource.from_contents(schema)
                registry = registry.with_resource(schema['$id'], resource).with_resource('https://tia-mcp.local/contracts/'+path.name, resource)
        def validate(name, value):
            schema = json.loads((ROOT / 'contracts' / name).read_text(encoding='utf-8'))
            Draft202012Validator(schema, registry=registry).validate(value)
        validate('translation-profile.v1.schema.json', alarm_profile)
        alarm_capture = call(client,'translation_capture',{
            'workbookUploadRef':upload(client,'alarm.xlsx',alarm_original),
            'profileUploadRef':upload(client,'alarm-profile.json',json.dumps(alarm_profile).encode()),
            'projectUploadRef':project_ref})
        alarm_page = call(client,'translation_inspect',{'captureRef':alarm_capture['recordRef'],'pageSize':1})
        alarm_row = alarm_page['rows'][0]
        require(alarm_row['alarmInheritance']['status']=='mixed', 'Mixed mapped inheritance hidden')
        require(alarm_row['alarmInheritance']['mappedFields']==3, 'Third language omitted')
        # This schema describes the library page; Host adds these two retained handles.
        validate('translation-inspection-page.v1.schema.json', {k:v for k,v in alarm_page.items() if k not in ('captureRef','profileArtifactRef')})
        def alarm_prepare(value):
            batch = {'edits':[dict(nativeKey='K-001',culture='de-DE',kind='set',value=value,
                expectedSourceSha256=alarm_row['sourceSha256'],expectedTargetSha256=alarm_row['targetSha256'],approved=False)]}
            return call(client,'translation_prepare',{'captureRef':alarm_capture['recordRef'],
                'proposalsArtifactRef':upload(client,'alarm-proposals.json',json.dumps(batch).encode())})
        bad_alarm = alarm_prepare('Still literal')
        bad_issues = call(client,'translation_review_page',{'recordRef':bad_alarm['recordRef'],'section':'issues'})['rows']
        require(any(i['code']=='AlarmInheritanceMixed' for i in bad_issues), 'Mixed inheritance did not block')
        refused = client.request('tools/call',{'name':'translation_approve','arguments':{
            'reviewRef':bad_alarm['recordRef'],'exactReviewSha256':bad_alarm['metadata']['reviewSha256'],
            'reviewer':'MCP test reviewer','approveEveryChange':True}})
        require(refused.get('isError') and refused['structuredContent']['error']['code']=='TranslationReviewBlocked', 'Approval bypassed inheritance consistency')
        good_alarm = alarm_prepare(marker_text)
        inheritance = call(client,'translation_review_page',{'recordRef':good_alarm['recordRef'],'section':'alarmInheritance','pageSize':1})
        require(inheritance['rows'][0]['before']['status']=='mixed' and inheritance['rows'][0]['after']['status']=='inherited', 'Effective final language states incorrect')
        validate('translation-review.v1.schema.json', json.loads(download(client,artifact(good_alarm,'review.json'))))
        alarm_approval = call(client,'translation_approve',{'reviewRef':good_alarm['recordRef'],
            'exactReviewSha256':good_alarm['metadata']['reviewSha256'],'reviewer':'MCP test reviewer','approveEveryChange':True})
        alarm_candidate = call(client,'translation_candidate',{'reviewRef':good_alarm['recordRef'],
            'exactReviewSha256':good_alarm['metadata']['reviewSha256'],'approvalSha256':alarm_approval['recordSha256']})
        alarm_bytes = download(client,artifact(alarm_candidate,'candidate.xlsx'))
        with zipfile.ZipFile(io.BytesIO(alarm_original)) as before, zipfile.ZipFile(io.BytesIO(alarm_bytes)) as after:
            require(set(before.namelist())==set(after.namelist()), 'Alarm candidate changed part set')
            for name in before.namelist():
                if name!='xl/worksheets/sheet1.xml':require(before.read(name)==after.read(name), 'Alarm candidate changed unrelated part')
            old = ET.fromstring(before.read('xl/worksheets/sheet1.xml')); new = ET.fromstring(after.read('xl/worksheets/sheet1.xml'))
            old_row = old.find(ns+'sheetData/'+ns+'row[@r="2"]'); new_row = new.find(ns+'sheetData/'+ns+'row[@r="2"]')
            require(new_row.find(ns+'c[@r="C2"]/'+ns+'is/'+ns+'t').text==marker_text, 'Marker not written exactly')
            old_row.remove(old_row.find(ns+'c[@r="C2"]')); new_row.remove(new_row.find(ns+'c[@r="C2"]'))
            require(ET.tostring(old)==ET.tostring(new), 'Alarm candidate changed an unselected worksheet node')
        require(not alarm_candidate['nativeImportAllowed'], 'Alarm candidate promoted native admission')
        report['hostExits'].append(client.close())
        require(client.process.returncode==0,'Second Host did not close cleanly')
        client=start()
        alarm_reopened=call(client,'translation_reopen',{'recordRef':good_alarm['recordRef']})
        require(download(client,artifact(alarm_reopened['candidate'],'candidate.xlsx'))==alarm_bytes,'Restart changed alarm candidate')
        require(call(client,'translation_review_page',{'recordRef':good_alarm['recordRef'],'section':'alarmInheritance'})['rows']==inheritance['rows'],'Restart changed alarm evidence')
        require(call(client,'translation_review_page',{'recordRef':review['recordRef'],'section':'alarmInheritance'})['total']==0,'Old profile gained alarm semantics')
        report['steps'].append('Explicit alarm inheritance profile: three languages, mixed-state refusal, approval guard, exact peer-preserving repair, schema checks and fresh Host restart')
        # The Host constructor may create an empty durable store, but these calls
        # must not publish any native reservation, plan or operation record.
        native_store = output / "host-artifacts/engineering-operations"
        require(not native_store.exists() or not any(native_store.iterdir()), "Offline tools altered native operation state")
        require(not missing.exists(), "Unexpected worker executable appeared")
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
    finally:
        if client is not None:
            report['hostExits'].append(client.close())
            report["hostExitCode"] = client.process.returncode
            report["completed"] = report["completed"] and all(
                receipt['hostExitCode'] == 0 and not receipt['forcedHostStop'] and receipt['stderrEofObserved'] and not receipt['workerExits']
                for receipt in report['hostExits'])
        (output / "verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(("PASS" if report["completed"] else "FAIL") + " production translation MCP offline workflow: " + json.dumps(report))
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
