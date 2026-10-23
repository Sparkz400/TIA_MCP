"""Catalog-grounded offline central I/O qualification; never invent identifiers."""

FAMILIES = {
    "DI": {"filter": "DI 16x24VDC", "articlePrefix": "6ES7 521", "ioType": "Input"},
    "DO": {"filter": "DQ 16x24VDC", "articlePrefix": "6ES7 522", "ioType": "Output"},
    "AI": {"filter": "AI 8xU/I", "articlePrefix": "6ES7 531", "ioType": "Input"},
    "AO": {"filter": "AQ 4xU/I", "articlePrefix": "6ES7 532", "ioType": "Output"},
}


def candidates(catalog, family):
    profile = FAMILIES[family]
    return sorted((entry for entry in catalog["entries"]
                   if profile["articlePrefix"] in (entry.get("articleNumber") or "")
                   and entry.get("typeIdentifier")), key=lambda item: item["typeIdentifier"], reverse=True)


CHANNEL_ATTRIBUTES = {"ChannelActivation", "MeasuringType", "MeasuringRange", "InputDelay", "SubstituteValue", "DigitalInputDelay"}


def qualify_address_change(call, apply, module, rows, selected, rail_path, family):
    addresses = [(item["path"], address) for result in rows for item in result["items"] for address in item.get("addresses", [])
                 if address.get("ioType") == FAMILIES[family]["ioType"] and address.get("length", 0) > 0]
    assert addresses, "No selected native address to qualify"
    path, address = addresses[0]
    peer_name = module["name"] + "_Peer"
    apply("hardware_plug", {"hardwarePath": rail_path, "typeIdentifier": selected["typeIdentifier"],
                           "name": peer_name, "positionNumber": 3}, family + " address-conflict peer")
    hardware = call("hardware_inspect", {"maxItems": 2000})
    assert not hardware.get("completeness", {}).get("truncated"), "Complete address inventory is required"
    peer = next(item for item in hardware["items"] if item["name"] == peer_name)
    all_addresses = [(item["path"], value) for item in hardware["items"] for value in item.get("addresses", [])
                     if value.get("ioType") == address["ioType"] and value.get("length", 0) > 0]
    peer_addresses = [(item_path, value) for item_path, value in all_addresses if item_path == peer["path"] or item_path.startswith(peer["path"] + "/")]
    assert peer_addresses, "Peer module has no native address range"
    conflict = peer_addresses[0][1]["startAddress"]
    payload = {"hardwarePath": path, "ioType": address["ioType"], "oldStartAddress": address["startAddress"], "startAddress": conflict}
    rejected = call("change_prepare", {"kind": "hardware_address_set", "payload": payload}, family + " address conflict", allow_error=True)
    assert rejected.get("error", {}).get("code") == "AddressConflict", rejected
    # Pick a positive aligned free range from observed native ranges. Siemens
    # still validates the selected module's bounds during prepare/apply/compile.
    end = max(value["startAddress"] + (value["length"] + 7) // 8 for _, value in all_addresses)
    target = ((end + 15) // 16) * 16 + 16
    payload["startAddress"] = target
    apply("hardware_address_set", payload, family + " address relocation")
    changed = call("hardware_inspect", {"hardwarePath": path, "maxItems": 2000})
    assert any(value["ioType"] == address["ioType"] and value["startAddress"] == target and value["length"] == address["length"]
               for item in changed["items"] for value in item.get("addresses", [])), "Address relocation was not observed"
    apply("hardware_address_set", {**payload, "oldStartAddress": target, "startAddress": address["startAddress"]}, family + " address restore")
    restored = call("hardware_inspect", {"hardwarePath": path, "maxItems": 2000})
    assert any(value["ioType"] == address["ioType"] and value["startAddress"] == address["startAddress"] and value["length"] == address["length"]
               for item in restored["items"] for value in item.get("addresses", [])), "Original address was not restored"
    apply("hardware_delete", {"hardwarePath": peer["path"]}, family + " address-conflict peer deletion")
    return {"status": "passed", "before": address, "temporaryStartAddress": target, "conflictStartAddress": conflict,
            "conflictRejected": True, "restored": True}


def qualify_hardware_families(call, apply, families, address_enabled=False, evidence=None):
    inventory = call("project_inventory", {"maxItems": 2000})
    rails = [item for item in inventory["hardware"] if item["positionNumber"] == 0 and item["path"].count("/items/") == 1]
    assert len(rails) == 1, "Owned fixture requires one central rail"
    evidence = evidence if evidence is not None else []
    for family in families:
        row = {"family": family, "status": "blocked", "limitations": [
            "Central rail fixture does not qualify distributed I/O relationships."]}
        evidence.append(row)
        catalog = call("hardware_catalog_search", {"filter": FAMILIES[family]["filter"], "maxItems": 500})
        entries = candidates(catalog, family)
        row["catalogCandidates"] = entries
        name = "TiaMcp_" + family + "_Fixture"
        selected = None
        for entry in entries:
            payload = {"hardwarePath": rails[0]["path"], "typeIdentifier": entry["typeIdentifier"],
                       "name": name, "positionNumber": 2}
            prepared = call("change_prepare", {"kind": "hardware_plug", "payload": payload},
                            "prepare catalog " + family + " module", allow_error=True)
            if prepared.get("changeId"):
                selected = entry
                row["status"] = "failed"
                row["selectedModule"] = selected
                apply("hardware_plug", payload, "catalog " + family + " module", prepared)
                break
            code = prepared.get("error", {}).get("code")
            if code != "IncompatibleHardware":
                raise RuntimeError("Unexpected " + family + " preparation failure: " + str(prepared))
        if selected is None:
            row["reason"] = "No discovered compatible central " + family + " module; no type identifier was guessed."
            continue
        row["selectedModule"] = selected
        inventory = call("project_inventory", {"maxItems": 2000})
        module = next(item for item in inventory["hardware"] if item["name"] == name)
        children = [item for item in inventory["hardware"] if item["path"] == module["path"] or item["path"].startswith(module["path"] + "/")]
        row["nativeInspection"] = [call("hardware_inspect", {"hardwarePath": item["path"], "maxItems": 2000}) for item in children]
        addresses = [address for result in row["nativeInspection"] for item in result["items"] for address in item.get("addresses", [])]
        assert any(address.get("ioType") == FAMILIES[family]["ioType"] and address.get("length", 0) > 0 for address in addresses), "Missing native " + family + " address range"
        row["addressRanges"] = addresses
        row["channels"] = [channel for result in row["nativeInspection"] for item in result["items"] for channel in item.get("channels", [])]
        row["addressEditing"] = qualify_address_change(call, apply, module, row["nativeInspection"], selected, rails[0]["path"], family) if address_enabled else {
            "status": "blocked", "reason": "hardware_address_set is not exposed by the tested feature catalog."}
        metadata = call("engineering_read", {"selectors": {"scope": "hardware", "hardwarePath": module["path"], "maxItems": 1000}})
        attributes = {entry["name"]: entry for entry in metadata["items"][0]["attributes"]}
        assert attributes["Name"]["writeSupportedByBridge"] and "Write" in attributes["Name"]["access"]
        parameter = next((entry for name, entry in attributes.items() if name in CHANNEL_ATTRIBUTES and entry.get("writeSupportedByBridge")
                          and "Write" in entry.get("access", "") and type(entry.get("value")) in (str, int, float, bool)), None)
        if parameter:
            apply("hardware_attributes", {"hardwarePath": module["path"], "attributes": {parameter["name"]: parameter["value"]}}, family + " exposed channel parameter roundtrip")
            reread = call("engineering_read", {"selectors": {"scope": "hardware", "hardwarePath": module["path"], "maxItems": 1000}})
            actual = next(entry for entry in reread["items"][0]["attributes"] if entry["name"] == parameter["name"])
            assert actual.get("value") == parameter["value"], "Channel parameter roundtrip changed native value"
            row["channelParameterRoundtrip"] = {"status": "passed", "attribute": parameter, "qualification": "unchanged-scalar-roundtrip"}
        else:
            row["channelParameterRoundtrip"] = {"status": "blocked", "reason": "No native scalar channel parameter on the module is writable through the exposed allowlist."}
        apply("hardware_attributes", {"hardwarePath": module["path"], "attributes": {"Name": name + "_Renamed"}}, family + " module attribute")
        inventory = call("project_inventory", {"maxItems": 2000})
        renamed = next(item for item in inventory["hardware"] if item["name"] == name + "_Renamed")
        apply("hardware_delete", {"hardwarePath": renamed["path"]}, family + " module deletion")
        inventory = call("project_inventory", {"maxItems": 2000})
        assert not any(item["name"] in (name, name + "_Renamed") for item in inventory["hardware"])
        row["status"] = "passed"
        row["qualifiedOperations"] = ["plug", "compile", "native-address-read", "native-channel-read", "rename", "delete"]
    return evidence
