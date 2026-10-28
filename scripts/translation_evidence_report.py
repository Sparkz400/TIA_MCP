"""Combine translation qualification run roots into one report for scripts/curate_evidence.py.

Each run root keeps result.json and one NNN-<tool>.json file per MCP call ({request, response}).
The report lists every call with its error flag, each run's result and the independent verifier
outputs, and is complete only when every run and every verifier completed.
"""
import argparse
import hashlib
import json
from pathlib import Path


def load(path): return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def calls(root):
    rows = []
    for path in sorted(Path(root).glob('[0-9][0-9][0-9]-*.json')):
        value = load(path)
        if isinstance(value, dict) and 'request' in value and 'response' in value:
            rows.append({'run': Path(root).name, 'name': path.stem.split('-', 1)[1], 'isError': bool(value['response'].get('isError'))})
    return rows


def report(roots, verifications):
    runs = []
    for root in roots:
        result = load(Path(root) / 'result.json')
        runs.append({'root': str(root), 'result': hashlib.sha256((Path(root) / 'result.json').read_bytes()).hexdigest(),
                     'completed': result.get('completed') is True, 'failure': result.get('failure'),
                     **{k: result[k] for k in ('trials', 'reverseTrials', 'interruptions', 'refusals', 'operation', 'priorOperationReconciled') if k in result}})
    checked = [{'path': str(p), 'sha256': hashlib.sha256(Path(p).read_bytes()).hexdigest(), 'completed': load(p).get('completed') is True} for p in verifications]
    return {'completed': all(r['completed'] for r in runs) and all(v['completed'] for v in checked), 'runs': runs,
            'verifications': checked, 'toolCalls': [c for root in roots for c in calls(root)],
            'checks': [f"{Path(r['root']).name}: {'completed' if r['completed'] else 'failed'}" for r in runs] +
                      [f"verifier {Path(v['path']).name}: {'completed' if v['completed'] else 'failed'}" for v in checked]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run', type=Path, action='append', required=True)
    parser.add_argument('--verification', type=Path, action='append', default=[])
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    value = report(args.run, args.verification)
    args.output.write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(json.dumps({'completed': value['completed'], 'runs': len(value['runs']), 'calls': len(value['toolCalls'])}))


if __name__ == '__main__': main()
