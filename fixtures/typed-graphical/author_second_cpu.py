"""Typed references and cases for a second CPU: CPU 1513-1 PN V4.1 (6ES7 513-1AM03-0AB0).

The unchanged standard-route reference programs are captured in owned scratches of that CPU
(capture_typed_routes_owned.py --cpu-type-identifier), so its profiles carry their own ids
(v21.6es7-513-1am03-0ab0-v4-1.*), native folder (native/6es7-513-1am03-0ab0-v4-1/) and dependent-effect
manifests (instance-effects*.6es7-513-1am03-0ab0-v4-1.v1.json). Four runs: Boolean, Int compare and calls;
creation/instance templates; TON/TOF/TP and CTU/CTD/CTUD with global storage; the three effect pairs.
Wave 9 families and the software-unit routes are not captured on this CPU. The cases are the standard cases bound
to that CPU and its references, engineering only. Writes second-cpu-capture-packet.v1.json and
second-cpu-cases.v1.json; enables nothing.
"""
import copy
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent
CPU = "OrderNumber:6ES7 513-1AM03-0AB0/V4.1"
SLUG = "6es7-513-1am03-0ab0-v4-1"  # extract_typed_graphical_profiles.cpu_slug(CPU)
HOST = "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe"
WORKER = "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe"
LANGUAGES = ("lad", "fbd")
RUNS = {
    "artifacts/c2a": ["boolean-lad.s7dcl", "boolean-fbd.s7dcl", "and-fbd.s7dcl", "not-fbd.s7dcl", "not-one-fbd.s7dcl"]
                     + [op + "-" + language + ".s7dcl" for language in LANGUAGES for op in ("eq", "ne", "lt", "le", "gt", "ge")]
                     + [family + "-" + language + ".s7dcl" for family in ("fc", "fb-multi", "fb-global") for language in LANGUAGES]
                     + ["numeric-callees.scl"] + [family + "-" + language + ".s7dcl" for family in ("numeric-fc", "numeric-fb") for language in LANGUAGES],
    # Each TemplateFB precedes its DB and multi-instance host.
    "artifacts/c2b": [kind + "-" + language + ".s7dcl" for kind in ("template-fc", "template-fb") for language in LANGUAGES]
                     + [kind + "-template-fb-" + language + ".scl" for kind in ("global", "multi") for language in LANGUAGES],
    "artifacts/c2c": ["global-timer-storage.scl"] + [family + suffix + "-" + language + ".s7dcl" for language in LANGUAGES
                     for family in ("ton", "tof", "tp") for suffix in ("", "-global")]
                     + ["global-counter-storage.scl"] + [family + suffix + "-" + language + ".s7dcl" for language in LANGUAGES
                     for family in ("ctu", "ctd", "ctud") for suffix in ("", "-global")],
    # The effect pairs run alone: an after phase re-exports its FB, which a profile staging would reject as a name collision.
    # The instance effect's after phase embeds Tgp_TemplateFB_LAD.
    "artifacts/c2d": ["template-fb-lad.s7dcl"] + [effect + "-effect-" + phase + ".scl" for effect in ("instance", "iec", "transitive")
                                                  for phase in ("before", "after")],
}
# The after phase redeclares the effect FB; TIA updates its instance DB (and, transitively, the parents) natively.
EFFECTS = {"instance-effect-after.scl": ("Tgp_InstanceEffect", ["Tgp_InstanceEffect_DB"]),
           "iec-effect-after.scl": ("Tgp_IecEffect", ["Tgp_IecEffect_DB"]),
           "transitive-effect-after.scl": ("Tgp_NestEffect", ["Tgp_NestParent", "Tgp_NestGrand", "Tgp_NestEffect_DB", "Tgp_NestParent_DB", "Tgp_NestGrand_DB"])}


def native(path):
    """A case's committed reference of the first target, retargeted to this CPU's native folder."""
    prefix = "fixtures/typed-graphical/native/"
    assert path.startswith(prefix) and "/" not in path[len(prefix):], path
    return prefix + SLUG + "/" + path[len(prefix):]


def cases():
    """Every standard case whose references this CPU's captures provide; insert-first needs the uncaptured network-free probe."""
    result = []
    for name in ("cases.v1.json", "existing-global-cases.v1.json", "extended-cases.v1.json", "numeric-cases.v1.json"):
        for original in json.loads((ROOT / name).read_text(encoding="utf-8"))["cases"]:
            if "network-free" in original["referenceXml"]:
                continue
            case = copy.deepcopy(original)
            case.update(fixtureId=original["fixtureId"].replace("typed-", "typed-cpu1513-", 1), cpuTypeIdentifier=CPU,
                        referenceXml=native(original["referenceXml"]), engineeringOnly=True)
            for dependency in case.get("dependencyXmls", []):
                dependency["path"] = native(dependency["path"])
            result.append(case)
    return result


def main():
    sources, runs = [], {}
    for output, files in RUNS.items():
        paths = ["fixtures/typed-graphical/sources/" + file for file in files]
        for path in paths:
            sources.append({"path": path, "sha256": hashlib.sha256((REPO / path).read_bytes()).hexdigest()})
        flags = [flag for file in files if file in EFFECTS for flag in
                 ["--modify", file + ":" + EFFECTS[file][0]] + [item for block in EFFECTS[file][1] for item in ("--observe", file + ":" + block)]]
        runs[output] = {"argsFile": paths, "command": ["python", "-B", "scripts/capture_typed_routes_owned.py", "@" + output + ".args",
                        "--host", HOST, "--worker", WORKER, "--output", output, "--cpu-type-identifier", CPU, "--unit", "", *flags]}
    profiles = [output for output in runs if output != "artifacts/c2d"]
    document = {"schemaVersion": 1, "status": "authored-not-executed", "cpuTypeIdentifier": CPU, "cpuSlug": SLUG,
                "purpose": "Standard-route references of the base typed families on a second CPU (Boolean, Int compare, FC/FB calls, "
                           "creation/instance templates, TON/TOF/TP, CTU/CTD/CTUD with global storage) and its three dependent-effect "
                           "pairs, each run in a new owned scratch of that CPU. Unobserved until captured; enables nothing.",
                "sources": sources, "runs": runs,
                "afterCapture": ["python -B scripts/extract_typed_graphical_profiles.py " + " ".join(o + "/capture.json" for o in profiles)
                                 + " --stage-output artifacts/c2s",
                                 "python -B scripts/audit_typed_profile_candidates.py --catalog artifacts/c2s/profiles.candidate.v1.json "
                                 + " ".join("--capture " + o + "/capture.json" for o in profiles) + " --output artifacts/c2s/audit.json",
                                 "python -B scripts/extract_typed_instance_effects.py artifacts/c2d/capture.json artifacts/c2d/capture.json",
                                 "python -B scripts/extract_typed_instance_effects.py artifacts/c2d/capture.json artifacts/c2d/capture.json --iec",
                                 "python -B scripts/extract_typed_transitive_effects.py artifacts/c2d/capture.json"]}
    (ROOT / "second-cpu-capture-packet.v1.json").write_bytes((json.dumps(document, indent=2) + "\n").encode("utf-8"))
    authored = cases()
    (ROOT / "second-cpu-cases.v1.json").write_bytes((json.dumps({"schemaVersion": 1, "cases": authored}, indent=2) + "\n").encode("utf-8"))
    print(str(len(sources)) + " second-CPU references in " + str(len(runs)) + " runs and " + str(len(authored)) + " cases authored.")


if __name__ == "__main__":
    main()
