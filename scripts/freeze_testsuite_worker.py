"""Freeze and verify the disabled TS-Q01 candidate. Never starts a TIA worker or native instance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


REPO = Path(__file__).resolve().parents[1]


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def save(path, value):
    with Path(path).open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--assisted-qualification", action="store_true", help="Compile the separate owned-scratch UI-assisted qualification tools; normal production admission remains closed.")
    args = parser.parse_args()
    output = args.out.resolve()
    output.relative_to(REPO / "artifacts")
    output.mkdir(parents=True, exist_ok=False)
    source = output / "source"
    source.mkdir()
    listed = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=REPO).decode().split("\0")
    paths = sorted({p for p in listed if p and (p.startswith(("src/", "tests/", "contracts/", "fixtures/", "scripts/")) or
                    p in {"Directory.Build.props", "TiaMcp.slnx", "NuGet.Config", "global.json"}) and (REPO / p).is_file()})
    hashes = {p: sha(REPO / p) for p in paths}
    for p in paths:
        destination = source / p
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(REPO / p, destination)
        if sha(destination) != hashes[p] or sha(REPO / p) != hashes[p]:
            raise RuntimeError("Concurrent source edit while freezing: " + p)
    save(output / "source-manifest.json", hashes)
    commands = []

    def run(name, command):
        command = list(map(str, command))
        with (output / (name + ".log")).open("xb") as log:
            result = subprocess.run(command, cwd=source, stdout=log, stderr=subprocess.STDOUT,
                                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        commands.append({"name": name, "command": command, "cwd": str(source), "exitCode": result.returncode,
                         "log": name + ".log", "logSha256": sha(output / (name + ".log"))})
        print(name + ": " + str(result.returncode), flush=True)
        if result.returncode:
            save(output / "failed-commands.json", commands)
            raise RuntimeError("Failed frozen candidate check; retain failed package: " + name)

    run("dotnet-info", ["dotnet", "--info"])
    build = output / "build"
    projects = ["src/TiaMcp.Host/TiaMcp.Host.csproj", "src/TiaMcp.Openness.V21/TiaMcp.Openness.V21.csproj", "tests/TiaMcp.TestSuite.Tests/TiaMcp.TestSuite.Tests.csproj",
                "tests/TiaMcp.TestSuite.Worker.Tests/TiaMcp.TestSuite.Worker.Tests.csproj",
                "tests/TiaMcp.WatchAdapter.Boundary.Tests/TiaMcp.WatchAdapter.Boundary.Tests.csproj"]
    for project in projects:
        run("build-" + Path(project).stem, ["dotnet", "build", project, "-c", "Release", "--artifacts-path", build,
                                          "-p:RestorePackagesPath=" + str(REPO / ".packages"), "-v", "minimal"] +
            (["-p:TestSuiteQualification=true"] if args.assisted_qualification else []))
    binpath = lambda name: build / "bin" / name / "release" / (name + ".exe")
    results = output / "managed-results"
    run("managed-checks", [binpath("TiaMcp.TestSuite.Tests"), results])
    run("worker-boundary-checks", [binpath("TiaMcp.TestSuite.Worker.Tests"), output / "worker-checks.json"])
    run("watch-boundary-checks", [binpath("TiaMcp.WatchAdapter.Boundary.Tests")])
    fixture = Path(str(results) + "-production") / "host-fixture.json"
    run("host-mcp-checks", [sys.executable, "-B", "scripts/test_testsuite_production_host.py", "--host", binpath("TiaMcp.Host"),
                            "--fixture", fixture, "--report", output / "host-mcp-checks.json"])
    binaries = {str(p.relative_to(output)).replace("\\", "/"): sha(p) for p in sorted((build / "bin").rglob("*")) if p.is_file()}
    api = Path(r"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48")
    native_api = {str(p): sha(p) for p in sorted(api.glob("Siemens.Engineering.*")) if p.suffix in {".dll", ".xml"}}
    for p, digest in hashes.items():
        if sha(source / p) != digest:
            raise RuntimeError("Frozen source changed during build: " + p)
    save(output / "manifest.json", {"schemaVersion": 1, "purpose": "TS-Q01 disabled owning-worker candidate",
         "nativeExecutionAuthorized": False, "nativeExecuted": False, "qualifiedProfiles": [],
         "assistedQualificationBuild": args.assisted_qualification,
         "scopeState": "unresolvedCompleteEffectiveScope", "licenseState": "currentObservationProviderUnresolved",
         "queueState": "Build-only freeze; each native attempt requires its own current sequential admission and retained process ownership",
         "sourceManifestSha256": sha(output / "source-manifest.json"), "binaryHashes": binaries,
         "installedPublicApiHashes": native_api, "commands": commands,
         "nextStep": "Inspect current prerequisites and complete effective scope in an owned scratch. The optional scope-probe Host cannot execute tests or enable a production profile. No checkpoint or Run while complete scope/license are unresolved."})
    print("Frozen disabled candidate: " + str(output / "manifest.json"), flush=True)


if __name__ == "__main__":
    main()
