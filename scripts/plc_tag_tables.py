"""A PLC's default tag table resolved by structure, not by name, so native runners work in any TIA UI language.

TIA names the default tag table in the UI language ("Default tag table", German "Standard-Variablentabelle"). V21 Openness
marks it with PlcTagTable.IsDefault, which software_inspect does not report; the rule here is TIA's own placement instead: the
hardware system constants of a PLC (Local~... and one per hardware object, which TIA generates and users cannot create) live in
its default tag table only. So the default table is the one table of the PLC's own scope (scopePath equal to the PLC path)
that lists a constant of kind "system"; zero or several fail. scripts/probe_safety_logic.py and the typed-resolution runner
(qualify_typed_resolution.empty_tag_template) select it the same way.

default_table reads software_inspect rows (one call, constants only); default_table_path reads a compared state that already
holds them (qualify_plc_object_mutations.view: "tagTable:<path>" rows and "member:<path>/systemConstants/<name>" keys).
"""
from __future__ import annotations

NOTE = "TIA names it in the UI language, so it is the PLC scope's one table holding system constants"


def _require(condition, message):
    if not condition:
        raise AssertionError(message)


def default_table(source, plc):
    """The default tag table row ({"path", "name", ...}) of plc. source is a software_inspect response, its tagTables list, or a
    tool callable (name, arguments) that is asked once for the PLC's constants."""
    if callable(source):
        source = source("software_inspect", {"plcPath": plc, "include": "constants", "maxItems": 2000})
    if isinstance(source, dict):
        completeness = source.get("completeness") or {}
        _require(completeness.get("truncated") is not True, "software_inspect truncated its scope: %s" % completeness)
        source = source["tagTables"]
    tables = [row for row in source if row.get("scopePath", plc) == plc]
    _require(not any(row.get("constantsTruncated") for row in tables), "software_inspect truncated the constants of %s's tag tables" % plc)
    found = [row for row in tables if any(constant.get("kind") == "system" for constant in row.get("constants") or [])]
    _require(len(found) == 1, "Expected one default tag table of %s (%s): %s" % (plc, NOTE, [row.get("name") for row in found]))
    return found[0]


def default_table_path(state, plc):
    """The default tag table's path from a compared state: the tagTable row of plc's own scope with a system constant below it."""
    tables = {key[len("tagTable:"):] for key, row in state.items() if key.startswith("tagTable:" + plc + "/tags/") and row.get("scopePath", plc) == plc}
    holding = {key[len("member:"):].split("/systemConstants/", 1)[0] for key in state if key.startswith("member:" + plc + "/tags/") and "/systemConstants/" in key}
    found = sorted(tables & holding)
    _require(len(found) == 1, "Expected one default tag table of %s (%s): %s" % (plc, NOTE, found))
    return found[0]


def count_follows_members(before, after, observed, category, key):
    """True for a tag table row that changed by tagCount/constantCount alone while members below it were added or removed.

    Since 10 November tag table rows carry those counts, so a table gaining a member (a plugged module's or an I-device
    interface's system constant, a created tag) changes its own row too. before/after are compared views, observed their diff.
    """
    if category != "changed" or not key.startswith("tagTable:"):
        return False
    fields = {f for f in set(before[key]) | set(after[key]) if before[key].get(f) != after[key].get(f)}
    members = "member:" + key[len("tagTable:"):] + "/"
    return fields <= {"tagCount", "constantCount"} and any(k.startswith(members) for k in observed["added"] + observed["removed"])
