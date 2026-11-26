"""TIA-generated hardware item paths resolved by structure, not by name, so native runners work in any TIA UI language.

TIA names the items it generates in the user interface language: an English installation has "PROFINET interface_1",
"Port_1" and "Rail_0", a German one "PROFINET-Schnittstelle_1" and "Profilschiene_0". Runners name only the items they create
themselves (devices, CPUs, heads, modules); everything below is found from hardware_inspect rows: the interface is the one direct
child carrying network nodes, its ports come from the interface's own port list, a rack is the root item at position 0, and a
built-in item is told apart by a native attribute it alone exposes. Each lookup fails when the structure is ambiguous.

The functions take rows a runner already holds, so repeated lookups cost no tool calls: a hardware_inspect response, a list of its
items, a dict of items keyed by exact path, or a compared state whose hardware keys carry the "hw:" prefix.
"""
from __future__ import annotations

NOTE = "TIA item names follow the UI language, so it is resolved by structure"


def _require(condition, message):
    if not condition:
        raise AssertionError(message)


def rows(source):
    """Hardware rows keyed by exact item path. A hardware_inspect response must not be truncated; non-hardware state keys are left out."""
    if isinstance(source, dict) and isinstance(source.get("items"), list):
        completeness = source.get("completeness") or {}
        _require(completeness.get("truncated") is not True, "hardware_inspect truncated its scope: %s" % completeness)
        source = source["items"]
    if isinstance(source, list):
        return {row["path"]: row for row in source}
    return {key[3:] if key.startswith("hw:") else key: row for key, row in source.items() if key.startswith(("hw:", "/"))}


def children(source, parent):
    """The direct child items of parent as (path, row), in the order the rows list them."""
    prefix = parent + "/items/"
    return [(path, row) for path, row in rows(source).items() if path.startswith(prefix) and "/" not in path[len(prefix):]]


def _network(row):
    return (row or {}).get("network") or {}


def network_interfaces(source, parent):
    """The direct children of parent that carry network nodes (a CPU's or an IO device head's PROFINET interfaces), in listed order."""
    return [path for path, row in children(source, parent) if _network(row).get("nodes")]


def network_interface(source, parent, index=None):
    """The network interface below parent: the one such child, or with index the index-th of several."""
    found = network_interfaces(source, parent)
    if index is None:
        _require(len(found) == 1, "Expected one network interface below %s (%s): %s" % (parent, NOTE, found))
        return found[0]
    _require(0 <= index < len(found), "Expected a network interface %d below %s (%s): %s" % (index, parent, NOTE, found))
    return found[index]


def node_name(source, interface):
    """The name of the interface's one network node ("X1" on an S7-1500 CPU, "IE1" on an IM 155-5)."""
    nodes = _network(rows(source).get(interface)).get("nodes") or []
    _require(len(nodes) == 1, "Expected one network node on %s: %s" % (interface, nodes))
    return nodes[0]["name"]


def ports(source, interface):
    """The interface's port items in TIA's port order (the first is Port_1), from its own port list: an ET 200SP head's ports sit
    on the BusAdapter beside the interface, not below it. An interface without ports has none; an unresolved port fails."""
    listed = sorted(_network(rows(source).get(interface)).get("ports") or [], key=lambda row: row["portIndex"])
    paths = [((row.get("port") or {}).get("ownerHardwarePath")) for row in listed]
    _require(all(paths), "Expected resolved ports on %s: %s" % (interface, listed))
    return paths


def port(source, interface, number):
    """Port number (counted from 1, as TIA numbers Port_1, Port_2) of the interface."""
    found = ports(source, interface)
    _require(1 <= number <= len(found), "%s has no port %d: %s" % (interface, number, found))
    return found[number - 1]


def root_rack(source, device, exclude=()):
    """A station's one root rack: the direct child of the device at position 0 classified None, other than the excluded paths
    (an IO device's head)."""
    found = [path for path, row in children(source, device)
             if path not in exclude and row.get("positionNumber") == 0 and row.get("classification") == "None"]
    _require(len(found) == 1, "Expected one root rack of %s (%s): %s" % (device, NOTE, found))
    return found[0]


def child_with_attribute(source, parent, name):
    """The one direct child of parent that exposes the native attribute name (attribute names do not follow the UI language)."""
    def names(row):
        attributes = (row or {}).get("attributes") or {}
        return set(attributes) if isinstance(attributes, dict) else {a["name"] for a in attributes}
    found = [path for path, row in children(source, parent) if name in names(row)]
    _require(len(found) == 1, "Expected one item below %s exposing %s (%s): %s" % (parent, name, NOTE, found))
    return found[0]
