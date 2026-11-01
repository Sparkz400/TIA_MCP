"""Offline source checks of the worker's scratch ownership; nothing here starts TIA."""
import pathlib
import re
import unittest

WORKER = pathlib.Path(__file__).resolve().parents[1] / "src/TiaMcp.Openness.V21"


class ScratchOwnershipTests(unittest.TestCase):
    def test_only_scratch_creation_grants_scratch_status(self):
        # A reopen or restore may hand scratch status on only if the closed project held it, so every other assignment is guarded.
        unguarded = []
        for source in sorted(WORKER.glob("*.cs")):
            text = source.read_text(encoding="utf-8")
            for match in re.finditer(r"([^;{}]*)\b_scratchProject = ([^;]+);", text):
                guard, value = match.group(1).strip(), match.group(2).strip()
                if value == "null" or value.startswith("_portal.Projects.Create("):
                    continue
                if not re.fullmatch(r"if \(\w+\)(?: owner\.)?", guard):
                    unguarded.append(f"{source.name}:{text.count(chr(10), 0, match.start()) + 1}")
        self.assertEqual([], unguarded)


if __name__ == "__main__":
    unittest.main()
