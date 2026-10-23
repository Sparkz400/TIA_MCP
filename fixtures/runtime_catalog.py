"""Executable specifications for block testing; none is runtime qualification evidence.

Each case requires a freshly created simulator instance and a freshly downloaded
program. Inputs and observations use ``instance.parameter[index].member`` paths.
TIME values are integer milliseconds. ``calls`` counts acknowledged application
calls, not CPU scans. Timer windows observe host monotonic time, not PLC time.
Native interface declarations below support review/offline generation only; a
live run must inspect the imported blocks before it generates the test harness.
"""
import importlib.util
import pathlib
import re


_spec = importlib.util.spec_from_file_location(
    "tia_runtime_source_catalog", pathlib.Path(__file__).with_name("catalog.py"))
_catalog = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_catalog)


def _member(name, data_type, members=None):
    return {"name": name, "dataType": data_type, "members": members or []}


def _interface(name, kind, inputs, outputs, inout=(), statics=()):
    sections = [{"name": section, "members": list(members)} for section, members in
                (("Input", inputs), ("Output", outputs), ("InOut", inout))]
    if kind == "FB":
        sections.append({"name": "Static", "members": list(statics)})
    else:
        sections.append({"name": "Return", "members": [_member("Ret_Val", "Void")]})
    return {"block": {"name": name, "kind": kind},
            "objectKinds": ["SW.Blocks." + kind], "sections": sections}


def _exact(values, instance="dut"):
    return [{"kind": "exact", "path": instance + "." + path, "expected": value}
            for path, value in values.items()]


def _step(identifier, values=None, expected=None, *, calls=1, target="dut", wait_ms=0,
          assertions=()):
    return {"id": identifier, "target": target,
            "set": {target + "." + path: value for path, value in (values or {}).items()},
            "calls": calls, "waitMs": wait_ms,
            "assertions": _exact(expected or {}, target) + list(assertions)}


def _case(identifier, steps, initial=None):
    return {"id": identifier, "initial": initial or {}, "steps": steps}


def _suite(identifier, category, title, block, source, interface, cases, **extra):
    return {"fixtureId": identifier, "category": category, "title": title,
            "language": "SCL", "blockName": block, "source": source, "names": [block],
            "interface": interface, "typeDefinitions": {}, "instances": ["dut"],
            "timingMode": "application_call_controlled",
            "runtimeQualification": "specified_not_executed",
            "isolation": "fresh_simulator_per_case", "cases": cases, **extra}


def specifications(prefix="Rtf"):
    """Return a fresh, deterministic, JSON-serializable suite list covering nine families."""
    if not isinstance(prefix, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,19}", prefix):
        raise ValueError("Prefix must be 1..20 ASCII identifier characters, beginning with a letter.")
    prior = {item["fixtureId"]: item for item in _catalog.specifications(prefix)}
    suites = []

    # Preserve the independently authored Boolean table from the conformance corpus.
    original = prior["scl-boolean"]
    block = original["names"][0]
    cases = [_case("truth-" + str(index), [_step("evaluate", vector["inputs"], vector["outputs"])])
             for index, vector in enumerate(original["expectedBehavior"]["vectors"])]
    suites.append(_suite("runtime-boolean", "boolean_logic", original["title"], block, original["source"],
        _interface(block, "FC", [_member(n, "Bool") for n in ("A", "B", "Inhibit")],
                   [_member("Active", "Bool")]), cases, sourceFixtureId=original["fixtureId"]))

    original = prior["scl-arithmetic"]
    block = original["names"][0]
    vectors = list(original["expectedBehavior"]["vectors"]) + [
        {"inputs": {"Left": -32768, "Right": -32768},
         "outputs": {"Sum": -65536, "Difference": 0, "Scaled": -32768.0}},
        {"inputs": {"Left": 32767, "Right": -32768},
         "outputs": {"Sum": -1, "Difference": 65535, "Scaled": -0.5}},
        {"inputs": {"Left": 0, "Right": 0},
         "outputs": {"Sum": 0, "Difference": 0, "Scaled": 0.0}},
        {"inputs": {"Left": -32768, "Right": -1},
         "outputs": {"Sum": -32769, "Difference": -32767, "Scaled": -16384.5}},
        {"inputs": {"Left": 1, "Right": 0},
         "outputs": {"Sum": 1, "Difference": 1, "Scaled": 0.5}}]
    cases = []
    for index, vector in enumerate(vectors):
        exact = {key: value for key, value in vector["outputs"].items() if key != "Scaled"}
        cases.append(_case("arithmetic-" + str(index), [_step("evaluate", vector["inputs"], exact,
            assertions=[{"kind": "approx", "path": "dut.Scaled", "expected": vector["outputs"]["Scaled"],
                         "absoluteTolerance": 0.00001, "relativeTolerance": 0.0}])]))
    suites.append(_suite("runtime-arithmetic", "arithmetic_conversions", original["title"], block,
        original["source"], _interface(block, "FC", [_member("Left", "Int"), _member("Right", "Int")],
            [_member("Sum", "DInt"), _member("Difference", "DInt"), _member("Scaled", "Real")]), cases,
        sourceFixtureId=original["fixtureId"],
        domain="Representable INT inputs, explicit widening before arithmetic; no narrowing overflow claim."))

    original = prior["scl-branch"]
    block = original["names"][0]
    cases = []
    for value, band in ((-32768, -1), (-11, -1), (-10, 0), (-9, 0), (0, 0),
                        (9, 0), (10, 0), (11, 1), (32767, 1)):
        for mode, selected in ((0, 0), (1, value), (2, -999), (255, -999)):
            cases.append(_case(f"value-{value}-mode-{mode}", [_step("evaluate",
                {"Value": value, "Mode": mode}, {"Band": band, "Selected": selected})]))
    suites.append(_suite("runtime-branches", "if_case", original["title"], block, original["source"],
        _interface(block, "FC", [_member("Value", "Int"), _member("Mode", "USInt")],
            [_member("Band", "Int"), _member("Selected", "Int")]), cases,
        sourceFixtureId=original["fixtureId"]))

    block = prefix + "_Edges"
    source = _catalog.fb(block, "ClockValue : Bool;", "Rising : Bool;\nFalling : Bool;",
        "PreviousClock : Bool;", "#Rising := #ClockValue AND NOT #PreviousClock;\n"
        "#Falling := NOT #ClockValue AND #PreviousClock;\n#PreviousClock := #ClockValue;")
    cases = [_case("both-edges-and-held-levels", [
        _step("initial-low", {"ClockValue": False}, {"Rising": False, "Falling": False}),
        _step("rising", {"ClockValue": True}, {"Rising": True, "Falling": False},
              assertions=[{"kind": "transition", "path": "dut.Rising", "from": False, "to": True}]),
        _step("held-high", expected={"Rising": False, "Falling": False}, calls=4),
        _step("falling", {"ClockValue": False}, {"Rising": False, "Falling": True}),
        _step("held-low", expected={"Rising": False, "Falling": False}, calls=4),
        _step("rising-again", {"ClockValue": True}, {"Rising": True, "Falling": False})]),
        _case("fresh-retained-state", [_step("first-high", {"ClockValue": True},
                                            {"Rising": True, "Falling": False})]),
        _case("explicit-pulse", [
            {**_step("pulse-high-low", expected={"Rising": False, "Falling": True}, calls=0),
             "pulse": {"path": "dut.ClockValue", "high": True, "low": False,
                       "highCalls": 1, "lowCalls": 1}},
            _step("settle", expected={"Rising": False, "Falling": False})],
            initial={"dut.ClockValue": False})]
    suites.append(_suite("runtime-edges", "edge_detection", "Rising and falling edges, held levels and fresh state",
        block, source, _interface(block, "FB", [_member("ClockValue", "Bool")],
            [_member("Rising", "Bool"), _member("Falling", "Bool")],
            statics=[_member("PreviousClock", "Bool")]), cases))

    block = prefix + "_Latches"
    source = _catalog.fb(block, "SetValue : Bool;\nResetValue : Bool;",
        "SetDominant : Bool;\nResetDominant : Bool;", "ReservedValue : Bool;",
        "IF #SetValue THEN #SetDominant := TRUE;\n"
        "ELSIF #ResetValue THEN #SetDominant := FALSE; END_IF;\n"
        "IF #ResetValue THEN #ResetDominant := FALSE;\n"
        "ELSIF #SetValue THEN #ResetDominant := TRUE; END_IF;")
    cases = [_case("sr-rs-priorities", [
        _step("clear", {"SetValue": False, "ResetValue": True},
              {"SetDominant": False, "ResetDominant": False}),
        _step("set", {"SetValue": True, "ResetValue": False},
              {"SetDominant": True, "ResetDominant": True}),
        _step("retain", {"SetValue": False}, {"SetDominant": True, "ResetDominant": True}, calls=3),
        _step("simultaneous", {"SetValue": True, "ResetValue": True},
              {"SetDominant": True, "ResetDominant": False}),
        _step("retain-priority-result", {"SetValue": False, "ResetValue": False},
              {"SetDominant": True, "ResetDominant": False}, calls=3),
        _step("reset", {"ResetValue": True}, {"SetDominant": False, "ResetDominant": False})]),
        _case("fresh-latches", [_step("initial", {"SetValue": False, "ResetValue": False},
                                       {"SetDominant": False, "ResetDominant": False})])]
    suites.append(_suite("runtime-latches", "sr_rs_latches", "SR/RS retention and simultaneous set/reset priority",
        block, source, _interface(block, "FB", [_member("SetValue", "Bool"), _member("ResetValue", "Bool")],
            [_member("SetDominant", "Bool"), _member("ResetDominant", "Bool")],
            statics=[_member("ReservedValue", "Bool")]), cases))

    original = prior["scl-iec-ton"]
    block = original["names"][0]
    cases = [_case("start-elapsed-cancel-restart", [
        _step("clear", {"Enable": False}, {"Done": False, "Elapsed": 0}),
        _step("start", {"Enable": True}, {"Done": False, "Elapsed": 0}),
        _step("elapsed", expected={"Done": False}, wait_ms=60,
            assertions=[{"kind": "elapsed", "path": "dut.Elapsed", "sinceStep": "start",
                         "capMs": 1000, "resolutionMs": 1, "maxUncertaintyMs": 500}]),
        _step("cancel", {"Enable": False}, {"Done": False, "Elapsed": 0}),
        _step("restart", {"Enable": True}, {"Done": False}),
        _step("reset-again", {"Enable": False}, {"Done": False, "Elapsed": 0})],
        initial={"dut.Preset": 1000, "dut.Enable": False}),
        _case("completion-window-and-reset", [
        _step("clear", {"Enable": False}, {"Done": False, "Elapsed": 0}),
        _step("complete", {"Enable": True}, assertions=[
            {"kind": "window", "path": "dut.Done", "expected": True,
             "minMs": 100, "maxMs": 3000, "pollMs": 10}]),
        _step("completed-elapsed", expected={"Done": True, "Elapsed": 1000}),
        _step("reset-completed", {"Enable": False}, {"Done": False, "Elapsed": 0})],
        initial={"dut.Preset": 1000, "dut.Enable": False})]
    suites.append(_suite("runtime-timers", "timers", "TON start, elapsed time, cancellation, completion and reset",
        block, original["source"], _interface(block, "FB", [_member("Enable", "Bool"), _member("Preset", "Time")],
            [_member("Done", "Bool"), _member("Elapsed", "Time")], statics=[_member("DelayTimer", "TON_TIME")]),
        cases, sourceFixtureId=original["fixtureId"], requires=["native.TON_TIME"],
        timingLimitations=["Host observation windows include calls, transport and polling jitter.",
            "Completion uses a 1000 ms native preset so multiple negative observations can bracket the transition beyond the unchanged 100 ms minimum; 200 ms was not resolvable through this transport.",
            "No CPU scan synchronization or PLC monotonic timestamp evidence is implied."]))

    block = prefix + "_SmallCounter"
    source = _catalog.fb(block, "ClockValue : Bool;\nResetValue : Bool;\nPreset : SInt;",
        "Reached : Bool;\nCountValue : SInt;", "EdgeCounter : CTU_SINT;",
        "#EdgeCounter(CU := #ClockValue, R := #ResetValue, PV := #Preset, Q => #Reached, CV => #CountValue);")
    cases = [_case("edges-threshold-reset", [
        _step("clear", {"ResetValue": True, "ClockValue": False}, {"Reached": False, "CountValue": 0}),
        _step("edge-one", {"ResetValue": False, "ClockValue": True}, {"Reached": False, "CountValue": 1}),
        _step("held-high", expected={"Reached": False, "CountValue": 1}, calls=4),
        _step("low", {"ClockValue": False}, {"Reached": False, "CountValue": 1}),
        _step("threshold", {"ClockValue": True}, {"Reached": True, "CountValue": 2}),
        _step("reset-wins", {"ResetValue": True, "ClockValue": True}, {"Reached": False, "CountValue": 0}),
        _step("rearm", {"ResetValue": False, "ClockValue": False}, {"Reached": False, "CountValue": 0}),
        _step("count-after-reset", {"ClockValue": True}, {"Reached": False, "CountValue": 1})],
        initial={"dut.Preset": 2})]
    saturation = [_step("clear", {"ResetValue": True, "ClockValue": False}, {"Reached": False, "CountValue": 0})]
    for edge in range(1, 129):
        saturation.append(_step("low-" + str(edge), {"ResetValue": False, "ClockValue": False}))
        saturation.append(_step("edge-" + str(edge), {"ClockValue": True},
            {"Reached": edge >= 127, "CountValue": min(edge, 127)}))
    saturation.append(_step("reset-saturated", {"ResetValue": True, "ClockValue": False},
                            {"Reached": False, "CountValue": 0}))
    limit_case = _case("signed-storage-limit", saturation, initial={"dut.Preset": 127})
    # Hundreds of independently observed calls exceed the ordinary two-minute
    # case budget at native transport speed. Keep all stimuli/assertions and an
    # explicit finite deadline; this is host execution time, not PLC timing.
    limit_case["maxDurationMs"] = 300000
    cases.append(limit_case)
    suites.append(_suite("runtime-counters", "counters", "CTU edges, preset threshold, signed storage limit and reset",
        block, source, _interface(block, "FB", [_member("ClockValue", "Bool"), _member("ResetValue", "Bool"),
            _member("Preset", "SInt")], [_member("Reached", "Bool"), _member("CountValue", "SInt")],
            statics=[_member("EdgeCounter", "CTU_SINT")]), cases, requires=["native.CTU_SINT"],
        sourceFixtureId="scl-iec-ctu", sourceAdaptation="SINT storage makes saturation test finite at 128 rising edges."))

    original = prior["scl-multi-instance"]
    block = original["names"][-1]
    child = original["names"][0]
    cases = [_case("independent-outer-and-inner-instances", [
        _step("clear-left", {"ResetValue": True}, {"CountA": 0, "CountB": 0}, target="left"),
        _step("clear-right", {"ResetValue": True}, {"CountA": 0, "CountB": 0}, target="right"),
        _step("left-a-three", {"ResetValue": False, "EnableA": True}, {"CountA": 3, "CountB": 0},
              target="left", calls=3, assertions=_exact({"CountA": 0, "CountB": 0}, "right")),
        _step("right-b-two", {"ResetValue": False, "EnableB": True}, {"CountA": 0, "CountB": 2},
              target="right", calls=2, assertions=_exact({"CountA": 3, "CountB": 0}, "left")),
        _step("left-b-one", {"EnableA": False, "EnableB": True}, {"CountA": 3, "CountB": 1}, target="left"),
        _step("reset-left", {"ResetValue": True}, {"CountA": 0, "CountB": 0}, target="left"),
        _step("retain-right", {"EnableB": False}, {"CountA": 0, "CountB": 2}, target="right")],
        initial={"left.EnableA": False, "left.EnableB": False, "right.EnableA": False, "right.EnableB": False}),
        _case("fresh-instances", [
            _step("left-start", expected={"CountA": 0, "CountB": 0}, target="left"),
            _step("right-start", expected={"CountA": 0, "CountB": 0}, target="right")])]
    suites.append(_suite("runtime-fb-instances", "fb_instances", original["title"], block, original["source"],
        _interface(block, "FB", [_member(n, "Bool") for n in ("EnableA", "EnableB", "ResetValue")],
            [_member("CountA", "DInt"), _member("CountB", "DInt")],
            statics=[_member("First", '"' + child + '"'), _member("Second", '"' + child + '"')]), cases,
        instances=["left", "right"], names=original["names"], sourceFixtureId=original["fixtureId"],
        typeDefinitions={child: _interface(child, "FB", [_member("Enable", "Bool"), _member("ResetValue", "Bool")],
            [_member("CountValue", "DInt")], statics=[_member("ReservedValue", "Bool")])}))

    original = prior["scl-udt-array"]
    data_block = original["names"][0]
    data_type = original["typeNames"][0]
    block = prefix + "_ArrayAccess"
    source = (original["source"] + f'FUNCTION "{block}" : Void\n'
        "{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.1\n"
        "VAR_INPUT\nIndex : Int;\nLoadDefaults : Bool;\nEND_VAR\n"
        "VAR_OUTPUT\nIndexedValue : Int;\nCountValue : DInt;\nEND_VAR\n"
        f'VAR_IN_OUT\nItem : "{data_type}";\nEND_VAR\nBEGIN\n'
        f'IF #LoadDefaults THEN #Item := "{data_block}".Item; END_IF;\n'
        "#IndexedValue := #Item.Samples[#Index];\n#CountValue := #Item.Meta.CountValue;\nEND_FUNCTION\n")
    members = [_member("Enabled", "Bool"), _member("Samples", "Array[-2..2] of Int"),
               _member("Meta", "Struct", [_member("CountValue", "DInt"), _member("LabelText", "String[32]")])]
    cases = [_case("db-start-values-and-indexed-inout", [
        _step("load-start-values", {"Index": -2, "LoadDefaults": True},
            {"IndexedValue": -17, "CountValue": 5, "Item.Enabled": True,
             "Item.Samples[-2]": -17, "Item.Samples[0]": 0, "Item.Samples[2]": 23,
             "Item.Meta.CountValue": 5, "Item.Meta.LabelText": ""}),
        _step("nested-writes", {"LoadDefaults": False, "Index": -1, "Item.Samples[-1]": -32768,
            "Item.Meta.CountValue": -5, "Item.Meta.LabelText": "nested"},
            {"IndexedValue": -32768, "CountValue": -5, "Item.Meta.LabelText": "nested"}),
        _step("upper-index", {"Index": 2, "Item.Samples[2]": 32767, "Item.Enabled": False},
            {"IndexedValue": 32767, "Item.Enabled": False, "Item.Samples[-1]": -32768}),
        _step("restore-start-values", {"LoadDefaults": True},
            {"IndexedValue": 23, "CountValue": 5, "Item.Enabled": True,
             "Item.Samples[-1]": 0, "Item.Meta.LabelText": ""})]),
        _case("fresh-inout-initialization", [_step("default-zero", {"Index": 0, "LoadDefaults": False},
            {"IndexedValue": 0, "CountValue": 0, "Item.Enabled": False,
             "Item.Samples[-2]": 0, "Item.Samples[2]": 0, "Item.Meta.LabelText": ""})])]
    suites.append(_suite("runtime-arrays-udts", "arrays_udts", "Indexed in/out arrays, nested UDT values and DB initialization",
        block, source, _interface(block, "FC", [_member("Index", "Int"), _member("LoadDefaults", "Bool")],
            [_member("IndexedValue", "Int"), _member("CountValue", "DInt")],
            inout=[_member("Item", '"' + data_type + '"')]), cases,
        names=[data_block, block], typeSource=original["typeSource"], typeNames=[data_type],
        typeDefinitions={data_type: members}, sourceFixtureId=original["fixtureId"],
        domain="Index is restricted to the declared array range -2..2."))
    return suites
