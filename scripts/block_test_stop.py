"""Cooperative stop requests and exact remaining selections; no native operations."""
import datetime
import pathlib


class StopRequest:
    """Presence-only sentinel, latched on observation and never removed by a run.

    The file is not opened or interpreted. Native calls are never interrupted;
    the runner polls only before a suite or before admission of a new case.
    """

    def __init__(self, path):
        self.path = pathlib.Path(path).absolute()
        self.observed_at_utc = None

    def __call__(self):
        if self.observed_at_utc is None:
            try:
                self.path.lstat()
            except FileNotFoundError:
                return False
            self.observed_at_utc = datetime.datetime.now(datetime.timezone.utc).isoformat()
        return True

    def evidence(self):
        return {"path": str(self.path), "requested": self.observed_at_utc is not None,
                "observedAtUtc": self.observed_at_utc,
                "policy": "Finish the active case and verified cleanup before stopping admission; no native cancellation or automatic replay."}


def remaining_selection(suites, entries, suite_hash, selection=None):
    """Keep failed/unattempted selected repetitions; never combine run evidence.

    Output is directly consumable by --case-selection. Its new run remains a
    separate subset qualification and cannot establish a full-corpus pass.
    """
    by_id = {entry["fixtureId"]: entry for entry in entries}
    remaining = {}
    for suite in suites:
        fixture = suite["fixtureId"]
        expected = [(case["id"], rep) for case in suite["cases"]
                    for rep in range(1, case.get("repeat", 1) + 1)]
        selected = set(expected if selection is None else selection[fixture])
        entry = by_id.get(fixture, {})
        # Missing evidence is never interpreted as a successful case.
        passed = {(row["caseId"], row["repetition"])
                  for row in entry.get("caseCoverage", {}).get("passed", [])}
        pending = [{"caseId": case, "repetition": rep} for case, rep in expected
                   if (case, rep) in selected and (case, rep) not in passed]
        if pending:
            remaining[fixture] = {"suiteSha256": suite_hash(suite), "cases": pending}
    return {"schemaVersion": 1, "suites": remaining} if remaining else None
