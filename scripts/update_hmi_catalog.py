"""Reconcile only this workstream's feature contracts; preserve concurrent entries."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def update():
    path = ROOT / "contracts/feature-catalog.v1.json"
    catalog = json.loads(path.read_text(encoding="utf-8-sig"))
    entries = {
        "hmi_inspect": ("Bounded Classic and Unified HMI inspection", "partial", "conditional", [
            "Independent per-device/runtime native qualification is recorded separately in docs/verification/hmi-qualification.md.",
            "Classic alarm composition is unavailable; Classic items use retained native XML. Script bodies are read only on explicit source inspection and are never executed.",
            "Unknown/protected/missing targets and per-section bounds remain explicit; metadata is not a complete content fingerprint."]),
        "hmi_references": ("Evidence-backed HMI and PLC relationships", "partial", "conditional", [
            "Exact native object endpoints or native foreign-key semantics are required. Same names do not prove PLC ownership or declaration binding.",
            "Dynamic scripts, unsupported XML binding shapes, incomplete member mappings and missing native cross-reference providers remain unresolved."]),
        "hmi_capabilities": ("Optional HMI installation and operation diagnostics", "implemented", "available", [
            "Assembly/version evidence, bridge implementation, native qualification and current target availability are independent facts.",
            "Assembly presence does not establish a WinCC engineering license; target access/protection is operation-specific."]),
        "change.hmi_engineering": ("Guarded HMI engineering allowlist", "partial", "blocked", [
            "Classic BackColor scalar and Unified tag/property/placement/binding bodies use existing prepare/apply/status/restore. No current profile has passed every preservation/compile/persistence/recovery/visual gate.",
            "Incomplete HMI preservation blocks mutation before project writes; client-supplied evidence or a metadata hash cannot enable it.",
            "Library lifecycle stays in the library subsystem; HMI receives an exact source/destination/dependency/binding handoff."]),
        "hmi.engineering": ("Classic and Unified HMI integration", "partial", "conditional", [
            "Native bounded family adapters, exact v2 HMI identities, optional module diagnostics, project-model provider registration and evidence relationships are implemented.",
            "Inspection, engineering, visual and runtime qualification remain separate. See the exact-profile evidence ledger; broad H0-H5 acceptance is not claimed."]),
    }
    by_id = {item["featureId"]: item for item in catalog["features"]}
    for identifier, (title, implementation, availability, limitations) in entries.items():
        change = identifier.startswith("change.")
        entry = by_id.get(identifier)
        if entry is None:
            entry = {"featureId": identifier}
            catalog["features"].append(entry)
        entry.update({"domain": "hmi", "title": title,
                      "mcpTools": [identifier] if identifier.startswith("hmi_") else [],
                      "changeKind": "hmi_engineering" if change else None,
                      "api": {"status": "available", "reasons": ["Installed V21 Classic/Unified public documentation and assembly evidence; individual operation limitations are distinct."]},
                      "implementation": {"status": implementation, "limitations": limitations},
                      "qualification": {"status": "unverified", "evidenceIds": []},
                      "availability": {"status": availability, "reasons": [limitations[0]]},
                      "prerequisites": ["Installed TIA V21 Openness; optional matching WinCC family module for native reads", "Exact accessible project/device/runtime profile, license and protection state"],
                      "acceptanceCriteria": ["AC-identity", "AC-completeness", "AC-evidence"] + (["AC-preview", "AC-native", "AC-recovery", "AC-preserve"] if change else [])})
    path.write_text(json.dumps(catalog, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print("Updated five HMI feature contracts; preserved all other entries and evidence.")


if __name__ == "__main__":
    update()
