"""Module capability profiles (contracts/hardware-catalog-profiles.v1.json) from raw hardware_inspect items of a native run.

A profile describes what one catalog type exposes in V21: every item of the module's subtree with each attribute's access mode
and probed value type, its address lengths, channels with their attributes, and interface nodes and ports; plus the CanPlugNew
outcome at probed rack slots. It never records attribute values, so it states capability, not configuration.
"""
from __future__ import annotations

import json
from pathlib import Path

import jsonschema

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = ROOT / "contracts" / "hardware-catalog-profiles.v1.schema.json"


def attributes(rows):
    return [{"name": row["name"], "accessMode": row.get("accessMode"), "type": row.get("type")} for row in rows or []]


def items(raw, root):
    """Profile rows of the module at root and its subtree, from raw hardware_inspect items keyed by exact path."""
    def relative(path):
        if path != root and not path.startswith(root + "/"):
            raise ValueError("%s is outside the profiled module %s" % (path, root))
        return path[len(root) + 1:] if path != root else ""
    rows = []
    for path in sorted(path for path in raw if path == root or path.startswith(root + "/")):
        item, network = raw[path], raw[path].get("network")
        rows.append({"relativePath": relative(path), "name": item["name"], "typeIdentifier": item.get("typeIdentifier"), "positionNumber": item.get("positionNumber"),
                     "isBuiltIn": item.get("isBuiltIn"), "classification": item.get("classification"), "attributes": attributes(item.get("attributes")),
                     "addresses": [{"ioType": address["ioType"], "lengthBits": address["length"]} for address in item.get("addresses") or []],
                     "channels": [{"type": channel["type"], "ioType": channel["ioType"], "number": channel["number"], "attributes": attributes(channel.get("attributes"))}
                                  for channel in item.get("channels") or []],
                     "interface": None if not network else {"interfaceType": network.get("interfaceType"), "nodes": [node.get("name") for node in network.get("nodes", [])],
                                                            "ports": [relative(row["port"]["ownerHardwarePath"]) for row in network.get("ports", [])]}})
    if not rows or rows[0]["relativePath"] != "":
        raise ValueError("No raw hardware_inspect item for the profiled module " + root)
    return rows


def profile(type_identifier, kind, rows, entry, plug_rules, report):
    """One contract profile; entry is the module's exact hardware_catalog_search entry (None when the catalog did not list it)."""
    entry = entry or {}
    return {"typeIdentifier": type_identifier, "orderNumber": entry.get("articleNumber"), "firmwareVersion": entry.get("version"), "typeName": entry.get("typeName"),
            "catalogPath": entry.get("catalogPath"), "moduleKind": kind, "items": rows, "plugRules": plug_rules, "evidenceId": None,
            "capture": {"tiaVersion": "V21", "runner": "scripts/qualify_hardware_changes.py", "report": report}}


def contract(profiles):
    """The contract document, validated against its schema; type identifiers must be unique."""
    document = {"schemaVersion": 1, "profiles": sorted(profiles, key=lambda row: row["typeIdentifier"])}
    jsonschema.Draft202012Validator(json.loads(SCHEMA.read_text(encoding="utf-8"))).validate(document)
    identifiers = [row["typeIdentifier"] for row in document["profiles"]]
    if len(identifiers) != len(set(identifiers)):
        raise ValueError("Duplicate profile type identifiers")
    return document
