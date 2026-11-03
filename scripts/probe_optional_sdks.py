"""Read installed public XML documentation and hash its assemblies without loading Siemens code.

This inventories prerequisites only. It never starts TIA, checks licenses or qualifies a native route.
"""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def inspect(root, relative, prefixes):
    assembly = root / relative
    documentation = assembly.with_suffix('.xml')
    result = {'assembly': relative, 'present': assembly.is_file(), 'documentationPresent': documentation.is_file()}
    for name, path in [('assemblySha256', assembly), ('documentationSha256', documentation)]:
        if path.is_file():
            result[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    if documentation.is_file():
        members = ET.parse(documentation).findall('./members/member')
        result['documentedMembers'] = sorted(m.attrib['name'] for m in members
            if any(m.attrib.get('name', '').startswith(prefix) for prefix in prefixes)
            and '#Engineering#' not in m.attrib['name'])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--v21-root', type=Path, required=True)
    parser.add_argument('--v19-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = {'schemaVersion': 1, 'nativeExecuted': False, 'licenseValidated': False,
        'supportQualified': False, 'method': 'Public XML documentation and file hashes; no assembly loading.',
        'sdks': [
            inspect(args.v21_root, 'PublicAPI/V21/net48/Siemens.Engineering.CFC.dll',
                ['M:Siemens.Engineering.SW.FunctionCharts.ChartProvider.CompleteExport',
                 'M:Siemens.Engineering.SW.FunctionCharts.ChartProvider.SelectiveExport',
                 'M:Siemens.Engineering.SW.FunctionCharts.ChartProvider.Import']),
            inspect(args.v21_root, 'PublicAPI/V21/net48/Siemens.Engineering.DCC.dll',
                ['M:Siemens.Engineering.MC.Drives.Dcc.DriveControlChartComposition.Create',
                 'M:Siemens.Engineering.MC.Drives.Dcc.DriveControlChartComposition.Import',
                 'M:Siemens.Engineering.MC.Drives.Dcc.DriveControlChartComposition.Export',
                 'M:Siemens.Engineering.MC.Drives.Dcc.DriveControlChart.Export',
                 'M:Siemens.Engineering.MC.Drives.Dcc.DccBlockComposition.Create',
                 'M:Siemens.Engineering.MC.Drives.Dcc.DccSoftware.',
                 'M:Siemens.Engineering.MC.Drives.Dcc.DccChart.']),
            inspect(args.v19_root, 'PublicAPI/V19/Siemens.Engineering.dll',
                ['M:Siemens.Engineering.TiaPortal.#ctor', 'M:Siemens.Engineering.ProjectComposition.Create',
                 'M:Siemens.Engineering.ProjectComposition.Open', 'M:Siemens.Engineering.Project.Archive'])]}
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(json.dumps({'output': str(args.output), 'sdksPresent': sum(s['present'] for s in report['sdks']),
                      'nativeExecuted': False}))


if __name__ == '__main__':
    main()
