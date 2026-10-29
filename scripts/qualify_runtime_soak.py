"""Run bounded owned real-clock workers and independently audit retained bytes.

Each Popen retains its original process handle. Timeout cleanup addresses only
that owned child; unexpected exits fail the run and remain in immutable receipts.
No device/native access, mount manipulation, or actual system exhaustion.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time

import psutil

from runtime_soak_checks import audit


def write(path, value):
    with Path(path).open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def run(assembly, root, seconds, long=False, epochs=15):
    if os.name != "nt" or not 30 <= seconds <= 120:
        raise ValueError("Windows real-clock qualification requires 30..120 seconds")
    if long and (not 2 <= epochs <= 15 or epochs * seconds > 900):
        raise ValueError("Long mode requires 2..15 epochs and at most 900 acquisition seconds")
    root.mkdir(parents=True, exist_ok=False)
    assembly = assembly.resolve(strict=True)
    source_root = Path(__file__).resolve().parents[1]
    paths = ["scripts/qualify_runtime_soak.py", "scripts/runtime_soak_checks.py", "scripts/runtime_pressure_checks.py",
             "tests/TiaMcp.PhysicalRuntime.Tests/RuntimeCaptureSoakChecks.cs", "tests/TiaMcp.PhysicalRuntime.Tests/RuntimePollingChecks.cs",
             "tests/TiaMcp.PhysicalRuntime.Tests/RuntimeCaptureLongSoakChecks.cs",
             "tests/TiaMcp.PhysicalRuntime.Tests/Program.cs", "tests/TiaMcp.PhysicalRuntime.Tests/TiaMcp.PhysicalRuntime.Tests.csproj",
             "src/TiaMcp.Host/RuntimeCaptures.cs", "src/TiaMcp.Host/HostOptions.cs", "src/TiaMcp.Host/ToolGroups.cs",
             "src/TiaMcp.Core/PhysicalRuntime/RuntimeCaptureStore.cs", "src/TiaMcp.Core/PhysicalRuntime/WebRuntimeProtocol.cs"]
    def inputs():
        return {"sources": [{"path": p, "sha256": hashlib.sha256((source_root / p).read_bytes()).hexdigest()} for p in paths],
                "runtimeFiles": [{"path": str(p), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
                                 for p in sorted(assembly.parent.iterdir()) if p.is_file() and p.suffix in {".dll", ".json", ".exe"}]}
    original_inputs = inputs()
    write(root / "inputs-before.json", original_inputs)
    execution = {"schemaVersion": "runtime-soak-execution.v1", "assembly": str(assembly),
                 "assemblySha256": hashlib.sha256(assembly.read_bytes()).hexdigest(), "phases": [],
                 "nativeExecuted": False, "actualDiskFull": False}
    try:
        phases = [
            (("long", ["--soak-long", str(root / "long"), str(epochs), str(seconds)], 0, epochs * (seconds + 45) + 60)
             if long else ("steady", ["--soak", str(root / "steady"), str(seconds)], 0, seconds + 60)),
            ("crash", ["--soak-crash", str(root / "crash")], 73, 45),
            ("recover", ["--soak-recover", str(root / "crash")], 0, 30),
        ]
        for name, flags, expected, timeout in phases:
            command = ["dotnet", str(assembly), *flags]
            started = time.monotonic()
            phase = {"name": name, "command": command, "expectedExitCode": expected, "forcedTermination": False}
            with (root / (name + ".stdout.log")).open("xb") as stdout, (root / (name + ".stderr.log")).open("xb") as stderr:
                process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr,
                                           creationflags=subprocess.CREATE_NO_WINDOW)
                phase["pid"] = process.pid
                try:
                    identity = psutil.Process(process.pid)
                    phase["created"] = identity.create_time()
                    phase["exe"] = identity.exe()
                    write(root / (name + "-start.json"), phase)
                    try:
                        phase["exitCode"] = process.wait(timeout=timeout)
                    except subprocess.TimeoutExpired:
                        phase["forcedTermination"] = True
                        process.kill()  # Popen uses this child's retained Windows handle, not a reconstructed PID.
                        phase["exitCode"] = process.wait(timeout=10)
                finally:
                    if process.poll() is None:
                        phase["forcedTermination"] = True
                        process.kill()
                        phase["exitCode"] = process.wait(timeout=10)
                    phase["originalOwnedProcessWaited"] = process.returncode is not None
                    phase["elapsedSeconds"] = time.monotonic() - started
                    execution["phases"].append(phase)
                    write(root / (name + "-exit.json"), phase)
            if phase["exitCode"] != expected or phase["forcedTermination"]:
                raise RuntimeError(f"{name} failed: retained exit {phase['exitCode']}; see original logs")
    finally:
        write(root / "execution.json", execution)
    if inputs() != original_inputs:
        write(root / "input-change-failure.json", {"reason": "Qualification sources or runtime files changed during execution"})
        raise RuntimeError("Qualification inputs changed; retained execution is not final-source qualification")
    result = audit(root)
    write(root / "independent-oracle.json", result)
    write(root / "verification.json", {"schemaVersion": "runtime-soak-verification.v1",
          **original_inputs, "inputsUnchangedThroughRun": True,
          "evidence": [{"path": str(p.relative_to(root)), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
                       for p in sorted(root.rglob("*")) if p.is_file()],
          "assemblySha256": execution["assemblySha256"], "passed": True})
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seconds", type=int, default=60)
    parser.add_argument("--long", action="store_true", help="Run repeated bounded epochs in one original owned process")
    parser.add_argument("--epochs", type=int, default=15, help="Long-mode epoch count, 2..15; short mode is unchanged")
    args = parser.parse_args()
    print(json.dumps(run(args.assembly, args.output.resolve(), args.seconds, args.long, args.epochs), indent=2))
