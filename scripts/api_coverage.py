"""AC-01 offline coverage inventory. Standard library only; never loads Siemens code.

This first slice reconciles XML, a separately produced metadata-only report, XSD
leaves, reviewed decisions and current catalog/source bindings. Unreviewed routes
stay residuals. It does not generate support claims or execute qualification.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
MAX_FILE = 128 * 1024 * 1024
MAX_XML = 64 * 1024 * 1024
MAX_TOTAL = 512 * 1024 * 1024
MAX_FILES = 5000
MAX_NODES = 1000000
XS = "{http://www.w3.org/2001/XMLSchema}"


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def digest_bytes(value):
    return hashlib.sha256(value).hexdigest()


def read_bytes(path, limit=MAX_FILE):
    if path.is_symlink() or path.stat().st_size > limit:
        raise ValueError(f"Symlink or oversized input: {path}")
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError(f"Input grew beyond limit: {path}")
    return data


def json_read(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"Duplicate JSON key: {key}")
            result[key] = value
        return result
    return json.loads(read_bytes(path).decode("utf-8-sig"), object_pairs_hook=unique)


def xml_read(data):
    # Reject DTD and all entities, including UTF-16/32 encodings, before parsing.
    if len(data) > MAX_XML or re.search(br"<!\s*(DOCTYPE|ENTITY)", data.replace(b"\0", b""), re.I):
        raise ValueError("Oversized XML or forbidden DTD/entity declaration")
    root = ET.fromstring(data)
    count = 0
    stack = [(root, 0)]
    while stack:
        node, depth = stack.pop()
        count += 1
        if count > MAX_NODES or depth > 128:
            raise ValueError("XML node/depth limit exceeded")
        stack.extend((child, depth + 1) for child in node)
    return root


def within(root, relative):
    # Portable relative references only. Never resolve network or outside paths.
    if not isinstance(relative, str) or not relative or "\\" in relative or ":" in relative or relative.startswith("/"):
        raise ValueError(f"Unsafe relative path: {relative}")
    path = root / relative
    if path.is_symlink() or any(p.is_symlink() for p in path.parents if p != root.parent):
        raise ValueError(f"Symlink in path: {relative}")
    path.resolve().relative_to(root.resolve())
    return path


def enumerate_inputs(root, suffixes, skip=()):
    result = []
    for directory, dirs, files in os.walk(root):
        dirs[:] = sorted(d for d in dirs if d not in skip)
        if any((Path(directory) / d).is_symlink() for d in dirs):
            raise ValueError("Symlink directory in input tree")
        for name in sorted(files):
            path = Path(directory) / name
            if path.suffix.lower() in suffixes:
                within(root, path.relative_to(root).as_posix())
                result.append(path)
    if len(result) > MAX_FILES or sum(p.stat().st_size for p in result) > MAX_TOTAL:
        raise ValueError("Input file/byte budget exceeded")
    return sorted(result)


def xml_symbols(data, source):
    root = xml_read(data)
    assembly = root.findtext("assembly/name")
    result = {}
    for node in root.findall("members/member"):
        identifier = node.get("name", "")
        if not re.match(r"^[TMPFE]:.+", identifier) or identifier in result:
            raise ValueError(f"Invalid/duplicate XML member in {source}: {identifier}")
        result[identifier] = {"id": identifier, "xmlSource": source,
                              "documentationHash": digest_bytes(ET.tostring(node, encoding="utf-8"))}
    return assembly, result


def schema_inventory(data, relative, api_root):
    root = xml_read(data)
    if root.tag != XS + "schema":
        raise ValueError(f"Not an XSD: {relative}")
    dependencies, leaves = [], []

    def visit(node, route):
        tag = node.tag.removeprefix(XS)
        if tag in {"import", "include", "redefine"}:
            location = node.get("schemaLocation")
            row = {"kind": tag, "namespace": node.get("namespace"), "location": location,
                   "resolved": None, "state": "namespace_only"}
            if location:
                joined = (Path(relative).parent / location).as_posix()
                try:
                    path = within(api_root, joined)
                    row["resolved"] = path.resolve().relative_to(api_root.resolve()).as_posix()
                    row["state"] = "present" if path.is_file() else "missing"
                except ValueError:
                    row["state"] = "blocked_external_or_unsafe"
            dependencies.append(row)
        if tag in {"element", "attribute", "simpleType", "complexType", "enumeration"}:
            leaves.append({"path": route, "kind": tag, "name": node.get("name"),
                           "ref": node.get("ref"), "type": node.get("type"), "value": node.get("value"),
                           "review": "untriaged"})
        for index, child in enumerate(node):
            visit(child, route + "/" + child.tag.removeprefix(XS) + f"[{index}]")
    visit(root, "schema")
    return {"path": relative, "namespace": root.get("targetNamespace"),
            "dependencies": dependencies, "leaves": leaves}


def declaring_member(identifier):
    # Deliberately strip arguments BEFORE classification: IEngineeringObject as
    # a parameter must not swallow semantic VCI / translation operations.
    return identifier[2:].split("(", 1)[0].split("~", 1)[0]


def classify(identifier, decisions):
    if identifier in decisions:
        return {"classification": "semantic", "operationId": decisions[identifier]}
    name = declaring_member(identifier)
    infrastructure_types = {"Siemens.Engineering.IEngineeringObject", "Siemens.Engineering.IEngineeringInstance",
                            "Siemens.Engineering.IEngineeringComposition", "Siemens.Engineering.IEngineeringServiceProvider"}
    if name in infrastructure_types or any(name.startswith(t + ".") for t in infrastructure_types):
        return {"classification": "infrastructure", "reason": "Exact declaring interface; argument types are ignored."}
    return {"classification": "untriaged", "reason": "Requires semantic member review; no support inference."}


def source_inventory(repo):
    paths = []
    for folder, suffixes in [("src", {".cs", ".csproj"}), ("contracts", {".json"})]:
        paths.extend(enumerate_inputs(repo / folder, suffixes, {"bin", "obj"}))
    for name in ("scripts/api_coverage.py", "scripts/Inspect-PublicApi.ps1", "scripts/test_api_coverage.py",
                 "Directory.Build.props", "global.json"):
        path = repo / name
        if path.is_file():
            paths.append(path)
    if (repo / "scripts/public_api_metadata").exists():
        paths.extend(enumerate_inputs(repo / "scripts/public_api_metadata", {".cs", ".csproj", ".ps1"}, {"bin", "obj"}))
    rows, texts = [], {}
    for path in sorted(paths):
        relative = path.relative_to(repo).as_posix()
        data = read_bytes(path)
        rows.append({"path": relative, "sha256": digest_bytes(data), "bytes": len(data)})
        if path.suffix == ".cs":
            texts[relative] = data.decode("utf-8-sig")
    return rows, texts


def bindings(catalog, texts):
    tools, cases = {}, {}
    for path, text in sorted(texts.items()):
        if path.startswith("src/TiaMcp.Host/"):
            matches = list(re.finditer(r'McpServerTool\(Name\s*=\s*"([^"]+)"', text))
            for index, match in enumerate(matches):
                name = match[1]
                if name in tools:
                    raise ValueError(f"Duplicate tool: {name}")
                body = text[match.end():matches[index + 1].start() if index + 1 < len(matches) else len(text)]
                tools[name] = {"name": name, "path": path, "line": text.count("\n", 0, match.start()) + 1,
                               "literalBridgeCalls": sorted(set(re.findall(r'\bCall(?:Async)?\("([^"]+)"', body))),
                               "routeReview": "lexical_candidates_only"}
        for match in re.finditer(r'case\s+"([^"]+)"\s*:', text):
            cases.setdefault(match[1], []).append({"path": path, "line": text.count("\n", 0, match.start()) + 1})
    features = catalog["features"]
    if len({f["featureId"] for f in features}) != len(features):
        raise ValueError("Duplicate catalog feature")
    expected = {t for row in features for t in row["mcpTools"]}
    if expected != set(tools):
        raise ValueError("Catalog/Host tool bindings differ")
    result = []
    for feature in sorted(features, key=lambda x: x["featureId"]):
        registrations = [tools[t] for t in feature["mcpTools"]]
        routes = set(t for reg in registrations for t in reg["literalBridgeCalls"])
        if feature["changeKind"]:
            routes.add(feature["changeKind"])
        result.append({"featureId": feature["featureId"], "catalogRecord": feature,
                       "registrations": registrations,
                       "dispatchCandidates": [{"operation": r, "locations": cases.get(r, [])} for r in sorted(routes)],
                       "schemaBindings": [], "mappingReview": "handler_and_schema_review_required",
                       "releaseAccepted": False})
    return result


def decision_index(registry):
    ids, symbols = set(), {}
    domains = {row["id"] for row in registry["domains"]}
    if len(domains) != len(registry["domains"]) or len(domains) != 30:
        raise ValueError("Expected unique D01-D17 and X01-X13 domain rows")
    if domains != {f"D{i:02}" for i in range(1, 18)} | {f"X{i:02}" for i in range(1, 14)}:
        raise ValueError("Incorrect family IDs")
    for domain in registry["domains"]:
        if domain.get("releaseObligation") is not True or domain.get("releaseAccepted") is not False:
            raise ValueError("Domain obligation cannot be removed or promoted")
    residual_domains = [r["domain"] for r in registry["manualResiduals"]]
    if set(residual_domains) != domains or len(residual_domains) != len(domains):
        raise ValueError("Manual residuals must cover every family exactly once")
    for operation in registry["operations"]:
        if operation["id"] in ids or operation["domain"] not in domains:
            raise ValueError("Duplicate operation or unresolved domain")
        ids.add(operation["id"])
        if operation["releaseObligation"] is not True or operation["releaseAccepted"] is not False:
            raise ValueError("First-slice registry cannot accept or defer a release obligation")
        if operation["implementation"] != "not_implemented" or operation["qualification"] != "not_run" or operation["availability"] != "unobserved":
            raise ValueError("Seed operation incorrectly promoted from metadata")
        for symbol in operation["symbols"]:
            if symbol in symbols:
                raise ValueError(f"Symbol assigned twice: {symbol}")
            symbols[symbol] = operation["id"]
    return symbols


def validate_feature_obligations(registry, features):
    mappings = registry["featureObligations"]
    identifiers = [f["featureId"] for f in features]
    if len(identifiers) != len(set(identifiers)):
        raise ValueError("Duplicate feature records")
    if {m["featureId"] for m in mappings} != set(identifiers) or len(mappings) != len(identifiers):
        raise ValueError("Feature obligation mapping must cover catalog exactly")
    # The historical release ledger also includes foundation/legacy/composite
    # obligations. Their finite IDs are preserved; none means an exclusion.
    known = {f"D{i:02}" for i in range(1, 18)} | {f"X{i:02}" for i in range(1, 14)} | {f"F{i:02}" for i in range(4)} | {f"L{i:02}" for i in range(1, 7)} | {f"C{i:02}" for i in range(1, 5)}
    for mapping in mappings:
        if not mapping["obligations"] or not set(mapping["obligations"]) <= known:
            raise ValueError("Unresolved feature obligation reference")


def metadata_members(report, api_files):
    if report.get("inspectionMode") != "metadataOnly" or report.get("nativeCodeExecuted") is not False or report.get("targetProbed") is not False:
        raise ValueError("Not a non-executing metadata report")
    result, inspected = {}, set()
    if report.get("errors"):
        raise ValueError("Metadata inspection reported errors")
    for assembly in report["assemblies"]:
        # Match DLL basename AND digest; refuse stale or ambiguous reports.
        candidates = [p for p, row in api_files.items() if Path(p).name == assembly["name"] + ".dll" and row["sha256"] == assembly["sha256"]]
        if len(candidates) != 1:
            raise ValueError(f"Stale/ambiguous metadata assembly: {assembly['name']}")
        inspected.add(candidates[0])
        if assembly.get("xmlSha256"):
            xml_path = str(Path(candidates[0]).with_suffix(".xml")).replace("\\", "/")
            if xml_path not in api_files or api_files[xml_path]["sha256"] != assembly["xmlSha256"]:
                raise ValueError("Stale metadata XML annotations")
        if assembly["name"] in result:
            raise ValueError("Duplicate metadata assembly")
        if assembly.get("errors"):
            raise ValueError("Assembly metadata inspection reported errors")
        members = {}
        for typ in assembly["types"]:
            if typ.get("errors"):
                raise ValueError("Type metadata inspection reported errors")
            identifier = "T:" + typ["name"].replace("+", ".")
            if identifier in members:
                raise ValueError(f"Duplicate metadata type: {identifier}")
            members[identifier] = {**{k: v for k, v in typ.items() if k != "members"}, "recordKind": "type"}
            for member in typ["members"]:
                if member.get("isInherited"):
                    continue
                if not member.get("id"):
                    raise ValueError("Metadata member has no exact XML ID")
                if member.get("error") or member.get("idStatus", "exact") != "exact":
                    raise ValueError("Unresolved metadata member")
                if member["id"] in members:
                    raise ValueError(f"Duplicate metadata member: {member['id']}")
                members[member["id"]] = member
        result[assembly["name"]] = {"version": assembly["version"], "members": members,
                                     "unresolved": assembly.get("unresolved", [])}
    dlls = {p for p in api_files if p.lower().endswith(".dll")}
    if inspected != dlls:
        raise ValueError("Metadata report does not cover exactly the configured DLL set")
    return result


def collect(api_root, repo, registry_path, metadata_path):
    paths = enumerate_inputs(api_root, {".xml", ".dll", ".xsd", ".exe", ".pdf"})
    files, docs, schemas = {}, {}, []
    for path in paths:
        relative = path.relative_to(api_root).as_posix()
        data = read_bytes(path)
        files[relative] = {"path": relative, "bytes": len(data), "sha256": digest_bytes(data)}
        if path.suffix.lower() == ".xml":
            assembly, members = xml_symbols(data, relative)
            if assembly:
                if assembly in docs:
                    raise ValueError(f"Duplicate assembly XML: {assembly}")
                docs[assembly] = members
        elif path.suffix.lower() == ".xsd":
            schemas.append(schema_inventory(data, relative, api_root))
    registry_bytes, metadata_bytes = read_bytes(registry_path), read_bytes(metadata_path)
    registry, report = json_read(registry_path), json_read(metadata_path)
    for field, relative in (("scriptSha256", "scripts/Inspect-PublicApi.ps1"),
                            ("helperSha256", "scripts/public_api_metadata/MetadataInspector.cs")):
        producer_path = repo / relative
        if producer_path.is_file() and report.get("producer", {}).get(field) != digest_bytes(read_bytes(producer_path)):
            raise ValueError("Stale or unknown metadata producer: " + relative)
    if report.get("producer"):
        for assembly in report["assemblies"]:
            if any(Path(p).name == assembly["name"] + ".xml" for p in files) and not assembly.get("xmlSha256"):
                raise ValueError("Metadata report missing XML input hash")
    decisions = decision_index(registry)
    metadata = metadata_members(report, files)
    symbols, discrepancies, observed = [], [], set()
    for assembly in sorted(set(docs) | set(metadata)):
        xml = docs.get(assembly, {})
        dll = metadata.get(assembly, {}).get("members", {})
        for identifier in sorted(set(xml) | set(dll)):
            state = "both" if identifier in xml and identifier in dll else "xml_only" if identifier in xml else "dll_only"
            row = {"assembly": assembly, "id": identifier, "presence": state,
                   "xml": xml.get(identifier), "metadata": dll.get(identifier), **classify(identifier, decisions)}
            symbols.append(row)
            observed.add(identifier)
            if state != "both":
                discrepancies.append({"assembly": assembly, "id": identifier, "presence": state, "review": "unresolved"})
    if not set(decisions) <= observed:
        raise ValueError("Reviewed operation symbols absent: " + str(sorted(set(decisions) - observed)))
    source_rows, texts = source_inventory(repo)
    catalog_path = repo / "contracts/feature-catalog.v1.json"
    catalog = json_read(catalog_path)
    features = bindings(catalog, texts)
    validate_feature_obligations(registry, features)
    profile_ids = {e["id"] for e in catalog["evidence"]}
    for feature in features:
        if not set(feature["catalogRecord"]["qualification"]["evidenceIds"]) <= profile_ids:
            raise ValueError("Catalog evidence reference is unresolved")
    payload = {"files": list(files.values()), "symbols": symbols, "schemas": schemas,
               "metadataUnresolved": [{"assembly": a, "items": v["unresolved"]} for a, v in sorted(metadata.items()) if v["unresolved"]],
               "discrepancies": discrepancies, "registry": registry, "features": features,
               "retainedEvidence": catalog["evidence"], "sourceInputs": source_rows,
               "registrySha256": digest_bytes(registry_bytes),
               "metadataSemanticSha256": digest_bytes(canonical({k: v for k, v in report.items() if k not in {"observedAtUtc", "generatedAtUtc"}})),
               "residuals": ["Full semantic review of every untriaged member and XML/DLL discrepancy",
                             "All manual leaves and dynamic attribute/service routes; this is not a complete manual inventory",
                             "All XSD leaves and import dependencies require domain review",
                             "Exact handler/schema/profile mapping beyond lexical registration/case candidates",
                             "Current-source qualification; retained catalog labels are historical scoped claims",
                             "Native metadata probes, product/license/target availability and all release gates"]}
    # Hash twice, including source and decisions, and detect added/deleted files.
    if paths != enumerate_inputs(api_root, {".xml", ".dll", ".xsd", ".exe", ".pdf"}):
        raise ValueError("API file set changed during collection")
    for relative, row in files.items():
        if digest_bytes(read_bytes(api_root / relative)) != row["sha256"]:
            raise ValueError(f"API input changed during collection: {relative}")
    source_after, _ = source_inventory(repo)
    if source_rows != source_after or read_bytes(registry_path) != registry_bytes or read_bytes(metadata_path) != metadata_bytes:
        raise ValueError("Registry/metadata/source changed during collection")
    result = {"schemaVersion": 1, "kind": "api-coverage-offline-snapshot",
              "observedAtUtc": datetime.now(timezone.utc).isoformat(), "apiRoot": str(api_root),
              "nativeExecuted": False, "targetProbed": False, "releaseAccepted": False,
              "semanticSha256": digest_bytes(canonical(payload)), "payload": payload}
    validate(result)
    return result


def validate(snapshot):
    if snapshot.get("schemaVersion") != 1 or snapshot.get("kind") != "api-coverage-offline-snapshot":
        raise ValueError("Unknown snapshot format")
    if any(snapshot.get(k) is not False for k in ("nativeExecuted", "targetProbed", "releaseAccepted")):
        raise ValueError("Offline inventory cannot claim execution, availability or release acceptance")
    payload = snapshot["payload"]
    if snapshot["semanticSha256"] != digest_bytes(canonical(payload)):
        raise ValueError("Semantic hash mismatch")
    decision_index(payload["registry"])
    validate_feature_obligations(payload["registry"], payload["features"])
    keys = [(s["assembly"], s["id"]) for s in payload["symbols"]]
    if len(keys) != len(set(keys)):
        raise ValueError("Duplicate symbols")
    decisions = decision_index(payload["registry"])
    if not set(decisions) <= {s["id"] for s in payload["symbols"]}:
        raise ValueError("Reviewed decision references absent symbol")
    for symbol in payload["symbols"]:
        expected = classify(symbol["id"], decisions)
        if any(symbol.get(k) != v for k, v in expected.items()):
            raise ValueError("Symbol classification does not match reviewed decision")
        if symbol["presence"] != ("both" if symbol["xml"] and symbol["metadata"] else "xml_only" if symbol["xml"] else "dll_only"):
            raise ValueError("Symbol presence mismatch")
    if not payload["residuals"] or any(f["releaseAccepted"] is not False for f in payload["features"]):
        raise ValueError("Missing residuals or accidental promotion")
    for name in ("files", "sourceInputs"):
        entries = payload[name]
        if len({r["path"] for r in entries}) != len(entries):
            raise ValueError("Duplicate input path")
        for row in entries:
            if not re.fullmatch(r"[0-9a-f]{64}", row["sha256"]):
                raise ValueError("Invalid input digest")
            if row["bytes"] < 0 or ":" in row["path"] or "\\" in row["path"] or row["path"].startswith("/") or ".." in Path(row["path"]).parts:
                raise ValueError("Unsafe manifest path")
    return summary(snapshot)


def summary(snapshot):
    p = snapshot["payload"]
    return {"semanticSha256": snapshot["semanticSha256"], "files": len(p["files"]),
            "symbols": len(p["symbols"]), "xmlMembers": sum(bool(s["xml"]) for s in p["symbols"]),
            "discrepancies": len(p["discrepancies"]), "untriaged": sum(s["classification"] == "untriaged" for s in p["symbols"]),
            "schemas": len(p["schemas"]), "schemaLeaves": sum(len(s["leaves"]) for s in p["schemas"]),
            "domains": len(p["registry"]["domains"]), "reviewedOperations": len(p["registry"]["operations"]),
            "catalogFeatures": len(p["features"]), "sourceInputs": len(p["sourceInputs"]), "releaseAccepted": False}


def diff(before, after):
    validate(before)
    validate(after)
    def delta(left, right, key):
        a, b = {key(r): r for r in left}, {key(r): r for r in right}
        return {"added": sorted(b.keys() - a.keys()), "removed": sorted(a.keys() - b.keys()),
                "changed": sorted(k for k in a.keys() & b.keys() if a[k] != b[k])}
    a, b = before["payload"], after["payload"]
    symbol_delta = delta(a["symbols"], b["symbols"], lambda r: r["assembly"] + "|" + r["id"])
    source_delta = delta(a["sourceInputs"], b["sourceInputs"], lambda r: r["path"])
    api_delta = delta(a["files"], b["files"], lambda r: r["path"])
    schema_delta = delta(a["schemas"], b["schemas"], lambda r: r["path"])
    changed = a != b
    return {"schemaVersion": 1, "kind": "api-coverage-proposed-impact", "before": before["semanticSha256"],
            "after": after["semanticSha256"], "symbols": symbol_delta, "apiInputs": api_delta,
            "sourceInputs": source_delta, "schemas": schema_delta,
            # Until mappings are reviewed, conservative selection is deliberate.
            "affectedFeatureIds": sorted({f["featureId"] for p in (a, b) for f in p["features"]}) if changed else [],
            "profilesToReview": sorted({e["id"] for p in (a, b) for e in p["retainedEvidence"]}) if changed else [],
            "selectionMode": "conservative_all_until_dependency_mapping_reviewed",
            "proposedOnly": True, "nativeExecuted": False, "catalogModified": False,
            "qualificationPromoted": False}


def write_new(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, ensure_ascii=False)
        stream.write("\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    commands = ap.add_subparsers(dest="command", required=True)
    c = commands.add_parser("collect")
    c.add_argument("--api-root", type=Path, default=Path(r"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21"))
    c.add_argument("--repo", type=Path, default=ROOT)
    c.add_argument("--registry", type=Path, default=ROOT / "contracts/api-coverage/registry.v1.json")
    c.add_argument("--metadata", type=Path, required=True)
    c.add_argument("--output", type=Path, required=True)
    v = commands.add_parser("validate")
    v.add_argument("snapshot", type=Path)
    v.add_argument("--check-inputs", action="store_true")
    v.add_argument("--repo", type=Path, default=ROOT)
    d = commands.add_parser("diff")
    d.add_argument("before", type=Path)
    d.add_argument("after", type=Path)
    d.add_argument("--output", type=Path, required=True)
    args = ap.parse_args()
    if args.command == "collect":
        if args.output.exists():
            raise ValueError("Refusing to overwrite a snapshot")
        result = collect(args.api_root.resolve(), args.repo.resolve(), args.registry, args.metadata)
        write_new(args.output, result)
        print(json.dumps(summary(result), indent=2))
    elif args.command == "validate":
        result = json_read(args.snapshot)
        info = validate(result)
        if args.check_inputs:
            for base, key in [(Path(result["apiRoot"]), "files"), (args.repo, "sourceInputs")]:
                for row in result["payload"][key]:
                    if digest_bytes(read_bytes(within(base, row["path"]))) != row["sha256"]:
                        raise ValueError(f"Input no longer matches: {row['path']}")
            current_sources, _ = source_inventory(args.repo)
            if current_sources != result["payload"]["sourceInputs"]:
                raise ValueError("Source input set changed")
            api_root = Path(result["apiRoot"])
            current_api = {p.relative_to(api_root).as_posix() for p in enumerate_inputs(api_root, {".xml", ".dll", ".xsd", ".exe", ".pdf"})}
            if current_api != {r["path"] for r in result["payload"]["files"]}:
                raise ValueError("API input set changed")
            info["currentInputsMatch"] = True
        print(json.dumps(info, indent=2))
    else:
        write_new(args.output, diff(json_read(args.before), json_read(args.after)))


if __name__ == "__main__":
    main()
