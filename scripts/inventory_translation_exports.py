"""Inventory the package layout of retained native V21 project-text exports (read-only).

The Host-side category filter (TranslationCategoryScope.Restrict) removes rows, so it accepts only parts and
worksheet/workbook elements that cannot reference a row. This lists, over every workbook the worker exported
under a `translation-native/` directory, the part sets and the child element names of `xl/workbook.xml` and both
worksheets, and reports each workbook that uses anything outside the filter's lists. Host-written candidates
(`candidate-*`) and synthetic test workbooks are not native exports and are skipped. Exit code 1 when any
workbook is outside the lists or unreadable.
"""
import argparse
from collections import Counter
import json
from pathlib import Path
import xml.etree.ElementTree as ET
from zipfile import BadZipFile, ZipFile

from verify_translation_populated_category_probe import MAX_COMPRESSED_BYTES, MAX_XML_BYTES, S, BoundedXmlTree

REPO = Path(__file__).resolve().parents[1]
# Same lists as TranslationCategoryScope.Filterable* (src/TiaMcp.Translations/TranslationCategoryScope.cs).
PARTS = {'[Content_Types].xml', '_rels/.rels', 'docProps/app.xml', 'docProps/core.xml', 'docProps/custom.xml', 'xl/_rels/workbook.xml.rels',
         'xl/sharedStrings.xml', 'xl/styles.xml', 'xl/theme/theme1.xml', 'xl/workbook.xml', 'xl/worksheets/sheet1.xml', 'xl/worksheets/sheet2.xml'}
WORKBOOK = {'fileVersion', 'workbookPr', 'bookViews', 'sheets', 'calcPr'}
WORKSHEET = {'sheetPr', 'dimension', 'sheetViews', 'sheetFormatPr', 'cols', 'sheetData', 'pageMargins', 'pageSetup', 'headerFooter'}
LAYOUT = (('xl/workbook.xml', WORKBOOK), ('xl/worksheets/sheet1.xml', WORKSHEET), ('xl/worksheets/sheet2.xml', WORKSHEET))


def native_exports(root):
    # project-text-* roots are synthetic workbooks written by TiaMcp.TranslationImport.Tests, not worker exports.
    return sorted(p for p in Path(root).rglob('*.xlsx') if 'translation-native' in p.parts and
                  not any(part.startswith(('candidate-', 'project-text-')) for part in p.parts))


def layout(path):
    """Part names and, per layout part, the child element names (a foreign namespace keeps its full tag)."""
    if path.stat().st_size > MAX_COMPRESSED_BYTES: raise ValueError('Oversized workbook')
    with ZipFile(path) as archive:
        names = sorted(archive.namelist())
        children = {}
        for part, _ in LAYOUT:
            raw = archive.open(part).read(MAX_XML_BYTES + 1)
            if len(raw) > MAX_XML_BYTES: raise ValueError('XML part byte budget exceeded')
            root = ET.fromstring(raw, parser=ET.XMLParser(target=BoundedXmlTree()))
            children[part] = [c.tag[len(S):] if c.tag.startswith(S) else c.tag for c in root]
    return names, children


def inventory(root):
    files = native_exports(root)
    parts, elements, outside, unreadable = Counter(), Counter(), [], []
    for path in files:
        try: names, children = layout(path)
        except (BadZipFile, KeyError, ValueError, ET.ParseError) as error:
            unreadable.append({'path': str(path), 'error': str(error)}); continue
        parts[tuple(names)] += 1
        for part, allowed in LAYOUT: elements[(part, tuple(children[part]))] += 1
        extra = sorted(set(names) - PARTS) + sorted({e for part, allowed in LAYOUT for e in children[part] if e not in allowed})
        if extra: outside.append({'path': str(path), 'notFilterable': extra})
    return {'root': str(Path(root).resolve()), 'workbooks': len(files),
            'partSets': [{'parts': list(k), 'count': v} for k, v in parts.most_common()],
            'elementSets': [{'part': k[0], 'children': list(k[1]), 'count': v} for k, v in elements.most_common()],
            'outsideFilterableLayout': outside, 'unreadable': unreadable}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=REPO / 'artifacts', help='Retained evidence tree to scan (default: this checkout\'s artifacts/)')
    args = parser.parse_args()
    result = inventory(args.root)
    print(json.dumps(result, indent=2))
    return 1 if result['outsideFilterableLayout'] or result['unreadable'] else 0


if __name__ == '__main__': raise SystemExit(main())
