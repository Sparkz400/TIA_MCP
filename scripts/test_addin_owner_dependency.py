"""The Framework fixture must use the worker's declared Newtonsoft version."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


RUNNER = Path(__file__).with_name('Test-AddInOwner.ps1')
PWSH = shutil.which('pwsh')
PROBE = r'''
$ErrorActionPreference = 'Stop'
$repoRoot = $env:TIA_OWNER_ROOT
$globalPackages = Join-Path $repoRoot 'global-packages'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($env:TIA_OWNER_RUNNER, [ref]$null, [ref]$null)
$assignments = $ast.FindAll({param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -in @('[xml]$workerProject', '$newtonsoftVersion', '$newtonsoft')
}, $true)
foreach ($assignment in $assignments) { Invoke-Expression $assignment.Extent.Text }
ConvertTo-Json -InputObject $newtonsoft -Compress
'''


@unittest.skipUnless(PWSH, 'PowerShell 7 is required')
class OwnerDependencyTests(unittest.TestCase):
    def probe(self, declared='13.0.4', available=('13.0.3', '13.0.4'), global_cache=False):
        with tempfile.TemporaryDirectory(prefix='tia-owner-dependency-') as directory:
            root = Path(directory)
            project = root / 'src/TiaMcp.Openness.V21/TiaMcp.Openness.V21.csproj'
            project.parent.mkdir(parents=True)
            project.write_text('<Project><ItemGroup><PackageReference Include="Newtonsoft.Json" '
                               f'Version="{declared}" /></ItemGroup></Project>', encoding='utf-8')
            cache = root / ('global-packages' if global_cache else '.packages')
            for version in available:
                dll = cache / f'newtonsoft.json/{version}/lib/net45/Newtonsoft.Json.dll'
                dll.parent.mkdir(parents=True)
                dll.write_bytes(b'path selection only; never loaded')
            result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', PROBE],
                                    env=dict(os.environ, TIA_OWNER_ROOT=str(root), TIA_OWNER_RUNNER=str(RUNNER)),
                                    capture_output=True, text=True, timeout=30)
            self.assertEqual(0, result.returncode, result.stderr)
            selected = json.loads(result.stdout)
            expected = str(cache / f'newtonsoft.json/{declared}/lib/net45/Newtonsoft.Json.dll') if declared in available else None
            self.assertEqual(expected, selected)

    def test_declared_version_wins_over_old_cached_version(self):
        self.probe()

    def test_old_version_alone_cannot_satisfy_new_dependency(self):
        self.probe(available=('13.0.3',))

    def test_clean_global_cache_uses_exact_declared_version(self):
        self.probe(declared='14.2.7', available=('14.2.7',), global_cache=True)


if __name__ == '__main__':
    unittest.main()
