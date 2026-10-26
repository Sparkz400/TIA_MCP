"""IEC timer/counter dependent-instance effect pairs for the standard, software-unit and namespace routes.

Before: an FB and its instance DB (RetainedMarker 42). After: the FB redeclared with one static member of each IEC
instance type while the dependent DB is observed. One packet per capture scope (unit routes; standard). Rerun after editing.
"""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent
UNIT = "TiaMcp_UnitA"
ROUTES = (("Unit", UNIT), ("Namespace", UNIT + ".Alternate"))
ARCHIVE = "artifacts/changes/change-12d1d3b9e3a74cd3be6f53b9f2f35cf4/before.zap21"
ARCHIVE_SHA256 = "39cea56a73b8e58263e256c2f27b2402e1d29989fb12cb471f66970e1cce74d6"
MEMBERS = (("Timer_TON", "TON_TIME"), ("Timer_TOF", "TOF_TIME"), ("Timer_TP", "TP_TIME"),
           ("Counter_CTU", "CTU_INT"), ("Counter_CTD", "CTD_INT"), ("Counter_CTUD", "CTUD_INT"))


def source(prefix, namespace, phase):
    added = "".join(f" {name} : {datatype};\n" for name, datatype in MEMBERS) if phase == "after" else ""
    fb = (f'FUNCTION_BLOCK "{prefix}IecEffect"\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\n'
          'VAR_INPUT\n Enable : Bool;\nEND_VAR\nVAR_OUTPUT\n Active : Bool;\nEND_VAR\n'
          f'VAR\n RetainedMarker : Int := 17;\n{added}END_VAR\nBEGIN\n #Active := #Enable;\nEND_FUNCTION_BLOCK\n')
    qualified = (namespace + "." if namespace else "") + prefix + "IecEffect"
    db = (f'\nDATA_BLOCK "{prefix}IecEffect_DB"\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\n'
          f'"{qualified}"\nBEGIN\n RetainedMarker := 42;\nEND_DATA_BLOCK\n') if phase == "before" else ""
    # The standard route has no namespace: plain sources for the PLC program scope.
    return f"NAMESPACE {namespace}\n{fb}{db}\nEND_NAMESPACE\n" if namespace else fb + db


def packet(name, routes, unit, output, scope_text):
    sources = []
    for phase in ("before", "after"):
        for label, namespace in routes:
            prefix = "Tgp" + label + "_"
            path = ROOT / "sources" / (("route-" + label + "-" if label else "") + "iec-effect-" + phase + ".scl")
            path.write_bytes(source(prefix, namespace, phase).encode("utf-8"))
            names = [prefix + "IecEffect"] + ([prefix + "IecEffect_DB"] if phase == "before" else [])
            row = {"path": path.relative_to(REPO).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                   "route": "standard" if not unit else "softwareUnit" if namespace == unit else "namespace", "authoredFormat": "SCL",
                   "names": names, "namespace": namespace}
            if phase == "after":
                row.update(modifies=[prefix + "IecEffect"], observes=[prefix + "IecEffect_DB"])
            sources.append(row)
    command = ["python", "-B", "scripts/capture_typed_routes_owned.py", "@" + output + ".args",
               "--host", "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe",
               "--worker", "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe",
               "--output", output, "--archive", ARCHIVE, "--sha256", ARCHIVE_SHA256] + ([] if unit else ["--unit", ""])
    for row in sources:
        file = Path(row["path"]).name
        command += [arg for member in row.get("modifies", []) for arg in ("--modify", file + ":" + member)]
        command += [arg for member in row.get("observes", []) for arg in ("--observe", file + ":" + member)]
    document = {"schemaVersion": 1, "status": "authored-not-executed",
                "purpose": "Native dependent-instance effects of adding one static member of each IEC instance type to an FB with an "
                           "instance DB, " + scope_text + ". Unobserved until captured; enables nothing.",
                "unit": unit, "archive": {"path": ARCHIVE, "sha256": ARCHIVE_SHA256},
                "members": [{"name": member, "datatype": datatype} for member, datatype in MEMBERS],
                "sources": sources, "argsFile": [row["path"] for row in sources], "command": command,
                "afterCapture": ["scripts/extract_typed_instance_effects.py <capture> <capture> --iec --route "
                                 + ("standard" if not namespace else "softwareUnit" if namespace == unit else "namespace")
                                 + " --prefix Tgp" + label + "_" + (" --namespace " + namespace if namespace else "") for label, namespace in routes]}
    (ROOT / name).write_bytes((json.dumps(document, indent=2) + "\n").encode("utf-8"))
    print(name + ": " + str(len(sources)) + " IEC effect inputs authored; packet status authored-not-executed.")


def main():
    packet("route-capture-iec-effect-packet.v1.json", ROUTES, UNIT, "artifacts/cap-iec-effect", "in the software-unit and namespace routes")
    packet("route-capture-iec-effect-standard-packet.v1.json", (("", ""),), "", "artifacts/cap-iec-effect-standard", "in the standard PLC program scope")


if __name__ == "__main__":
    main()
