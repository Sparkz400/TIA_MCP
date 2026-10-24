"""Check diagnostic mode refusal on a compiled worker without loading Siemens APIs."""
import argparse
import itertools
import json
from pathlib import Path
import subprocess
import tempfile


def verify(worker):
    modes = ('--plcsim-discovery-probe', '--plcsim-startup-discovery-probe', '--plcsim-ui-discovery-probe')
    checked = []
    with tempfile.TemporaryDirectory(prefix='tia-probe-arguments-') as temporary:
        root = Path(temporary)
        output = root / 'must-not-exist'
        missing_api = root / 'missing-api'
        cases = [([left, str(output), right, str(output)], 'Only one discovery probe mode is allowed.')
                 for left, right in itertools.product(modes, repeat=2)]
        for mode in modes:
            for other in (['--pipe', 'unused'], ['--component-qualification'], ['--testsuite-scope-probe'], ['--translation-qualification']):
                for reverse in (False, True):
                    probe = [mode, str(output)]
                    cases.append(((other + probe) if reverse else (probe + other),
                                  'Discovery probes cannot be combined with a pipe or another qualification mode.'))
        for arguments, message in cases:
            # A missing API directory ensures the old parser cannot start TIA in this negative control.
            result = subprocess.run([str(worker.resolve()), *arguments, '--api-dir', str(missing_api)],
                                    capture_output=True, text=True, timeout=10,
                                    creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            assert result.returncode == 1 and 'ArgumentException: ' + message in result.stderr, (arguments, result.returncode, result.stderr)
            assert not output.exists(), 'Refused arguments created probe output.'
            checked.append(arguments)
    return {'completed': True, 'nativeExecuted': False, 'checks': len(checked)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worker', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(verify(args.worker)))
