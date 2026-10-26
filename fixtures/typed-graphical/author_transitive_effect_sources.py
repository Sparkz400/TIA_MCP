"""Transitive dependent-instance effect pair in the standard PLC program scope.

Before: FB Tgp_NestEffect, embedded as multi-instance Inner in Tgp_NestParent, itself embedded as Middle in Tgp_NestGrand,
each with an instance DB holding explicit start values (42). After: only Tgp_NestEffect is redeclared with one FB member
and one static member of each IEC instance type while both parents and all three DBs are observed. Rerun after editing.
"""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent
ARCHIVE = "artifacts/changes/change-12d1d3b9e3a74cd3be6f53b9f2f35cf4/before.zap21"
ARCHIVE_SHA256 = "39cea56a73b8e58263e256c2f27b2402e1d29989fb12cb471f66970e1cce74d6"
OUTPUT = "artifacts/cte"  # short: TIA limits project paths to 143 characters
MEMBERS = (("Child", '"TiaMcp_EnableGate"'), ("Timer_TON", "TON_TIME"), ("Timer_TOF", "TOF_TIME"), ("Timer_TP", "TP_TIME"),
           ("Counter_CTU", "CTU_INT"), ("Counter_CTD", "CTD_INT"), ("Counter_CTUD", "CTUD_INT"))
EFFECT, PARENT, GRAND = "Tgp_NestEffect", "Tgp_NestParent", "Tgp_NestGrand"


def fb(name, statics):
    return (f'FUNCTION_BLOCK "{name}"\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\n'
            'VAR_INPUT\n Enable : Bool;\nEND_VAR\nVAR_OUTPUT\n Active : Bool;\nEND_VAR\n'
            f'VAR\n RetainedMarker : Int := 17;\n{statics}END_VAR\nBEGIN\n #Active := #Enable;\nEND_FUNCTION_BLOCK\n\n')


def db(name, starts):
    return f'DATA_BLOCK "{name}_DB"\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\n"{name}"\nBEGIN\n{starts}END_DATA_BLOCK\n\n'


def source(phase):
    added = "".join(f" {name} : {datatype};\n" for name, datatype in MEMBERS) if phase == "after" else ""
    if phase == "after":
        return fb(EFFECT, added)
    return (fb(EFFECT, "") + fb(PARENT, f' Inner : "{EFFECT}";\n') + fb(GRAND, f' Middle : "{PARENT}";\n')
            + db(EFFECT, " RetainedMarker := 42;\n")
            + db(PARENT, " RetainedMarker := 42;\n Inner.RetainedMarker := 42;\n")
            + db(GRAND, " RetainedMarker := 42;\n Middle.RetainedMarker := 42;\n Middle.Inner.RetainedMarker := 42;\n"))


def main():
    observed = [PARENT, GRAND, EFFECT + "_DB", PARENT + "_DB", GRAND + "_DB"]
    sources = []
    for phase in ("before", "after"):
        path = ROOT / "sources" / ("transitive-effect-" + phase + ".scl")
        path.write_bytes(source(phase).encode("utf-8"))
        names = [EFFECT] if phase == "after" else [EFFECT, PARENT, GRAND] + [n + "_DB" for n in (EFFECT, PARENT, GRAND)]
        row = {"path": path.relative_to(REPO).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
               "route": "standard", "authoredFormat": "SCL", "names": names, "namespace": ""}
        if phase == "after":
            row.update(modifies=[EFFECT], observes=observed)
        sources.append(row)
    command = ["python", "-B", "scripts/capture_typed_routes_owned.py", "@" + OUTPUT + ".args",
               "--host", "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe",
               "--worker", "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe",
               "--output", OUTPUT, "--archive", ARCHIVE, "--sha256", ARCHIVE_SHA256, "--unit", "",
               "--modify", "transitive-effect-after.scl:" + EFFECT]
    command += [arg for name in observed for arg in ("--observe", "transitive-effect-after.scl:" + name)]
    document = {"schemaVersion": 1, "status": "authored-not-executed",
                "purpose": "Native transitive dependent-instance effects of adding one FB member and one static member of each IEC "
                           "instance type to an FB embedded two levels deep as a multi-instance, in the standard PLC program scope. "
                           "Unobserved until captured; enables nothing.",
                "unit": "", "archive": {"path": ARCHIVE, "sha256": ARCHIVE_SHA256},
                "chain": [{"fb": EFFECT}, {"fb": PARENT, "member": "Inner"}, {"fb": GRAND, "member": "Middle"}],
                "members": [{"name": member, "datatype": datatype} for member, datatype in MEMBERS],
                "sources": sources, "argsFile": [row["path"] for row in sources], "command": command,
                "afterCapture": ["scripts/extract_typed_transitive_effects.py <capture>"]}
    target = ROOT / "route-capture-transitive-effect-standard-packet.v1.json"
    target.write_bytes((json.dumps(document, indent=2) + "\n").encode("utf-8"))
    print(target.name + ": " + str(len(sources)) + " transitive effect inputs authored; packet status authored-not-executed.")


if __name__ == "__main__":
    main()
