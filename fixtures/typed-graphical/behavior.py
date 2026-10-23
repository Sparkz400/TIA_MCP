"""Independent executable expectations for typed graphical qualification.

The truth tables and transition cases predate and never call the XML emitter.
Delay assertions use application-call control and bounded host timing; they do
not establish exact PLC scan timing.
"""
import itertools


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
    if family in ("eq", "ne", "lt", "le", "gt", "ge"):
        operators = {"eq": lambda x, y: x == y, "ne": lambda x, y: x != y,
                     "lt": lambda x, y: x < y, "le": lambda x, y: x <= y,
                     "gt": lambda x, y: x > y, "ge": lambda x, y: x >= y}
        return [{"id": "signed-int-boundaries", "initial": {}, "steps": [
            step("pair-" + str(i), {"Left": x, "Right": y}, {"Result": operators[family](x, y)})
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
    raise ValueError("No independent expected behavior for " + family)

