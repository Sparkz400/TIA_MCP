"""Wave 9 references and typed cases for the software-unit and namespace routes.

DInt/Real compare, MOVE and ADD/SUB/MUL/DIV are the standard wave 9 SIMATIC SD programs in each route's
namespace, as author_advanced_route_sources.py writes the Int families. TONR and R_TRIG/F_TRIG are native
derivations of the captured standard references, as author_native_route_iec_sources.py writes TON..CTUD:
SIMATIC SD unit IEC probes keep TIA's "Missing instance DB" diagnostic. Existing global storage only for TONR:
V21 marks an edge instance DB know-how protected (local.typed.edge-global-storage.20261030). Created storage
needs route IEC effect members for TONR_TIME/R_TRIG/F_TRIG, which no route capture has, so no created cases.
Writes sources/, route-capture-wave9-packet.v1.json and wave9-route-cases.v1.json; enables nothing.
"""
import copy
import hashlib
import json
import re
from pathlib import Path
from xml.etree import ElementTree

from author_advanced_route_sources import envelope
from author_advanced_route_cases import replace_names
from author_native_route_iec_sources import derive
from author_route_cases import ARCHIVE, ARCHIVE_SHA256
from author_wave9_families import ARITHMETIC, COMPARISONS, EDGES, cases as standard_cases

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent
UNIT = "TiaMcp_UnitA"
ROUTES = (("Unit", UNIT), ("Namespace", UNIT + ".Alternate"))
HOST = "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe"
WORKER = "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe"


def qualified(route, namespace, name):
    """The standard SIMATIC SD program in the route namespace; only its own block name is rewritten."""
    text = (ROOT / "sources" / name).read_text(encoding="utf-8").replace("Tgp_", "Tgp" + route + "_")
    target = ROOT / "sources" / ("route-" + route + "-qualified-" + name)
    target.write_bytes(envelope(namespace, text).encode("utf-8"))
    return target


def references(route, namespace):
    """Two capture runs per route, each from the baseline: (A) compare and MOVE, (B) arithmetic, TONR and edges."""
    first, second = [], []
    storage = ROOT / "sources" / ("route-" + route + "-global-tonr-storage.scl")
    storage.write_bytes(envelope(namespace, (ROOT / "sources/global-tonr-storage.scl").read_text(encoding="utf-8")
                                 .replace("Tgp_", "Tgp" + route + "_")).encode("utf-8"))
    second.append(storage)
    number = 650 if route == "Unit" else 750
    for language in ("lad", "fbd"):
        for data_type in ("dint", "real"):
            first += [qualified(route, namespace, op.lower() + "-" + data_type + "-" + language + ".s7dcl") for op in COMPARISONS]
        for data_type in ("int", "dint", "real"):
            first.append(qualified(route, namespace, "move-" + data_type + "-" + language + ".s7dcl"))
            second += [qualified(route, namespace, op.lower() + "-" + data_type + "-" + language + ".s7dcl") for op in ARITHMETIC]
        for suffix, component in [("TONR", None), ("TONR_Global", "Tgp_GlobalTonr")] + [(edge, None) for edge in EDGES]:
            second.append(derive(route, namespace, language.upper(), suffix + "_" + language.upper(), number, component))
            number += 1
    return first, second


def entry(path, route, namespace):
    text = path.read_text(encoding="utf-8")
    native = path.suffix == ".xml"
    names = [ElementTree.fromstring(text).findtext("SW.Blocks.FB/AttributeList/Name")] if native else re.findall(r'\b(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK)\s+"([^"]+)"', text)
    return {"path": path.relative_to(REPO).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "route": "softwareUnit" if route == "Unit" else "namespace",
            "authoredFormat": "native_xml_template_derivation" if native else "SCL" if path.suffix == ".scl" else "SIMATIC_SD",
            "names": names, "namespace": namespace}


def cases():
    """The standard wave 9 local and existing-global TONR cases in each route; created and edge-global storage stay out."""
    result = []
    for route, namespace in ROUTES:
        for original in standard_cases():
            kind = (original["payload"].get("instances") or [{}])[0].get("kind")
            if kind in ("createMulti", "createGlobal") or original.get("dependentSources") or (
                    kind == "existingGlobal" and original["family"] != "tonr"):
                continue
            case = replace_names(copy.deepcopy(original), "Tgp" + route + "_")
            case.update(fixtureId="typed-" + route.lower() + "-" + original["fixtureId"].removeprefix("typed-"), engineeringOnly=True,
                        archivePath=ARCHIVE, archiveSha256=ARCHIVE_SHA256, unitName=UNIT, namespace=namespace)
            result.append(case)
    return result


def main():
    sources, runs = [], {}
    for route, namespace in ROUTES:
        for label, paths in zip(("a", "b"), references(route, namespace)):
            rows = [entry(path, route, namespace) for path in paths]
            sources += rows
            output = "artifacts/w9" + route[0].lower() + label  # short: TIA limits project paths to 143 characters
            runs[output] = {"argsFile": [row["path"] for row in rows],
                            "command": ["python", "-B", "scripts/capture_typed_routes_owned.py", "@" + output + ".args", "--host", HOST,
                                        "--worker", WORKER, "--output", output, "--archive", ARCHIVE, "--sha256", ARCHIVE_SHA256, "--unit", UNIT]}
    captures = " ".join(output + "/capture.json" for output in runs)
    document = {"schemaVersion": 1, "status": "authored-not-executed",
                "purpose": "Software-unit and namespace route references for the wave 9 typed families (DInt/Real compare, MOVE, "
                           "ADD/SUB/MUL/DIV, TONR, R_TRIG/F_TRIG; LAD and FBD; local and existing-global TONR storage), "
                           "two runs per route from the SHA-bound baseline. Unobserved until captured; enables nothing.",
                "unit": UNIT, "archive": {"path": ARCHIVE, "sha256": ARCHIVE_SHA256}, "sources": sources, "runs": runs,
                "afterCapture": ["python -B scripts/extract_typed_graphical_profiles.py --route-reports " + captures + " --stage-output artifacts/w9rs",
                                 "python -B scripts/audit_typed_profile_candidates.py --catalog artifacts/w9rs/profiles.candidate.v1.json "
                                 + " ".join("--capture " + output + "/capture.json" for output in runs) + " --output artifacts/w9rs/audit.json"]}
    (ROOT / "route-capture-wave9-packet.v1.json").write_bytes((json.dumps(document, indent=2) + "\n").encode("utf-8"))
    authored = cases()
    (ROOT / "wave9-route-cases.v1.json").write_bytes((json.dumps({"schemaVersion": 1, "cases": authored}, indent=2) + "\n").encode("utf-8"))
    print(str(len(sources)) + " wave 9 route references and " + str(len(authored)) + " route cases authored; packet status authored-not-executed.")


if __name__ == "__main__":
    main()
