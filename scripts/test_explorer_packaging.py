import json
import shutil
import tempfile
import unittest
from pathlib import Path
from smoke_offline_package import verify_explorer_assets


class ExplorerPackagingTests(unittest.TestCase):
    def test_source_and_entry_point_drift_refuse(self):
        root=Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory() as folder:
            bundle=Path(folder);ui=bundle/'ui/project-explorer'
            manifest=json.loads((root/'ui/project-explorer/index.html.manifest.json').read_bytes())
            ui.mkdir(parents=True)
            for name in ['index.html','index.html.manifest.json']+[row['path'] for row in manifest['assets']]:
                shutil.copyfile(root/'ui/project-explorer'/name,ui/name)
            verify_explorer_assets(bundle)
            for name in ['explorer.js','index.html']:
                target=ui/name;original=target.read_bytes();target.write_bytes(original+b'\n')
                with self.assertRaises(AssertionError):verify_explorer_assets(bundle)
                target.write_bytes(original)
            manifest['assets'].pop();(ui/'index.html.manifest.json').write_text(json.dumps(manifest))
            with self.assertRaises(AssertionError):verify_explorer_assets(bundle)
