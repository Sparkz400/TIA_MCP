"""Machine-wide lock so only one session runs TIA Portal/PLCSIM work at a time.

    python scripts/native_lane.py acquire --owner lane-a   # waits, prints the token
    python scripts/native_lane.py release --token <token>
    python scripts/native_lane.py status
    python scripts/native_lane.py run --owner lane-a -- python scripts/run_block_tests.py ...   # preferred

`run` holds the lock for exactly the child process and always releases it; a failed or
interrupted acquire never starts the command.
"""
import argparse
import json
import os
import subprocess
import sys
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

LOCK = Path(os.environ.get("TIA_MCP_NATIVE_LANE_LOCK", r"D:\Entwicklung\native-lane.lock"))


def acquire(owner, poll_seconds=15, timeout_seconds=None, lock=LOCK):
    token, start = uuid.uuid4().hex, time.monotonic()
    while True:
        try:
            with lock.open("x", encoding="utf-8") as stream:  # exclusive create is the lock
                json.dump({"owner": owner, "token": token, "pid": os.getpid(),
                           "acquiredAtUtc": datetime.now(timezone.utc).isoformat()}, stream)
            return token
        except FileExistsError:
            if timeout_seconds is not None and time.monotonic() - start >= timeout_seconds:
                raise TimeoutError("Native lane is held: " + lock.read_text(encoding="utf-8"))
            time.sleep(poll_seconds)


def release(token, lock=LOCK):
    holder = json.loads(lock.read_text(encoding="utf-8"))
    if holder["token"] != token:
        raise PermissionError("Native lane is held by another owner: " + holder["owner"])
    lock.unlink()


def run(owner, command, lock=LOCK):
    token = acquire(owner, lock=lock)
    try:
        return subprocess.run(command).returncode
    finally:
        release(token, lock=lock)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    a = sub.add_parser("acquire"); a.add_argument("--owner", required=True); a.add_argument("--timeout-seconds", type=float)
    r = sub.add_parser("release"); r.add_argument("--token", required=True)
    sub.add_parser("status")
    x = sub.add_parser("run"); x.add_argument("--owner", required=True); x.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.command == "run":
        command = args.command[1:] if args.command[:1] == ["--"] else args.command
        if not command: parser.error("run needs a command after --")
        sys.exit(run(args.owner, command))
    if args.command == "acquire":
        print(acquire(args.owner, timeout_seconds=args.timeout_seconds))
    elif args.command == "release":
        release(args.token)
    else:
        print(LOCK.read_text(encoding="utf-8") if LOCK.exists() else "free")


if __name__ == "__main__":
    main()
