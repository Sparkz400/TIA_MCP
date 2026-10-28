"""Qualify an explicitly configured encoder on a new owned offline scratch.

The native seeder supplies the missing channel connection prerequisite. MCP
still performs the parameter edit, compilation, checkpoint, and recovery.
No physical device, download, online connection or motion is involved.
"""
import argparse
import copy
import hashlib
import json
import pathlib
import uuid

from smoke_mcp import ROOT
from encoder_lifecycle import new_session, sync_steps, finish_session, run_seeder
from compilation_evidence import require_clean_compile, require_plc_compile, require_compile_only, capture_device_targets, device_inventory_targets, fixture_plc_selection
from encoder_checkpoint import verify_seeder_checkpoint, verified_file
from encoder_copy_admission import capture_closed
from engineering_preservation import require, native_payload, decode_native_artifact
import encoder_software_preservation as software_peers
import encoder_hardware_preservation as hardware_peers
from qualify_technology_families import parameter_rows
from encoder_endpoints import POLICY as ENDPOINT_POLICY, connection_attributes, capture_endpoint, endpoint, require_endpoint_calls, same


CPU = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"
NAME = "TiaMcp_EncoderConnected_Fixture"
COMPILER_POLICY = "encoder-exact-compiler-targets-v2"
RUNS = ROOT / "artifacts"


def require_source_compiles(source):
    require(source["seederConnect"]["completed"] is True, 'Native seeding did not complete')
    require_clean_compile(source["seederConnect"].get("compile"))
    require(source.get('compilerTargetPolicy') == COMPILER_POLICY, 'Exact encoder compiler-target policy is required')
    require(source["edit"]["applied"]["state"] == "applied", 'Native encoder change was not applied')
    require(source["recovery"]["status"] == "passed", 'Native encoder recovery failed')
    for value in (source["edit"]["applied"], source["recovery"]):
        require_clean_compile(value.get("compile"))
    flattened = {"sessions": source["sessions"]}
    sync_steps(flattened)
    require(source["steps"] == flattened["steps"], 'Source steps differ from retained session calls')
    def inventory_after(calls, start, project):
        pages = []
        while start < len(calls) and calls[start]['tool'] == 'tia_session_status': start += 1
        while start < len(calls):
            row = calls[start]
            expected_args = {'pageSize': 200, 'maxVisited': 50000} if not pages else {
                'snapshotId': pages[0]['snapshot']['snapshotId'], 'cursor': pages[-1]['nextCursor'], 'pageSize': 200}
            require(row['tool'] == 'device_inventory' and row['arguments'] == expected_args,
                    'Original compiler inventory requests are missing, filtered or reordered')
            pages.append(row['result'])
            if row['result'].get('nextCursor') is None: break
            start += 1
        return pages, device_inventory_targets(pages, project)

    require(len(source['sessions']) == 3, 'Encoder target proof requires all three original sessions')
    applications, restores, creations, native_reads = [], [], [], []
    prepared = {}
    for session in source['sessions']:
        project = selector = pending_scratch = None
        calls = session['calls']
        for index, row in enumerate(calls):
            require(row.get('status') == 'passed', 'Compiler proof contains an unsuccessful original call')
            tool, args, value = row['tool'], row['arguments'], row['result']
            if tool in ('tia_create_scratch', 'project_open', 'change_restore'):
                if tool == 'tia_create_scratch':
                    require(value.get('completed') is True and type(value.get('processId')) is int and value['processId'] == session['processId']
                            and args.get('cpuTypeIdentifier') == CPU, 'Scratch creation owner differs')
                    pending_scratch = value
                    creations.append(value)
                elif tool == 'change_restore':
                    change = prepared.get(args.get('changeId'))
                    require(change is not None and change['session'] is session and args.get('previewHash') == change['result']['previewHash']
                            and value.get('state') == 'restored', 'Restore does not belong to its original preparation')
                    restores.append((change, value))
                else:
                    require(value.get('opened') is True and value.get('processId') == session['processId']
                            and value.get('project', {}).get('path') == args.get('path'), 'Reopen owner differs')
                project = value['recoveredProject']['path'] if tool == 'change_restore' else value['project']['path']
                selector = None
            elif tool == 'project_inventory':
                require(args == {'maxItems': 2000}, 'Original PLC inventory request was filtered or changed')
                selector = fixture_plc_selection(value, project, session['processId'])
                if pending_scratch is not None:
                    require_plc_compile(pending_scratch['compile'], selector['plcPath'])
                    pending_scratch = None
            elif tool == 'change_prepare':
                require(pending_scratch is None and selector is not None and all(args.get(key) == val for key, val in selector.items()),
                        'Preparation omitted or changed its exact inventoried PLC selection')
                identity = value.get('changeId')
                require(isinstance(identity, str) and identity and identity not in prepared, 'Preparation identity is missing or repeated')
                prepared[identity] = dict(result=value, arguments=args, selector=selector, project=project, session=session, applied=False, index=index)
            elif tool == 'change_apply':
                change = prepared.get(args.get('changeId'))
                require(change is not None and change['session'] is session and not change['applied'] and change['project'] == project
                        and args.get('previewHash') == change['result']['previewHash']
                        and all(args.get(flag) is True for flag in ('checkpointSave', 'compile', 'save'))
                        and value.get('state') == 'applied', 'Apply differs from its original exact preparation')
                kind = change['arguments']['kind']
                if kind == 'hardware_plug':
                    _, expected = inventory_after(calls, index + 1, project)
                    require(value['compile'].get('targetIdentityPolicy') == 'device-path-v1', 'Hardware compiler identities are missing')
                else:
                    require(kind in ('to_parameters', 'plc_compile'), 'Unexpected compiler operation in encoder fixture')
                    if kind == 'plc_compile':
                        require(change['arguments'].get('payload') == {}, 'Compile-only request contains a mutation payload')
                        require_compile_only(change['result'], value, change['selector']['plcPath'])
                    expected = [change['selector']['plcPath']]
                if kind == 'hardware_plug': require_clean_compile(value.get('compile'), expected_targets=expected)
                else: require_plc_compile(value.get('compile'), change['selector']['plcPath'])
                change['applied'] = True
                change['appliedIndex'] = index
                applications.append((change, value))
                selector = None
            elif tool in ('technology_read', 'engineering_read'):
                selected = args.get('selectors', {}) if tool == 'engineering_read' else args
                require(selector is not None and all(selected.get(key) == val for key, val in selector.items())
                        and value.get('project', {}).get('path') == project and type(value.get('processId')) is int and value['processId'] == session['processId'],
                        'Encoder read differs from its independently inventoried owner')
                if tool == 'technology_read':
                    require(value.get('plcPath') == selector['plcPath'], 'Native technology read identifies another PLC')
                    if args.get('technologyName') == NAME:
                        native_reads.append((value, project, session['processId'], len(applications)))
                else:
                    require(all(isinstance(obj.get('path'), str) and obj['path'].startswith(selector['plcPath'] + '/technology/')
                                for obj in value.get('technologyObjects', [])), 'Parameter read contains another PLC object')
        require(pending_scratch is None, 'Scratch compile lacks independent target inventory')
    require(len(creations) == 1 and creations[0]['project']['path'] == source['projectPath'], 'Original scratch creation is missing or ambiguous')
    require(len(applications) == 4 and [entry[0]['arguments']['kind'] for entry in applications] == ['hardware_plug', 'plc_compile', 'to_parameters', 'plc_compile'],
            'Exact hardware, fresh baseline, edit and compile-only recovery are required')
    baseline, edit, recovery = applications[1:4]
    require(baseline[0]['result'] == source['baselineCompile']['prepared'] and baseline[1] == source['baselineCompile']['applied']
            and baseline[0]['project'] == source['projectPath'], 'Fresh baseline differs from the original compile-only calls')
    require(edit[0]['result'] == source['edit']['prepared'] and edit[1] == source['edit']['applied']
            and edit[0]['project'] == source['projectPath'], 'Edit summary differs from original compiler calls')
    require(len(restores) == 1 and restores[0][0] is edit[0] and restores[0][1] == source['recovery']['restored']
            and restores[0][1]['recoveredProject']['path'] == source['recoveredProjectPath'] != source['projectPath'],
            'Recovery does not identify the exact independently restored project')
    require(recovery[0]['project'] == source['recoveredProjectPath'] and recovery[1]['compile'] == source['recovery']['compile']
            and recovery[0]['result'] == source['recovery']['compileOnly']['prepared']
            and recovery[1] == source['recovery']['compileOnly']['applied'],
            'Recovery compilation summary differs from original selected calls')
    for value, expected_project, expected_process, phase in (
        (source['nativeBefore'], source['projectPath'], source['sessions'][0]['processId'], 2),
        (source['edit']['nativeAfter'], source['projectPath'], source['sessions'][0]['processId'], 3),
        (source['recovery']['nativeRecovered'], source['recoveredProjectPath'], source['sessions'][0]['processId'], 3),
        (source['recovery']['reopen']['nativeReopened'], source['recoveredProjectPath'], source['sessions'][1]['processId'], 4),
        (source['saveReopen']['nativeReopened'], source['projectPath'], source['sessions'][2]['processId'], 4)):
        observed = {key: val for key, val in value.items() if key not in ('xml', 'endpointEvidence', 'softwarePeers', 'hardwarePeers')}
        matches = [(project, process, count) for read, project, process, count in native_reads
                   if {key: val for key, val in read.items() if key not in ('xml', 'endpointEvidence', 'softwarePeers', 'hardwarePeers')} == observed]
        require(matches == [(expected_project, expected_process, phase)], 'Native phase summary differs from its original selected read')
    require(source.get('endpointPolicy') == ENDPOINT_POLICY, 'Current encoder endpoint policy is required')
    phases = [source['nativeBefore'], source['edit']['nativeAfter'], source['recovery']['nativeRecovered'],
              source['recovery']['reopen']['nativeReopened'], source['saveReopen']['nativeReopened']]
    baseline_endpoint = None
    for value, session_index in zip(phases, (0, 0, 0, 1, 2)):
        observed_endpoint = require_endpoint_calls(value, source['profile'], source['sessions'][session_index]['calls'])
        if baseline_endpoint is None: baseline_endpoint = observed_endpoint
        require(same(observed_endpoint, baseline_endpoint), 'Encoder endpoint changed across edit, recovery or reopen')
    require('hardwarePeerPolicy' not in source or source.get('softwarePeerPolicy') == software_peers.POLICY, 'Hardware proof requires complete software proof')
    if 'softwarePeerPolicy' in source:
        require(source['softwarePeerPolicy'] == software_peers.POLICY, 'Unknown software peer evidence policy')
        require(same(source['profile']['parameterEdit'], {'Sensor.InverseDirection': True}), 'Software peer proof requires the exact Boolean fixture')
        expected_edit = native_payload(software_peers.expected_encoder_edit(phases[0]['xml']))
        expected_baseline = native_payload(phases[0]['xml'])
        for index, (value, session_index) in enumerate(zip(phases, (0, 0, 0, 1, 2))):
            hardware_start = software_peers.require_calls(value, source['sessions'][session_index]['calls'])
            if 'hardwarePeerPolicy' in source:
                require(source['hardwarePeerPolicy'] == hardware_peers.POLICY, 'Unknown hardware peer evidence policy')
                hardware_peers.require_calls(value, source['sessions'][session_index]['calls'], hardware_start)
                hardware_peers.compare(phases[0], value)
            software_peers.compare(phases[0], value)
            require(native_payload(value['xml']) == (expected_edit if index in (1, 4) else expected_baseline),
                    'Selected encoder XML differs from the independently expected phase')
    verify_seeder_checkpoint(source)
    packets = [row for row in source['seederLifetimes'] if row['mode'] == 'connect']
    require(len(packets) == 1, 'One exact connected seeder invocation is required')
    packet = packets[0]
    calls = source['sessions'][0]['calls']
    start, end = packet.get('startCallCount'), packet.get('endCallCount')
    require(type(start) is int and type(end) is int and applications[0][0]['appliedIndex'] < start < end <= baseline[0]['index'],
            'Seeder original-call boundaries are missing or outside hardware/edit ordering')
    inventories = [row['result'] for row in calls[:start] if row['tool'] == 'project_inventory']
    require(inventories, 'Seeder has no independent pre-dispatch PLC inventory')
    selected = fixture_plc_selection(inventories[-1], source['projectPath'], source['sessions'][0]['processId'])
    auth_path = pathlib.Path(packet['authorization'])
    with auth_path.open('rb') as stream:
        raw = stream.read(1024 * 1024 + 1)
    require(len(raw) <= 1024 * 1024 and hashlib.sha256(raw).hexdigest() == packet['authorizationSha256'], 'Seeder authorization bytes changed')
    auth = json.loads(raw)
    _, authorized_targets = inventory_after(calls, applications[0][0]['appliedIndex'] + 1, source['projectPath'])
    require(auth.get('deviceCompilerTargets') == authorized_targets, 'Seeder device authorization differs from original independent inventory')
    require(auth.get('projectPath') == source['projectPath'] and type(auth.get('processId')) is int and
            auth['processId'] == source['sessions'][0]['processId'] and auth.get('ownsPortalProcess') is True and
            auth.get('creationTool') == 'tia_create_scratch' and auth.get('technologyName') == NAME and
            auth.get('cpuTypeIdentifier') == source['profile']['cpuTypeIdentifier'] == CPU, 'Seeder authorization owner/profile differs')
    plug = applications[0][0]['arguments']['payload']
    require(plug.get('name') == 'TiaMcp_EncoderHardware' and type(plug.get('positionNumber')) is int and plug['positionNumber'] == 2
            and auth.get('moduleName') == plug['name'] and auth.get('moduleHardwarePath') == plug['hardwarePath'] + '/items/' + plug['name']
            and auth.get('moduleTypeIdentifier') == plug.get('typeIdentifier') == source['profile']['moduleTypeIdentifier'],
            'Seeder module differs from the original configured hardware operation')
    baseline_module = source['nativeBefore']['endpointEvidence']['hardware'][0]['items'][0]['path']
    require(baseline_module == auth.get('moduleHardwarePath'), 'Connected endpoint differs from the exact seeded module')
    require(auth.get('plcPath') == selected['plcPath'] and auth.get('cpuHardwarePath') == inventories[-1]['plcs'][0]['hardwarePath'],
            'Seeder authorization differs from independent PLC inventory')
    modules = [row for row in inventories[-1]['hardware'] if row.get('path') == auth.get('moduleHardwarePath')]
    require(len(modules) == 1 and modules[0].get('name') == auth.get('moduleName') and modules[0].get('positionNumber') == 2,
            'Seeder module is not the independently selected hardware item')
    output = verified_file(pathlib.Path(source['runDirectory']), {'path': packet['stdout'], 'bytes': packet.get('outcomeBytes'), 'sha256': packet.get('outcomeSha256')}, 4 * 1024 * 1024)
    with output.open('rb') as stream:
        raw = stream.read(4 * 1024 * 1024 + 1)
    require(len(raw) <= 4 * 1024 * 1024 and hashlib.sha256(raw).hexdigest() == packet.get('outcomeSha256'), 'Original seeder outcome bytes changed')
    seeded = json.loads(raw.decode('utf-8').strip().splitlines()[-1])
    require({**seeded, 'seederSha256': packet['seederSha256']} == source['seederConnect'], 'Seeder summary differs from its original output')
    require(seeded.get('projectPath') == source['projectPath'] and seeded.get('processId') == source['sessions'][0]['processId']
            and all(seeded.get(key) == auth[key] for key in ('plcPath', 'cpuHardwarePath', 'moduleHardwarePath')),
            'Seeder result identifies another hardware/software owner')
    pages, expected = inventory_after(calls, end, source['projectPath'])
    require(set(expected) == set(authorized_targets), 'Seeder device scope changed after authorization')
    require(seeded['compile'].get('targetIdentityPolicy') == 'device-path-v1', 'Seeder device compiler identities are missing')
    require_clean_compile(seeded['compile'], expected_targets=expected)
    require(source.get('seederCompilerInventory') == {'targets': expected, 'pages': pages}, 'Seeder inventory summary differs from original calls')


def assert_parameter_edit(before, after, requested):
    parameter_rows(list(before.values()))
    parameter_rows(list(after.values()))
    expected = copy.deepcopy(before)
    for name, value in requested.items():
        require(before[name]["writable"] is True, "Native parameter is not writable")
        require(type(before[name]["value"]) is type(value), "Native scalar type differs")
        require(before[name]["value"] != value, "An actual native parameter change is required")
        expected[name]["value"] = value
    require(json.dumps(after, sort_keys=True) == json.dumps(expected, sort_keys=True), "Unrequested native parameter metadata or values changed")


def parameter_map(read):
    require(len(read["technologyObjects"]) == 1 and read.get("truncated") is False and read.get("scopeComplete") is True, 'Native evidence requirement failed: len(read["technologyObjects"]) == 1 and read.get("truncated") is False and read.get("scopeComplete") is True')
    obj = read["technologyObjects"][0]
    require(obj["typeIdentifier"] == "TO_ExternalEncoder" and obj["version"] == "10.0", 'Native evidence requirement failed: obj["typeIdentifier"] == "TO_ExternalEncoder" and obj["version"] == "10.0"')
    require(obj.get("parametersTruncated") is False and obj.get("parametersComplete") is True, 'Native evidence requirement failed: obj.get("parametersTruncated") is False and obj.get("parametersComplete") is True')
    return parameter_rows(obj["parameters"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, required=True)
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--seeder", type=pathlib.Path, default=ROOT / "scripts/native/EncoderSeeder/bin/Debug/net48/EncoderSeeder.exe")
    parser.add_argument("--configuration", type=pathlib.Path, help="Exact hardware channel and observed attribute configuration; omission runs discovery only.")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/encoder-hardware-qualification.json")
    args = parser.parse_args()
    args.report = args.report.resolve()
    require(not args.report.exists(), "Choose a new report path; prior evidence is immutable")
    run_dir = RUNS / ("encoder-qualification-" + uuid.uuid4().hex[:8])
    run_dir.mkdir(parents=True)
    report = {"completed": False, "steps": [], "scope": "Worker-owned offline connected encoder fixture; no physical/runtime operations", "runDirectory": str(run_dir), "compilerTargetPolicy": COMPILER_POLICY, "endpointPolicy": ENDPOINT_POLICY, "softwarePeerPolicy": software_peers.POLICY, "hardwarePeerPolicy": hardware_peers.POLICY}
    report["selectedBinaries"] = {key: {"path": str(value.resolve()), "sha256": hashlib.sha256(value.read_bytes()).hexdigest()}
                          for key, value in (("host", args.host), ("worker", args.worker), ("seeder", args.seeder))}
    report["fixtureSources"] = {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest()
                               for path in (pathlib.Path(__file__).resolve(), ROOT / "scripts/compilation_evidence.py", ROOT / "scripts/encoder_endpoints.py", ROOT / "scripts/encoder_software_preservation.py", ROOT / "scripts/encoder_hardware_preservation.py", ROOT / "scripts/engineering_preservation.py", ROOT / "scripts/encoder_copy_admission.py", ROOT / "scripts/hmi_fixture_copy.py",
                                            ROOT / "scripts/native/EncoderSeeder/Program.cs",
                                            ROOT / "scripts/native/EncoderSeeder/CheckpointEvidence.cs", ROOT / "scripts/encoder_checkpoint.py",
                                            ROOT / "src/TiaMcp.Openness.V21/CompilationEvidenceRules.cs", ROOT / "src/TiaMcp.Openness.V21/DeviceCompilation.cs")}
    configuration = json.loads(args.configuration.read_text(encoding="utf-8-sig")) if args.configuration else None
    if args.configuration:
        require(isinstance(configuration, dict) and configuration, "An explicit configuration cannot become discovery mode")
        require(configuration["cpuTypeIdentifier"] == CPU, 'Native evidence requirement failed: configuration["cpuTypeIdentifier"] == CPU')
        require(configuration["typeIdentifier"] == "TO_ExternalEncoder" and configuration["version"] == "10.0", 'Native evidence requirement failed: configuration["typeIdentifier"] == "TO_ExternalEncoder" and configuration["version"] == "10.0"')
        report["profile"] = configuration
        report["fixtureSources"][str(args.configuration.resolve())] = hashlib.sha256(args.configuration.read_bytes()).hexdigest()
    active_change = None

    def persist():
        sync_steps(report)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    session = new_session(args.host.resolve(), args.worker.resolve(), run_dir, report, persist, run_dir / "lifetimes")
    persist()

    def call(name, arguments=None):
        value = session.call(name, arguments)
        print("PASS " + name, flush=True)
        return value

    def selection():
        session.guard()
        inventory = call("project_inventory", {"maxItems": 2000})
        selector = fixture_plc_selection(inventory, session.current_project, session.session["processId"])
        session.guard()
        return inventory, selector

    def apply(kind, payload):
        nonlocal active_change
        _, selector = selection()
        prepared = call("change_prepare", {"kind": kind, "payload": payload, **selector})
        active_change = prepared
        applied = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
            "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
        require(applied["state"] == "applied", 'Native encoder change was not applied')
        require_clean_compile(applied.get("compile"))
        if kind.startswith('hardware_') or kind in ('network_configure', 'network_io_assign'):
            require(applied['compile'].get('targetIdentityPolicy') == 'device-path-v1', 'Exact hardware compiler identities are required')
            session.guard()
            inventory = capture_device_targets(call, report['projectPath'])
            session.guard()
            report.setdefault('deviceCompilerInventories', []).append({'changeId': prepared['changeId'], **inventory})
            require_clean_compile(applied['compile'], expected_targets=inventory['targets'])
        else:
            require_plc_compile(applied["compile"], selector["plcPath"])
            if kind == "plc_compile": require_compile_only(prepared, applied, selector["plcPath"])
        return prepared, applied

    def native():
        inventory, selector = selection()
        value = call("technology_read", {"technologyName": NAME, "maxItems": 2000, "exportXml": True, **selector})
        require(value.get("project", {}).get("path") == session.current_project and type(value.get("processId")) is int and value.get("processId") == session.session["processId"] and value.get("plcPath") == selector["plcPath"], "Encoder native read owner differs from its selected inventory")
        require(not value.get("warnings") and value["completeness"]["scopeComplete"], 'Native evidence requirement failed: not value.get("warnings") and value["completeness"]["scopeComplete"]')
        artifact = call("artifact_read", {"path": value["artifact"]["path"]})
        value["xml"] = decode_native_artifact(value, artifact)
        capture_endpoint(call, value, inventory, configuration)
        software_peers.capture(call, value, selector)
        hardware_peers.capture(call, value)
        return value

    def parameters():
        _, selector = selection()
        read = call("engineering_read", {"selectors": {"scope": "technology", "name": NAME, "maxItems": 2000, **selector}})
        require(read.get("project", {}).get("path") == session.current_project and type(read.get("processId")) is int
                and read["processId"] == session.session["processId"] and
                all(isinstance(row.get("path"), str) and row["path"].startswith(selector["plcPath"] + "/technology/") for row in read.get("technologyObjects", [])),
                "Encoder parameter read owner differs from its selected inventory")
        return parameter_map(read)

    def seed(mode, auth):
        return run_seeder(session, args.seeder.resolve(), report["selectedBinaries"]["seeder"]["sha256"],
                          mode, auth, run_dir, report, persist)

    try:
        start = session.start()
        scratch = call("tia_create_scratch", {"cpuTypeIdentifier": CPU})
        require(scratch["completed"] is True, 'Native scratch creation did not complete')
        require_clean_compile(scratch.get("compile"))
        report["projectPath"] = scratch["project"]["path"]
        inventory, selector = selection()
        require_plc_compile(scratch["compile"], selector["plcPath"])
        rail = [item for item in inventory["hardware"] if item["positionNumber"] == 0 and item["path"].count("/items/") == 1]
        require(len(rail) == 1, 'Native evidence requirement failed: len(rail) == 1')
        catalog = call("hardware_catalog_search", {"filter": "TM PosInput 2", "maxItems": 500})
        candidates = [item for item in catalog["entries"] if "6ES7 551" in (item.get("articleNumber") or "")]
        if configuration:
            candidates = [item for item in candidates if item["typeIdentifier"] == configuration["moduleTypeIdentifier"]]
        else:
            candidates.sort(key=lambda item: item["typeIdentifier"], reverse=True)
        require(candidates, "No exact catalog-backed TM PosInput 2 module is available")
        selected = candidates[0]
        report["selectedModule"] = selected
        apply("hardware_plug", {"hardwarePath": rail[0]["path"], "typeIdentifier": selected["typeIdentifier"], "name": "TiaMcp_EncoderHardware", "positionNumber": 2})
        inventory, selector = selection()
        modules = [row for row in inventory["hardware"] if row.get("name") == "TiaMcp_EncoderHardware" and row.get("positionNumber") == 2]
        require(len(modules) == 1, "One exact newly plugged encoder module is required")
        auth = {"deviceCompilerTargets": report["deviceCompilerInventories"][-1]["targets"], "plcPath": selector["plcPath"], "cpuHardwarePath": inventory["plcs"][0]["hardwarePath"], "moduleHardwarePath": modules[0]["path"], "ownsPortalProcess": True, "creationTool": "tia_create_scratch", "processId": start["processId"],
                "projectPath": report["projectPath"], "cpuTypeIdentifier": CPU, "moduleName": "TiaMcp_EncoderHardware",
                "moduleTypeIdentifier": selected["typeIdentifier"], "technologyName": NAME}
        inspection = seed("inspect", auth)
        if not configuration:
            report["discoveryCompleted"] = True
            return
        auth.update({key: configuration[key] for key in ("hardwareAttributes", "channelItemName", "channelNumber", "channelIoType")})
        active_change = None  # Native seeding owns a different prerequisite checkpoint.
        seeded = seed("connect", auth)
        require(seeded.get("projectPath") == session.current_project and seeded.get("processId") == session.session["processId"] and
                all(seeded.get(key) == auth[key] for key in ("plcPath", "cpuHardwarePath", "moduleHardwarePath")), "Seeder target owner differs from its exact authorization")
        require(seeded.get("compile", {}).get("targetIdentityPolicy") == "device-path-v1", "Seeder requires exact complete device compiler targets")
        report["seederCompilerInventory"] = capture_device_targets(call, session.current_project)
        require(set(report["seederCompilerInventory"]["targets"]) == set(auth["deviceCompilerTargets"]), "Seeder project compiler inventory changed after authorization")
        require_clean_compile(seeded.get("compile"), expected_targets=report["seederCompilerInventory"]["targets"])
        baseline_prepared, baseline_applied = apply("plc_compile", {})
        report["baselineCompile"] = {"prepared": baseline_prepared, "applied": baseline_applied}
        active_change = None
        before = native()
        baseline_connections = endpoint(before, configuration)
        before_parameters = parameters()
        report["nativeBefore"] = before
        for name, value in configuration["dependencies"].items():
            require(before_parameters[name]["value"] == value and type(before_parameters[name]["value"]) is type(value), "Native encoder prerequisite differs: " + name)
        requested = configuration["parameterEdit"]
        for name, value in requested.items():
            require(before_parameters[name]["writable"] is True, 'Native evidence requirement failed: before_parameters[name]["writable"] is True')
            require(type(before_parameters[name]["value"]) is type(value), 'Native evidence requirement failed: type(before_parameters[name]["value"]) is type(value)')
            require(before_parameters[name]["value"] != value, "Qualification requires an actual parameter change")
        require(same(requested, {"Sensor.InverseDirection": True}), "This preservation fixture requires the exact Boolean edit")
        expected_xml = software_peers.expected_encoder_edit(before["xml"])
        prepared, applied = apply("to_parameters", {"name": NAME, "parameters": requested})
        after = native()
        require(native_payload(after["xml"]) == native_payload(expected_xml), "Encoder XML changed beyond the selected Boolean edit")
        software_peers.compare(before, after)
        hardware_peers.compare(before, after)
        after_parameters = parameters()
        report["parameterDifferences"] = {name: {"before": before_parameters.get(name), "after": after_parameters.get(name)}
                                          for name in set(before_parameters) | set(after_parameters) if before_parameters.get(name) != after_parameters.get(name)}
        assert_parameter_edit(before_parameters, after_parameters, requested)
        require(same(endpoint(after, configuration), baseline_connections), "Parameter edit changed hardware dependencies")
        for name, value in requested.items():
            require(after_parameters[name]["value"] == value and type(after_parameters[name]["value"]) is type(value), 'Native evidence requirement failed: after_parameters[name]["value"] == value and type(after_parameters[name]["value"]) is type(value)')
        report["edit"] = {"prepared": prepared, "applied": applied, "readback": after_parameters, "nativeAfter": after}
        restored = call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        active_change = None
        require(restored["state"] == "restored", 'Native evidence requirement failed: restored["state"] == "restored"')
        report["recoveredProjectPath"] = restored["recoveredProject"]["path"]
        recovered = native()
        software_peers.compare(before, recovered)
        hardware_peers.compare(before, recovered)
        require(native_payload(before["xml"]) == native_payload(recovered["xml"]), "Recovered encoder XML differs from the checkpoint baseline")
        require(parameters() == before_parameters, 'Native evidence requirement failed: parameters() == before_parameters')
        require(same(endpoint(recovered, configuration), baseline_connections), 'Native evidence requirement failed: same(endpoint(recovered, configuration), baseline_connections)')
        compile_prepared, compiled = apply("plc_compile", {})
        report["recovery"] = {"status": "passed", "restored": restored, "nativeRecovered": recovered,
                              "compile": compiled["compile"], "compileOnly": {"prepared": compile_prepared, "applied": compiled}, "exactParameters": True, "exactHardwareConnections": True, "fullNativeXml": True}
        # Recovery must precede detach: a preview/checkpoint is bound to its
        # original native process. Independently verify recovery across reopen.
        active_change = None
        session.reopen(report["recoveredProjectPath"])
        reopened = native()
        software_peers.compare(before, reopened)
        hardware_peers.compare(before, reopened)
        require(native_payload(before["xml"]) == native_payload(reopened["xml"]), "Recovered encoder XML changed across save/reopen")
        require(parameters() == before_parameters, 'Native evidence requirement failed: parameters() == before_parameters')
        require(same(endpoint(reopened, configuration), baseline_connections), 'Native evidence requirement failed: same(endpoint(reopened, configuration), baseline_connections)')
        report["recovery"]["reopen"] = {"status": "passed", "nativeReopened": reopened}
        session.reopen(report["projectPath"])
        reopened = native()
        software_peers.compare(before, reopened)
        hardware_peers.compare(before, reopened)
        require(native_payload(after["xml"]) == native_payload(reopened["xml"]), "Complete encoder XML changed across save/reopen")
        require(parameters() == after_parameters, "Native parameter metadata changed across save/reopen")
        require(same(endpoint(reopened, configuration), baseline_connections), "Hardware dependencies changed across save/reopen")
        report["saveReopen"] = {"status": "passed", "nativeReopened": reopened,
                                "exactParameters": True, "exactHardwareConnections": True, "fullNativeXml": True}
        sync_steps(report)
        require_source_compiles(report)
        report["qualificationChecksPassed"] = True
    except BaseException as error:
        report["failure"] = str(error)
        try:
            seeded = report.get("seederConnect", {})
            if seeded.get("checkpointArchive") and seeded.get("completed") is False:
                require(session.client is not None and not session.poisoned and
                        all(row.get('status') == 'passed' for row in session.session['calls']),
                        'Uncertain owned session cannot dispatch checkpoint recovery')
                require(any(row.get('mode') == 'connect' and row.get('checkpointVerified') is True
                            for row in report.get('seederLifetimes', [])), 'Seeder checkpoint was not verified when its outcome arrived')
                checkpoint = verify_seeder_checkpoint(report)
                session.guard()
                session.close()
                session.start()
                require(session.current_project is None, "Seeder recovery requires an empty owned session")
                require(verify_seeder_checkpoint(report) == checkpoint, 'Checkpoint changed before recovery dispatch')
                restored = call("project_retrieve", {"archivePath": checkpoint['path'], "expectedSha256": checkpoint['sha256']})
                archive = restored.get('archive', {})
                require(restored.get("retrieved") is True and restored.get('processId') == session.session['processId'] and
                        archive.get('sha256') == checkpoint['sha256'] and archive.get('bytes') == checkpoint['bytes'] and
                        pathlib.Path(archive.get('path', '')) == pathlib.Path(checkpoint['path']), "Seeder checkpoint retrieval receipt differs")
                session.remember(restored["project"]["path"])
                session.guard()
                report["failureRecovery"] = {"status": "passed", "scope": "Hash-bound prerequisite checkpoint retrieval only",
                                             "fullNativePreservationVerified": False, "checkpoint": checkpoint, "result": restored,
                                             "nativeInventory": call("project_inventory", {"maxItems": 2000})}
            elif (active_change is not None and session.client is not None and not session.poisoned
                    and all(row.get("status") == "passed" for row in session.session["calls"])
                    and all(row.get("terminal", {}).get("completed") is True and row.get("outcomeCaptured") is True
                            for row in report.get("seederLifetimes", []))):
                status = call("change_status", {"changeId": active_change["changeId"]})
                if status.get("state") in ("applied", "recovery_required"):
                    report["failureRecovery"] = call("change_restore", {"changeId": active_change["changeId"], "previewHash": active_change["previewHash"]})
            elif active_change is not None or report.get("seederLifetimes"):
                report["failureRecoveryBlocked"] = "Uncertain or closed owned session; no recovery command was dispatched."
        except Exception as recovery_error:
            report["failureRecoveryError"] = str(recovery_error)
        raise
    finally:
        def closed():
            if report.get('qualificationChecksPassed') is True and 'failure' not in report:
                report['closedCopies'] = capture_closed(report, args.report)
        finish_session(session, report, persist, after_cleanup=closed)
        print("Report: " + str(args.report), flush=True)
        print("Report SHA-256: " + hashlib.sha256(args.report.read_bytes()).hexdigest(), flush=True)


if __name__ == "__main__":
    main()
