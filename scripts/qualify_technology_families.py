"""Opt-in axis/encoder fixtures with explicit CPU/type/version/dependency data.

Profile fields: family, cpuTypeIdentifier, typeIdentifier, version; optionally
technologyName (existing scratch object), parameters, dependencies and
expectedHardwareConnections. No type or version is guessed from a family name.
New objects receive parameters as part of to_create before its compile. Their
dependency assertions therefore describe the configured native object. Existing
objects validate dependencies first, then use the separate to_parameters route.
"""
import json
import pathlib
import re
import math
import copy
import struct
from urllib.parse import quote


def technology_name(profile, index=0):
    if "technologyName" not in profile:
        return "TiaMcp_" + profile["family"] + "_Fixture" + str(index)
    name = profile["technologyName"]
    if not isinstance(name, str) or not name.strip() or len(name) > 128 or any(ord(c) < 32 or ord(c) == 127 for c in name):
        raise ValueError("Existing technology name must be explicit, bounded and nonblank.")
    try:
        name.encode("utf-8", errors="strict")
    except UnicodeError as error:
        raise ValueError("Existing technology name contains invalid Unicode.") from error
    return name


def load_profiles(paths):
    profiles = []
    for path in paths:
        value = json.loads(pathlib.Path(path).read_text(encoding="utf-8-sig"))
        for profile in value if isinstance(value, list) else [value]:
            allowed = {"family", "cpuTypeIdentifier", "typeIdentifier", "version", "technologyName", "parameters",
                       "dependencies", "expectedHardwareConnections"}
            required = {"family", "cpuTypeIdentifier", "typeIdentifier", "version"}
            if not isinstance(profile, dict) or not required <= set(profile) or set(profile) - allowed:
                raise ValueError("Technology fixture requires explicit family, CPU, type and version fields.")
            if profile["family"] not in ("axis", "encoder"):
                raise ValueError("Technology fixture family must be axis or encoder.")
            if not all(isinstance(profile[key], str) and profile[key].strip() for key in required):
                raise ValueError("Technology fixture identifiers must be nonempty strings.")
            if profile["family"] not in profile["typeIdentifier"].lower():
                raise ValueError("Explicit technology type must identify the requested axis/encoder family.")
            if not re.fullmatch(r"\d+(?:\.\d+){1,3}", profile["version"]):
                raise ValueError("Technology version must be an explicit dotted numeric value.")
            technology_name(profile)
            for key in ("parameters", "dependencies"):
                if not isinstance(profile.get(key, {}), dict):
                    raise ValueError(key + " must map exact native parameter names to values.")
                if any(not isinstance(name, str) or not name or type(value) not in (str, int, float, bool)
                       for name, value in profile.get(key, {}).items()):
                    raise ValueError(key + " supports explicit scalar values only.")
                if any(isinstance(value, float) and not math.isfinite(value) for value in profile.get(key, {}).values()):
                    raise ValueError(key + " requires finite numeric values.")
            if "expectedHardwareConnections" in profile and not isinstance(profile["expectedHardwareConnections"], list):
                raise ValueError("expectedHardwareConnections must be a native connection list.")
            profiles.append(profile)
    return profiles


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def same(first, second):
    # Python equates True with 1; native evidence must preserve the JSON value kind.
    try:
        return json.dumps(first, sort_keys=True, allow_nan=False) == json.dumps(second, sort_keys=True, allow_nan=False)
    except (TypeError, ValueError) as error:
        raise AssertionError("Native evidence must contain finite JSON values") from error


def parameter_rows(rows):
    require(isinstance(rows, list), "Native parameter collection is missing")
    result = {}
    for item in rows:
        require(isinstance(item, dict) and isinstance(item.get("name"), str) and item["name"] and
                item["name"] not in result and "value" in item and "error" not in item,
                "Native parameter identity is missing, duplicated or unreadable")
        require(item.get("valueCaptured") is True and item.get("valueComplete") is True and
                item.get("valueStatus") == "captured" and item.get("readAccessKnown") is True and
                item.get("readable") is True and not item.get("excluded"),
                "Native parameter lacks complete readable capture evidence")
        same(item, item)
        result[item["name"]] = item
    return result


def complete_connection_observations(connections, coverage, technology_path, project_path, plc_path):
    require(isinstance(connections, list) and len(connections) <= 50000, "Native connection observations are missing or exceed the review bound")
    require(isinstance(coverage, dict) and type(coverage.get("schemaVersion")) is int and coverage["schemaVersion"] == 1
            and coverage.get("complete") is False and coverage.get("scopeComplete") is True and coverage.get("truncated") is False
            and isinstance(technology_path, str) and technology_path and coverage.get("technologyPath") == technology_path,
            "Complete connection-service coverage for the exact technology path is required")
    references = coverage.get("referenceCoverage")
    require(isinstance(references, dict) and references.get("providerStatus") in ("notRequested", "available") and
            references.get("selectionPlcPath") == plc_path and isinstance(plc_path, str) and plc_path and
            references.get("truncated") is False and references.get("identifierCollisionDetected") is False and
            references.get("persistenceVerified") is False and references.get("atomicSnapshot") is False and
            type(references.get("visitedReads")) is int and type(references.get("maximumReads")) is int and
            0 <= references["visitedReads"] <= references["maximumReads"] <= 50000,
            "Complete bounded reference observations for the selected PLC are required")
    if references["providerStatus"] == "notRequested":
        require(references.get("projectPath") is None and references["visitedReads"] == 0,
                "An unused reference provider cannot claim project identity or native reads")
    else:
        require(isinstance(project_path, str) and project_path and references.get("projectPath") == project_path and
                references["visitedReads"] > 0, "Reference provider project context differs")
    service_rows = coverage.get("services")
    names = {"axis", "encoder", "measuringInput", "outputCam"}
    require(isinstance(service_rows, list) and len(service_rows) == 4 and
            all(isinstance(row, dict) and row.get("service") in names for row in service_rows) and
            {row["service"] for row in service_rows} == names, "Exact four-service observation inventory required")
    require(all(isinstance(row, dict) and row.get("service") in names for row in connections), "Connection service identity is missing")
    fields = {"actor": {"isConnected", "connectOption", "attributes"}, "sensor": {"isConnected", "connectOption", "attributes"},
              "torque": {"attributes"}, "encoder": {"isConnected", "connectOption", "attributes"},
              "measuringInput": {"isConnected", "inputAddress", "inputModule", "channel", "channelIndex"},
              "outputCam": {"isConnected", "outputAddress", "outputTag", "channel"}}
    root = technology_path + "/hardwareConnections/"
    for service in service_rows:
        name = service["service"]
        rows = [row for row in connections if row["service"] == name]
        require(service.get("scopeComplete") is True and service.get("status") in ("observed", "notExposed") and
                type(service.get("rowsCaptured")) is int and service["rowsCaptured"] == len(rows), "Connection service is incomplete or its row count differs")
        if service["status"] == "notExposed":
            require(not rows, "A non-exposed service cannot own connection rows")
            continue
        if name == "axis":
            require(service.get("sensorCollectionStatus") == "observed", "Complete sensor collection observation required")
            sensors = [row for row in rows if row.get("role") == "sensor"]
            expected = {root + "axis/actor": "actor", root + "axis/torque": "torque"}
            expected.update({root + "axis/sensors/" + str(i): "sensor" for i in range(len(sensors))})
        else:
            expected = {root + name: name}
        require(len(rows) == len(expected) and all(isinstance(row.get("observationPath"), str) for row in rows) and
                {row["observationPath"] for row in rows} == set(expected), "Connection slots are missing, duplicated or outside the exact owner")
        for row in rows:
            require(row.get("role") == expected[row["observationPath"]] and row.get("scopeComplete") is True and
                    row.get("status") in ("observed", "notExposed"), "Connection slot observation is incomplete")
            required_fields = fields[row["role"]] if row["status"] == "observed" else set()
            require(row["role"] != "sensor" or row["status"] == "observed", "Null sensor entries cannot qualify")
            require(isinstance(row.get("fieldStatus"), dict) and set(row["fieldStatus"]) == required_fields and
                    all(status == "captured" for status in row["fieldStatus"].values()) and required_fields <= row.keys(),
                    "Complete explicit connection-field observations required")
            if row["status"] == "notExposed":
                require(not fields[row["role"]].intersection(row), "An absent connection slot cannot contain observed values")
            if "attributes" in required_fields:
                parameter_rows(row["attributes"])
            if "isConnected" in required_fields:
                require(type(row["isConnected"]) is bool, "Connection state must be explicitly observed as Boolean")
            if "connectOption" in required_fields:
                require(isinstance(row["connectOption"], str), "Connection option is missing")
    pending = [connections]
    visited = 0
    while pending:
        visited += 1
        require(visited <= 50000, "Native connection evidence exceeds its review bound")
        item = pending.pop()
        if isinstance(item, dict):
            require(item.get("valueComplete") is not False and item.get("identityComplete") is not False and
                    not item.get("truncated") and not item.get("error") and item.get("expanded") is not False,
                    "Native connection evidence is incomplete or display-only")
            if "engineeringType" in item or item.get("valueSemantics") == "engineeringReference":
                require(references["providerStatus"] == "available" and item.get("valueSemantics") == "engineeringReference" and
                        item.get("valueStatus") == "capturedReference" and item.get("valueComplete") is True and
                        item.get("identityComplete") is True and item.get("resolution") == "resolved" and
                        item.get("persistenceVerified") is False and item.get("projectPath") == project_path and
                        item.get("selectionPlcPath") == plc_path and isinstance(item.get("engineeringType"), str) and item["engineeringType"],
                        "Exact native reference context and current-read resolution are required")
                if item.get("kind") == "channel":
                    require(item.get("identityBasis") == "ownerProviderRoundTripAndUniqueNativeChannel" and
                            "nativeIdentifier" in item and item["nativeIdentifier"] is None and
                            type(item.get("number")) is int and isinstance(item.get("type"), str) and item["type"] and
                            isinstance(item.get("ioType"), str) and item["ioType"] and
                            isinstance(item.get("parent"), dict) and item["parent"].get("kind") == "hardware" and
                            item["parent"].get("valueSemantics") == "engineeringReference",
                            "Channel identity requires a verified owner and exact native selector")
                    container = item["parent"].get("container")
                    require(container is None or isinstance(container, dict) and container.get("kind") == "hardware" and
                            container.get("valueSemantics") == "engineeringReference", "Channel container identity is incomplete")
                else:
                    identifier = item.get("nativeIdentifier")
                    require(item.get("kind") in ("hardware", "tag", "block", "softwareUnit") and
                            item.get("identityBasis") == "projectProviderRoundTrip" and
                            isinstance(identifier, str) and identifier.strip() and len(identifier.encode("utf-16-le", errors="surrogatepass")) <= 8192,
                            "Native object identifier and provider roundtrip are required")
                    try:
                        identifier.encode("utf-8", errors="strict")
                    except UnicodeError as error:
                        raise AssertionError("Native object identifier contains invalid Unicode") from error
            pending.extend(item.values())
        elif isinstance(item, list):
            pending.extend(item)
        require(len(pending) <= 50000, "Native connection evidence exceeds its review bound")
    same(connections, connections)


def engineering_snapshot(value, profile, name):
    require(value.get("truncated") is False and value.get("scopeComplete") is True, "Complete native technology inventory required")
    objects = value.get("technologyObjects")
    require(isinstance(objects, list) and len(objects) == 1, "Selected native technology identity is missing or ambiguous")
    obj = objects[0]
    owner, composition = obj.get("ownerPath"), obj.get("composition")
    require(isinstance(owner, str) and owner and composition in ("TechnologicalObjects", "OutputCams", "MeasuringInputs"),
            "Native technology selection owner/composition is missing or unsupported")
    expected_path = owner + ("" if composition == "TechnologicalObjects" else "/children/" + composition) + "/" + quote(name, safe="")
    require(obj.get("path") == expected_path, "Native technology path contradicts its owner/composition/name")
    require(isinstance(obj.get("path"), str) and obj["path"] and obj.get("name") == name and obj.get("typeIdentifier") == profile["typeIdentifier"] and
            obj.get("version") == profile["version"], "Native technology identity differs from profile")
    require(obj.get("parametersTruncated") is False and obj.get("parametersComplete") is True, "Complete native dependency metadata required")
    require(isinstance(value.get("project"), dict) and value["project"].get("path") and
            type(value.get("processId")) is int and value["processId"] > 0, "Native project/session identity required")
    return obj, parameter_rows(obj.get("parameters"))


def native_snapshot(value, engineering, obj, parameters):
    coverage = value.get("completeness", {})
    require(coverage.get("scopeComplete") is True and coverage.get("truncated") is False and value.get("warnings") == [] and
            value.get("parameterPrefix") in (None, "") and value.get("parametersComplete") is True, "Complete unfiltered native readback required")
    identity = value.get("technologyObject", {})
    require(identity.get("name") == obj["name"] and identity.get("systemLibraryType") == obj["typeIdentifier"] and
            identity.get("systemLibraryVersion") == obj["version"] and identity.get("path") == obj["path"] and
            identity.get("ownerPath") == obj["ownerPath"] and identity.get("composition") == obj["composition"] and
            isinstance(value.get("plcPath"), str) and value["plcPath"] and obj["path"].startswith(value["plcPath"] + "/technology/") and
            same(value.get("project"), engineering["project"]) and same(value.get("processId"), engineering["processId"]),
            "Native inspection identity/session differs from engineering readback")
    observed = parameter_rows(value.get("parameters"))
    require(same({k: v["value"] for k, v in observed.items()}, {k: v["value"] for k, v in parameters.items()}),
            "Native parameter views differ")
    complete_connection_observations(value.get("hardwareConnections"), value.get("hardwareConnectionCoverage"), obj["path"], value["project"]["path"], value["plcPath"])


def requested_value(expected, actual, native_type):
    if type(expected) in (int, float) and type(actual) in (int, float):
        require(math.isfinite(expected) and math.isfinite(actual), "Native scalar must be finite")
        if native_type == "System.Single":
            try:
                packed = struct.pack("f", expected), struct.pack("f", actual)
            except (OverflowError, struct.error) as error:
                raise AssertionError("Requested scalar exceeds native Single range") from error
            # Newer Python packs out-of-range values as infinity instead of raising.
            require(all(math.isfinite(struct.unpack("f", p)[0]) for p in packed),
                    "Requested scalar exceeds native Single range")
            return packed[0] == packed[1]
        return expected == actual
    return type(actual) is type(expected) and actual == expected


def qualify_technology_families(call, apply, profiles, cpu_type, evidence=None):
    evidence = evidence if evidence is not None else []
    for family in ("axis", "encoder"):
        selected = [profile for profile in profiles if profile["family"] == family]
        if not selected:
            evidence.append({"family": family, "status": "blocked",
                             "reason": "No explicit CPU/type/version profile was supplied; native type enumeration is unavailable."})
        for index, profile in enumerate(selected):
            row = {"family": family, "profile": profile, "status": "blocked"}
            evidence.append(row)
            if profile["cpuTypeIdentifier"] != cpu_type:
                row["reason"] = "Fixture CPU does not match the explicitly supported profile."
                continue
            row["status"] = "failed"
            name = technology_name(profile, index)
            created = "technologyName" not in profile
            requested = profile.get("parameters", {})
            if created:
                payload = {"name": name, "typeIdentifier": profile["typeIdentifier"], "version": profile["version"]}
                if requested:
                    payload["parameters"] = requested
                row["initialParameters"] = requested
                apply("to_create", payload, family + " initialized technology fixture")
            row["nativeBaselineStage"] = "after-initialized-creation-and-compile" if created else "before-parameter-edit"
            before = row["engineeringBefore"] = copy.deepcopy(call("engineering_read", {"selectors": {"scope": "technology", "name": name, "maxItems": 2000}}))
            obj, parameters = engineering_snapshot(before, profile, name)
            row["nativeBefore"] = copy.deepcopy(call("technology_read", {"technologyPath": obj["path"], "technologyName": name, "maxItems": 2000, "exportXml": True}))
            native_snapshot(row["nativeBefore"], before, obj, parameters)
            row["dependencies"] = {}
            for key, expected in profile.get("dependencies", {}).items():
                require(key in parameters and same(parameters[key].get("value"), expected), "Native parameter dependency does not match: " + key)
                row["dependencies"][key] = parameters[key]
            for key in requested:
                require(key in parameters and parameters[key].get("writable") is True and parameters[key].get("writeAccessKnown") is True and
                        isinstance(parameters[key].get("type"), str) and parameters[key]["type"],
                        "Parameter lacks native write permission: " + key)
            if requested and not created:
                apply("to_parameters", {"technologyPath": obj["path"], "name": name, "parameters": requested}, family + " parameter dependencies")
            after = row["engineeringAfter"] = copy.deepcopy(call("engineering_read", {"selectors": {"scope": "technology", "name": name, "maxItems": 2000}}))
            after_obj, after_parameters = engineering_snapshot(after, profile, name)
            require(same(before["project"], after["project"]) and same(before["processId"], after["processId"]) and
                    same({k: v for k, v in obj.items() if k != "parameters"}, {k: v for k, v in after_obj.items() if k != "parameters"}),
                    "Native owner/session or technology metadata changed")
            require(parameters.keys() == after_parameters.keys(), "Native parameter inventory changed")
            expected_parameters = copy.deepcopy(parameters)
            for key, expected in requested.items():
                actual = after_parameters[key].get("value")
                require(requested_value(expected, actual, parameters[key].get("type")), "Native parameter value differs: " + key)
                expected_parameters[key]["value"] = actual
            require(same(expected_parameters, after_parameters), "Unrequested parameter or native access/type metadata changed")
            row["parameterChanges"] = {key: {"before": parameters.get(key), "after": value} for key, value in after_parameters.items() if not same(value, parameters.get(key))}
            row["nativeAfter"] = copy.deepcopy(call("technology_read", {"technologyPath": obj["path"], "technologyName": name, "maxItems": 2000, "exportXml": True}))
            native_snapshot(row["nativeAfter"], after, after_obj, after_parameters)
            for field in ("technologyObject", "plcPath", "hardwareConnections", "hardwareConnectionCoverage"):
                require(same(row["nativeBefore"][field], row["nativeAfter"][field]), "Native connection/owner readback changed: " + field)
            if "expectedHardwareConnections" in profile:
                require(same(row["nativeAfter"]["hardwareConnections"], profile["expectedHardwareConnections"]), "Configured hardware connection dependencies differ")
            row["hardwareConnectionQualification"] = "matched-explicit-native-connections" if profile.get("expectedHardwareConnections") else "observed-only-no-connected-hardware-qualified"
            row["limitations"] = ["Checks cover complete returned parameter records and configured connections, not whole native XML, excluded display values, save/reopen, physical motion or runtime behavior.",
                                  "A scalar edit that changes connections needs a separately qualified complete-effect recipe; this harness cannot approve it."]
            if created:
                apply("to_delete", {"technologyPath": obj["path"], "name": name}, family + " technology fixture deletion")
                remaining = call("engineering_read", {"selectors": {"scope": "technology", "name": name, "maxItems": 2000}})
                require(remaining.get("truncated") is False and remaining.get("technologyObjects") == [] and
                        same(remaining.get("project"), before["project"]) and same(remaining.get("processId"), before["processId"]),
                        "Created technology fixture absence is not completely observed after deletion")
            row["status"] = "passed"
    return evidence
