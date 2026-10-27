"""Transitive dependent-instance effect pairs for the standard, software-unit and namespace routes.

Before: FB <prefix>NestEffect, embedded as multi-instance Inner in <prefix>NestParent, itself embedded as Middle in
<prefix>NestGrand, each with an instance DB holding explicit start values (42). After: only <prefix>NestEffect is
redeclared with one FB member and one static member of each IEC instance type while both parents and all three DBs are
observed. One packet per capture scope (unit routes; standard). Rerun after editing.
"""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent
ARCHIVE = "artifacts/changes/change-12d1d3b9e3a74cd3be6f53b9f2f35cf4/before.zap21"
ARCHIVE_SHA256 = "39cea56a73b8e58263e256c2f27b2402e1d29989fb12cb471f66970e1cce74d6"
UNIT = "TiaMcp_UnitA"
ROUTES = (("Unit", UNIT), ("Namespace", UNIT + ".Alternate"))
IEC_MEMBERS = (("Timer_TON", "TON_TIME"), ("Timer_TOF", "TOF_TIME"), ("Timer_TP", "TP_TIME"),
               ("Counter_CTU", "CTU_INT"), ("Counter_CTD", "CTD_INT"), ("Counter_CTUD", "CTUD_INT"))
MEMBERS = (("Child", '"TiaMcp_EnableGate"'),) + IEC_MEMBERS
EFFECT, PARENT, GRAND = "Tgp_NestEffect", "Tgp_NestParent", "Tgp_NestGrand"


def route_of(namespace):
    return "standard" if not namespace else "softwareUnit" if namespace == UNIT else "namespace"


def qualified(namespace, name):
    return (namespace + "." if namespace else "") + name


def members(prefix, namespace):
    # Unit routes embed the route's own captured template FB (the direct route effect's Child type).
    child = '"' + qualified(namespace, prefix + "TemplateFB_LAD") + '"' if namespace else MEMBERS[0][1]
    return (("Child", child),) + IEC_MEMBERS


def fb(name, statics):
    return (f'FUNCTION_BLOCK "{name}"\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\n'
            'VAR_INPUT\n Enable : Bool;\nEND_VAR\nVAR_OUTPUT\n Active : Bool;\nEND_VAR\n'
            f'VAR\n RetainedMarker : Int := 17;\n{statics}END_VAR\nBEGIN\n #Active := #Enable;\nEND_FUNCTION_BLOCK\n\n')


def db(name, instance_of, starts):
    return f'DATA_BLOCK "{name}_DB"\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\n"{instance_of}"\nBEGIN\n{starts}END_DATA_BLOCK\n\n'


def source(phase, prefix="Tgp_", namespace=""):
    effect, parent, grand = (prefix + n for n in ("NestEffect", "NestParent", "NestGrand"))
    q = lambda name: qualified(namespace, name)
    if phase == "after":
        text = fb(effect, "".join(f" {name} : {datatype};\n" for name, datatype in members(prefix, namespace)))
    else:
        text = (fb(effect, "") + fb(parent, f' Inner : "{q(effect)}";\n') + fb(grand, f' Middle : "{q(parent)}";\n')
                + db(effect, q(effect), " RetainedMarker := 42;\n")
                + db(parent, q(parent), " RetainedMarker := 42;\n Inner.RetainedMarker := 42;\n")
                + db(grand, q(grand), " RetainedMarker := 42;\n Middle.RetainedMarker := 42;\n Middle.Inner.RetainedMarker := 42;\n"))
    # The standard route has no namespace: plain sources for the PLC program scope.
    return f"NAMESPACE {namespace}\n{text}END_NAMESPACE\n" if namespace else text


def entry(path, route, form, names, namespace, **extra):
    return {"path": path.relative_to(REPO).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "route": route, "authoredFormat": form, "names": names, "namespace": namespace, **extra}


def packet(name, routes, unit, output, scope_text):
    sources = []
    for label, namespace in routes:
        if namespace:  # the route Child type is imported first, as in the scoped direct-effect capture
            template = REPO / ("fixtures/typed-graphical/native/Tgp" + label + "_TemplateFB_LAD.xml")
            sources.append(entry(template, route_of(namespace), "native_xml_template_derivation", ["Tgp" + label + "_TemplateFB_LAD"], namespace))
    for phase in ("before", "after"):
        for label, namespace in routes:
            prefix = "Tgp" + label + "_"
            effect, parent, grand = (prefix + n for n in ("NestEffect", "NestParent", "NestGrand"))
            path = ROOT / "sources" / (("route-" + label + "-" if label else "") + "transitive-effect-" + phase + ".scl")
            path.write_bytes(source(phase, prefix, namespace).encode("utf-8"))
            names = [effect] if phase == "after" else [effect, parent, grand] + [n + "_DB" for n in (effect, parent, grand)]
            extra = {"modifies": [effect], "observes": [parent, grand, effect + "_DB", parent + "_DB", grand + "_DB"]} if phase == "after" else {}
            sources.append(entry(path, route_of(namespace), "SCL", names, namespace, **extra))
    command = ["python", "-B", "scripts/capture_typed_routes_owned.py", "@" + output + ".args",
               "--host", "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe",
               "--worker", "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe",
               "--output", output, "--archive", ARCHIVE, "--sha256", ARCHIVE_SHA256] + ([] if unit else ["--unit", ""])
    for row in sources:
        file = Path(row["path"]).name
        command += [arg for member in row.get("modifies", []) for arg in ("--modify", file + ":" + member)]
        command += [arg for member in row.get("observes", []) for arg in ("--observe", file + ":" + member)]
    document = {"schemaVersion": 1, "status": "authored-not-executed",
                "purpose": "Native transitive dependent-instance effects of adding one FB member and one static member of each IEC "
                           "instance type to an FB embedded two levels deep as a multi-instance, " + scope_text + ". "
                           "Unobserved until captured; enables nothing.",
                "unit": unit, "archive": {"path": ARCHIVE, "sha256": ARCHIVE_SHA256}}
    if unit:
        document["routes"] = [{"route": route_of(namespace), "namespace": namespace, "prefix": "Tgp" + label + "_",
                               "chain": [{"fb": qualified(namespace, "Tgp" + label + "_" + fb_name), **({"member": member} if member else {})}
                                         for fb_name, member in (("NestEffect", None), ("NestParent", "Inner"), ("NestGrand", "Middle"))],
                               "members": [{"name": m, "datatype": t} for m, t in members("Tgp" + label + "_", namespace)]}
                              for label, namespace in routes]
    else:
        document["chain"] = [{"fb": EFFECT}, {"fb": PARENT, "member": "Inner"}, {"fb": GRAND, "member": "Middle"}]
        document["members"] = [{"name": member, "datatype": datatype} for member, datatype in MEMBERS]
    document.update(sources=sources, argsFile=[row["path"] for row in sources], command=command,
                    afterCapture=["scripts/extract_typed_transitive_effects.py <capture>" + ("" if not namespace else " --route " + route_of(namespace))
                                  for _, namespace in routes])
    (ROOT / name).write_bytes((json.dumps(document, indent=2) + "\n").encode("utf-8"))
    print(name + ": " + str(len(sources)) + " transitive effect inputs authored; packet status authored-not-executed.")


def main():
    packet("route-capture-transitive-effect-packet.v1.json", ROUTES, UNIT, "artifacts/ctr", "in the software-unit and namespace routes")
    # short: TIA limits project paths to 143 characters
    packet("route-capture-transitive-effect-standard-packet.v1.json", (("", ""),), "", "artifacts/cte", "in the standard PLC program scope")


if __name__ == "__main__":
    main()
