import hashlib
import unittest

from preview_payload import preview_payload


class PreviewPayloadTests(unittest.TestCase):
    def test_persisted_text_is_shown_by_length_and_hash(self):
        payload = {"language": "SCL", "source": "FUNCTION Ä : Void\nEND_FUNCTION", "expectedBlockNames": ["Ä"]}
        shown = preview_payload(payload)
        data = payload["source"].encode("utf-8")
        self.assertEqual({"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}, shown["source"])
        self.assertEqual(payload["expectedBlockNames"], shown["expectedBlockNames"])
        self.assertIsInstance(payload["source"], str)

    def test_other_payloads_are_unchanged(self):
        payload = {"blockPath": "/x", "source": {"libraryId": "l"}}
        self.assertEqual(payload, preview_payload(payload))


if __name__ == "__main__":
    unittest.main()
