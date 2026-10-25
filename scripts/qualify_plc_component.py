"""Explicit, owned-scratch-only native qualification of the staged component route.

No automatic retries, native process kills, user project attachment, or runtime
access. A blocker is retained as failed qualification, never converted to success.
"""
from __future__ import annotations
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import shutil
import traceback
import uuid
from xml.etree import ElementTree as ET

from block_test_runtime import canonical, utc_now
from qualification_owned_scratch import assert_snapshot, complete_inventory, native_hash, require, snapshot
from component_exact_lifetime import ComponentOwnedScratch as OwnedScratch
from qualify_typed_resolution import artifact_policy, empty_tag_template, select, tag_fixture
from run_conformance import native_document, safe_xml, structural_xml
from run_block_tests import save_json
from smoke_mcp import Client, ROOT
from typed_graphical_transport import artifact_bytes
from component_source_reuse import prepare_source_reuse, validate_original_inputs, verify_exact_storage_reopen
from component_window_lifetime import ComponentLifetime
from component_window_recovery import read_inputs as parent_recovery_inputs, run_recovery as run_parent_recovery

FIXTURES = ROOT / "fixtures/libraries/component-qualification"
KIND = "library_component_qualification"
EXTRA_TOOLS = ("library_open", "library_close", "library_list", "library_query", "qualification_component_window")


def pin(version):
    result = {key: version[key] for key in ("typeGuid", "versionGuid")}
    require(all(uuid.UUID(value).int for value in result.values()), "Native version GUID is empty.")
    return result


def component_client(host, timeout, arguments, allocated_client=None):
    client = allocated_client if allocated_client is not None else Client.__new__(Client)
    Client.__init__(client, host, timeout, [*arguments, "--component-qualification", "--timeout-seconds", str(timeout)])
    return client


component_client.retained_type = Client


def step_request(prepared):
    return {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
            "idempotencyKey": "component-review-" + uuid.uuid4().hex,
            "checkpointSave": True, "compile": True, "save": True}


def drive_component(call, initial, review, retain):
    """Every mutation has its own reviewed, persisted exact preview. Never retry."""
    value = initial
    identity = value["changeId"]
    require(value.get("state") == "library_prepared", "Unexpected initial component state; never replay or recover automatically.")
    dispatched = set()
    for _ in range(7):
        require(value.get("changeId") == identity, "Component response changed operation identity.")
        state = value["state"]
        retain("observation", value)
        if state == "installation_verified":
            return value
        if state in {"library_prepared", "graphical_prepared"}:
            require(state not in dispatched, "A component step cannot be replayed.")
            review(value)
            request = step_request(value)
            retain("dispatching", request)
            dispatched.add(state)
            value = call("change_apply", request)
            expected = "library_applied" if state == "library_prepared" else "graphical_applied"
        elif state in {"library_applied", "import_verified", "graphical_applied"}:
            value = call("change_prepare", {"kind": KIND, "payload": {"schemaVersion": 1,
                "action": "advance", "changeId": identity}})
            expected = {"library_applied": "import_verified", "import_verified": "graphical_prepared",
                        "graphical_applied": "installation_verified"}[state]
        else:
            raise RuntimeError("Component stopped at " + state + "; inspect status/first checkpoint, never replay.")
        retain("response", value)
        require(value.get("changeId") == identity, "Component response changed operation identity.")
        require(value.get("state") == expected, "Unexpected component response state; never replay or recover automatically.")
    raise RuntimeError("Component exceeded its bounded state sequence.")


def terminal_network(xml, language):
    document = safe_xml(xml)
    nodes = list(document.iter())
    units = [node for node in nodes if node.tag == "SW.Blocks.CompileUnit"
             and node.findtext("AttributeList/ProgrammingLanguage") == language]
    require(bool(units), "Independent caller has no native " + language + " network.")
    unit = units[-1]
    descendants = set(unit.iter())
    following = [node for node in nodes[nodes.index(unit) + 1:] if node not in descendants and node.get("ID")]
    return unit.get("ID"), [{"kind": node.tag, "id": node.get("ID")} for node in following]


def verify_dependency_graph(versions, manifest):
    require(len(versions) == 2 and {v["typeName"] for v in versions} == {manifest["root"], manifest["dependency"]},
            "Native closure differs from the independently specified FB plus UDT.")
    identities = [pin(version) for version in versions]
    for field in ("typeGuid", "versionGuid"):
        require(len({uuid.UUID(identity[field]) for identity in identities}) == len(versions),
                "Native dependency closure contains duplicate " + field + " identities.")
    by_id = {uuid.UUID(v["versionGuid"]): v["typeName"] for v in versions}
    graph = {}
    for version in versions:
        require(version["state"] == "Committed" and version["version"] == manifest["version"]
                and version.get("doNotUse") is False, "Native dependency is not exact committed usable release.")
        edges = version.get("requiredVersionGuids")
        require(isinstance(edges, list), "Native dependency edges must be a complete list.")
        edge_ids = [uuid.UUID(value) for value in edges]
        require(len(edge_ids) == len(set(edge_ids)) and all(value in by_id for value in edge_ids),
                "Native dependency edges contain duplicate or unresolved identities.")
        graph[version["typeName"]] = sorted(by_id[value] for value in edge_ids)
    require(graph == manifest["expectedDependencyGraph"], "Native FB dependency edges differ from fixture specification.")


def verify_component_call(xml, manifest):
    """Independent native CallInfo/instance/tag/EN wiring oracle, not planner output."""
    document = safe_xml(xml)
    local = lambda node: node.tag.rsplit("}", 1)[-1]
    calls = [node for node in document.iter() if local(node) == "CallInfo" and node.get("Name") == manifest["root"]]
    require(len(calls) == 1 and calls[0].get("BlockType") == "FB", "Expected exactly one FB component call.")
    info = calls[0]
    instances = [node for node in info if local(node) == "Instance"]
    require(len(instances) == 1 and instances[0].get("Scope") == "GlobalVariable"
            and [n.get("Name") for n in instances[0].iter() if local(n) == "Component"] == [manifest["instance"]],
            "Native instance identity differs from independent specification.")
    require([(p.get("Name"), p.get("Section"), p.get("Type")) for p in info if local(p) == "Parameter"]
            == [(s["parameter"], s["direction"], s["dataType"]) for s in manifest["signals"]], "Native call interface differs.")
    networks = [node for node in document.iter() if local(node) == "FlgNet" and info in set(node.iter())]
    require(len(networks) == 1, "Component call has no exact native network.")
    net = networks[0]
    parts = next(node for node in net if local(node) == "Parts")
    ids = {}
    for part in parts:
        if local(part) == "Call":
            require(info in list(part), "Unexpected second call in component network.")
            name = "call"
        else:
            require(local(part) == "Access", "Unexpected instruction in inserted component network.")
            if part.get("Scope") == "GlobalVariable":
                components = [node.get("Name") for node in part.iter() if local(node) == "Component"]
                matches = [s for s in manifest["signals"] if components == [s["name"]]]
                require(len(matches) == 1, "Unexpected/ambiguous native mapped tag.")
                name = matches[0]["parameter"]
            else:
                constants = {local(n): n.text for n in part.iter() if local(n) in {"ConstantType", "ConstantValue"}}
                require(part.get("Scope") == "LiteralConstant" and constants.get("ConstantType") == "Bool"
                        and (constants.get("ConstantValue") or "").lower() == "true" and len(constants) == 2,
                        "FBD enable operand differs from explicit Boolean true.")
                name = "enable"
        require(name not in ids.values() and part.get("UId") not in ids, "Duplicate native component part.")
        ids[part.get("UId")] = name
    require(set(ids.values()) == {"call", "enable", "Enable", "Active"}, "Component part inventory differs.")
    observed = []
    for wire in next(node for node in net if local(node) == "Wires"):
        ends = []
        for end in wire:
            if local(end) == "OpenCon":
                ends.append(("open", "value"))
            else:
                require(local(end) in {"IdentCon", "NameCon"} and end.get("UId") in ids, "Unresolved component connector.")
                ends.append((ids[end.get("UId")], end.get("Name", "value")))
        observed.append(tuple(sorted(ends)))
    wanted = [(('enable', 'value'), ('call', 'en')), (('Enable', 'value'), ('call', 'Enable')),
              (('Active', 'value'), ('call', 'Active')), (('call', 'eno'), ('open', 'value'))]
    # ENO may be omitted entirely by the qualified profile; no other open ports allowed.
    normalized = sorted(tuple(sorted(pair)) for pair in wanted)
    require(sorted(observed) in (normalized, sorted(tuple(sorted(pair)) for pair in wanted[:-1])), "Native call/tag wiring differs.")


def verify_explicit_source_attributes(xml, manifest, *, read_only=False):
    """Source settings are authored values, never inferred from omitted defaults."""
    document = safe_xml(xml)
    blocks = list(document.iter("SW.Blocks.FB"))
    require(len(blocks) == 1, "Explicit source must contain exactly one FB.")
    attributes = blocks[0].find("AttributeList")
    require(attributes is not None and attributes.findtext("Name") == manifest["root"]
            and attributes.findtext("ProgrammingLanguage") == "SCL", "Explicit source FB identity differs.")
    for name, expected in manifest["explicitBlockAttributes"].items():
        entries = attributes.findall(name)
        require(len(entries) == 1 and not list(entries[0]) and entries[0].attrib == ({"ReadOnly": "true"} if read_only else {}) and entries[0].text == expected,
                "Source FB must explicitly declare exact " + name + "=" + expected + "; omitted defaults remain unqualified.")


def verify_pinned_source_oracle(base_bytes, oracle_bytes, version, manifest):
    """Independent pre-dispatch check: actual source values, never omitted defaults."""
    policy = version.get("contentPolicy", {})
    require(policy.get("policyId") == manifest["contentPolicyId"] and policy.get("sourceLibraryName") == manifest["sourceLibraryName"],
            "Explicit source content policy/global-library identity differs.")
    require(hashlib.sha256(base_bytes).hexdigest() == version["contentSha256"] == version["artifact"]["sha256"], "Pinned source base export bytes changed.")
    require(hashlib.sha256(oracle_bytes).hexdigest() == policy.get("sourceOracleSha256") == policy.get("sourceOracleArtifact", {}).get("sha256"),
            "Pinned full source oracle bytes changed.")
    require(policy.get("properties") == manifest["explicitBlockAttributes"] and policy.get("sourceBaseExportOptions") == "WithDefaults"
            and policy.get("sourceOracleExportOptions") == "WithDefaults, WithReadOnly" and policy.get("documentInfoOptions") == "None", "Source oracle export contract differs.")
    base = safe_xml(base_bytes.decode("utf-8-sig")); oracle = safe_xml(oracle_bytes.decode("utf-8-sig"))
    # Match the exact native envelope and complete object identity inventory.
    # Hash agreement alone does not establish that the two exports describe
    # the same objects or exclude hidden duplicate content objects.
    inventories = []
    for label, document in (("base", base), ("oracle", oracle)):
        require(document.tag == "Document", "Source " + label + " must have the exact native Document root.")
        versions_all = list(document.iter("SW.Blocks.CodeBlockLibraryTypeVersion"))
        require(len(versions_all) == 1 and len(document.findall("SW.Blocks.CodeBlockLibraryTypeVersion")) == 1,
                "Source " + label + " requires exactly one direct native code-block version.")
        objects = versions_all[0].findall("ObjectList")
        require(len(objects) == 1 and len(objects[0].findall("SW.Blocks.FB")) == 1 and len(list(document.iter("SW.Blocks.FB"))) == 1,
                "Source " + label + " requires exactly one direct native FB content object.")
        block = objects[0].find("SW.Blocks.FB")
        require(block.get("CompositionName") == "ContentObject" and len(block.findall("AttributeList")) == 1,
                "Source " + label + " FB composition/attribute scope differs.")
        identity = [node for node in document.iter() if "ID" in node.attrib]
        identifiers = [node.get("ID") for node in identity]
        require(all(value.strip() for value in identifiers) and len(set(identifiers)) == len(identifiers),
                "Source " + label + " has empty or duplicate native object IDs.")
        inventories.append([(node.tag, sorted(node.attrib.items())) for node in identity])
    require(inventories[0] == inventories[1], "Source base/oracle native object IDs, kinds or attributes differ.")
    require(base.find("DocumentInfo") is None and oracle.find("DocumentInfo") is None, "DocumentInfo.None required for source content proof.")
    versions = oracle.findall("SW.Blocks.CodeBlockLibraryTypeVersion")
    require(len(versions) == 1, "Full source oracle must have one exact code-block version.")
    attributes = versions[0].find("AttributeList")
    require(attributes is not None, "Full source oracle version attributes missing.")
    for name, value in (("Guid", version["versionGuid"]), ("LibraryTypeGuid", version["typeGuid"]), ("LibraryTypeName", version["typeName"]), ("State", "Committed")):
        entries = attributes.findall(name)
        require(len(entries) == 1 and not list(entries[0]) and entries[0].attrib == {"ReadOnly": "true"} and entries[0].text == value,
                "Full source oracle exact native identity differs: " + name)
    base_blocks = base.findall("SW.Blocks.CodeBlockLibraryTypeVersion/ObjectList/SW.Blocks.FB")
    oracle_blocks = versions[0].findall("ObjectList/SW.Blocks.FB")
    require(len(base_blocks) == len(oracle_blocks) == 1 and base_blocks[0].attrib == oracle_blocks[0].attrib
            and base_blocks[0].findtext("AttributeList/Name") == manifest["root"], "Source base/oracle content-object identity differs.")
    for name in manifest["explicitBlockAttributes"]:
        require(not base_blocks[0].findall("AttributeList/" + name), "This profile requires explicit base omissions; existing values cannot be rewritten.")
    verify_explicit_source_attributes(oracle_bytes.decode("utf-8-sig"), manifest, read_only=True)


def recovery_inputs(report_path):
    """Validate retained first-checkpoint lineage without writing original evidence."""
    report_path = Path(report_path).resolve()
    previous = json.loads(report_path.read_text(encoding="utf-8-sig"))
    require(previous.get("status") == "failed", "Separate recovery requires an explicitly selected failed attempt.")
    record = previous["failureStatus"]["record"]
    checkpoint = record["initialCheckpoint"]
    first = record["library"]["prepared"]
    require(checkpoint["changeId"] == first["changeId"] and checkpoint["previewHash"] == first["previewHash"],
            "Recovery anchor differs from exact first import.")
    checkpoint_path = Path(checkpoint["archivePath"])
    require(hashlib.sha256(checkpoint_path.read_bytes()).hexdigest() == checkpoint["sha256"], "Original first checkpoint hash differs.")
    baseline_path = report_path.parent / "before-import/manifest.json"
    baseline = json.loads(baseline_path.read_text(encoding="utf-8-sig"))
    require(baseline["inventory"]["project"]["path"] == checkpoint["originalPath"], "Baseline belongs to a different original project.")
    journal_path = Path(record["library"]["result"]["journalPath"])
    journal = json.loads(journal_path.read_text(encoding="utf-8-sig"))
    require(journal == record["library"]["result"]["journal"] and journal.get("state") == "applied"
            and not journal.get("recoveryAttempts"), "Original first import journal changed or already records recovery.")
    paths = [report_path, baseline_path, checkpoint_path, journal_path]
    for row in baseline["objects"].values():
        file = Path(row["file"]).resolve()
        require(file.is_relative_to(report_path.parent), "Baseline export escaped its retained evidence directory.")
        raw = file.read_bytes()
        require(hashlib.sha256(raw).hexdigest() == row["sha256"] and native_hash(raw.decode("utf-8-sig")) == row["payloadSha256"],
                "Retained baseline XML no longer matches its raw and complete payload hashes.")
        paths.append(file)
    inputs = [{"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in paths]
    return previous, checkpoint, baseline, inputs


def verify_standalone_caller(xml, manifest):
    document = safe_xml(xml)
    blocks = [node for node in document if node.tag.startswith("SW.Blocks.")]
    require(len(blocks) == 1 and blocks[0].tag == "SW.Blocks.FC"
            and blocks[0].findtext("AttributeList/Name") == manifest["caller"]
            and blocks[0].findtext("AttributeList/ProgrammingLanguage") == manifest["language"],
            "Standalone caller identity differs from independent FC specification.")
    require(len(list(blocks[0].iter("SW.Blocks.CompileUnit"))) == 1, "Standalone caller must contain exactly one network.")


def verify_preserved_installation(before, after, caller, added):
    require(set(after["objects"]) - set(before["objects"]) == set(added)
            and not set(before["objects"]) - set(after["objects"]), "Installed object inventory differs from exact fixture additions.")
    reduced = {"objects": {k: v for k, v in after["objects"].items() if k in before["objects"]}}
    if "block:" + caller not in before["objects"]:
        require("block:" + caller in added, "Standalone caller must be an exact new fixture addition.")
        assert_snapshot(before, reduced)
        return
    assert_snapshot(before, reduced, except_paths=[caller])
    # Read and hash one bounded snapshot, including the caller excluded from
    # the unchanged-object comparison above. Never compare unpinned replacement
    # bytes or discard comments, processing instructions or QName bindings.
    from engineering_preservation import native_payload
    from xml.dom import Node, minidom
    documents = []
    for snapshot in (before, after):
        row = snapshot["objects"]["block:" + caller]
        count = row.get("bytes")
        require(type(count) is int and 0 <= count <= 4 * 1024 * 1024,
                "Caller snapshot requires an exact bounded byte count.")
        with Path(row["file"]).open("rb") as stream:
            raw = stream.read(4 * 1024 * 1024 + 1)
        require(len(raw) == count and hashlib.sha256(raw).hexdigest() == row.get("sha256"),
                "Caller snapshot raw bytes differ from the captured hash/count.")
        text = raw.decode("utf-8-sig")
        require("<!DOCTYPE" not in text.upper() and "<!ENTITY" not in text.upper(),
                "Caller snapshot cannot contain DTD/entity declarations.")
        document = minidom.parseString(text)
        require(document.documentElement.tagName == "Document" and not document.documentElement.namespaceURI,
                "Caller snapshot requires the native Document root.")
        documents.append(document)
    old, new = documents
    require((old.version, old.encoding, old.standalone) == (new.version, new.encoding, new.standalone),
            "Caller XML declaration changed.")

    def units(document):
        root = document.documentElement
        blocks = [node for node in root.childNodes if node.nodeType == Node.ELEMENT_NODE
                  and not node.namespaceURI and node.tagName in ("SW.Blocks.FC", "SW.Blocks.FB", "SW.Blocks.OB")]
        require(len(blocks) == 1, "Caller snapshot requires one direct native code block.")
        scopes = [node for node in blocks[0].childNodes if node.nodeType == Node.ELEMENT_NODE
                  and not node.namespaceURI and node.tagName == "ObjectList"]
        require(len(scopes) == 1, "Caller snapshot requires one direct native object scope.")
        found = list(document.getElementsByTagName("SW.Blocks.CompileUnit"))
        require(all(node.parentNode is scopes[0] for node in found),
                "Caller networks must belong to its direct native object scope.")
        ids = [node.getAttribute("ID") for node in document.getElementsByTagName("*") if node.hasAttribute("ID")]
        require(all(value.strip() for value in ids) and len(ids) == len(set(ids))
                and all(node.hasAttribute("ID") for node in found),
                "Caller snapshot has missing, empty or duplicate native object IDs.")
        return found

    old_count, new_count = len(units(old)), len(units(new))
    require(new_count == old_count + 1, "Caller must gain exactly one network with distinct native identity.")
    expected = native_payload(old.toxml())

    def restores(index):
        # Scope decision 2026-10-25: V21 shifts every following object ID by the
        # inserted network's ID count. Reverse exactly that shift, nothing else.
        candidate = new.cloneNode(True)
        unit = units(candidate)[index]
        inside = [unit, *unit.getElementsByTagName("*")]
        count = sum(node.hasAttribute("ID") for node in inside)
        nodes = list(candidate.getElementsByTagName("*"))
        for node in nodes[nodes.index(unit) + len(inside):]:
            if node.hasAttribute("ID"):
                value = node.getAttribute("ID")
                if not re.fullmatch(r"0|[1-9A-F][0-9A-F]{0,6}", value) or int(value, 16) < count:
                    return False
                node.setAttribute("ID", format(int(value, 16) - count, "X"))
        unit.parentNode.removeChild(unit)
        return native_payload(candidate.toxml()) == expected

    # Two candidates can restore only when they are equal up to that shift.
    require(any(restores(index) for index in range(new_count)),
            "Caller content outside the inserted network changed beyond the accepted following-ID shift.")


def first_available_db_number(blocks):
    used = {block['number'] for block in blocks if block['kind'] in ('DB', 'GlobalDB', 'InstanceDB')}
    return next(number for number in range(1, 65536) if number not in used)


def review_typed_effects(preview, manifest, read_artifact, expected_instance_number):
    """Accept only the two explicit creations and their bound typed dependencies."""
    effects, typed = preview['effects'], preview['typed']
    require(isinstance(effects, list) and all(isinstance(e, dict) for e in effects), 'Typed effects must be explicit objects.')
    mutations = [effect for effect in effects if 'action' in effect]
    standalone = manifest.get('callerMode') == 'createStandaloneFc'
    wanted_actions = ['create', 'createInstanceDB'] if standalone else ['graphical_typed_edit', 'createInstanceDB']
    require(sorted(effect['action'] for effect in mutations) == sorted(wanted_actions),
            'Typed effects contain missing, duplicate or unknown mutation actions.')
    if standalone:
        creation = next(effect for effect in mutations if effect['action'] == 'create')
        require(creation == {'action': 'create', 'blockName': manifest['caller']}, 'Fresh caller creation effect differs.')
    else:
        edit = next(effect for effect in mutations if effect['action'] == 'graphical_typed_edit')
        block = edit.get('existingBlock') or {}
        require(set(edit) == {'action', 'existingBlock'} and block.get('name') == manifest['caller'] and block.get('kind') == 'FC'
                and block.get('programmingLanguage') == manifest['language'] and block.get('isKnowHowProtected') is False
                and block.get('path') == typed['blockPath'], 'Existing caller edit effect differs.')
    instance = next(effect for effect in mutations if effect['action'] == 'createInstanceDB')
    require(set(instance) == {'action', 'artifact', 'autoNumber', 'calleePath', 'instanceOfName', 'name', 'number', 'path', 'startValueImpact'}
            and instance['instanceOfName'] == manifest['root'] and instance['path'].endswith('/' + manifest['instance']),
            'Fresh DB effect differs from fixture or has unknown mutation fields.')
    callees, instances, operands = typed['resolvedCallees'], typed['resolvedInstances'], typed['resolvedOperands']
    require(len(callees) == len(instances) == 1 and len(operands) == len(manifest['signals']) + 1,
            'Typed dependency inventory differs from the independently specified call.')
    require(callees[0]['name'] == manifest['root'] and callees[0]['blockType'] == 'FB'
            and instances[0]['identity'] == instance['path'] and instances[0]['calleePath'] == callees[0]['path']
            and instances[0]['components'] == [manifest['instance']], 'Typed effect dependency identities differ.')
    scope = preview['payload']['scopePath']
    require(typed['blockPath'] == scope + '/blocks/' + manifest['caller']
            and callees[0]['path'] == scope + '/blocks/' + manifest['root']
            and instance['path'] == scope + '/blocks/' + manifest['instance'], 'Typed dependency scope differs from exact component paths.')
    expected_operands = [{'key': 'p' + str(index), 'scope': 'GlobalVariable', 'components': [signal['name']],
                          'dataType': signal['dataType'], 'value': None, 'writable': True,
                          'identity': scope + '/tags/' + manifest['table'] + '::' + str(len(signal['name'])) + ':' + signal['name']}
                         for index, signal in enumerate(manifest['signals'])]
    expected_operands.append({'key': 'enable', 'scope': 'LiteralConstant', 'components': [], 'dataType': 'Bool',
                              'value': 'true', 'writable': False, 'identity': 'constant:Bool:true'})
    require([{key: value for key, value in row.items() if key != 'declarationHash'} for row in operands] == expected_operands,
            'Typed operand dependencies differ from independent signal mappings and explicit enable.')
    require(instance['calleePath'] == callees[0]['path'] and instance['name'] == manifest['instance']
            and type(expected_instance_number) is int and type(instance['number']) is int
            and instance['number'] == expected_instance_number and 1 <= instance['number'] <= 65535
            and instance['autoNumber'] is False
            and instance['startValueImpact'] == "New instance DB initialized from the exact captured system storage or callee's declared start values.",
            'Fresh DB mutation identity, first available number or initialization differs.')
    raw = read_artifact(instance['artifact'])
    require(len(raw) == instance['artifact']['bytes']
            and hashlib.sha256(raw).hexdigest() == instance['artifact']['sha256'] == instances[0]['declarationHash'],
            'Fresh DB artifact differs from the exact resolved storage dependency.')
    document = safe_xml(raw.decode('utf-8-sig'))
    blocks = [node for node in document if node.tag.startswith('SW.Blocks.')]
    require(len(blocks) == 1 and blocks[0].tag == 'SW.Blocks.InstanceDB', 'Fresh DB artifact must contain exactly one instance DB.')
    attrs = blocks[0].find('AttributeList')
    require(attrs is not None and all(attrs.findtext(key) == value for key, value in {
        'Name': manifest['instance'], 'InstanceOfName': manifest['root'], 'InstanceOfType': 'FB',
        'Number': str(expected_instance_number), 'AutoNumber': 'false'}.items()), 'Fresh DB native identity differs from reviewed mutation.')
    network_kind = 'replaceNetwork' if standalone else typed['operation']
    expected = [{'kind': network_kind, 'identity': typed['networkId'], 'sourceSha256': None,
                 'description': 'Native ' + manifest['language'] + ' network ' + network_kind + '; compilation and complete readback required.'}]
    expected += [{'kind': 'dependency', 'identity': row['path'], 'sourceSha256': row['interfaceHash'],
                  'description': 'Exact FB callee interface dependency.'} for row in callees]
    expected += [{'kind': 'instanceDependency', 'identity': row['identity'], 'sourceSha256': row['declarationHash'],
                  'description': 'Explicit instance storage; no internal IEC offsets are synthesized.'} for row in instances]
    expected += [{'kind': 'operandDependency', 'identity': row['identity'], 'sourceSha256': row['declarationHash'],
                  'description': row['scope'] + ' ' + row['dataType'] + ' operand.'} for row in operands]
    metadata = [effect for effect in effects if 'action' not in effect]
    if not standalone:
        # Lane A's typed route reports V21's following-ID renumbering as its own effect; it must name the inserted network.
        shifts = [effect for effect in metadata if effect.get('kind') == 'followingObjectIdShift']
        require(len(shifts) == 1 and shifts[0].get('identity') == typed['networkId'], 'Typed insertion must report exactly one following-ID shift.')
        metadata.remove(shifts[0])
    normalize = lambda rows: sorted(json.dumps(row, sort_keys=True) for row in rows)
    require(normalize(metadata) == normalize(expected), 'Typed metadata effects contain missing, duplicate, unknown or changed dependencies.')


def review_step(value, selection, manifest, read_artifact, expected_instance_number=None):
    exact = value["review"]
    require(exact["previewHash"] == value["previewHash"], "Step review hash differs from wrapper.")
    if value["state"] == "library_prepared":
        require(exact["preview"]["kind"] == "library_instantiate" and exact["preview"]["selection"] == selection,
                "Fresh import differs from independently pinned root.")
        closures = exact["preview"]["dependencies"]
        require(len(closures) == 1, "Fresh import has an unexpected dependency root set.")
        verify_dependency_graph(closures[0]["versions"], manifest)
    else:
        require(value["state"] == "graphical_prepared" and exact["preview"]["kind"] == "graphical_typed_edit",
                "Fresh stage is not the expected typed edit.")
        text = read_artifact(exact["preview"]["typed"]["nativeArtifact"]).decode("utf-8-sig")
        verify_component_call(text, manifest)
        if manifest.get("callerMode") == "createStandaloneFc":
            verify_standalone_caller(text, manifest)
            payload = exact["preview"]["payload"]
            require(payload["model"]["operation"] == "createBlock" and "blockPath" not in payload
                    and payload["model"]["creation"] == {"name": manifest["caller"], "blockType": "FC", "interface": []},
                    "Fresh typed payload must create the exact standalone FC.")
        review_typed_effects(exact['preview'], manifest, read_artifact, expected_instance_number)
    return exact


class ComponentRun:
    def __init__(self, args):
        require(args.run_native, "Explicit --run-native is required.")
        self.args, self.manifest = args, json.loads((FIXTURES / "manifest.json").read_text())
        if getattr(args, "caller_mode", None) == "existing":
            # The same caller source exists before import; the component call is inserted after its network.
            self.manifest["callerMode"] = "existing"
        policy = artifact_policy(args.artifacts, args.artifact_policy_root or ROOT / "artifacts")
        self.output = args.output.resolve()
        self.output.mkdir(parents=True, exist_ok=False)
        self.native = args.artifacts.resolve() / ("c" + uuid.uuid4().hex[:6])
        require(len(str(self.native / "scratch" / ("TiaMcpScratch_" + "0" * 24) / ("TiaMcpScratch_" + "0" * 24))) <= 143,
                "Choose a short native --artifacts path.")
        self.native.mkdir(parents=True, exist_ok=False)
        self.store_root = (ROOT / "artifacts").resolve()
        require(self.store_root.is_dir(), "The canonical repository artifacts store must exist.")
        self.report = {"schemaVersion": 1, "fixture": self.manifest, "status": "running", "startedAtUtc": utc_now(),
            "artifactPolicy": policy, "nativeArtifactRoot": str(self.store_root), "fixtureWorkspace": str(self.native), "events": [], "fixtureChanges": [],
            "productionApplySupported": False, "runtimeVerified": False, "automaticReplayAllowed": False,
            "preservationVerified": False, "freshReopenVerified": False, "firstCheckpointRecoveryVerified": False}
        self.report["sourceMode"] = "reuse_exact_native04_source_new_consumer" if getattr(args, "reuse_source_report", None) else "author_new_source"
        self.report["harnessFiles"] = [{"path": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
            for path in (Path(__file__), Path(__file__).with_name("qualification_owned_scratch.py"),
                         Path(__file__).with_name("qualify_typed_resolution.py"), Path(__file__).with_name("smoke_mcp.py"),
                         Path(__file__).with_name("run_block_tests.py"), Path(__file__).with_name("component_source_reuse.py"), Path(__file__).with_name("component_window_lifetime.py"),
                         Path(__file__).with_name("component_exact_lifetime.py"), Path(__file__).with_name("component_window_recovery.py"), Path(__file__).with_name("retained_native_lifecycle.py"))]
        self.session = None
        self.component_id = None
        self.restore_dispatched = False
        self.lifetime = ComponentLifetime(self)
        self.persist()

    def persist(self):
        # Retry only the same report pending-file replacement for bounded Windows
        # sharing/access collisions. No native call is inside this retry scope.
        save_json(self.output / "result.json", self.report)

    def retain(self, event, value):
        self.report["events"].append({"event": event, "utc": utc_now(), "value": copy.deepcopy(value)})
        self.persist()
        if "state" in value: print(event + ": " + value["state"], flush=True)

    def call(self, tool, arguments=None):
        if tool.startswith("library_"): self.session.guard()
        return self.session.call(tool, arguments)

    def apply(self, kind, payload):
        prepared = self.call("change_prepare", {"kind": kind, "payload": payload})
        row = {"kind": kind, "prepared": prepared, "dispatched": True}
        self.report["fixtureChanges"].append(row); self.persist()
        result = self.call("change_apply", step_request(prepared))
        row["result"] = result; self.persist()
        require(result.get("state") == "applied", "Fixture setup did not apply; no retry.")
        return result

    def source_ref(self, library):
        entries = [x for x in self.call("library_list")["libraries"] if x["libraryId"] == library]
        require(len(entries) == 1, "Exact source library no longer resolves.")
        return {"libraryId": library, "revision": entries[0]["revision"]}

    def query(self, library, **arguments):
        result = self.call("library_query", {"request": {"libraryId": library, "maxItems": 200, **arguments}})
        require(result["completeness"]["scopeComplete"] and result.get("nextOffset") is None, "Native library evidence is incomplete.")
        return result["items"]

    def author(self):
        self.session.start()
        self.call("tia_create_scratch", {"cpuTypeIdentifier": "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"})
        # Installed library blocks are not editable, so PLCSIM can run them only if
        # the source was compiled with simulation support before release.
        self.apply("project_simulation_enable", {"enabled": True})
        plc = complete_inventory(self.call)["plcPath"]
        library = self.call("library_open", {"request": {"kind": "project"}})["libraryId"]
        documents = self.native / "documents"
        shutil.copytree(FIXTURES, documents)
        self.report["fixtureInputs"] = [{"name": p.relative_to(documents).as_posix(), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
                                      for p in sorted(documents.rglob("*")) if p.is_file()]
        self.persist()
        released = {}
        for name, composition in ((self.manifest["dependency"], "types"), (self.manifest["root"], "blocks")):
            authoring_documents = documents
            if name == self.manifest["root"]:
                native_document_path = documents / self.manifest["rootDocument"]
                require(native_document_path.stem == name and native_document_path.suffix == ".xml", "Root authoring document must be exact native SimaticML.")
                verify_explicit_source_attributes(native_document_path.read_text(encoding="utf-8-sig"), self.manifest)
                authoring_documents = native_document_path.parent
            self.apply("library_create_type", {"schemaVersion": 1, "destinationLibraryId": library,
                "documentDirectory": str(authoring_documents), "documentName": name, "targetEnvironment": "selectedPlc",
                "destinationComposition": composition, "target": {"plcPath": plc}})
            rows = [v for v in self.query(library) if v.get("kind") == "version" and v["typeName"] == name]
            require(len(rows) == 1 and rows[0]["state"] == "InWork", "Expected one newly authored in-work type.")
            result = self.apply("library_release_version", {"schemaVersion": 1, "source": self.source_ref(library),
                "selection": [pin(rows[0])], "versionNumber": self.manifest["version"], "author": "TIA MCP qualification",
                "comment": "Independent component fixture " + name})
            committed = result["journal"]["result"]["objects"]
            require(len(committed) == 1, "Release returned ambiguous native identity.")
            released[name] = pin(committed[0])
        closure = self.query(library, query="dependencies", **released[self.manifest["root"]])
        self.report["authoredDependencyClosure"] = closure; self.persist()
        verify_dependency_graph(closure, self.manifest)
        global_id = self.apply("library_create", {"schemaVersion": 1, "name": "ComponentQualification"})["journal"]["result"]["library"]["libraryId"]
        self.apply("library_publish", {"schemaVersion": 1, "source": self.source_ref(library),
            "selection": [released[self.manifest["root"]]], "destinationLibraryId": global_id})
        archive = self.apply("library_archive", {"schemaVersion": 1, "source": self.source_ref(global_id)})["journal"]["result"]["archive"]
        require(hashlib.sha256(Path(archive["path"]).read_bytes()).hexdigest() == archive["sha256"], "Published archive bytes changed.")
        self.report["sourceArchive"] = archive; self.persist()
        self.call("library_close", {"libraryId": global_id})
        self.session.close()
        return archive, released

    def consumer(self, archive, released):
        self.session.start()
        scratch = self.call("tia_create_scratch", {"cpuTypeIdentifier": "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"})
        self.report["consumerScratch"] = scratch
        opened_library = self.call("library_open", {"request": {"path": archive["path"], "expectedSha256": archive["sha256"]}})
        library = opened_library["libraryId"]
        verify_dependency_graph(self.query(library, query="dependencies", **released[self.manifest["root"]]), self.manifest)
        # An independently authored peer proves complete preservation; the caller
        # selected for installation is deliberately absent before the first import.
        peer_source = (self.native / "documents" / (self.manifest["caller"] + ".s7dcl")).read_text(encoding="utf-8-sig")
        peer_source = peer_source.replace(self.manifest["caller"], self.manifest["unrelatedCaller"])
        self.apply("source_upsert", {"language": "SIMATIC_SD", "source": peer_source,
            "expectedBlockNames": [self.manifest["unrelatedCaller"]]})
        existing = self.manifest["callerMode"] == "existing"
        if existing:
            caller_source = (self.native / "documents" / (self.manifest["caller"] + ".s7dcl")).read_text(encoding="utf-8-sig")
            self.apply("source_upsert", {"language": "SIMATIC_SD", "source": caller_source, "expectedBlockNames": [self.manifest["caller"]]})
        inventory = complete_inventory(self.call)
        plc = inventory["plcPath"]
        empty = empty_tag_template(inventory, plc)
        xml = artifact_bytes(self.call, self.call("tag_table_read", {"tagTablePath": empty["path"]})["artifact"]).decode("utf-8-sig")
        first, second = self.manifest["signals"]
        tags = safe_xml(tag_fixture(xml, self.manifest["table"], first["name"], first["address"]))
        objects = tags.find("SW.Tags.PlcTagTable/ObjectList")
        tag = copy.deepcopy(objects.find("SW.Tags.PlcTag"))
        tag.set("ID", format(max(int(n.get("ID"), 16) for n in tags.iter() if n.get("ID")) + 1, "X"))
        tag.find("AttributeList/Name").text = second["name"]
        tag.find("AttributeList/LogicalAddress").text = second["address"]
        objects.append(tag)
        self.apply("tag_table_import_xml", {"xml": ET.tostring(tags, encoding="unicode"), "expectedTableNames": [self.manifest["table"]]})
        baseline = snapshot(self.call, self.output / "before-import")
        caller = plc + "/blocks/" + self.manifest["caller"]
        if existing:
            require(select(baseline["inventory"]["blocks"], self.manifest["caller"], plc)["path"] == caller, "Existing caller must be in the initial checkpoint.")
            network, following = terminal_network(Path(baseline["objects"]["block:" + caller]["file"]).read_text(encoding="utf-8-sig"), self.manifest["language"])
            self.report["callerInitialState"] = {"mode": "existing", "path": caller, "exists": True, "calledFromOb": False,
                "insertAfterNetworkId": network, "followingObjectIds": following}
            caller_recipe = {"mode": "existing", "blockPath": caller, "insertAfterNetworkId": network}
        else:
            require(not [b for b in baseline["inventory"]["blocks"] if b["name"].lower() == self.manifest["caller"].lower()],
                    "Standalone caller must be absent from the initial checkpoint.")
            self.report["callerInitialState"] = {"mode": "createStandaloneFc", "path": caller, "exists": False, "calledFromOb": False}
            caller_recipe = {"mode": "createStandaloneFc", "name": self.manifest["caller"], "language": self.manifest["language"],
                             "numberPolicy": "firstAvailable"}
        table = select(baseline["inventory"]["tagTables"], self.manifest["table"], plc)
        mappings = [{"parameter": signal["parameter"], "direction": signal["direction"], "dataType": signal["dataType"],
            "tagPath": select(table["tags"], signal["name"])["path"]} for signal in self.manifest["signals"]]
        payload = {"schemaVersion": 1, "action": "plc_component_plan", "planOnly": True, "source": self.source_ref(library),
            "selection": [released[self.manifest["root"]]], "target": {"projectName": scratch["project"]["name"], "plcPath": plc},
            "integration": {"dependencyPins": [released[self.manifest["dependency"]]],
                "instanceDb": {"name": self.manifest["instance"], "numberPolicy": "firstAvailable"},
                "caller": caller_recipe, "mappings": mappings}}
        prepared = self.call("change_prepare", {"kind": "library_integrate", "payload": payload})
        self.report["plan"] = prepared; self.persist()
        plan = prepared["preview"]["integrationPlan"]
        require(not plan["blockers"] and plan["caller"]["initiallyAbsent"] is (not existing) and plan["caller"]["blockPath"] == caller,
                "Caller plan differs: " + canonical(plan["blockers"]))
        # No native mutation has been dispatched by planning. This exact plan is
        # reviewed separately from each real prepared stage below.
        verify_dependency_graph(plan["versions"], self.manifest)
        root_version = next(version for version in plan["versions"] if version["typeName"] == self.manifest["root"])
        self.report["sourceContentEvidenceAttempt"] = {"pin": copy.deepcopy(root_version), "verified": False}; self.persist()
        source_bytes = artifact_bytes(self.call, root_version["artifact"])
        oracle_bytes = artifact_bytes(self.call, root_version["contentPolicy"]["sourceOracleArtifact"])
        self.report["sourceContentEvidenceAttempt"].update(baseObservedSha256=hashlib.sha256(source_bytes).hexdigest(), oracleObservedSha256=hashlib.sha256(oracle_bytes).hexdigest())
        self.persist()
        verify_pinned_source_oracle(source_bytes, oracle_bytes, root_version, self.manifest)
        self.report["sourceContentEvidenceAttempt"]["verified"] = True
        self.report["explicitSourceAttributesVerified"] = True; self.report["sourceContentPolicyVerified"] = True; self.persist()
        require(plan["steps"][0]["prepareArguments"]["payload"]["selection"] == payload["selection"], "Planned root pin differs.")
        initial = self.lifetime.begin(prepared, opened_library)
        self.component_id = initial["changeId"]

        def review(value):
            # The exact imported closure contains only FB + UDT, so no DB number
            # is added between this complete baseline and typed preparation.
            expected_number = first_available_db_number(baseline['inventory']['blocks'])
            exact = review_step(value, payload["selection"], self.manifest, lambda artifact: artifact_bytes(self.call, artifact), expected_number)
            self.retain("step_reviewed", exact)

        installed = drive_component(self.call, initial, review, self.retain)
        self.report["installed"] = installed; self.persist()
        after = snapshot(self.call, self.output / "after-install")
        added = ["block:" + select(after["inventory"]["blocks"], name, plc)["path"]
                 for name in (self.manifest["root"], self.manifest["instance"]) + (() if existing else (self.manifest["caller"],))]
        added.append("type:" + select(after["inventory"]["types"], self.manifest["dependency"], plc)["path"])
        verify_preserved_installation(baseline, after, caller, added)
        verify_component_call(Path(after["objects"]["block:" + caller]["file"]).read_text(encoding="utf-8-sig"), self.manifest)
        if existing:
            proof = installed["record"]["installationEvidence"]["callerInsertion"]
            require(proof["verified"] is True and proof["insertAfterNetworkId"] == network, "Native caller insertion proof differs.")
            self.report["callerInsertion"] = proof
        else:
            verify_standalone_caller(Path(after["objects"]["block:" + caller]["file"]).read_text(encoding="utf-8-sig"), self.manifest)
        self.report["preservationVerified"] = True; self.persist()
        self.call("library_close", {"libraryId": library})
        path = self.session.current_project
        self.session.reopen(path)
        original_fresh = self.lifetime.resume()
        self.report["windowOriginalFresh"] = original_fresh; self.persist()
        reopened = snapshot(self.call, self.output / "reopened-install")
        assert_snapshot(after, reopened)
        # Both source modes install through the captured storage profile.
        instance = "block:" + select(after["inventory"]["blocks"], self.manifest["instance"], plc)["path"]
        self.report["exactStorageFreshReopen"] = verify_exact_storage_reopen(after, reopened, instance)
        self.persist()
        native_versions = original_fresh["componentWindow"]["observation"]["componentVersions"]
        require({(v["typeGuid"], v["versionGuid"]) for v in native_versions} == {(v["typeGuid"], v["versionGuid"]) for v in released.values()} and
                all(v["exists"] is True and v["state"] == "Committed" and v["doNotUse"] is False and len(v["objectPaths"]) == 1 for v in native_versions),
                "Fresh parent-owned native dependency/use evidence differs.")
        self.report["freshReopenVerified"] = True; self.persist()
        self.recover(installed["record"], baseline)

    def recover(self, record, baseline):
        checkpoint = record["initialCheckpoint"]
        require(checkpoint["changeId"] == record["library"]["prepared"]["changeId"]
                and checkpoint["previewHash"] == record["library"]["prepared"]["previewHash"], "Recovery anchor is not first import.")
        require(hashlib.sha256(Path(checkpoint["archivePath"]).read_bytes()).hexdigest() == checkpoint["sha256"], "First checkpoint archive changed.")
        self.session.close()
        self.session.start()
        self.lifetime.resume()
        status = self.session.audit("tia_session_status")
        require(status.get("ownsPortalProcess") is True and status.get("processId") == self.session.session["processId"]
                and status.get("projects") == [] and not self.restore_dispatched, "Recovery requires one fresh empty owned session, at most once.")
        request = {"changeId": checkpoint["changeId"], "previewHash": checkpoint["previewHash"]}
        self.restore_dispatched = True
        self.retain("initial_checkpoint_restore_dispatching", request)
        # Existing library restore requires an EMPTY Portal, unlike engineering
        # restore. Use the same audited tool call after the stricter empty guard.
        restored = self.lifetime.control("recover")
        self.retain("initial_checkpoint_restore_result", restored)
        resources = restored.get("resources", [])
        require(restored.get("state") == "restored_to_new_locations" and len(resources) == 1
                and resources[0].get("kind") == "project" and resources[0].get("retrieved") is True
                and resources[0].get("archiveSha256") == checkpoint["sha256"], "First checkpoint recovery receipt differs.")
        recovered = resources[0]["project"]["path"]
        require(Path(recovered).resolve() != Path(checkpoint["originalPath"]).resolve(), "Recovery overwrote original project.")
        self.session.remember(recovered)
        self.session.guard()
        assert_snapshot(baseline, snapshot(self.call, self.output / "recovered-first-checkpoint"))
        self.session.reopen(recovered)
        restored_fresh = self.lifetime.resume()
        assert_snapshot(baseline, snapshot(self.call, self.output / "reopened-recovery"))
        require(all(v["exists"] is False for v in restored_fresh["componentWindow"]["observation"]["componentVersions"]), "Recovered initial project still contains component library types.")
        completed = self.lifetime.control("complete")
        require(completed["state"] == "released" and completed["lifecycleEvidenceComplete"] is True, "Complete parent-owned lifecycle evidence was not accepted.")
        self.report["componentWindowComplete"] = completed
        self.report["firstCheckpointRecoveryVerified"] = True; self.persist()

    def run(self):
        original_inputs = []
        primary_error = None
        try:
            reuse = getattr(self.args, "reuse_source_report", None)
            expected = getattr(self.args, "reuse_source_report_sha256", None)
            require(bool(reuse) == bool(expected), "Source reuse requires both report path and its reviewed SHA256.")
            require(not reuse or not getattr(self.args, "recover_from_report", None), "Source reuse and checkpoint-only recovery are mutually exclusive.")
            if reuse:
                archive, released, provenance = prepare_source_reuse(reuse, expected, FIXTURES, self.manifest,
                    self.native, self.args.artifact_policy_root or ROOT / "artifacts", verify_dependency_graph, verify_pinned_source_oracle)
                original_inputs = provenance["originalInputFiles"]
                self.report.update(sourceReuse=provenance, sourceArchive=archive, fixtureInputs=provenance["fixtureInputs"])
                self.persist()
            # No host, worker or TIA startup exists before the retained source
            # preflight and exact fresh archive copy have completed.
            self.session = OwnedScratch(self.args.host, self.args.worker, self.store_root, self.report, self.persist,
                timeout=self.args.timeout, client_factory=component_client, extra_tools=EXTRA_TOOLS, lifecycle_directory=self.output / "exact-process-lifetimes")
            if not reuse:
                archive, released = self.author()
            self.consumer(archive, released)
            self.report["status"] = "passed"
        except Exception as error:
            primary_error = error
            self.report.update(status="failed", failure=str(error), traceback=traceback.format_exc())
            # Inspection only; a broken bridge/uncertain response never triggers
            # replay, continuation, recovery, or a second owned session.
            if self.lifetime.owner and self.session is not None and self.session.client:
                try: self.report["failureStatus"] = self.lifetime.control("status")
                except Exception as inspection: self.report["failureStatusError"] = str(inspection)
            self.persist()
            raise
        finally:
            cleanup_error = input_error = None
            try:
                if self.session is not None: self.session.close()
            except Exception as error:
                cleanup_error = error
                self.report.update(status="failed", cleanupFailure=str(error))
            try:
                self.lifetime.close()
            except Exception as error:
                cleanup_error = cleanup_error or error
                self.report.update(status="failed", componentStoreCloseFailure=str(error))
            try:
                if original_inputs:
                    validate_original_inputs(original_inputs)
                    self.report["originalSourceInputsPreserved"] = True
            except Exception as error:
                input_error = error
                self.report.update(status="failed", originalSourceInputFailure=str(error), originalSourceInputsPreserved=False)
            self.report["completedAtUtc"] = utc_now(); self.persist()
            print("Report: " + str(self.output / "result.json"), flush=True)
            # Retain the first failure as the raised error. Cleanup and original
            # input audit each execute independently and remain separate receipts.
            if primary_error is None:
                if cleanup_error is not None: raise cleanup_error
                if input_error is not None: raise input_error

    def run_recovery(self, previous_report):
        self.report["qualificationScope"] = "first_checkpoint_recovery_only"
        self.report["installationAccepted"] = self.report["installationVerified"] = False
        try:
            parent_recovery_inputs(self, previous_report)
        except Exception as error:
            # A rejected retained-input contract is terminal before creating any
            # native session. Keep that distinction durable in the new report.
            self.report.update(status="failed", failure=str(error), traceback=traceback.format_exc(),
                completedAtUtc=utc_now(), recoveryPreflightFailed=True, nativeRecoveryDispatched=False)
            self.persist()
            raise
        try:
            run_parent_recovery(self, previous_report, OwnedScratch, component_client, EXTRA_TOOLS)
        except Exception as error:
            self.report.update(status="failed", failure=str(error), traceback=traceback.format_exc()); self.persist(); raise
        finally:
            self.report["completedAtUtc"] = utc_now(); self.persist()
            print("Report: " + str(self.output / "result.json"), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--artifact-policy-root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    parser.add_argument("--caller-mode", choices=("createStandaloneFc", "existing"), default="createStandaloneFc",
                        help="existing: insert the component call into a pre-existing caller FC (insertion-ID scope decision 2026-10-25).")
    source_mode = parser.add_mutually_exclusive_group()
    source_mode.add_argument("--recover-from-report", type=Path, help="Explicit separate first-checkpoint recovery from one failed report; never resumes installation.")
    source_mode.add_argument("--reuse-source-report", type=Path, help="Reuse only native04's exact committed archive in a new consumer; never author/reopen/resume the old attempt.")
    parser.add_argument("--reuse-source-report-sha256", help="Required exact reviewed SHA256 of --reuse-source-report.")
    parser.add_argument("--known-store-root", type=Path, action="append", default=[], help="Additional known retained engineering store; locked in canonical order for the component window.")
    args = parser.parse_args()
    require(5 <= args.timeout <= 900, "Timeout must be 5..900 seconds.")
    require(not args.recover_from_report or not args.reuse_source_report_sha256, "Source-reuse hash cannot accompany checkpoint-only recovery.")
    run = ComponentRun(args)
    if args.recover_from_report: run.run_recovery(args.recover_from_report)
    else: run.run()


if __name__ == "__main__": main()
