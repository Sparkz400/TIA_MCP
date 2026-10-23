"""Independently authored observable scalar call/state expectations."""


def step(identifier, target, writes, expected, other=()):
    return {"id": identifier, "target": target, "set": {target + "." + key: value for key, value in writes.items()},
            "calls": 1, "waitMs": 0, "assertions": [{"kind": "exact", "path": target + "." + key, "expected": value}
                                                      for key, value in expected.items()] + list(other)}


def cases(family):
    if family == "numeric-fc":
        return [{"id": "inout-output-return-flow", "initial": {"dut.Accumulator": 10}, "steps": [
            step("positive", "dut", {"Value": 7}, {"Accumulator": 17, "Result": 34, "Returned": 18}),
            step("negative", "dut", {"Value": -20}, {"Accumulator": -3, "Result": -6, "Returned": -2}),
            step("zero", "dut", {"Value": 0}, {"Accumulator": -3, "Result": -6, "Returned": -2})]}]
    if family == "numeric-fb":
        return [{"id": "separate-instance-history-and-inout", "initial": {}, "steps": [
            step("left-reset", "left", {"Enable": False, "Delta": 0, "Reset": True, "Accumulator": 10}, {"Total": 10, "Accumulator": 10}),
            step("right-reset", "right", {"Enable": False, "Delta": 0, "Reset": True, "Accumulator": 100}, {"Total": 100, "Accumulator": 100}),
            step("left-increment", "left", {"Enable": True, "Reset": False, "Delta": 3}, {"Total": 16, "Accumulator": 13}),
            step("right-decrement", "right", {"Enable": True, "Reset": False, "Delta": -2}, {"Total": 96, "Accumulator": 98}),
            step("left-again", "left", {}, {"Total": 22, "Accumulator": 16},
                 ({"kind": "exact", "path": "right.Total", "expected": 96}, {"kind": "exact", "path": "right.Accumulator", "expected": 98})),
            step("left-disabled", "left", {"Enable": False, "Delta": 50}, {"Total": 22, "Accumulator": 16}),
            step("right-reset-history", "right", {"Reset": True}, {"Total": 98, "Accumulator": 98})]}]
    raise ValueError("No independent scalar-call expectations for " + family)
