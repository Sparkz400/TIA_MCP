"""Keep the counts in docs/STATUS.md and docs/TODO.md in step with the catalog and the release matrix.

The counts are copies: the feature catalog and docs/plans/release-matrix.v1.json are the source. `--check` (the
default) compares every copied number and exits 1 on a difference; `--write` replaces the numbers in place and leaves
the prose around them alone. Dates and the prose of the sentences stay hand-written. scripts/tests/test_status_counts.py
runs the check in the offline suite, so a catalog or matrix change that forgets the docs fails there.

Usage: python -B scripts/status_counts.py [--check | --write]
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

from release_matrix import NOT_OPEN

ROOT = Path(__file__).resolve().parent.parent
CATALOG = ROOT / "contracts" / "feature-catalog.v1.json"
MATRIX = ROOT / "docs" / "plans" / "release-matrix.v1.json"
STATUS = ROOT / "docs" / "STATUS.md"
TODO = ROOT / "docs" / "TODO.md"


def number(value: int) -> str:
    return f"{value:,}"


def counts(catalog: dict, matrix: dict) -> dict:
    features = catalog["features"]
    qualification = Counter(f["qualification"]["status"] for f in features)
    support = matrix["summary"]["support"]
    cells = matrix["summary"]["cells"]
    open_by_first_owner: Counter = Counter()
    open_rows = 0
    for row in matrix["rows"]:
        # Classified as release_matrix.py does: blocked and descoped rows have no open cell.
        opened = sum(1 for cell in row["cells"] if NOT_OPEN.get(row["support"], cell["status"]) == "open")
        if opened:
            open_rows += 1
            open_by_first_owner[row["owners"][0]] += opened
    return {
        "catalogVersion": catalog["catalogVersion"],
        "matrixCatalogVersion": matrix["frozenFrom"]["catalogVersion"],
        "features": len(features),
        "verified": qualification["verified"],
        "unverified": len(features) - qualification["verified"],
        "tools": len({tool for f in features for tool in f.get("mcpTools") or []}),
        "shipped": support.get("shipped", 0),
        "limited": support.get("limited", 0),
        "unqualified": support.get("unqualified", 0),
        "unavailable": support.get("unavailable_target", 0),
        "descopedRows": support.get("descoped", 0),
        "bound": cells.get("bound", 0),
        "open": cells.get("open", 0),
        "blocked": cells.get("blocked", 0),
        "descopedCells": cells.get("descoped", 0),
        "openRows": open_rows,
        "openByFirstOwner": dict(open_by_first_owner),
    }


# (file, pattern, {group: count key}). Each pattern must match exactly once; its named groups hold the copied values.
RULES = [
    (STATUS, r"\| Feature catalog \| `(?P<catalogVersion>[\d.]+)` \([^)]*\): (?P<features>[\d,]+) features, "
             r"(?P<verified>[\d,]+) verified, (?P<unverified>[\d,]+) unverified; (?P<tools>[\d,]+) MCP tools"),
    (STATUS, r"\| Release matrix rows \| (?P<shipped>[\d,]+) shipped, (?P<limited>[\d,]+) limited, "
             r"(?P<unqualified>[\d,]+) unqualified, (?P<unavailable>[\d,]+) unavailable on V21, "
             r"(?P<descopedRows>[\d,]+) descoped"),
    (STATUS, r"\| Matrix cells \| (?P<bound>[\d,]+) bound, \*\*(?P<open>[\d,]+) open\*\*, (?P<blocked>[\d,]+) blocked, "
             r"(?P<descopedCells>[\d,]+) descoped"),
    (STATUS, r"Counts from catalog `(?P<catalogVersion>[\d.]+)`"),
    (TODO, r"Catalog `(?P<matrixCatalogVersion>[\d.]+)` \(the matrix's `frozenFrom\.catalogVersion`[^)]*\): "
           r"(?P<open>[\d,]+) open cells in (?P<openRows>[\d,]+) `unqualified` rows"),
    (TODO, r"the matrix has (?P<bound>[\d,]+) bound, (?P<blocked>[\d,]+) blocked and (?P<descopedCells>[\d,]+) descoped cells"),
]
OWNER_ROW = re.compile(r"^\| (?P<owner>[A-Z]\d\d) [^|]+\| (?P<open>\d+) \|", re.M)
OWNER_TABLE = re.compile(r"### Open matrix rows by owner\n(?P<body>.*?)\n### ", re.S)


def render(key: str, values: dict) -> str:
    value = values[key]
    return number(value) if isinstance(value, int) else str(value)


def apply(text: str, path: Path, values: dict, problems: list[str]) -> str:
    for rule_path, pattern in RULES:
        if rule_path != path:
            continue
        matches = list(re.finditer(pattern, text))
        if len(matches) != 1:
            problems.append(f"{path.name}: expected one match of {pattern!r}, found {len(matches)}")
            continue
        match = matches[0]
        pieces, last = [], match.start()
        for key in match.re.groupindex:
            expected = render(key, values)
            if match.group(key) != expected:
                problems.append(f"{path.name}: {key} is {match.group(key)}, the source says {expected}")
            pieces += [text[last:match.start(key)], expected]
            last = match.end(key)
        text = text[:match.start()] + "".join(pieces) + text[last:]
    if path == TODO:
        table = OWNER_TABLE.search(text)
        if not table:
            problems.append("TODO.md: the open matrix rows by owner section is missing")
            return text
        listed = {m.group("owner"): int(m.group("open")) for m in OWNER_ROW.finditer(table.group("body"))}
        expected = values["openByFirstOwner"]
        for owner in sorted(set(listed) | set(expected)):
            if listed.get(owner) != expected.get(owner):
                problems.append(f"TODO.md: owner {owner} lists {listed.get(owner)} open cells, the matrix has {expected.get(owner)}")

        def fix(m: re.Match) -> str:
            owner = m.group("owner")
            if owner not in expected:
                return m.group(0)
            return m.group(0)[:m.start("open") - m.start()] + str(expected[owner]) + m.group(0)[m.end("open") - m.start():]

        body = OWNER_ROW.sub(fix, table.group("body"))
        text = text[:table.start("body")] + body + text[table.end("body"):]
    return text


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="report differences and exit 1 (default)")
    mode.add_argument("--write", action="store_true", help="replace the copied numbers in place")
    args = parser.parse_args(argv)
    values = counts(json.loads(CATALOG.read_text(encoding="utf-8")), json.loads(MATRIX.read_text(encoding="utf-8")))
    problems: list[str] = []
    for path in (STATUS, TODO):
        original = path.read_text(encoding="utf-8")
        updated = apply(original, path, values, problems)
        if args.write and updated != original:
            path.write_bytes(updated.encode("utf-8"))
    structural = [p for p in problems if "expected one match" in p or "is missing" in p or "lists None" in p]
    if args.write:
        for problem in structural:
            print(problem, file=sys.stderr)
        return 1 if structural else 0
    for problem in problems:
        print(problem, file=sys.stderr)
    if problems:
        print("Run python -B scripts/status_counts.py --write, then review the dates and prose around the numbers.", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
