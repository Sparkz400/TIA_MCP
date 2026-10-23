"""Compare authored Classic screen exports without erasing IDs, bindings or native attributes."""
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def screen_evidence(path):
    raw = Path(path).read_bytes()
    if len(raw) > 64 * 1024 * 1024 or b"<!DOCTYPE" in raw.upper() or b"<!ENTITY" in raw.upper():
        raise ValueError("Screen evidence exceeds bounds or contains a prohibited DTD/entity declaration.")
    root = ET.fromstring(raw, parser=ET.XMLParser(target=ET.TreeBuilder(insert_comments=True)))
    if root.tag != "Document":
        raise ValueError("Expected native public SimaticML Document.")
    screens = root.findall("Hmi.Screen.Screen")
    if len(screens) != 1 or any(child.tag not in {"Engineering", "DocumentInfo", "Hmi.Screen.Screen"} for child in root):
        raise ValueError("Exactly one screen export and known document metadata are required.")

    def node(element):
        # Whitespace used only to indent child elements is formatting. Leaf text is exact.
        text = element.text or ""
        if len(element) and not text.strip():
            text = ""
        tail = element.tail or ""
        if not tail.strip():
            tail = ""
        return [element.tag if isinstance(element.tag, str) else "#comment", sorted(element.attrib.items()),
                text, tail, [node(child) for child in element]]

    semantic = [root.find("Engineering").attrib if root.find("Engineering") is not None else {}, node(screens[0])]
    canonical = json.dumps(semantic, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return {"path": str(Path(path).resolve()), "sha256": hashlib.sha256(raw).hexdigest(),
            "screenSha256": hashlib.sha256(canonical).hexdigest()}


def compare(paths):
    evidence = [screen_evidence(path) for path in paths]
    if len(evidence) < 2:
        raise ValueError("Supply source and at least one readback export.")
    return {"schemaVersion": 1, "policy": "classic-exported-screen-v1",
            "exclusions": ["DocumentInfo installation/export timestamp metadata", "interelement indentation"],
            "evidence": evidence, "exportedScreenPreserved": len({item["screenSha256"] for item in evidence}) == 1,
            "visualVerified": False, "runtimeVerified": False}


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+", type=Path)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if args.report.exists():
        parser.error("Choose a fresh report file; prior evidence is retained.")
    result = compare(args.paths)
    args.report.write_text(json.dumps(result, indent=2), encoding="utf-8")
    raise SystemExit(0 if result["exportedScreenPreserved"] else 1)
