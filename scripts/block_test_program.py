"""Inspect native FC/FB declarations and generate an offline block-test harness.

The generated Main OB gates *DUT calls*, not PLC scans. Compilation, symbol
access, native type behavior and timer accuracy still require TIA/PLCSIM
qualification. This module neither starts nor modifies those applications.
"""
from __future__ import annotations

import copy
import hashlib
import itertools
import json
import re


class InterfaceError(ValueError):
    """The declaration cannot be represented without guessing its interface."""


SCALARS = {
    "bool": ("Bool", "bool"), "sint": ("SInt", "int8"),
    "usint": ("USInt", "uint8"), "int": ("Int", "int16"),
    "uint": ("UInt", "uint16"), "dint": ("DInt", "int32"),
    "udint": ("UDInt", "uint32"), "lint": ("LInt", "int64"),
    "ulint": ("ULInt", "uint64"), "byte": ("Byte", "uint8"),
    "word": ("Word", "uint16"), "dword": ("DWord", "uint32"),
    "lword": ("LWord", "uint64"), "real": ("Real", "float"),
    "lreal": ("LReal", "double"), "time": ("Time", "int32"),
    "ltime": ("LTime", "int64"),
}
SYSTEM_INSTANCES = {
    *[f"{timer}_{clock}" for timer in ("TON", "TOF", "TP", "TONR")
      for clock in ("TIME", "LTIME")],
    *[f"{counter}_{integer}" for counter in ("CTU", "CTD", "CTUD")
      for integer in ("SINT", "USINT", "INT", "UINT", "DINT", "UDINT", "LINT", "ULINT")],
}
MAX_LEAVES = 4096
MAX_DEPTH = 16
RESERVED_MEMBERS = set("IF THEN ELSE ELSIF END_IF CASE OF END_CASE FOR TO BY DO END_FOR WHILE END_WHILE "
                       "REPEAT UNTIL END_REPEAT BEGIN END_VAR VAR VAR_INPUT VAR_OUTPUT VAR_IN_OUT VAR_TEMP "
                       "STRUCT END_STRUCT FUNCTION FUNCTION_BLOCK END_FUNCTION END_FUNCTION_BLOCK DATA_BLOCK "
                       "END_DATA_BLOCK ORGANIZATION_BLOCK END_ORGANIZATION_BLOCK RETURN EXIT CONTINUE TRUE FALSE "
                       "NOT AND OR XOR MOD".split())


def _hash(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"),
                                     ensure_ascii=False).encode("utf-8")).hexdigest()


def _name(value, context="identifier", maximum=64):
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", value) or len(value) > maximum:
        raise InterfaceError(f"{context} must be a safe ASCII identifier of at most {maximum} characters: {value!r}")
    if context in ("member name", "instance name", "FC return name") and value.upper() in RESERVED_MEMBERS:
        raise InterfaceError(f"Reserved SCL token is unsupported as {context}: {value}")
    return value


def _unique(names, context):
    folded = [name.casefold() for name in names]
    if len(set(folded)) != len(folded):
        raise InterfaceError(f"Ambiguous case-insensitive names in {context}")


def _check_coverage(native, context):
    if not isinstance(native, dict):
        raise InterfaceError(f"{context} must be a native inspection object")
    if native.get("warnings") or native.get("error"):
        raise InterfaceError(f"{context} contains inspection warnings/errors")
    coverage = native.get("completeness", {})
    if not isinstance(coverage, dict) or coverage.get("truncated") or coverage.get("scopeComplete") is False:
        raise InterfaceError(f"{context} is incomplete or truncated")


def _sections(native, context):
    _check_coverage(native, context)
    sections = native.get("sections")
    if not isinstance(sections, list) or not sections:
        raise InterfaceError(f"{context} has no native sections")
    result = {}
    for section in sections:
        if not isinstance(section, dict) or not isinstance(section.get("name"), str) or not isinstance(section.get("members"), list):
            raise InterfaceError(f"Malformed section in {context}")
        name = section["name"]
        if name in result:
            raise InterfaceError(f"Duplicate section {name} in {context}")
        result[name] = section["members"]
    return result


def _native_hash(native):
    digest = native.get("artifact", {}).get("sha256")
    if digest is not None and (not isinstance(digest, str) or not re.fullmatch(r"[a-fA-F0-9]{64}", digest)):
        raise InterfaceError("Native artifact SHA-256 is invalid")
    return digest.lower() if digest else None


class _Types:
    def __init__(self, definitions):
        if not isinstance(definitions, dict):
            raise InterfaceError("type_definitions must map exact type names to native declarations")
        _unique(list(definitions), "type definitions")
        self.definitions = definitions
        self.required = set()
        self.revisions = {}
        self.used = {}
        self.limits = []

    def members(self, rows, direction, trail=(), depth=0):
        if not isinstance(rows, list) or len(rows) > MAX_LEAVES or depth > MAX_DEPTH:
            raise InterfaceError("Malformed or excessive member nesting")
        result = []
        for row in rows:
            if not isinstance(row, dict):
                raise InterfaceError("Each native member must be an object")
            name = _name(row.get("name"), "member name")
            attributes = row.get("xmlAttributes", {})
            if not isinstance(attributes, dict) or attributes.get("Accessibility") not in (None, "Public"):
                raise InterfaceError(f"Member {name} is not publicly accessible for call wiring/state capture")
            descriptor = self.parse(row.get("dataType"), row.get("members", []), direction, trail, depth)
            item = {"name": name, "direction": direction, "dataType": descriptor["dataType"], "type": descriptor}
            for metadata in ("startValueXml", "defaultValueXml", "xmlAttributes", "attributeListXml"):
                if metadata in row:
                    item[metadata] = copy.deepcopy(row[metadata])
            result.append(item)
        _unique([item["name"] for item in result], "member collection")
        return result

    def parse(self, spelling, children, direction, trail=(), depth=0):
        if not isinstance(spelling, str) or not spelling or len(spelling) > 512 or depth > MAX_DEPTH:
            raise InterfaceError(f"Invalid or overly nested data type: {spelling!r}")
        if not isinstance(children, list):
            raise InterfaceError("Native members must be a list")
        spelling = spelling.strip()
        key = spelling.lower()
        if key in SCALARS:
            if children:
                raise InterfaceError(f"Scalar {spelling} unexpectedly contains members")
            data_type, signal_type = SCALARS[key]
            result = {"kind": "scalar", "dataType": data_type, "signalType": signal_type}
            if key in ("time", "ltime"):
                result["unit"] = "ms" if key == "time" else "ns"
            return result
        string = re.fullmatch(r"(W?String)(?:\[([0-9]+)\])?", spelling, re.I)
        if string:
            wide = string[1].lower() == "wstring"
            length = int(string[2] or 254)
            if length < 1 or length > (16382 if wide else 254) or children:
                raise InterfaceError("Invalid String/WString capacity or child members")
            canonical = "WString" if wide else "String"
            return {"kind": "scalar", "dataType": f"{canonical}[{length}]", "signalType": canonical.lower(), "capacity": length}
        array = re.fullmatch(r"Array\s*\[([^\]]+)\]\s+of\s+(.+)", spelling, re.I)
        if array:
            dimensions = []
            count = 1
            for part in array[1].split(","):
                bound = re.fullmatch(r"\s*(-?[0-9]+)\s*\.\.\s*(-?[0-9]+)\s*", part)
                if not bound:
                    raise InterfaceError("Only fixed integer array bounds are supported")
                lower, upper = int(bound[1]), int(bound[2])
                if lower < -(2 ** 31) or upper > 2 ** 31 - 1 or lower > upper:
                    raise InterfaceError("Invalid array bounds")
                count *= upper - lower + 1
                if count > MAX_LEAVES or len(dimensions) >= 6:
                    raise InterfaceError("Array expansion exceeds 4096 elements or six dimensions")
                dimensions.append([lower, upper])
            element = self.parse(array[2], children, direction, trail, depth + 1)
            if element["kind"] in ("opaque", "instance"):
                raise InterfaceError("Arrays of FB/system instances require explicit native support")
            if element["kind"] == "array":
                raise InterfaceError("Nested Array-of-Array declarations are unsupported; use native multidimensional bounds")
            bounds = ", ".join(f"{lo}..{hi}" for lo, hi in dimensions)
            return {"kind": "array", "dataType": f"Array[{bounds}] of {element['dataType']}",
                    "dimensions": dimensions, "element": element}
        if key == "struct":
            if not children:
                raise InterfaceError("Struct declaration is missing its member interface")
            return {"kind": "struct", "dataType": "Struct", "members": self.members(children, direction, trail, depth + 1)}
        if spelling.upper() in SYSTEM_INSTANCES:
            if direction != "Static":
                raise InterfaceError("Native IEC instances are supported only in FB static data")
            name = spelling.upper()
            self.required.add(name)
            self.limits.append(f"{name}: native instance internals and timer clock are not reset or reconstructed by the harness.")
            return {"kind": "instance" if children else "opaque", "dataType": name,
                    "members": self.members(children, direction, trail, depth + 1)}
        quoted = re.fullmatch(r'"([A-Za-z_][A-Za-z0-9_]*)"', spelling)
        if quoted:
            type_name = _name(quoted[1], "type name")
        elif re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", spelling):
            type_name = _name(spelling, "type name")
        else:
            raise InterfaceError(f"Unsupported data type: {spelling}")
        if type_name not in self.definitions:
            raise InterfaceError(f"Unsupported type or missing native dependency interface: {type_name}")
        if type_name.casefold() in [name.casefold() for name in trail]:
            raise InterfaceError(f"Recursive dependency type: {type_name}")
        self.required.add(type_name)
        definition = self.definitions[type_name]
        self.used[type_name] = copy.deepcopy(definition)
        kind = "udt"
        if isinstance(definition, list):
            rows = definition
        else:
            sections = _sections(definition, f"type {type_name}")
            kinds = definition.get("objectKinds", [])
            observed_name = definition.get("type", definition.get("block", {})).get("name")
            if observed_name is not None and observed_name != type_name:
                raise InterfaceError(f"Dependency identity differs from requested type: {type_name}")
            if kinds == ["SW.Blocks.FB"]:
                if direction != "Static":
                    raise InterfaceError("FB dependency is only valid as instance state")
                if not {"Input", "Output", "InOut", "Static"}.issubset(sections):
                    raise InterfaceError(f"Incomplete FB dependency interface: {type_name}")
                # Optimized S7-1500 FBs store elementary IN_OUT by value in the instance, between
                # Output and Static; structured IN_OUT is a reference and TEMP is not instance storage.
                by_value = [row for row in sections["InOut"] if str(row.get("dataType", "")).strip().lower() in SCALARS]
                rows = [*sections["Input"], *sections["Output"], *by_value, *sections["Static"]]
                kind = "instance"
                self.limits.append(f"{type_name}: dependency instance captures omit structured (by-reference) IN_OUT and local TEMP storage.")
            elif set(sections) == {"None"} and (not kinds or kinds == ["SW.Types.PlcStruct"]):
                rows = sections["None"]
            else:
                raise InterfaceError(f"Expected native UDT or FB definition: {type_name}")
            self.revisions[type_name] = _native_hash(definition)
        members = self.members(rows, direction, (*trail, type_name), depth + 1)
        if not members and kind == "udt":
            raise InterfaceError(f"Empty UDT definition: {type_name}")
        if children:
            expanded = self.members(children, direction, (*trail, type_name), depth + 1)
            def descriptor_shape(descriptor):
                return {key: (shape(value) if key == "members" else
                              descriptor_shape(value) if key == "element" else value)
                        for key, value in descriptor.items()}
            def shape(items):
                # Instance-specific starts/access metadata may legitimately differ
                # from the dependency type's defaults without changing its shape.
                return [(item["name"], descriptor_shape(item["type"])) for item in items]
            if shape(expanded) != shape(members):
                raise InterfaceError(f"Inline members disagree with native dependency: {type_name}")
        return {"kind": kind, "dataType": f'"{type_name}"', "name": type_name, "members": members}


def inspect_interface(native_result, type_definitions=None):
    """Normalize the bridge's block_interface result; fail closed on ambiguity.

    type_definitions maps exact UDT/FB names to type_read/block_interface results.
    Member lists are also accepted for authored offline fixture declarations.
    Authored declarations without native artifact hashes never claim native proof.
    """
    sections = _sections(native_result, "block interface")
    block = native_result.get("block", {})
    name = _name(block.get("name"), "block name")
    kinds = native_result.get("objectKinds")
    kind = block.get("kind")
    if kinds is not None:
        if kinds not in (["SW.Blocks.FC"], ["SW.Blocks.FB"]):
            raise InterfaceError("Exactly one ordinary FC or FB native object is required")
        native_kind = kinds[0].rsplit(".", 1)[-1]
        if kind is not None and kind != native_kind:
            raise InterfaceError("Block kind conflicts with native object kind")
        kind = native_kind
    if kind not in ("FC", "FB") or block.get("isKnowHowProtected") or block.get("isFailsafe"):
        raise InterfaceError("Only inspectable ordinary FC/FB interfaces are supported")
    allowed = {"Input", "Output", "InOut", "Static", "Temp", "Constant", "Return"}
    required = {"Input", "Output", "InOut", "Return" if kind == "FC" else "Static"}
    if not required.issubset(sections) or set(sections) - allowed:
        raise InterfaceError("Missing required or unrecognized native interface sections")
    if kind == "FC" and sections.get("Static") or kind == "FB" and sections.get("Return"):
        raise InterfaceError("Native section does not match the block kind")
    types = _Types(type_definitions or {})
    parameters = []
    for direction in ("Input", "Output", "InOut"):
        parameters.extend(types.members(sections[direction], direction))
    if kind == "FC":
        returns = sections["Return"]
        if len(returns) != 1 or not isinstance(returns[0], dict):
            raise InterfaceError("FC must expose exactly one native Return declaration, including Void")
        _name(returns[0].get("name"), "FC return name")
        if str(returns[0].get("dataType", "")).lower() == "void" and returns[0].get("members"):
            raise InterfaceError("Void return cannot contain members")
        if str(returns[0].get("dataType", "")).lower() != "void":
            parameters.extend(types.members(returns, "Return"))
    state = types.members(sections.get("Static", []), "Static")
    _unique([item["name"] for item in parameters + state], "block interface")
    if any(item["name"].casefold() == "state" for item in parameters):
        raise InterfaceError("Parameter State conflicts with the generated state capture namespace")
    total = 0
    for item in parameters + state:
        for _ in _leaves(item["type"], item["name"]):
            total += 1
            if total > MAX_LEAVES:
                raise InterfaceError("Expanded interface exceeds 4096 scalar leaves")
    return {
        "schemaVersion": 1, "name": name, "kind": kind, "parameters": parameters,
        "state": state, "requiredTypes": sorted(types.required),
        "typeRevisions": types.revisions, "nativeSha256": _native_hash(native_result),
        "declarationRevision": _hash({"block": {"name": name, "kind": kind}, "sections": sections,
                                       "typeDefinitions": types.used}),
        "stateCaptureLimitations": sorted(set(types.limits)),
        "initialization": "FB instance start values are supplied by native instance creation; test-data inputs require explicit case initialization.",
        # Reinspection prevents a caller from injecting unvalidated SCL descriptors.
        "nativeInterface": copy.deepcopy(native_result), "typeDefinitions": copy.deepcopy(types.used),
    }


def _leaves(descriptor, path):
    kind = descriptor["kind"]
    if kind == "scalar":
        yield path, descriptor
    elif kind == "array":
        dimensions = [range(lo, hi + 1) for lo, hi in descriptor["dimensions"]]
        for indices in itertools.product(*dimensions):
            yield from _leaves(descriptor["element"], path + "[" + ",".join(map(str, indices)) + "]")
    elif kind in ("struct", "udt", "instance"):
        for member in descriptor["members"]:
            yield from _leaves(member["type"], path + "." + member["name"])
    elif kind != "opaque":
        raise InterfaceError(f"Unsupported descriptor kind: {kind}")


def _declaration(name, descriptor, indent):
    """Emit only validated descriptor tokens; never source from exported XML."""
    if descriptor["kind"] == "struct":
        lines = [f"{indent}{name} : Struct"]
        for child in descriptor["members"]:
            lines.extend(_declaration(child["name"], child["type"], indent + "  "))
        return lines + [f"{indent}END_STRUCT;"]
    if descriptor["kind"] == "array" and descriptor["element"]["kind"] == "struct":
        bounds = ", ".join(f"{lo}..{hi}" for lo, hi in descriptor["dimensions"])
        lines = [f"{indent}{name} : Array[{bounds}] of Struct"]
        for child in descriptor["element"]["members"]:
            lines.extend(_declaration(child["name"], child["type"], indent + "  "))
        return lines + [f"{indent}END_STRUCT;"]
    return [f"{indent}{name} : {descriptor['dataType']};"]


def _capture_type(descriptor):
    """Capture values in plain structs, never clone live IEC/FB instances."""
    if descriptor["kind"] == "opaque":
        return None
    if descriptor["kind"] in ("instance", "struct", "udt"):
        members = []
        for item in descriptor["members"]:
            child = _capture_type(item["type"])
            if child is not None:
                members.append({"name": item["name"], "type": child})
        return {"kind": "struct", "dataType": "Struct", "members": members} if members else None
    if descriptor["kind"] == "array":
        element = _capture_type(descriptor["element"])
        if element is None:
            return None
        return {**descriptor, "element": element}
    return descriptor


def generate_program(interface, instances=("dut",), prefix="BlockTest"):
    """Generate a Main OB, test-data DB and separate native FB instance DBs.

    Write inputs and InstanceIndex while RequestId == AckId, then increment
    RequestId once. Await the matching AckId before reading outputs or modifying
    any request data. ErrorCode 1 means invalid instance; 2 means counter exhausted.
    The client must serialize requests; wraparound/reused outstanding IDs are not
    supported. A fresh simulator deployment is the reset boundary between cases.
    """
    if not isinstance(interface, dict) or "nativeInterface" not in interface:
        raise InterfaceError("Use inspect_interface before generate_program")
    verified = inspect_interface(interface["nativeInterface"], interface.get("typeDefinitions"))
    if interface != verified:
        raise InterfaceError("Normalized interface changed after native inspection")
    prefix = _name(prefix, "prefix", 32)
    if isinstance(instances, (str, bytes)) or not isinstance(instances, (tuple, list)) or not 1 <= len(instances) <= 64:
        raise InterfaceError("Provide 1..64 distinct instance names")
    instances = [_name(name, "instance name", 20) for name in instances]
    _unique(instances, "instances")
    leaf_count = sum(1 for item in interface["parameters"] + interface["state"]
                     for _ in _leaves(item["type"], item["name"]))
    if leaf_count * len(instances) > MAX_LEAVES:
        raise InterfaceError("All generated instances together exceed 4096 scalar bindings")
    controls = ("RequestId", "AckId", "InstanceIndex", "CallCount", "ErrorCode")
    if {name.casefold() for name in instances} & {name.casefold() for name in controls}:
        raise InterfaceError("Instance name conflicts with a handshake control")
    db_name = prefix + "_Data"
    instance_blocks = {name: prefix + "_" + name + "_Instance" for name in instances} if interface["kind"] == "FB" else {}
    block_names = [db_name, *instance_blocks.values(), "Main"]
    _unique([interface["name"], *block_names, *interface["requiredTypes"]], "generated blocks and dependencies")
    parameters = interface["parameters"]
    capture = [(item, _capture_type(item["type"])) for item in interface["state"]]
    capture = [(item, descriptor) for item, descriptor in capture if descriptor is not None]
    lines = ["// Generated offline: TIA compilation and PLCSIM runtime qualification pending.",
             "// One request executes one DUT call; this does not synchronize PLC scans.",
             f"// Interface declaration SHA-256: {interface['declarationRevision']}",
             f'DATA_BLOCK "{db_name}"', "{ S7_Optimized_Access := 'TRUE' }", "VERSION : 0.1", "NON_RETAIN", "VAR"]
    lines.extend(f"  {name} : UDInt;" for name in controls)
    bindings = {}
    for instance in instances:
        lines.append(f"  {instance} : Struct")
        bindings[instance] = {}
        for parameter in parameters:
            lines.extend(_declaration(parameter["name"], parameter["type"], "    "))
            for path, descriptor in _leaves(parameter["type"], parameter["name"]):
                binding = {"symbol": f"{db_name}.{instance}.{path}", "dataType": descriptor["dataType"],
                           "type": descriptor["signalType"], "direction": parameter["direction"],
                           "writable": parameter["direction"] in ("Input", "InOut")}
                for metadata in ("unit", "capacity"):
                    if metadata in descriptor:
                        binding[metadata] = descriptor[metadata]
                bindings[instance][path] = binding
        if capture:
            lines.append("    State : Struct")
            for item, descriptor in capture:
                lines.extend(_declaration(item["name"], descriptor, "      "))
                for path, scalar in _leaves(descriptor, item["name"]):
                    bindings[instance]["state." + path] = {
                        "symbol": f"{db_name}.{instance}.State.{path}", "dataType": scalar["dataType"],
                        "type": scalar["signalType"], "direction": "Static", "writable": False,
                        **({"unit": scalar["unit"]} if "unit" in scalar else {}),
                        **({"capacity": scalar["capacity"]} if "capacity" in scalar else {})}
            lines.append("    END_STRUCT;")
        if not parameters and not capture:
            lines.append("    Reserved : Bool;")
        lines.append("  END_STRUCT;")
    lines.extend(["END_VAR", "BEGIN", "END_DATA_BLOCK", ""])
    for instance_db in instance_blocks.values():
        lines.extend([f'DATA_BLOCK "{instance_db}"', "{ S7_Optimized_Access := 'TRUE' }", "VERSION : 0.1",
                      "NON_RETAIN", f'"{interface["name"]}"', "BEGIN", "END_DATA_BLOCK", ""])
    ref = f'"{db_name}"'
    lines.extend(['ORGANIZATION_BLOCK "Main"', "{ S7_Optimized_Access := 'TRUE' }", "VERSION : 0.1",
                  "BEGIN", f"  IF {ref}.RequestId <> {ref}.AckId THEN", f"    {ref}.ErrorCode := UDINT#0;",
                  f"    IF {ref}.CallCount = UDINT#4294967295 THEN", f"      {ref}.ErrorCode := UDINT#2;",
                  "    ELSE", f"      CASE {ref}.InstanceIndex OF"])
    for index, instance in enumerate(instances):
        lines.append(f"        {index}:")
        call = f'"{instance_blocks.get(instance, interface["name"])}"'
        arguments = []
        returns = []
        for parameter in parameters:
            target = f'{ref}.{instance}.{parameter["name"]}'
            if parameter["direction"] == "Return":
                returns.append(target)
            else:
                assignment = "=>" if parameter["direction"] == "Output" else ":="
                arguments.append(f'{parameter["name"]} {assignment} {target}')
        call_line = (returns[0] + " := " if returns else "") + call + "(" + ", ".join(arguments) + ");"
        lines.append("          " + call_line)
        for item, descriptor in capture:
            for path, _ in _leaves(descriptor, item["name"]):
                lines.append(f'          {ref}.{instance}.State.{path} := "{instance_blocks[instance]}".{path};')
        lines.append(f"          {ref}.CallCount := {ref}.CallCount + UDINT#1;")
    lines.extend(["        ELSE", f"          {ref}.ErrorCode := UDINT#1;", "      END_CASE;", "    END_IF;",
                  f"    {ref}.AckId := {ref}.RequestId;", "  END_IF;", "END_ORGANIZATION_BLOCK", ""])
    source = "\n".join(lines)
    signals = {name[0].lower() + name[1:]: {"symbol": f"{db_name}.{name}", "type": "uint32"} for name in controls}
    limitations = ["Acknowledgement gates application calls only; other PLC scans and wall-clock timers continue.",
                   "Polling/API latency bounds observations; elapsed PLC scan count and exact timer transitions are not measured.",
                   "Stable snapshots require one serialized controller and an isolated Main with no other writers.",
                   "Native compilation, symbol access and timer behavior await runtime qualification."]
    return {"schemaVersion": 1, "source": source, "revision": hashlib.sha256(source.encode("utf-8")).hexdigest(),
            "nativeSha256": interface["nativeSha256"], "interfaceRevision": interface["declarationRevision"],
            "expectedBlockNames": block_names, "requiredTypes": interface["requiredTypes"],
            "signals": signals, "bindings": bindings, "instanceIds": dict(zip(instances, range(len(instances)))),
            "instanceBlocks": instance_blocks, "typeRevisions": interface["typeRevisions"],
            "stateCaptureLimitations": interface["stateCaptureLimitations"],
            "initialization": interface["initialization"],
            "resetStrategy": "fresh_simulator_deployment_per_case",
            "timing": {"mode": "application_call_controlled", "scanSynchronized": False, "limitations": limitations},
            "qualification": "generated_not_compiled_or_runtime_qualified"}
