"""Independent bounded driver for the CTU Int upper-limit runtime acceptance.

The driver calls the actual typed graphical FB. It does not access IEC internals
or use the XML emitter. 512 edges per invocation keeps each PLC loop bounded;
the ordinary test executor retains its existing host-call and operation limits.
"""
import copy
import hashlib
import json
import pathlib


ROOT = pathlib.Path(__file__).resolve().parent


def author():
    baseline = json.loads((ROOT / "cases.v1.json").read_text(encoding="utf-8"))
    result = []
    for language in ("LAD", "FBD"):
        case = copy.deepcopy(next(c for c in baseline["cases"] if c["family"] == "ctu" and c["language"] == language))
        block = "Tgp_CTU_LimitDriver_" + language
        source = f'''FUNCTION_BLOCK "{block}"
{{ S7_Optimized_Access := 'TRUE' }}
VAR_INPUT
    Edges : Int;
    Reset : Bool;
END_VAR
VAR_OUTPUT
    Count : Int;
    Done : Bool;
    Rejected : Bool;
END_VAR
VAR
    Counter : "Tgp_CTU_{language}";
END_VAR
VAR_TEMP
    Index : Int;
END_VAR
BEGIN
    #Rejected := (#Edges < 0) OR (#Edges > 512);
    IF NOT #Rejected THEN
        IF #Reset OR (#Edges = 0) THEN
            #Counter(Enable := FALSE, Down := FALSE, Reset := #Reset,
                     Load := FALSE, Preset := 32767,
                     Done => #Done, Count => #Count);
        ELSE
            FOR #Index := 1 TO #Edges DO
                #Counter(Enable := FALSE, Down := FALSE, Reset := FALSE,
                         Load := FALSE, Preset := 32767,
                         Done => #Done, Count => #Count);
                #Counter(Enable := TRUE, Down := FALSE, Reset := FALSE,
                         Load := FALSE, Preset := 32767,
                         Done => #Done, Count => #Count);
            END_FOR;
        END_IF;
    END_IF;
END_FUNCTION_BLOCK
'''
        path = ROOT / "sources" / ("ctu-limit-driver-" + language.lower() + ".scl")
        path.write_bytes(source.encode("utf-8"))
        case.update(fixtureId="typed-ctu-upper-limit-" + language.lower(), family="ctu-upper-limit",
                    runtimeBlockName=block, interfaceDependencies=[case["blockName"]],
                    runtimeSources=[{"language": "SCL", "source": source, "expectedBlockNames": [block]}],
                    runtimeDriverProvenance={"path": str(path.relative_to(ROOT.parent.parent)).replace("\\", "/"),
                        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                        "nativeDutCallsPerDriverInvocationAtMost": 1024,
                        "attemptedRisingEdges": 32768,
                        "assertionScope": "Signed Int CTU upper saturation and reset, not exact scan timing."})
        result.append(case)
    (ROOT / "counter-limit-cases.v1.json").write_text(json.dumps({"schemaVersion": 1, "cases": result}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    author()
