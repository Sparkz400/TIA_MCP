"""Extract deployable profile evidence from successful independent native captures.

This copies the actual native export bytes and preserves observed native Part,
CompileUnit and Static Member payloads. Pin direction/type contracts below are
independently reviewed against each source program and the exported wires;
a wired pin outside its contract, or an unwired required pin, stops extraction.
Compilation of reference fixtures is not qualification of the typed builder.
"""
import argparse
import hashlib
import json
import pathlib
from xml.dom import minidom

from engineering_preservation import native_payload_hash
from smoke_mcp import ROOT

NET = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"
INTERFACE = "http://www.siemens.com/automation/Openness/SW/Interface/v5"
# Global IEC storage reference DB and its system type, by instruction (else kind).
GLOBAL_STORAGE = {"timer": ("Tgp_GlobalTimer", "IEC_TIMER"), "counter": ("Tgp_GlobalCounter", "IEC_COUNTER"),
                  "TONR": ("Tgp_GlobalTonr", "IEC_TIMER"), "R_TRIG": ("Tgp_GlobalRTrig", "R_TRIG"), "F_TRIG": ("Tgp_GlobalFTrig", "F_TRIG")}


def fragment(node, namespace=None):
    node = node.cloneNode(True)
    if namespace:
        node.setAttribute("xmlns", namespace)
    return node.toxml()


def pin(name, direction, data_type, required=True):
    return {"name": name, "direction": direction, "dataType": data_type, "required": required}


def extract(reports, route="standard", *, stage_output=None):
    if stage_output is not None:
        stage_output = pathlib.Path(stage_output).resolve()
        if not stage_output.is_relative_to((ROOT / "artifacts").resolve()):
            raise ValueError("Candidate extraction must stay under repository artifacts.")
    template_root = stage_output if stage_output is not None else ROOT / "fixtures/typed-graphical"
    native = template_root / "native"
    native.mkdir(parents=True, exist_ok=True)
    sources, observed, profiles = [], {}, []
    loaded = [(path, json.loads(path.read_text(encoding="utf-8"))) for path in reports]
    archive_cpu = {}
    for _, item in loaded:
        if "hardware" in item:
            cpus = [value for value in item["hardware"]["items"] if value.get("classification") == "CPU"]
            assert len(cpus) == 1
            cpu_type = next(a["value"] for a in cpus[0]["attributes"] if a["name"] == "TypeIdentifier")
            archive_cpu[item["sourceArchive"]["sha256"]] = cpu_type
    for report_path, report in loaded:
        assert report["builderUsed"] is False and report["origin"] in ("independently_authored_simatic_sd", "independently_authored_native_references")
        cpu = report["scratch"]["selectedCpu"]["typeIdentifier"] if "scratch" in report else archive_cpu[report["sourceArchive"]["sha256"]]
        for entry in report["fixtures"]:
            if not entry.get("engineeringVerified") or entry.get("route", "standard") != route:
                continue
            source_path = pathlib.Path(entry["sourcePath"])
            for exported in entry["exports"]:
                records = [row["result"] for row in report["calls"] if row["tool"] == "block_read" and
                           row.get("status") == "passed" and row["result"]["block"]["name"] == exported["name"]]
                if stage_output is not None:
                    identity = exported.get("block", {})
                    if (not identity.get("path") or identity.get("name") != exported["name"]
                            or not isinstance(identity.get("scopePath"), str) or not identity["scopePath"]
                            or not isinstance(entry.get("scopePath"), str) or not entry["scopePath"]
                            or not isinstance(identity.get("identity", {}).get("namespace"), str)
                            or not isinstance(entry.get("namespace"), str)
                            or identity.get("scopePath") != entry.get("scopePath")
                            or identity.get("identity", {}).get("namespace") != entry.get("namespace")):
                        raise ValueError("Candidate export lacks an exact captured scope/namespace identity.")
                    records = [row for row in records if row["block"].get("path") == identity["path"]
                        and all(identity.get(key) == value for key, value in row["block"].items())
                        and row["artifact"]["sha256"] == exported.get("sha256")]
                    if not records:
                        raise ValueError("No exact native read receipt matches the captured candidate export.")
                record = records[0]
                raw = pathlib.Path(record["artifact"]["path"]).read_bytes()
                assert hashlib.sha256(raw).hexdigest() == record["artifact"]["sha256"]
                target = native / (exported["name"] + ".xml")
                if stage_output is not None and (any(c in exported["name"] for c in ("/", "\\", ":"))
                        or target.parent.resolve() != native.resolve()):
                    raise ValueError("Candidate native filename must remain directly inside staging/native.")
                if stage_output is not None and target.exists():
                    # A source admitted again in a later capture run only confirms the first capture: same authored
                    # source and the same native payload. Anything else is a real collision.
                    first = next((s for s in sources if s["path"] == target.relative_to(ROOT).as_posix()), None)
                    if (first is None or first["transportSourceSha256"] != entry["sha256"] or first["changeId"] == entry["changeId"]
                            or native_payload_hash(target.read_text(encoding="utf-8-sig")) != native_payload_hash(raw.decode("utf-8-sig"))):
                        raise ValueError("Candidate native filename collision; select exact source/route identities: " + str(target))
                    first.setdefault("confirmingCaptures", []).append({"captureReport": report_path.relative_to(ROOT).as_posix(),
                        "changeId": entry["changeId"], "sha256": hashlib.sha256(raw).hexdigest()})
                    continue
                target.write_bytes(raw)
                source_hash = hashlib.sha256(raw).hexdigest()
                source_bytes = source_path.read_bytes()
                if hashlib.sha256(source_path.read_text(encoding="utf-8-sig").encode()).hexdigest() != entry["sha256"]:
                    raise ValueError("Authored source content changed since captured transport: " + str(source_path))
                record = {"path": target.relative_to(ROOT).as_posix(), "sha256": source_hash,
                    "authoredSourcePath": source_path.relative_to(ROOT).as_posix(), "authoredSourceSha256": hashlib.sha256(source_bytes).hexdigest(),
                    "transportSourceSha256": entry["sha256"], "transportNormalization": "UTF-8 source text; CRLF/CR normalized to LF by Python text read; native request written with UTF-8 BOM",
                    "captureReport": report_path.relative_to(ROOT).as_posix(), "changeId": entry["changeId"],
                    "nativeImportCompileVerified": True, "builderUsed": False, "runtimeVerified": False}
                if entry.get("authoredFormat") == "native_xml_template_derivation":
                    record["authoredFormat"] = entry["authoredFormat"]
                    record["transportNormalization"] = "UTF-8 native XML source text; CRLF/CR normalized to LF by Python text read; submitted through block_import_xml"
                    derivation_path = source_path.with_suffix(".provenance.json")
                    record["derivationPath"] = derivation_path.relative_to(ROOT).as_posix()
                    record["derivationSha256"] = hashlib.sha256(derivation_path.read_bytes()).hexdigest()
                if entry.get("authoredFormat") == "instance_db_spec":
                    record["authoredFormat"] = entry["authoredFormat"]
                    record["transportNormalization"] = "UTF-8 JSON spec; name and instanceOfName submitted through instance_db_create (PlcBlockComposition.CreateInstanceDB)"
                sources.append(record)
                observed_name = exported["name"]
                if route != "standard":
                    prefix = "Unit" if route == "softwareUnit" else "Namespace"
                    observed_name = observed_name.replace("Tgp_" + prefix + "_", "Tgp_Boolean_").replace("Tgp" + prefix + "_", "Tgp_")
                observed[observed_name] = (minidom.parseString(raw), target, source_hash, cpu)
    def add(block, language, kind, native_name, instruction=None, data_type=None, pins=None, reader_only=False):
        if block not in observed:
            return
        document, target, source_hash, cpu = observed[block]
        # MOVE/arithmetic/edge casing comes from the capture; the worker matches those families case-insensitively too.
        folded = kind in ("move", "math", "edge")
        candidates = [part for part in document.getElementsByTagName("Part")
                      if (part.getAttribute("Name").lower() == native_name.lower() if folded else part.getAttribute("Name") == native_name)]
        if kind == "call":
            candidates = list(document.getElementsByTagName("Call"))
        if kind == "powerrail":
            candidates = list(document.getElementsByTagName("Powerrail"))
        if not candidates:
            raise ValueError("Native reference lacks exact instruction: " + block + "/" + native_name)
        if kind in ("and", "or", "branch") and not reader_only:
            candidates = [part for part in candidates if not part.getElementsByTagName("Negated")]
        if kind == "contact":
            candidates = [part for part in candidates if not part.getElementsByTagName("Negated")]
        if kind == "not":
            # Never convert an assignment inversion into a different NOT format.
            candidates = [part for part in candidates if len(part.getElementsByTagName("Negated")) == 1
                          and part.getElementsByTagName("Negated")[0].getAttribute("Name") == "in1"]
        if len(candidates) == 0:
            raise ValueError("Native reference is not the requested profile variant: " + block + "/" + kind)
        part = candidates[0]
        native_name = part.getAttribute("Name") or native_name
        wired = {con.getAttribute("Name") for con in document.getElementsByTagName("NameCon") if con.getAttribute("UId") == part.getAttribute("UId")}
        if pins and kind != "call" and not {item["name"] for item in pins if item["required"]} <= wired <= {item["name"] for item in pins}:
            raise ValueError("Reviewed pin contract differs from the pins wired in " + block + ": " + ", ".join(sorted(wired)))
        profile = {"id": "v21.fw41." + (route + "." if route != "standard" else "") + language.lower() + "." + kind + ("." + instruction if instruction else "") +
                   ("." + data_type.lower() if data_type else "") + ".v1", "version": 1,
            "language": language, "kind": kind, "instruction": instruction, "dataType": data_type,
            "tiaVersion": "V21", "cpuFamily": cpu, "route": route, "nativeName": native_name,
            "nativeTemplateXml": fragment(part, NET), "sourceSha256": source_hash,
            "provenance": target.relative_to(ROOT).as_posix(), "nativeFormatValidated": True,
            "engineeringVerified": False, "preservationVerified": False, "persistenceVerified": False,
            "recoveryVerified": False, "runtimeVerified": False,
            "networkTemplateXml": fragment(document.getElementsByTagName("SW.Blocks.CompileUnit")[0]),
            "pins": pins or []}
        if reader_only:
            profile["id"] += ".reader"
            profile["readerOnly"] = True
        if kind == "call" and route != "standard":
            call_info = part.getElementsByTagName("CallInfo")[0]
            namespace = document.getElementsByTagName("Namespace")[0].firstChild.data
            assert call_info.getAttribute("Name").startswith(namespace + ".")
            assert not call_info.hasAttribute("Namespace")
            profile["calleeNameEncoding"] = "namespaceQualified"
        if kind in ("timer", "counter", "edge"):
            member = next(m for m in document.getElementsByTagName("Member") if m.getAttribute("Name") == "Storage")
            profile["instanceType"] = member.getAttribute("Datatype")
            profile["instanceDeclarationXml"] = fragment(member, INTERFACE)
            global_name, global_type = GLOBAL_STORAGE.get(instruction) or GLOBAL_STORAGE[kind]
            global_call = "Tgp_" + instruction + "_Global_" + language
            if global_name in observed and global_call in observed:
                global_document, _, global_hash, _ = observed[global_name]
                if [node.firstChild.data for node in global_document.getElementsByTagName("InstanceOfName")] != [global_type]:
                    raise ValueError("Global storage reference is not one " + global_type + " instance: " + global_name)
                profile["globalInstanceType"] = global_type
                profile["globalInstanceTemplateXml"] = global_document.toxml()
                profile["globalInstanceSourceSha256"] = global_hash
        if kind == "call" and "Tgp_FB_multi_" + language in observed:
            fb_doc = observed["Tgp_FB_multi_" + language][0]
            member = next(m for m in fb_doc.getElementsByTagName("Member") if m.getAttribute("Name") == "Child")
            profile["instanceDeclarationXml"] = fragment(member, INTERFACE)
        profiles.append(profile)
    for language in ("LAD", "FBD"):
        boolean = "Tgp_Boolean_" + language
        if language == "LAD":
            add(boolean, language, "powerrail", "Powerrail")
            add(boolean, language, "contact", "Contact")
            add(boolean, language, "coil", "Coil")
            add(boolean, language, "branch", "O")
        else:
            add(boolean, language, "assign", "Coil")
            add(boolean, language, "or", "O")
            add("Tgp_And_FBD", language, "and", "A")
            add(boolean, language, "and", "A", reader_only=True)
            add("Tgp_Not_FBD", language, "assign", "Coil", reader_only=True)
            if "Tgp_NotOne_FBD" in observed:
                add("Tgp_NotOne_FBD", language, "not", "A")
        # The Int references keep their original block names; DInt/Real references carry the type.
        for data_type, suffix in (("Int", ""), ("DInt", "DInt"), ("Real", "Real")):
            for instruction in ("Eq", "Ne", "Lt", "Le", "Gt", "Ge"):
                pins = [pin("in1", "input", data_type), pin("in2", "input", data_type), pin("out", "output", "Bool")]
                if language == "LAD":
                    pins.insert(0, pin("pre", "input", "Bool"))
                add("Tgp_" + instruction + suffix + "_" + language, language, "compare", instruction, instruction.lower(), data_type, pins)
        for instruction in ("TON", "TOF", "TP", "TONR"):
            pins = [pin("IN", "input", "Bool"), pin("PT", "input", "Time"), pin("ET", "output", "Time", False), pin("Q", "output", "Bool", False)]
            if instruction == "TONR":
                pins.insert(1, pin("R", "input", "Bool"))
            add("Tgp_" + instruction + "_" + language, language, "timer", instruction, instruction, "Time", pins)
        # MOVE, arithmetic and edge boxes have EN/ENO in LAD and FBD, like calls.
        enable = [pin("en", "input", "Bool"), pin("eno", "output", "Bool", False)]
        for data_type in ("Int", "DInt", "Real"):
            add("Tgp_Move" + data_type + "_" + language, language, "move", "Move", None, data_type,
                enable + [pin("in", "input", data_type), pin("out1", "output", data_type, False)])
            for instruction in ("ADD", "SUB", "MUL", "DIV"):
                add("Tgp_" + instruction.title() + data_type + "_" + language, language, "math", instruction.title(), instruction, data_type,
                    enable + [pin("in1", "input", data_type), pin("in2", "input", data_type), pin("out", "output", data_type, False)])
        for instruction in ("R_TRIG", "F_TRIG"):
            add("Tgp_" + instruction + "_" + language, language, "edge", instruction, instruction, None,
                enable + [pin("CLK", "input", "Bool"), pin("Q", "output", "Bool", False)])
        for instruction in ("CTU", "CTD", "CTUD"):
            inputs = {"CTU": ("CU", "R"), "CTD": ("CD", "LD"), "CTUD": ("CU", "CD", "R", "LD")}[instruction]
            outputs = ("QU", "QD") if instruction == "CTUD" else ("Q",)
            pins = [pin(name, "input", "Bool") for name in inputs] + [pin("PV", "input", "Int")]
            pins += [pin(name, "output", "Bool", False) for name in outputs] + [pin("CV", "output", "Int", False)]
            add("Tgp_" + instruction + "_" + language, language, "counter", instruction, instruction, "Int", pins)
        # Each route must carry a call template observed in that exact route.
        # Numeric route fixtures cover input, output, inout and return wiring.
        call_reference = "Tgp_FC_" + language
        if route != "standard" and call_reference not in observed:
            call_reference = "Tgp_NumericFC_" + language
        add(call_reference, language, "call", "Call", pins=[pin("en", "input", "Bool"), pin("eno", "output", "Bool", False)])
    creation_templates = []
    unresolved_templates = []
    global_instance_templates = []
    multi_instance_templates = []
    for block, (document, target, source_hash, cpu) in observed.items():
        if block.startswith("Tgp_Template"):
            block_type, language = block[len("Tgp_Template"):].split("_")
            creation_templates.append({"id": "v21.fw41." + (route + "." if route != "standard" else "") + language.lower() + "." + block_type.lower() + ".creation.v1",
                "tiaVersion": "V21", "cpuFamily": cpu, "route": route, "language": language, "blockType": block_type,
                "path": target.relative_to(template_root).as_posix(), "sha256": source_hash})
        if block.startswith("Tgp_GlobalTemplateFB_"):
            language = block.rsplit("_", 1)[-1]
            reference_name = "Tgp_TemplateFB_" + language
            if reference_name not in observed and stage_output is not None:
                unresolved_templates.append({"block": block, "missingReference": reference_name, "route": route})
                continue
            _, reference_path, reference_hash, _ = observed[reference_name]
            global_instance_templates.append({"id": "v21.fw41." + (route + "." if route != "standard" else "") + language.lower() + ".global-instance.v1",
                "tiaVersion": "V21", "cpuFamily": cpu, "route": route, "blockType": "InstanceDB",
                "path": target.relative_to(template_root).as_posix(), "sha256": source_hash,
                "referenceCalleePath": reference_path.relative_to(template_root).as_posix(),
                "referenceCalleeSha256": reference_hash})
        if block.startswith("Tgp_MultiTemplateFB_"):
            language = block.rsplit("_", 1)[-1]
            if "Tgp_TemplateFB_" + language not in observed and stage_output is not None:
                unresolved_templates.append({"block": block, "missingReference": "Tgp_TemplateFB_" + language, "route": route})
                continue
            _, reference_path, reference_hash, _ = observed["Tgp_TemplateFB_" + language]
            multi_instance_templates.append({"id": "v21.fw41." + (route + "." if route != "standard" else "") + language.lower() + ".multi-instance.v1",
                "tiaVersion": "V21", "cpuFamily": cpu, "route": route, "memberName": "Child",
                "path": target.relative_to(template_root).as_posix(), "sha256": source_hash,
                "referenceCalleePath": reference_path.relative_to(template_root).as_posix(),
                "referenceCalleeSha256": reference_hash})
    return {"schemaVersion": 1, "profileVersion": "tia-v21-flgnet-v5-1",
        "nativeFormat": {"tia": "V21 Update 2", "step7ApiFileVersion": "2100.0.121.1", "networkNamespace": NET,
                         "interfaceNamespace": INTERFACE, "instructionVersions": ["1.0"],
                         "evidenceMeaning": "NativeFormatValidated records independently authored reference import/compile/export only. Sources distinguish SIMATIC SD/SCL from explicitly derived native XML. Builder qualification gates remain separate."},
        "sources": sources, "profiles": profiles, "creationTemplates": creation_templates,
        "globalInstanceTemplates": global_instance_templates, "multiInstanceTemplates": multi_instance_templates,
        **({"candidateOnly": True, "unresolvedTemplates": unresolved_templates} if stage_output is not None else {})}


def extract_catalog(reports, route_reports=(), *, append=False, stage_output=None):
    if stage_output is not None:
        stage_output = pathlib.Path(stage_output).resolve()
        if append:
            raise ValueError("Candidate staging cannot append to the deployment catalog.")
        if not stage_output.is_relative_to((ROOT / "artifacts").resolve()):
            raise ValueError("Candidate extraction must stay under repository artifacts.")
        stage_output.mkdir(parents=True, exist_ok=False)
    result = extract([path.resolve() for path in reports], stage_output=stage_output)
    if route_reports:
        for route in ("softwareUnit", "namespace"):
            extra = extract([path.resolve() for path in route_reports], route, stage_output=stage_output)
            for collection in ("sources", "profiles", "creationTemplates", "globalInstanceTemplates", "multiInstanceTemplates"):
                result[collection].extend(extra[collection])
            if stage_output is not None:
                result["unresolvedTemplates"].extend(extra["unresolvedTemplates"])
    catalog_path = (stage_output / "profiles.candidate.v1.json" if stage_output is not None
        else ROOT / "fixtures/typed-graphical/profiles.v1.json")
    if append:
        current = json.loads(catalog_path.read_text(encoding="utf-8"))
        current["nativeFormat"]["evidenceMeaning"] = result["nativeFormat"]["evidenceMeaning"]
        for collection in ("sources", "profiles", "creationTemplates", "globalInstanceTemplates", "multiInstanceTemplates"):
            key = "path" if collection == "sources" else "id"
            existing = {item[key]: item for item in current.get(collection, [])}
            for item in result[collection]:
                if item[key] in existing:
                    if collection == "profiles":
                        evidence = {"engineeringVerified", "preservationVerified", "persistenceVerified", "recoveryVerified", "runtimeVerified"}
                        comparable = lambda value: {k: v for k, v in value.items() if k not in evidence}
                        if comparable(item) != comparable(existing[item[key]]):
                            raise ValueError("Existing profile identity changed; version the new capture: " + item[key])
                    elif item != existing[item[key]]:
                        raise ValueError("Existing captured resource identity changed: " + item[key])
                else:
                    current.setdefault(collection, []).append(item)
        result = current
    catalog_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return result, catalog_path


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reports", nargs="+", type=pathlib.Path)
    parser.add_argument("--route-reports", nargs="*", type=pathlib.Path, default=[])
    parser.add_argument("--append", action="store_true", help="Append newly captured resources without resetting existing qualification evidence.")
    parser.add_argument("--stage-output", type=pathlib.Path, help="Write inactive candidates and native copies to a new artifacts directory; never update deployable fixtures.")
    args = parser.parse_args()
    result, catalog_path = extract_catalog(args.reports, args.route_reports, append=args.append, stage_output=args.stage_output)
    print(("Staged " if args.stage_output else "Captured ") + str(len(result["profiles"])) + " exact native profiles: " + str(catalog_path))
