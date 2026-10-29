"""Operator helper for TS-Q01: records what you see in TIA and ALM as the observation the worker waits for.

Run it in your own terminal while qualify_testsuite_native.py runs. It watches --root for each new challenge.json,
shows what the challenge names, asks what you see and copies your original screenshots into the challenge folder.
It writes your answers as given; the worker, not this helper, decides whether they qualify. Stop it with Ctrl+C.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import time

SELECTOR = "CodeBlock_FB_FC_OB"
PRODUCT = "TIA Portal Test Suite Advanced V21"


def pending(root):
    return sorted((p for p in root.rglob("challenge.json") if not (p.parent / "observation.json").exists()), key=lambda p: p.stat().st_mtime)


def observation(challenge, answers, screenshots):
    """The document StyleUiEvidence.Validate reads; screenshots are (file name, sha256, purpose) inside the challenge folder."""
    document = {"method": "operator-read-native-scope-editor", "challenge": challenge,
                "selectedProgramBlocks": answers["selectedProgramBlocks"], "excludedBranches": answers["excludedBranches"],
                "allLeavesVisible": answers["allLeavesVisible"], "checkboxesChanged": answers["checkboxesChanged"],
                "objectSelector": answers["objectSelector"], "ruleEnabled": answers["ruleEnabled"],
                "screenshots": [{"file": name, "sha256": digest, "purpose": purpose} for name, digest, purpose in screenshots]}
    if challenge.get("requireLicenseCheck"):
        document["license"] = {"product": answers["licenseProduct"], "method": "Automation License Manager Check",
                               "validKeys": answers["validKeys"], "defectiveKeys": answers["defectiveKeys"]}
    return document


def yes(question):
    while True:
        answer = input(question + " [y/n] ").strip().lower()
        if answer in ("y", "n"):
            return answer == "y"


def names(question, expected):
    if yes(question + "\n    " + ", ".join(expected) + "\n  Exactly these?"):
        return list(expected)
    return [n.strip() for n in input("  Type what you see instead, comma-separated: ").split(",") if n.strip()]


def number(question):
    while True:
        answer = input(question + " ").strip()
        if answer.isdigit():
            return int(answer)


def answer(challenge_path, screenshot_folder):
    challenge = json.loads(challenge_path.read_text(encoding="utf-8"))
    folder = challenge_path.parent
    issued = datetime.datetime.fromisoformat(challenge["issuedUtc"][:26].rstrip("Z")).replace(tzinfo=datetime.timezone.utc)
    print("\n=== Challenge %s, phase %s ===" % (folder.name, challenge["phase"]))
    print("  TIA shows rule set '%s' in the rule-set editor (project %s)." % (challenge["ruleName"], challenge["projectPath"]))
    print("  Answer within 12 minutes of %s UTC." % issued.strftime("%H:%M:%S"))
    print("  1. Expand the complete scope tree. Change no checkbox.")
    print("  2. Screenshot the expanded tree (Win+Shift+S saves to %s)." % screenshot_folder)
    if challenge.get("requireLicenseCheck"):
        print("  3. Open Automation License Manager, select '%s', run Check and screenshot the result." % PRODUCT)
    input("  Press Enter when the screenshots are taken. ")
    answers = {
        "ruleEnabled": yes("Is the rule enabled?"),
        "objectSelector": SELECTOR if yes("Does the rule's object selector show code blocks (FB, FC, OB)?") else input("  Type the selector shown: ").strip(),
        "allLeavesVisible": yes("Is the whole scope tree expanded, with every leaf visible?"),
        "checkboxesChanged": yes("Did you change any checkbox?"),
        "selectedProgramBlocks": names("Checked under Program blocks:", challenge["selectedProgramBlocks"]),
        "excludedBranches": names("Unchecked branches:", challenge["excludedBranches"])}
    if challenge.get("requireLicenseCheck"):
        answers["licenseProduct"] = PRODUCT if yes("Did the check run on '%s'?" % PRODUCT) else input("  Type the product checked: ").strip()
        answers["validKeys"] = number("How many valid keys does the check show?")
        answers["defectiveKeys"] = number("How many defective keys?")
    while True:
        taken = sorted((p for p in screenshot_folder.glob("*") if p.suffix.lower() in (".png", ".jpg", ".jpeg")
                        and p.stat().st_mtime >= issued.timestamp()), key=lambda p: p.stat().st_mtime)
        # The Snipping Tool can save a few seconds after the snip; never send an observation without its screenshots.
        if taken:
            break
        input("  No screenshot taken since %s UTC is in %s yet. Save them there, then press Enter to look again. " % (issued.strftime("%H:%M:%S"), screenshot_folder))
    screenshots = []
    for path in taken:
        purpose = {"s": "scope", "l": "license"}.get(input("  %s: [s]cope, [l]icense or [i]gnore? " % path.name).strip().lower())
        if purpose:
            name = "%s-%d%s" % (purpose, len(screenshots) + 1, path.suffix.lower())
            shutil.copyfile(path, folder / name)  # The original bytes; the worker rehashes them.
            screenshots.append((name, hashlib.sha256((folder / name).read_bytes()).hexdigest(), purpose))
    temporary = folder / "observation.json.tmp"
    temporary.write_text(json.dumps(observation(challenge, answers, screenshots), indent=2), encoding="utf-8")
    os.replace(temporary, folder / "observation.json")  # The worker polls for the final name; never let it read a partial file.
    print("  Observation written with %d screenshot(s); the worker checks it now." % len(screenshots))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True, help="The TS-Q01 run folder (or a parent of several runs).")
    parser.add_argument("--screenshots", type=Path, default=Path.home() / "Pictures" / "Screenshots")
    args = parser.parse_args()
    print("Waiting for challenges under %s (Ctrl+C to stop)." % args.root)
    while True:
        waiting = pending(args.root) if args.root.exists() else []
        if waiting:
            answer(waiting[0], args.screenshots)
        else:
            time.sleep(1)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
