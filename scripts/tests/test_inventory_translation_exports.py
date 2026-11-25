"""Layout inventory of native project-text exports: synthetic trees, and the retained store when it is present."""
import os
from pathlib import Path
import tempfile
import unittest
from zipfile import ZipFile

import inventory_translation_exports as inventory

NS = 'xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"'


def export(path, sheet_extra='', workbook_extra='', parts=()):
    path.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(path, 'w') as archive:
        archive.writestr('xl/workbook.xml', f'<workbook {NS}><sheets/>{workbook_extra}</workbook>')
        for name in ('sheet1', 'sheet2'): archive.writestr(f'xl/worksheets/{name}.xml', f'<worksheet {NS}><sheetData/>{sheet_extra}</worksheet>')
        for name in parts: archive.writestr(name, '<x/>')


class InventoryTests(unittest.TestCase):
    def test_native_exports_only_and_row_references_reported(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            export(root / 'run' / 'translation-native' / 'observe-1' / 'all.xlsx', sheet_extra='<sheetFormatPr/>', workbook_extra='<calcPr/>')
            export(root / 'run' / 'translation-native' / 'candidate-1' / 'candidate.xlsx', sheet_extra='<mergeCells/>')
            export(root / 'project-text-1' / 'translation-native' / 'export-1' / 'full.xlsx', sheet_extra='<mergeCells/>')
            self.assertEqual(inventory.inventory(root)['outsideFilterableLayout'], [])
            export(root / 'run' / 'translation-native' / 'categories-1' / 'plc-all.xlsx', sheet_extra='<mergeCells/><hyperlinks/>',
                   workbook_extra='<definedNames/>', parts=['xl/comments1.xml'])
            result = inventory.inventory(root)
            self.assertEqual(result['workbooks'], 2)
            self.assertEqual([o['notFilterable'] for o in result['outsideFilterableLayout']], [['xl/comments1.xml', 'definedNames', 'hyperlinks', 'mergeCells']])

    def test_retained_native_exports_fit_the_filterable_layout(self):
        root = Path(os.environ.get('TIA_MCP_RETAINED_ARTIFACTS', inventory.REPO / 'artifacts'))
        if not root.is_dir() or not inventory.native_exports(root): self.skipTest('No retained native project-text exports under ' + str(root))
        result = inventory.inventory(root)
        self.assertEqual((result['outsideFilterableLayout'], result['unreadable']), ([], []))


if __name__ == '__main__': unittest.main()
