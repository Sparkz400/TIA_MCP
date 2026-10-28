"""Independent executable expectations for typed graphical qualification.

The truth tables and transition cases predate and never call the XML emitter.
Delay assertions use application-call control and bounded host timing; they do
not establish exact PLC scan timing.
"""
import itertools

COMPARE = {"eq": lambda x, y: x == y, "ne": lambda x, y: x != y,
           "lt": lambda x, y: x < y, "le": lambda x, y: x <= y,
           "gt": lambda x, y: x > y, "ge": lambda x, y: x >= y}
# DInt at its signed limits; Real values are exactly representable in 32 bits.
BOUNDARIES = {"dint": (-2147483648, -1, 0, 1, 2147483647), "real": (-1.5, 0.0, 0.25, 1.5, 16777216.0)}
MOVES = {"int": (-32768, 0, 32767), "dint": (-2147483648, 0, 2147483647), "real": (-1.5, 0.25, 16777216.0)}
# Hand-computed exact results inside each type's range. Overflow, division by zero and inexact
# quotients are left out: their OUT/ENO results are not documented in this repository.
ARITHMETIC = {
    "int": {"add": ((32766, 1, 32767), (-32767, -1, -32768), (-5, 3, -2)),
            "sub": ((-32767, 1, -32768), (32766, -1, 32767), (5, 8, -3)),
            "mul": ((181, 181, 32761), (-128, 256, -32768), (-7, -6, 42)),
            "div": ((-32768, 2, -16384), (32767, 1, 32767), (-84, 7, -12), (0, -5, 0))},
    "dint": {"add": ((2147483646, 1, 2147483647), (-2147483647, -1, -2147483648), (-5, 3, -2)),
             "sub": ((-2147483647, 1, -2147483648), (2147483646, -1, 2147483647), (5, 8, -3)),
             "mul": ((46340, 46340, 2147395600), (-65536, 32768, -2147483648), (-7, -6, 42)),
             "div": ((-2147483648, 2, -1073741824), (2147483647, 1, 2147483647), (-84, 7, -12), (0, -5, 0))},
    "real": {"add": ((1.5, -2.25, -0.75), (16777215.0, 1.0, 16777216.0), (0.25, 0.25, 0.5)),
             "sub": ((1.5, 2.25, -0.75), (-0.5, 0.25, -0.75), (16777216.0, 1.0, 16777215.0)),
             "mul": ((1.5, -2.25, -3.375), (0.5, 0.5, 0.25), (-4.0, -4.0, 16.0)),
             "div": ((6.0, -1.5, -4.0), (0.75, 0.5, 1.5), (-7.0, 2.0, -3.5))}}


def step(identifier, writes, expected, calls=1, wait_ms=0):
    return {"id": identifier, "target": "dut", "set": {"dut." + k: v for k, v in writes.items()},
            "calls": calls, "waitMs": wait_ms,
            "assertions": [{"kind": "exact", "path": "dut." + k, "expected": v} for k, v in expected.items()]}


def cases(family):
    if family == "ctu-upper-limit":
        # Independent arithmetic expectations, deliberately outside the XML
        # emitter. Each driver invocation pulses the real typed CTU FB at most
        # 512 times, with a falling level before each rising edge.
        steps = [step("reset", {"Edges": 0, "Reset": True}, {"Count": 0, "Done": False, "Rejected": False})]
        steps.extend(step("batch-" + str(i), {"Edges": 512, "Reset": False},
                          {"Count": 512 * i, "Done": False, "Rejected": False}) for i in range(1, 64))
        steps.extend([
            step("reach-signed-maximum", {"Edges": 511}, {"Count": 32767, "Done": True, "Rejected": False}),
            step("maximum-does-not-wrap", {"Edges": 1}, {"Count": 32767, "Done": True, "Rejected": False}),
            step("reset-after-saturation", {"Edges": 0, "Reset": True}, {"Count": 0, "Done": False, "Rejected": False})])
        return [{"id": "signed-int-upper-saturation-and-reset", "initial": {}, "maxDurationMs": 300000, "steps": steps}]
    if family == "boolean":
        # Explicit independent truth table for (A OR B) AND NOT Inhibit.
        rows = [(False, False, False, False), (False, False, True, False),
                (False, True, False, True), (False, True, True, False),
                (True, False, False, True), (True, False, True, False),
                (True, True, False, True), (True, True, True, False)]
        return [{"id": "all-eight-input-combinations", "initial": {}, "steps": [
            step("vector-" + str(i), dict(zip(("A", "B", "Inhibit"), row[:3])), {"Active": row[3]})
            for i, row in enumerate(rows)]}]
    if family in COMPARE:
        return [{"id": "signed-int-boundaries", "initial": {}, "steps": [
            step("pair-" + str(i), {"Left": x, "Right": y}, {"Result": COMPARE[family](x, y)})
            for i, (x, y) in enumerate(itertools.product((-32768, -1, 0, 1, 32767), repeat=2))]}]
    if family in ("fc", "fb-global", "fb-multi"):
        return [{"id": "parameter-flow", "initial": {}, "steps": [
            step("low", {"Enable": False}, {"Active": False}),
            step("high", {"Enable": True}, {"Active": True}),
            step("low-again", {"Enable": False}, {"Active": False})]}]
    if family == "ton":
        return [{"id": "start-cancel-restart-complete", "initial": {}, "steps": [
            step("reset", {"Enable": False, "Preset": 3000}, {"Done": False, "Elapsed": 0}),
            step("start", {"Enable": True}, {"Done": False}),
            step("cancel", {"Enable": False}, {"Done": False, "Elapsed": 0}),
            step("restart", {"Enable": True}, {"Done": False}),
            step("complete", {}, {"Done": True, "Elapsed": 3000}, wait_ms=3500),
            step("reset-after-complete", {"Enable": False}, {"Done": False, "Elapsed": 0})]}]
    if family == "tof":
        return [{"id": "off-delay-and-restart", "initial": {}, "steps": [
            step("off", {"Enable": False, "Preset": 3000}, {"Done": False}),
            step("on", {"Enable": True}, {"Done": True, "Elapsed": 0}),
            step("start-off-delay", {"Enable": False}, {"Done": True}),
            step("cancel-off-delay", {"Enable": True}, {"Done": True, "Elapsed": 0}),
            step("restart-off-delay", {"Enable": False}, {"Done": True}),
            step("complete", {}, {"Done": False, "Elapsed": 3000}, wait_ms=3500)]}]
    if family == "tp":
        return [{"id": "pulse-survives-input-cancel", "initial": {}, "steps": [
            step("off", {"Enable": False, "Preset": 3000}, {"Done": False}),
            step("rising", {"Enable": True}, {"Done": True}),
            step("input-low", {"Enable": False}, {"Done": True}),
            step("complete", {}, {"Done": False}, wait_ms=3500),
            step("restart", {"Enable": True}, {"Done": True})]}]
    if family == "ctu":
        return [{"id": "rising-edges-held-level-and-reset", "initial": {}, "steps": [
            step("reset", {"Enable": False, "Reset": True, "Preset": 2}, {"Count": 0, "Done": False}),
            step("release-reset", {"Reset": False}, {"Count": 0}),
            step("edge-one", {"Enable": True}, {"Count": 1, "Done": False}),
            step("held-high", {}, {"Count": 1}, calls=4),
            step("low", {"Enable": False}, {"Count": 1}),
            step("edge-two", {"Enable": True}, {"Count": 2, "Done": True}),
            step("reset-wins", {"Reset": True}, {"Count": 0, "Done": False})]}]
    if family == "ctd":
        return [{"id": "load-edges-and-zero", "initial": {}, "steps": [
            step("load", {"Enable": False, "Load": True, "Preset": 2}, {"Count": 2, "Done": False}),
            step("release-load", {"Load": False}, {"Count": 2}),
            step("edge-one", {"Enable": True}, {"Count": 1, "Done": False}),
            step("held-high", {}, {"Count": 1}, calls=4),
            step("low", {"Enable": False}, {"Count": 1}),
            step("edge-two", {"Enable": True}, {"Count": 0, "Done": True}),
            step("load-wins", {"Load": True}, {"Count": 2, "Done": False}),
            step("load-signed-minimum", {"Enable": False, "Preset": -32768}, {"Count": -32768, "Done": True}),
            step("release-minimum-load", {"Load": False}, {"Count": -32768}),
            step("minimum-does-not-wrap", {"Enable": True}, {"Count": -32768, "Done": True})]}]
    if family == "ctud":
        return [{"id": "up-down-simultaneous-reset-load", "initial": {}, "steps": [
            step("reset", {"Enable": False, "Down": False, "Reset": True, "Load": False, "Preset": 2},
                 {"Count": 0, "Done": False, "Lower": True}),
            step("up", {"Reset": False, "Enable": True}, {"Count": 1, "Lower": False}),
            step("held", {}, {"Count": 1}, calls=4),
            step("low", {"Enable": False}, {"Count": 1}),
            step("simultaneous-edges", {"Enable": True, "Down": True}, {"Count": 1}),
            step("both-low", {"Enable": False, "Down": False}, {"Count": 1}),
            step("down", {"Down": True}, {"Count": 0, "Lower": True}),
            step("load", {"Load": True}, {"Count": 2, "Done": True}),
            step("reset-and-load", {"Reset": True}, {"Count": 0, "Done": False, "Lower": True}),
            step("load-signed-maximum", {"Enable": False, "Down": False, "Reset": False, "Load": True, "Preset": 32767},
                 {"Count": 32767, "Done": True, "Lower": False}),
            step("release-maximum-load", {"Load": False}, {"Count": 32767}),
            step("maximum-does-not-wrap", {"Enable": True}, {"Count": 32767, "Done": True}),
            step("load-signed-minimum", {"Enable": False, "Down": False, "Load": True, "Preset": -32768},
                 {"Count": -32768, "Done": True, "Lower": True}),
            step("release-minimum-load", {"Load": False}, {"Count": -32768}),
            step("minimum-does-not-wrap", {"Down": True}, {"Count": -32768, "Done": True, "Lower": True})]}]
    operation, _, numeric = family.partition("-")
    if operation in COMPARE and numeric in BOUNDARIES:
        return [{"id": numeric + "-boundaries", "initial": {}, "steps": [
            step("pair-" + str(i), {"Left": x, "Right": y}, {"Result": COMPARE[operation](x, y)})
            for i, (x, y) in enumerate(itertools.product(BOUNDARIES[numeric], repeat=2))]}]
    if operation == "move" and numeric in MOVES:
        return [{"id": numeric + "-copies", "initial": {}, "steps": [
            step("copy-" + str(i), {"Source": value}, {"Target": value}) for i, value in enumerate(MOVES[numeric])]}]
    if numeric in ARITHMETIC and operation in ARITHMETIC[numeric]:
        return [{"id": numeric + "-exact-results", "initial": {}, "steps": [
            step("vector-" + str(i), {"Left": x, "Right": y}, {"Result": result})
            for i, (x, y, result) in enumerate(ARITHMETIC[numeric][operation])]}]
    if family == "tonr":
        # Two segments shorter than PT reach it together: paused time is not counted, Q holds until R.
        return [{"id": "accumulate-pause-resume-hold-reset", "initial": {}, "steps": [
            step("reset", {"Enable": False, "Reset": True, "Preset": 3000}, {"Done": False, "Elapsed": 0}),
            step("release-reset", {"Reset": False}, {"Done": False, "Elapsed": 0}),
            step("start", {"Enable": True}, {"Done": False}),
            step("accumulate", {}, {"Done": False}, wait_ms=2000),
            step("pause", {"Enable": False}, {"Done": False}),
            step("paused-time-not-counted", {}, {"Done": False}, wait_ms=2000),
            step("resume", {"Enable": True}, {"Done": False}),
            step("complete-from-both-segments", {}, {"Done": True, "Elapsed": 3000}, wait_ms=1500),
            step("output-held-without-input", {"Enable": False}, {"Done": True, "Elapsed": 3000}),
            step("reset-clears", {"Reset": True}, {"Done": False, "Elapsed": 0})]}]
    if family == "rtrig":
        # CLK low makes Q low whatever the initial edge memory; the pulse lasts exactly one call.
        return [{"id": "one-call-pulse-per-rising-edge", "initial": {}, "steps": [
            step("low", {"Signal": False}, {"Pulse": False}),
            step("rising", {"Signal": True}, {"Pulse": True}),
            step("held-high", {}, {"Pulse": False}),
            step("still-high", {}, {"Pulse": False}, calls=3),
            step("falling", {"Signal": False}, {"Pulse": False}),
            step("held-low", {}, {"Pulse": False}, calls=3),
            step("rising-again", {"Signal": True}, {"Pulse": True}),
            step("after-second-pulse", {}, {"Pulse": False})]}]
    if family == "ftrig":
        # CLK high makes Q low whatever the initial edge memory; the pulse lasts exactly one call.
        return [{"id": "one-call-pulse-per-falling-edge", "initial": {}, "steps": [
            step("high", {"Signal": True}, {"Pulse": False}),
            step("held-high", {}, {"Pulse": False}, calls=3),
            step("falling", {"Signal": False}, {"Pulse": True}),
            step("held-low", {}, {"Pulse": False}),
            step("still-low", {}, {"Pulse": False}, calls=3),
            step("rising", {"Signal": True}, {"Pulse": False}),
            step("falling-again", {"Signal": False}, {"Pulse": True}),
            step("after-second-pulse", {}, {"Pulse": False})]}]
    raise ValueError("No independent expected behavior for " + family)

