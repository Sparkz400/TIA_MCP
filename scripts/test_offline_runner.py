"""Exercise the runner's real Git selection with controlled executable search paths."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


PWSH = shutil.which('pwsh')
RUNNER = Path(__file__).with_name('Test-Offline.ps1')
PROBE = r'''
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($env:TIA_TEST_RUNNER, [ref]$null, [ref]$null)
$selection = @($ast.FindAll({param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -eq '$gitExecutable'
}, $true))
if ($selection.Count -ne 1) { throw 'Expected one runner Git selection.' }
$env:PATH = $env:TIA_TEST_PATH
Invoke-Expression $selection[0].Extent.Text
function Capture-Arguments { ConvertTo-Json -InputObject @($args) -Compress }
Capture-Arguments --git-executable $gitExecutable --git-fixtures 'fixture path with spaces'
'''


@unittest.skipUnless(PWSH, 'PowerShell 7 is required for runner regression checks')
class GitSelectionTests(unittest.TestCase):
    def probe(self, count):
        with tempfile.TemporaryDirectory(prefix='tia-runner-') as root:
            folders = [Path(root) / str(i) for i in range(count)]
            for folder in folders:
                folder.mkdir()
                executable = folder / ('git.exe' if os.name == 'nt' else 'git')
                executable.write_bytes(b'')  # Discovery only; this file is never executed.
                executable.chmod(0o755)
            env = dict(os.environ, TIA_TEST_RUNNER=str(RUNNER),
                       TIA_TEST_PATH=os.pathsep.join(map(str, folders)))
            result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', PROBE],
                                    env=env, capture_output=True, text=True, timeout=30)
            if count:
                self.assertEqual(0, result.returncode, result.stderr)
                expected = str(folders[0] / ('git.exe' if os.name == 'nt' else 'git'))
                self.assertEqual(['--git-executable', expected, '--git-fixtures',
                                  'fixture path with spaces'], json.loads(result.stdout))
            else:
                self.assertNotEqual(0, result.returncode)
                self.assertEqual('', result.stdout.strip())

    def test_one_git(self):
        self.probe(1)

    def test_multiple_git_installations_pass_one_path(self):
        self.probe(2)

    def test_missing_git_stops_before_dispatch(self):
        self.probe(0)


CAPTURE_PROBE = r'''
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($env:TIA_TEST_RUNNER, [ref]$null, [ref]$null)
$pipelines = @($ast.FindAll({param($node)
    $node -is [System.Management.Automation.Language.PipelineAst] -and
    $node.Extent.Text.Contains('Tee-Object') -and $node.Extent.Text.Contains('general-managed.log')
}, $true))
if ($pipelines.Count -ne 1) { throw 'Expected one explicit general-suite capture pipeline.' }
$pipeline = $pipelines[0]
$native = $pipeline.PipelineElements[0]
# Substitute only the native executable/arguments; execute the runner's actual redirection and Tee pipeline.
$commandEnd = $native.CommandElements[-1].Extent.EndOffset - $pipeline.Extent.StartOffset
$body = '& $env:TIA_TEST_PYTHON -c $env:TIA_TEST_NATIVE_CODE $env:TIA_TEST_EXIT' + $pipeline.Extent.Text.Substring($commandEnd)
$output = $env:TIA_TEST_OUTPUT
$managedSuite = 'TiaMcp.Tests'
Invoke-Expression $body
$checks = @($ast.FindAll({param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text -eq 'if ($LASTEXITCODE -ne 0) { throw "Managed offline tests failed: $managedSuite" }'
}, $true))
if ($checks.Count -ne 1) { throw 'Expected the managed native exit-code guard.' }
Invoke-Expression $checks[0].Extent.Text
'''


@unittest.skipUnless(PWSH, 'PowerShell 7 is required for native output capture checks')
class ManagedCaptureTests(unittest.TestCase):
    def test_redirected_native_output_is_retained_and_failure_propagates(self):
        for code in [0, 7]:
            with self.subTest(exit_code=code), tempfile.TemporaryDirectory(prefix='tia-capture-') as root:
                env = dict(os.environ, TIA_TEST_RUNNER=str(RUNNER), TIA_TEST_OUTPUT=root,
                           TIA_TEST_PYTHON=sys.executable, TIA_TEST_EXIT=str(code),
                           TIA_TEST_NATIVE_CODE='import sys,json;print("HANDOFF_FIXTURE "+json.dumps({"root":"fixture path"}));print("stderr-fixture",file=sys.stderr);sys.exit(int(sys.argv[1]))')
                result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', CAPTURE_PROBE],
                                        env=env, capture_output=True, text=True, timeout=30)
                capture = (Path(root) / 'general-managed.log').read_text(encoding='utf-8-sig')
                rows = [line for line in capture.splitlines() if line.startswith('HANDOFF_FIXTURE ')]
                self.assertEqual([{'root': 'fixture path'}], [json.loads(line.removeprefix('HANDOFF_FIXTURE ')) for line in rows])
                self.assertIn('stderr-fixture', capture)
                if code:
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn('Managed offline tests failed: TiaMcp.Tests', result.stderr)
                else:
                    self.assertEqual(0, result.returncode, result.stderr)


WORKSTATION_REPORT_PROBE = r'''
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($env:TIA_TEST_RUNNER, [ref]$null, [ref]$null)
$commands = @($ast.FindAll({param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq 'dotnet' -and
    $node.Extent.Text.Contains('TiaMcp.Deployment.Tests.dll')
}, $true))
if ($commands.Count -ne 1) { throw 'Expected one project-index test dispatch.' }
$repoRoot = $env:TIA_TEST_ROOT
$testTemp = Join-Path $repoRoot 'current run'
New-Item -ItemType Directory -Path $testTemp | Out-Null
$Configuration = 'Debug'
function dotnet {
    # Replace only the executable; exercise the real runner's output selection.
    [IO.File]::WriteAllText($args[1], 'new test result')
    $args[1]
}
$paths = @(Invoke-Expression $commands[0].Extent.Text; Invoke-Expression $commands[0].Extent.Text)
ConvertTo-Json -InputObject $paths -Compress
'''


@unittest.skipUnless(PWSH, 'PowerShell 7 is required for workstation runner checks')
class WorkstationEvidenceTests(unittest.TestCase):
    def test_repeated_index_runs_preserve_historical_evidence_and_prior_results(self):
        with tempfile.TemporaryDirectory(prefix='tia-evidence-') as root:
            historical = Path(root) / 'artifacts/project-index-store-tests.json'
            historical.parent.mkdir()
            historical.write_bytes(b'original retained evidence\x00\xff')
            env = dict(os.environ, TIA_TEST_RUNNER=str(RUNNER.with_name('Test.ps1')),
                       TIA_TEST_ROOT=root)
            result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', WORKSTATION_REPORT_PROBE],
                                    env=env, capture_output=True, text=True, timeout=30)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(b'original retained evidence\x00\xff', historical.read_bytes())
            paths = [Path(value) for value in json.loads(result.stdout)]
            self.assertEqual(2, len(set(paths)))
            for path in paths:
                self.assertEqual(Path(root) / 'current run', path.parent)
                self.assertEqual('new test result', path.read_text(encoding='utf-8'))


if __name__ == '__main__':
    unittest.main()
