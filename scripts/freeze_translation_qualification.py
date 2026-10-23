"""Create an immutable source copy and build isolated translation qualification binaries."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]; output = args.output.resolve()
    if output.exists(): raise ValueError('Fresh immutable package directory required')
    output.mkdir(parents=True); source = output / 'source'; source.mkdir()
    names = subprocess.check_output(['rg', '--files', '-g', '!artifacts/**', '-g', '!docs/**'], cwd=repo, text=True).splitlines()
    records = []
    for name in names:
        original = repo / name
        target = source / name; target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(original, target)
        records.append({'path': str(original), 'sha256': sha(target), 'retained': str(target)})
    current_names = subprocess.check_output(['rg', '--files', '-g', '!artifacts/**', '-g', '!docs/**'], cwd=repo, text=True).splitlines()
    if set(current_names) != set(names) or any(sha(Path(record['path'])) != record['sha256'] for record in records):
        raise ValueError('Source changed while freezing; retain this incomplete package and use a fresh directory')
    build = output / 'build'
    with (output / 'build.log').open('x', encoding='utf-8') as log:
        for project in ['TiaMcp.Host', 'TiaMcp.Openness.V21']:
            command = ['dotnet', 'build', str(source / 'src' / project / (project + '.csproj')), '-c', 'Release',
                       '-p:ArtifactsPath=' + str(build), '-v:q']
            log.write(json.dumps(command)+'\n'); log.flush()
            subprocess.run(command, cwd=source, stdout=log, stderr=subprocess.STDOUT, check=True)
    for name, project in [('host', 'TiaMcp.Host'), ('worker', 'TiaMcp.Openness.V21')]:
        shutil.copytree(build / 'bin' / project / 'release', output / name)
    manifest = {'productionEnabled': False, 'qualificationRuntimeArgument': '--translation-qualification', 'sources': records,
        'packages': [{'path': str(p), 'sha256': sha(p)} for part in ['host','worker'] for p in (output/part).rglob('*') if p.is_file()],
        'api': [{'path': str(p), 'sha256': sha(p)} for p in Path('C:/Program Files/Siemens/Automation/Portal V21/PublicAPI/V21/net48').glob('Siemens.Engineering*.dll')]}
    with (output/'manifest.json').open('x', encoding='utf-8') as stream: json.dump(manifest, stream, indent=2)
    print(json.dumps({'completed': True, 'package': str(output), 'sources': len(records)}))


if __name__ == '__main__': main()
