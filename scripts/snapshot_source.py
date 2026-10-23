"""Copy tracked and nonignored source, including uncommitted changes, with a hash receipt."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess


def snapshot(root, destination):
    root, destination = root.resolve(), destination.resolve()
    if destination.exists():
        raise ValueError('A fresh snapshot directory is required')
    names = subprocess.check_output(['git', '-C', str(root), 'ls-files', '-co', '--exclude-standard', '-z']).decode().split('\0')
    records = []
    for name in sorted(set(filter(None, names))):
        path = root / name
        if not path.is_file():
            continue  # Retained index entries deleted in the working tree.
        if path.is_symlink() or not path.resolve().is_relative_to(root):
            raise ValueError('Source escapes checkout: ' + name)
        data = path.read_bytes()
        records.append(dict(path=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
    destination.mkdir(parents=True)
    for row in records:
        source, target = root / row['path'], destination / row['path']
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        if hashlib.sha256(target.read_bytes()).hexdigest() != row['sha256']:
            raise ValueError('Source changed during copying: ' + row['path'])
    for row in records:
        if hashlib.sha256((root / row['path']).read_bytes()).hexdigest() != row['sha256']:
            raise ValueError('Source changed during snapshot: ' + row['path'])
    receipt = dict(schemaVersion=1, kind='working-tree-source-checkpoint', root=str(root), destination=str(destination),
                   head=subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD']).decode().strip(),
                   includesUncommittedChanges=True, nativeQualified=False, files=records)
    manifest = destination.parent / (destination.name + '-manifest.json')
    with manifest.open('x', encoding='utf-8') as stream:
        json.dump(receipt, stream, indent=2)
        stream.write('\n')
    print(json.dumps(dict(files=len(records), manifest=str(manifest), sha256=hashlib.sha256(manifest.read_bytes()).hexdigest())))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--destination', type=Path, required=True)
    options = parser.parse_args()
    snapshot(options.root, options.destination)
