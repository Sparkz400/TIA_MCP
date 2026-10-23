"""Inventory retained evidence or verify a staged recovery tree; never import or qualify it."""
import argparse
from collections import Counter
import hashlib
from pathlib import Path
import re
import stat
import api_coverage as inventory
import api_coverage_bindings as bindings


def evidence_path(root, relative):
    if not isinstance(relative, str) or any(part in ('', '.', '..') for part in relative.split('/')):
        raise ValueError('Evidence paths must be canonical relative paths.')
    path = inventory.within(root, relative)
    for part in (path, *path.parents):
        try:
            attributes = part.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(attributes.st_mode) or getattr(attributes, 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError('Evidence paths cannot follow links or junctions.')
    return path


def audit(binding, root, binding_sha256):
    bindings.validate(binding)
    entries = binding['evidenceInputs']
    if len(entries) > inventory.MAX_FILES or len({row['path'] for row in entries}) != len(entries):
        raise ValueError('Oversized or duplicate evidence inventory.')
    rows = []
    total = 0
    for entry in sorted(entries, key=lambda row: row['path']):
        relative = entry['path']
        expected = entry.get('sha256')
        if expected is not None and not re.fullmatch('[a-f0-9]{64}', expected):
            raise ValueError('Malformed retained digest.')
        features = []
        evidence_ids = set()
        for feature in binding['features']:
            refs = [ref for ref in feature['evidenceRefs'] if relative in ref['artifacts']]
            if refs:
                features.append(feature['featureId'])
                evidence_ids.update(ref['id'] for ref in refs)
        row = dict(path=relative, retainedState=entry['state'], expectedSha256=expected,
                   actualSha256=None, bytes=None, evidenceIds=sorted(evidence_ids), featureIds=sorted(features))
        try:
            path = evidence_path(root, relative)
            if not path.exists():
                row['state'] = 'missing'
            elif not path.is_file():
                row['state'] = 'not-a-file'
            else:
                size = path.stat().st_size
                if size > inventory.MAX_FILE or total + size > inventory.MAX_TOTAL:
                    row['state'] = 'over-budget'
                else:
                    data = inventory.read_bytes(path, min(inventory.MAX_FILE, inventory.MAX_TOTAL - total))
                    total += len(data)
                    row['bytes'] = len(data)
                    row['actualSha256'] = hashlib.sha256(data).hexdigest()
                    row['state'] = 'unbound' if expected is None else 'matched' if expected == row['actualSha256'] else 'mismatch'
        except (OSError, ValueError):
            row['state'] = 'unreadable-or-unsafe'
        rows.append(row)
    return dict(schemaVersion='retained-evidence-audit.v1', bindingSha256=binding_sha256,
                evidenceRoot=str(root.absolute()), nativeExecuted=False, qualificationPromoted=False,
                releaseAccepted=False, allRetainedInputsMatched=all(row['state'] == 'matched' for row in rows),
                counts=dict(sorted(Counter(row['state'] for row in rows).items())), inputs=rows)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binding', type=Path, default=bindings.ROOT / bindings.REGISTRY)
    parser.add_argument('--evidence-root', type=Path, default=bindings.ROOT)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--require-complete', action='store_true', help='Exit nonzero unless every retained input matches.')
    args = parser.parse_args()
    raw = inventory.read_bytes(args.binding)
    binding = inventory.json_read(args.binding)
    if raw != inventory.read_bytes(args.binding):
        raise ValueError('Binding changed while being read.')
    report = audit(binding, args.evidence_root, hashlib.sha256(raw).hexdigest())
    inventory.write_new(args.out, report)
    print(report['counts'])
    failed = any(row['state'] in ('mismatch', 'unreadable-or-unsafe', 'over-budget') or
                 row['state'] == 'not-a-file' and row['expectedSha256'] is not None for row in report['inputs'])
    return 1 if failed or args.require_complete and not report['allRetainedInputsMatched'] else 0


if __name__ == '__main__':
    raise SystemExit(main())
