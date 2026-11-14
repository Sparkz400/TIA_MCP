"""Generate or check contracts/change-kinds.v1.json: every change_prepare kind with its documented payload.

The index is derived, never hand-written: the kinds come from the feature catalog (changeKind), the payload text and
description from the first documentation table row naming the kind (`| `kind` | payload | effect |`), and the payload
fields are the identifiers written in backticks in that payload column. A kind without such a row is listed with
documented=false and its catalog title. Field lists say which names the docs use; they do not say which are required
(the worker checks that and refuses with InvalidRequest). Kinds that also have a JSON Schema name it in schema.

python -B scripts/change_kinds.py           writes the file
python -B scripts/change_kinds.py --check   fails when the committed file is stale
"""
import argparse
import glob
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = "contracts/change-kinds.v1.json"
DOCS = ["docs/guides/engineering-workflows.md", "docs/guides/*.md", "docs/reference/*.md"]
ROW = re.compile(r"^\|\s*`([a-z0-9_]+)`\s*\|(.*)\|(.*)\|\s*$")
FIELD = re.compile(r"`([A-Za-z][A-Za-z0-9]*)(?=[`:\s\[=])")
# Kinds whose payload has its own JSON Schema in contracts/ (the HMI engineering payload).
SCHEMAS = {"hmi_": "hmi-engineering.v1.schema.json"}


def anchor(heading):
    text = heading.strip().lstrip("#").strip().lower()
    return re.sub(r"[^a-z0-9 _-]", "", text).replace(" ", "-")


def build():
    catalog = json.loads((ROOT / "contracts/feature-catalog.v1.json").read_text(encoding="utf-8"))
    features = {f["changeKind"]: f for f in catalog["features"] if f.get("changeKind")}
    rows = {}
    seen = set()
    for pattern in DOCS:
        for path in sorted(glob.glob(str(ROOT / pattern))):
            relative = Path(path).relative_to(ROOT).as_posix()
            if relative in seen:
                continue
            seen.add(relative)
            heading = ""
            for line in Path(path).read_text(encoding="utf-8").splitlines():
                if line.startswith("#"):
                    heading = line
                match = ROW.match(line)
                if match and match.group(1) in features and match.group(1) not in rows:
                    payload, effect = match.group(2).strip(), match.group(3).strip()
                    fields = []
                    for name in FIELD.findall(payload):
                        if name not in fields and name != match.group(1):
                            fields.append(name)
                    rows[match.group(1)] = {"payload": payload, "fields": fields, "description": effect,
                                            "source": relative + ("#" + anchor(heading) if heading else "")}
    kinds = {}
    for kind in sorted(features):
        feature = features[kind]
        entry = {"featureId": feature["featureId"], "title": feature["title"],
                 "qualification": feature["qualification"]["status"], "documented": kind in rows}
        if kind in rows:
            entry.update(rows[kind])
        schema = next((name for prefix, name in SCHEMAS.items() if kind.startswith(prefix)), None)
        if schema and (ROOT / "contracts" / schema).is_file():
            entry["schema"] = schema
        kinds[kind] = entry
    return {"schemaVersion": 1, "catalogVersion": catalog["catalogVersion"],
            "generatedBy": "scripts/change_kinds.py",
            "note": "fields are the payload names the documentation uses for the kind; requiredness is checked by the worker at change_prepare. "
                    "Batchable kinds also accept {\"items\": [payload, ...]} (docs/guides/engineering-workflows.md#items-on-other-kinds).",
            "kinds": kinds}


def render(document):
    return json.dumps(document, indent=2, ensure_ascii=True) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    text = render(build())
    target = ROOT / OUTPUT
    if args.check:
        if not target.is_file() or target.read_text(encoding="utf-8") != text:
            print(OUTPUT + " is stale; run python -B scripts/change_kinds.py", file=sys.stderr)
            return 1
        print("PASS " + OUTPUT + " matches the catalog and the docs.")
        return 0
    target.write_text(text, encoding="utf-8", newline="\n")
    document = json.loads(text)
    print("Wrote %s: %d kinds, %d documented." % (OUTPUT, len(document["kinds"]), sum(1 for k in document["kinds"].values() if k["documented"])))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
