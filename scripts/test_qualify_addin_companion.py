"""Offline checks of qualify_addin_companion.py; nothing here starts TIA."""
import pathlib
import tempfile
import unittest
from unittest import mock

import qualify_addin_companion as q


class AddInCompanionTests(unittest.TestCase):
    def test_only_completed_handoffs_count_and_the_new_one_is_returned(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            for name in ("old", "pending", "new"):
                (root / "addin-handoffs" / name).mkdir(parents=True)
            (root / "addin-handoffs/old/completed.json").write_text("{}", encoding="utf-8")
            before = q.completed_handoffs(root)
            self.assertEqual([root / "addin-handoffs/old"], before)
            (root / "addin-handoffs/new/completed.json").write_text("{}", encoding="utf-8")
            self.assertEqual(root / "addin-handoffs/new", q.wait_for_handoff(root, before))

    def test_the_producer_comes_from_the_stored_record_that_hashes_to_its_id(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            data = b'{"report":{"producer":{"assemblySha256":"a","configurationSha256":"c"}}}'
            record = q.hashlib.sha256(data).hexdigest()
            (root / "addin-selections").mkdir()
            (root / "addin-selections" / (record + ".json")).write_bytes(data)
            summary = {"recordId": record, "reportArtifact": {"path": "addin-selections/%s.json" % record}}
            self.assertEqual({"assemblySha256": "a", "configurationSha256": "c"}, q.stored_producer(root, summary, record))
            (root / "addin-selections" / (record + ".json")).write_bytes(data + b" ")
            with self.assertRaises(AssertionError):
                q.stored_producer(root, summary, record)

    def test_waiting_gives_up_after_the_operator_timeout(self):
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(q, "SELECT_TIMEOUT", 0):
            with self.assertRaises(AssertionError):
                q.wait_for_handoff(pathlib.Path(directory), [])


if __name__ == "__main__":
    unittest.main()
