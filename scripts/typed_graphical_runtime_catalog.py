"""Independent behavioral specifications for typed LAD/FBD qualification.

No builder, serializer, native XML reader, or Siemens API is imported here. The
vectors state externally observable behavior, and use the existing block-test
executor contract. They are specifications, never evidence of runtime success.
An executing qualifier must inspect the actual generated caller interface and
use that fresh inspection to build the harness; `interface` below is a contract
for reviewing/preflighting the fixture only.
"""
import copy
import re


def _member(name, data_type):
    return {"name": name, "dataType": data_type, "members": []}


def _interface(name, kind, inputs, outputs, inout=()):
    sections = [{"name": section, "members": [_member(*pair) for pair in pairs]}
                for section, pairs in (("Input", inputs), ("Output", outputs), ("InOut", inout))]
    sections.append({"name": "Return" if kind == "FC" else "Static",
                     "members": [_member("Ret_Val", "Void")] if kind == "FC" else []})
    return {"block": {"name": name, "kind": kind}, "objectKinds": ["SW.Blocks." + kind],
            "sections": sections}


def _exact(values, target="dut"):
    return [{"kind": "exact", "path": target + "." + key, "expected": value}
            for key, value in values.items()]


def _step(identifier, values=None, expected=None, *, target="dut", calls=1, wait_ms=0,
          assertions=()):
    return {"id": identifier, "target": target,
            "set": {target + "." + key: value for key, value in (values or {}).items()},
            "calls": calls, "waitMs": wait_ms,
            "assertions": _exact(expected or {}, target) + list(assertions)}


def _case(identifier, steps, initial=None):
    return {"id": identifier, "initial": initial or {}, "steps": steps}


def specifications(prefix="Tgf"):
    """Return independent behavior suites, separately identified per language."""
    if not isinstance(prefix, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,19}", prefix):
        raise ValueError("Prefix must be 1..20 ASCII identifier characters beginning with a letter.")
    suites = []

    def add(language, family, name, kind, inputs, outputs, cases, *, inout=(), instances=("dut",), **extra):
        block = prefix + "_" + language + "_" + name
        suites.append({"schemaVersion": 1, "fixtureId": "typed-" + language.lower() + "-" + family,
            "language": language, "category": family, "blockName": block,
            "interface": _interface(block, kind, inputs, outputs, inout), "typeDefinitions": {},
            "instances": list(instances), "timingMode": "application_call_controlled",
            "runtimeQualification": "specified_not_executed", "isolation": "fresh_simulator_per_case",
            "nativeSource": "Typed change must be applied and read back before harness construction.",
            "expectationProvenance": "Independently authored stimulus/response vectors; no native emitter dependency.",
            "cases": copy.deepcopy(cases), **copy.deepcopy(extra)})

    # Full truth table for (A OR B) AND NOT Inhibit, in A/B/Inhibit order.
    boolean_rows = [(False, False, False, False), (False, False, True, False),
                    (False, True, False, True), (False, True, True, False),
                    (True, False, False, True), (True, False, True, False),
                    (True, True, False, True), (True, True, True, False)]
    # EQ, NE, LT, LE, GT, GE are explicitly authored truth columns.
    comparison_rows = [(-32768, 32767, (False, True, True, True, False, False)),
                       (32767, -32768, (False, True, False, False, True, True)),
                       (-1, 0, (False, True, True, True, False, False)),
                       (0, 0, (True, False, False, True, False, True)),
                       (0, 1, (False, True, True, True, False, False)),
                       (1, 0, (False, True, False, False, True, True)),
                       (32767, 32767, (True, False, False, True, False, True))]

    for language in ("LAD", "FBD"):
        add(language, "boolean-branches", "Boolean", "FC",
            [(name, "Bool") for name in ("A", "B", "Inhibit")], [("Active", "Bool")],
            [_case("truth-" + str(index), [_step("evaluate", {"A": a, "B": b, "Inhibit": inhibit},
                {"Active": result})]) for index, (a, b, inhibit, result) in enumerate(boolean_rows)])
        for column, operation in enumerate(("eq", "ne", "lt", "le", "gt", "ge")):
            add(language, "compare-" + operation + "-int", "Compare_" + operation, "FC",
                [("Left", "Int"), ("Right", "Int")], [("Result", "Bool")],
                [_case("boundary-" + str(index), [_step("evaluate", {"Left": left, "Right": right},
                    {"Result": expected[column]})])
                 for index, (left, right, expected) in enumerate(comparison_rows)], comparison=operation)
        add(language, "fc-parameter-flow", "FcCall", "FC", [("Value", "Int"), ("Delta", "Int")],
            [("Result", "Int")], [
                _case("positive", [_step("call", {"Value": 7, "Delta": 3}, {"Result": 10, "Accumulator": 17})],
                      {"dut.Accumulator": 10}),
                _case("signed", [_step("call", {"Value": -7, "Delta": 2}, {"Result": -5, "Accumulator": -3})],
                      {"dut.Accumulator": 4})], inout=[("Accumulator", "Int")],
            calleeBehavior="Result = Value + Delta; Accumulator = prior Accumulator + Value.")
        add(language, "fb-instance-independence", "FbCall", "FB", [("Increment", "Int"), ("Reset", "Bool")],
            [("Total", "Int")], [_case("separate-state", [
                _step("left-clear", {"Reset": True}, {"Total": 0}, target="left"),
                _step("right-clear", {"Reset": True}, {"Total": 0}, target="right"),
                _step("left-two-calls", {"Reset": False, "Increment": 3}, {"Total": 6}, target="left", calls=2,
                      assertions=_exact({"Total": 0}, "right")),
                _step("right-call", {"Reset": False, "Increment": -2}, {"Total": -2}, target="right",
                      assertions=_exact({"Total": 6}, "left")),
                _step("left-reset", {"Reset": True}, {"Total": 0}, target="left",
                      assertions=_exact({"Total": -2}, "right"))])], instances=("left", "right"),
            calleeBehavior="Reset clears Total, otherwise each call adds Increment.",
            requiredInstanceRoutes=["existingGlobal", "existingMulti", "createGlobal", "createMulti"])

        timer_io = ([("Enable", "Bool"), ("Preset", "Time")], [("Done", "Bool"), ("Elapsed", "Time")])
        timers = {
            "TON": [_case("cancel-restart", [
                _step("clear", {"Enable": False}, {"Done": False, "Elapsed": 0}),
                _step("start", {"Enable": True}, {"Done": False, "Elapsed": 0}),
                _step("progress", expected={"Done": False}, wait_ms=60, assertions=[
                    {"kind": "elapsed", "path": "dut.Elapsed", "sinceStep": "start",
                     "capMs": 1000, "resolutionMs": 1, "maxUncertaintyMs": 500}]),
                _step("cancel", {"Enable": False}, {"Done": False, "Elapsed": 0}),
                _step("restart", {"Enable": True}, {"Done": False}),
                _step("reset", {"Enable": False}, {"Done": False, "Elapsed": 0})], {"dut.Preset": 1000}),
                _case("complete", [_step("clear", {"Enable": False}, {"Done": False, "Elapsed": 0}),
                    _step("complete", {"Enable": True}, assertions=[
                        {"kind": "window", "path": "dut.Done", "expected": True,
                         "minMs": 100, "maxMs": 3000, "pollMs": 10}]),
                    _step("elapsed-limit", expected={"Done": True, "Elapsed": 1000}),
                    _step("reset", {"Enable": False}, {"Done": False, "Elapsed": 0})], {"dut.Preset": 1000})],
            "TOF": [_case("fall-cancel-restart", [
                _step("clear", {"Enable": False}, {"Done": False, "Elapsed": 0}),
                _step("energize", {"Enable": True}, {"Done": True, "Elapsed": 0}),
                _step("fall", {"Enable": False}, {"Done": True}),
                _step("cancel-off-delay", {"Enable": True}, {"Done": True, "Elapsed": 0}),
                _step("complete-off-delay", {"Enable": False}, assertions=[
                    {"kind": "window", "path": "dut.Done", "expected": False,
                     "minMs": 100, "maxMs": 1200, "pollMs": 10}]),
                _step("restart", {"Enable": True}, {"Done": True, "Elapsed": 0})], {"dut.Preset": 200})],
            "TP": [_case("pulse-survives-cancel", [
                _step("clear", {"Enable": False}, {"Done": False, "Elapsed": 0}),
                _step("rise", {"Enable": True}, {"Done": True}),
                _step("input-falls", {"Enable": False}, {"Done": True}),
                _step("input-rises-within-pulse", {"Enable": True}, {"Done": True}),
                _step("complete", assertions=[{"kind": "window", "path": "dut.Done", "expected": False,
                    "minMs": 0, "maxMs": 1500, "pollMs": 10}]),
                _step("held-high-no-retrigger", expected={"Done": False}, calls=3),
                _step("rearm", {"Enable": False}, {"Done": False, "Elapsed": 0}),
                _step("restart", {"Enable": True}, {"Done": True})], {"dut.Preset": 500})]
        }
        for instruction, cases in timers.items():
            add(language, "timer-" + instruction.lower(), instruction, "FB", *timer_io, cases,
                instruction=instruction, nativeStorageType=instruction + "_TIME",
                timingLimitations=["Host windows include transport, application calls and polling jitter.",
                    "No scan synchronization or exact PLC timing is established.",
                    "TP pulse checks require the early observations to precede PT; late observations fail."])

        count_outputs = [("CountValue", "Int"), ("UpReached", "Bool"), ("DownReached", "Bool")]
        count_inputs = [(name, "Bool") for name in ("Up", "Down", "Reset", "Load")] + [("Preset", "Int")]
        counter_cases = {
            "CTU": [_case("edges-threshold-reset", [
                _step("clear", {"Reset": True, "Up": False}, {"CountValue": 0, "UpReached": False}),
                _step("first-rise", {"Reset": False, "Up": True}, {"CountValue": 1, "UpReached": False}),
                _step("held-high", expected={"CountValue": 1}, calls=4),
                _step("rearm", {"Up": False}, {"CountValue": 1}),
                _step("threshold", {"Up": True}, {"CountValue": 2, "UpReached": True}),
                _step("reset-priority", {"Reset": True}, {"CountValue": 0, "UpReached": False})],
                {"dut.Preset": 2})],
            "CTD": [_case("load-falling-count-zero", [
                _step("load", {"Load": True, "Down": False}, {"CountValue": 2, "DownReached": False}),
                _step("first-rise", {"Load": False, "Down": True}, {"CountValue": 1, "DownReached": False}),
                _step("held-high", expected={"CountValue": 1}, calls=4),
                _step("rearm", {"Down": False}, {"CountValue": 1}),
                _step("zero", {"Down": True}, {"CountValue": 0, "DownReached": True}),
                _step("reload-priority", {"Load": True}, {"CountValue": 2, "DownReached": False})],
                {"dut.Preset": 2})],
            "CTUD": [_case("dual-edges-priorities", [
                _step("reset", {"Reset": True}, {"CountValue": 0, "UpReached": False, "DownReached": True}),
                _step("load", {"Reset": False, "Load": True}, {"CountValue": 2, "UpReached": True, "DownReached": False}),
                _step("both-rising", {"Load": False, "Up": True, "Down": True}, {"CountValue": 2}),
                _step("rearm", {"Up": False, "Down": False}, {"CountValue": 2}),
                _step("down", {"Down": True}, {"CountValue": 1, "UpReached": False, "DownReached": False}),
                _step("held-down", expected={"CountValue": 1}, calls=4),
                _step("up", {"Up": True, "Down": False}, {"CountValue": 2, "UpReached": True}),
                _step("reset-over-load", {"Reset": True, "Load": True}, {"CountValue": 0, "DownReached": True})],
                {"dut.Preset": 2})]
        }
        for instruction, cases in counter_cases.items():
            if instruction == "CTD":
                cases.append(_case("signed-storage-minimum", [
                    _step("load-minimum", {"Load": True, "Down": False}, {"CountValue": -32768, "DownReached": True}),
                    _step("minimum-saturates", {"Load": False, "Down": True}, {"CountValue": -32768, "DownReached": True}),
                    _step("reload-zero", {"Load": True, "Preset": 0}, {"CountValue": 0, "DownReached": True})],
                    {"dut.Preset": -32768}))
            elif instruction == "CTUD":
                cases.append(_case("signed-storage-both-limits", [
                    _step("load-maximum", {"Load": True, "Up": False, "Down": False}, {"CountValue": 32767}),
                    _step("maximum-saturates", {"Load": False, "Up": True}, {"CountValue": 32767}),
                    _step("load-minimum", {"Load": True, "Preset": -32768, "Up": False}, {"CountValue": -32768}),
                    _step("minimum-saturates", {"Load": False, "Down": True}, {"CountValue": -32768}),
                    _step("reset-limit", {"Reset": True}, {"CountValue": 0})], {"dut.Preset": 32767}))
            add(language, "counter-" + instruction.lower(), instruction, "FB", count_inputs, count_outputs, cases,
                instruction=instruction, nativeStorageType=instruction + "_INT",
                limitCoverage="CTU INT saturation needs 32768 rising edges, exceeding the existing executor's 50000-call suite bound; not asserted here."
                    if instruction == "CTU" else "Signed storage saturation is explicitly asserted through legal PV/LD inputs.")
    return suites
