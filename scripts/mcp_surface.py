"""Cut or check contracts/mcp-surface.v1.json, the frozen MCP surface of the contract freeze policy.

cut   --surface <surface.json>  writes the baseline from a normal Host's discovery (scripts/test_mcp_surface_host.py).
check --surface <surface.json>  lists every breaking change of the live surface against the baseline. From 1.0 a
      breaking change fails unless the product's major version moved past the baseline's; a 0.x product only
      reports them, since any 0.x release may change the surface.

Frozen (policy Q1, Q3, Q11, Q12): normally advertised tools whose feature is `shipped` or `limited` with their
input schemas, the safe-direction annotations and declared output schemas; prompts and their arguments; resource
templates and concrete resources; the negotiated protocol revision; stable error codes; Host options that are not
qualification-only. Other advertised tools are listed as experimental and may change in a minor release.
"""
import argparse
import json
import re
import sys
from pathlib import Path

from error_registry import stable_codes
from release_matrix import CATALOG, ROOT, support
from test_product_version import product_version
from tool_reference import host_flags

BASELINE = "contracts/mcp-surface.v1.json"
QUALIFICATION_OPTION = re.compile(r"qualification|scope-probe|fparameter-discovery")


def major(version):
    return int(version.split(".")[0])


def frozen(surface, catalog, codes, options, version):
    features = {f["featureId"]: f for f in catalog["features"]}
    tools, experimental = {}, []
    for tool in surface["tools"]:
        name = tool["name"]
        if name in features and support(features[name]) in ("shipped", "limited"):
            entry = {"inputSchema": tool.get("inputSchema", {}),
                     "annotations": {k: v for k, v in (tool.get("annotations") or {}).items() if k in ("readOnlyHint", "destructiveHint")}}
            if "outputSchema" in tool:
                entry["outputSchema"] = tool["outputSchema"]
            tools[name] = entry
        else:
            experimental.append(name)
    return {
        "schemaVersion": 1,
        "productVersion": version,
        "protocolVersions": [surface["protocolVersion"]],
        "tools": dict(sorted(tools.items())),
        "experimentalTools": sorted(experimental),
        "prompts": {p["name"]: sorted([a["name"], bool(a.get("required"))] for a in p.get("arguments") or []) for p in surface["prompts"]},
        "resourceTemplates": sorted(t["uriTemplate"] for t in surface["resourceTemplates"]),
        "resources": sorted(r["uri"] for r in surface["resources"]),
        "errorCodes": codes,
        "hostOptions": [o for o in options if not QUALIFICATION_OPTION.search(o)],
    }


def types(schema):
    t = schema.get("type")
    return set(t) if isinstance(t, list) else {t} if t else set()


def input_breaks(old, new, path, out):
    """A client's valid old request must stay valid with the same meaning."""
    if not isinstance(old, dict) or not isinstance(new, dict):  # a boolean schema
        if old != new:
            out.append(f"{path}: schema {old!r} changed to {new!r}")
        return
    if types(old) - types(new) and types(new):
        out.append(f"{path}: type {sorted(types(old))} narrowed to {sorted(types(new))}")
    if "default" in old and old.get("default") != new.get("default"):
        out.append(f"{path}: default {old.get('default')!r} changed to {new.get('default')!r}")
    if "enum" in old and "enum" in new and set(map(json.dumps, old["enum"])) - set(map(json.dumps, new["enum"])):
        out.append(f"{path}: enum values removed")
    for name, child in (old.get("properties") or {}).items():
        if name not in (new.get("properties") or {}):
            out.append(f"{path}.{name}: parameter removed")
        else:
            input_breaks(child, new["properties"][name], f"{path}.{name}", out)
    for name in set(new.get("required") or []) - set(old.get("required") or []):
        out.append(f"{path}.{name}: newly required")
    if isinstance(old.get("items"), dict) and isinstance(new.get("items"), dict):
        input_breaks(old["items"], new["items"], path + "[]", out)


def output_breaks(old, new, path, out):
    """Every field a client read before must still arrive with the same type."""
    if not isinstance(old, dict) or not isinstance(new, dict):  # a boolean schema
        if old != new:
            out.append(f"{path}: schema {old!r} changed to {new!r}")
        return
    if types(old) != types(new):
        out.append(f"{path}: type {sorted(types(old))} changed to {sorted(types(new))}")
    for name, child in (old.get("properties") or {}).items():
        if name not in (new.get("properties") or {}):
            out.append(f"{path}.{name}: output field removed")
        else:
            output_breaks(child, new["properties"][name], f"{path}.{name}", out)
    for name in set(old.get("required") or []) - set(new.get("required") or []):
        out.append(f"{path}.{name}: output field no longer required")
    if isinstance(old.get("items"), dict) and isinstance(new.get("items"), dict):
        output_breaks(old["items"], new["items"], path + "[]", out)


def breaking(old, new):
    out = []
    for name, tool in old["tools"].items():
        if name not in new["tools"]:
            out.append(f"tool {name}: removed" + (" (now experimental)" if name in new["experimentalTools"] else ""))
            continue
        live = new["tools"][name]
        input_breaks(tool["inputSchema"], live["inputSchema"], f"tool {name} input", out)
        if tool["annotations"].get("readOnlyHint") and not live["annotations"].get("readOnlyHint"):
            out.append(f"tool {name}: readOnlyHint true became false")
        if "outputSchema" in tool:
            if "outputSchema" not in live:
                out.append(f"tool {name}: output schema removed")
            else:
                output_breaks(tool["outputSchema"], live["outputSchema"], f"tool {name} output", out)
    for name, arguments in old["prompts"].items():
        if name not in new["prompts"]:
            out.append(f"prompt {name}: removed")
            continue
        before, after = dict(arguments), dict(new["prompts"][name])
        out += [f"prompt {name}: argument {a} removed" for a in before if a not in after]
        out += [f"prompt {name}: argument {a} newly required" for a, required in after.items() if required and not before.get(a)]
    for key, label in (("resourceTemplates", "resource template"), ("resources", "resource"), ("errorCodes", "stable error code"),
                       ("hostOptions", "Host option"), ("protocolVersions", "protocol revision")):
        out += [f"{label} {item}: removed" for item in old[key] if item not in new[key]]
    return out


def live(surface_path):
    surface = json.loads(surface_path.read_text(encoding="utf-8"))
    catalog = json.loads((ROOT / CATALOG).read_text(encoding="utf-8"))
    return frozen(surface, catalog, stable_codes(), host_flags(), product_version())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["cut", "check"])
    parser.add_argument("--surface", required=True, type=Path)
    args = parser.parse_args()
    current = live(args.surface)
    target = ROOT / BASELINE
    if args.command == "cut":
        target.write_text(json.dumps(current, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
        print(f"Wrote {BASELINE}: {len(current['tools'])} frozen and {len(current['experimentalTools'])} experimental tools.")
        return
    if not target.is_file():
        if major(current["productVersion"]) >= 1:
            raise SystemExit(f"{BASELINE} is missing; a 1.x release needs the baseline cut on its release candidate.")
        print(f"No {BASELINE} yet; nothing to compare before 1.0.")
        return
    baseline = json.loads(target.read_text(encoding="utf-8"))
    changes = breaking(baseline, current)
    for change in changes:
        print("BREAKING " + change)
    enforced = major(current["productVersion"]) >= 1 and major(current["productVersion"]) == major(baseline["productVersion"])
    if changes and enforced:
        raise SystemExit(f"{len(changes)} breaking changes against {BASELINE} without a major version change.")
    note = "" if enforced or not changes else f" ({len(changes)} breaking changes reported, not enforced for {current['productVersion']})"
    print(f"PASS live surface against {BASELINE}{note}.")
    sys.exit(0)


if __name__ == "__main__":
    main()
