"""Read-only MCP inventory/export qualification against one explicitly selected open TIA process."""
import argparse
import hashlib
import json
import pathlib
import csv
import zipfile
import re
import unicodedata
import xml.etree.ElementTree as ET
from smoke_mcp import Client


def decode_cell(value):
    # A single pass preserves text produced by escaped underscores.
    return re.sub(r"_x([0-9A-Fa-f]{4})_", lambda match: chr(int(match[1], 16)), value)


def csv_literal(value):
    offset = 0
    while offset < len(value) and (value[offset] in "\t\r\n\v\f\x85" or unicodedata.category(value[offset]).startswith("Z")):
        offset += 1
    formula = value[offset:offset + 1] in ("=", "+", "-", "@")
    return "'" + value if formula or value[:1] in ("\t", "\r", "\n") else value


def verify_exports(csv_path, xlsx_path, expected_rows):
    with csv_path.open(encoding="utf-8-sig", newline="") as source:
        csv_rows = list(csv.reader(source))
    with zipfile.ZipFile(xlsx_path) as package:
        for name in package.namelist():
            if name.endswith((".xml", ".rels")):
                ET.fromstring(package.read(name))
        sheet = ET.fromstring(package.read("xl/worksheets/sheet1.xml"))
        ns = {"s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
        assert not sheet.findall(".//s:f", ns)
        excel_rows = [[decode_cell(cell.find("s:is/s:t", ns).text or "") for cell in row] for row in sheet.findall("s:sheetData/s:row", ns)]
    assert len(csv_rows) == len(excel_rows) == expected_rows + 1
    # CSV alone adds an apostrophe before formula-like literal text.
    for csv_row, excel_row in zip(csv_rows, excel_rows):
        assert len(csv_row) == len(excel_row)
        for text, literal in zip(csv_row, excel_row):
            assert text == csv_literal(literal), "CSV/XLSX cell values differ"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, required=True)
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--process", type=int, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    client = Client(args.host.resolve(), 300, extra_arguments=["--worker", str(args.worker.resolve()), "--artifacts", str(output / "artifacts")])
    report = {"nativeQualification": "offline capture only", "physicalHardwareQualification": "unverified",
              "hostSha256": hashlib.sha256(args.host.read_bytes()).hexdigest(),
              "hostManagedSha256": hashlib.sha256(args.host.with_suffix(".dll").read_bytes()).hexdigest(),
              "coreSha256": hashlib.sha256((args.host.parent / "TiaMcp.Core.dll").read_bytes()).hexdigest(),
              "workerSha256": hashlib.sha256(args.worker.read_bytes()).hexdigest()}
    try:
        client.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "inventory-qualification", "version": "1"}})
        client.notify("notifications/initialized")
        names = {t["name"] for t in client.request("tools/list")["tools"]}
        assert {"device_inventory", "device_online_browse", "device_identify", "device_inventory_export"} <= names
        report["attachment"] = client.tool("tia_attach", {"processId": args.process})
        page = client.tool("device_inventory", {"pageSize": 37})
        metadata = page["snapshot"]
        rows = list(page["rows"])
        pages = 1
        while page["nextCursor"]:
            page = client.tool("device_inventory", {"snapshotId": metadata["snapshotId"], "cursor": page["nextCursor"], "pageSize": 37})
            assert page["offset"] == len(rows)
            rows.extend(page["rows"])
            pages += 1
        assert len(rows) == metadata["totalRows"]
        assert len({r["rowId"] for r in rows}) == len(rows)
        assert all(r["online"]["status"] == "notRead" for r in rows)
        assert all(all(f["state"] == "notRead" for f in r["actual"].values()) for r in rows)
        report.update(snapshot=metadata, pages=pages, rows=len(rows))
        (output / "inventory.json").write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
        report["exports"] = [client.tool("device_inventory_export", {"snapshotId": metadata["snapshotId"], "format": f}) for f in ("csv", "xlsx")]
        csv_path = pathlib.Path(report["exports"][0]["artifact"]["path"])
        xlsx_path = pathlib.Path(report["exports"][1]["artifact"]["path"])
        verify_exports(csv_path, xlsx_path, len(rows))
        report["exportCrossCheck"] = "all CSV/XLSX cells equivalent; no XLSX formulas; all XML parts parsed"
        report["deviceScopes"] = []
        for device in (r for r in rows if r["kind"] == "device"):
            scoped = client.tool("device_inventory", {"devicePath": device["path"], "pageSize": 200})
            expected = [r["rowId"] for r in rows if r["path"] == device["path"] or r["path"].startswith(device["path"] + "/")]
            observed = [r["rowId"] for r in scoped["rows"]]
            while scoped["nextCursor"]:
                scoped = client.tool("device_inventory", {"snapshotId": scoped["snapshot"]["snapshotId"], "cursor": scoped["nextCursor"], "pageSize": 200})
                observed.extend(r["rowId"] for r in scoped["rows"])
            assert observed == expected
            report["deviceScopes"].append({"devicePath": device["path"], "rows": len(observed), "status": "passed"})
        report["status"] = "passed"
    finally:
        report["cleanup"] = client.close()
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    assert report["cleanup"]["hostExitCode"] == 0 and not report["cleanup"]["forcedHostStop"]
    print(json.dumps({"status": report["status"], "rows": report["rows"], "pages": report["pages"], "output": str(output)}))


if __name__ == "__main__":
    main()
