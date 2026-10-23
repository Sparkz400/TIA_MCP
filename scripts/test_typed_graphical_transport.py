"""Transport-only checks; these do not qualify native engineering or a guest."""
import base64
import hashlib
import unittest

from typed_graphical_transport import artifact_bytes
from qualify_typed_graphical import read_artifact, read_graph


class ArtifactTransferTests(unittest.TestCase):
    def transfer(self, raw, alter=None):
        manifest = {"path": r"C:\GuestOnly\prepared.xml", "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}
        calls = []
        def call(name, arguments):
            self.assertEqual("artifact_read", name)
            self.assertEqual(manifest["path"], arguments["path"])
            self.assertEqual(65536, arguments["count"])
            offset = arguments["offset"]
            chunk = raw[offset:offset + arguments["count"]]
            value = {"offset": offset, "encoding": "base64", "data": base64.b64encode(chunk).decode(),
                     "bytes": len(raw), "sha256": manifest["sha256"], "count": len(chunk),
                     "nextOffset": offset + len(chunk), "complete": offset + len(chunk) == len(raw)}
            calls.append(arguments)
            if alter:
                alter(value, len(calls))
            return value
        return call, manifest, calls

    def test_remote_path_chunked_bytes_and_xml_bom(self):
        raw = b"\xef\xbb\xbf<root>" + ("Überprüfung" * 12000).encode() + b"</root>"
        call, manifest, calls = self.transfer(raw)
        self.assertEqual(raw.decode("utf-8-sig"), read_artifact(manifest, call))
        self.assertGreater(len(calls), 1)
        self.assertEqual([65536 * n for n in range(len(calls))], [c["offset"] for c in calls])

    def test_changed_hash_mid_transfer(self):
        call, manifest, calls = self.transfer(b"x" * 70000, lambda value, n: value.update(sha256="0" * 64) if n == 2 else None)
        with self.assertRaisesRegex(ValueError, "changed"):
            artifact_bytes(call, manifest)
        self.assertEqual(2, len(calls))

    def test_wrong_complete_hash_rejected(self):
        call, manifest, _ = self.transfer(b"valid", lambda value, _: value.update(data=base64.b64encode(b"wrong").decode()))
        with self.assertRaisesRegex(ValueError, "complete hash"):
            artifact_bytes(call, manifest)

    def test_offset_alias_rejected(self):
        call, manifest, _ = self.transfer(b"x", lambda value, _: value.update(nextOffset=2))
        with self.assertRaisesRegex(ValueError, "boundaries"):
            artifact_bytes(call, manifest)

    def test_premature_completion_rejected(self):
        call, manifest, _ = self.transfer(b"x" * 70000, lambda value, _: value.update(complete=True))
        with self.assertRaisesRegex(ValueError, "complete hash"):
            artifact_bytes(call, manifest)

    def test_manifest_bounds_before_transfer(self):
        call, manifest, calls = self.transfer(b"x")
        manifest["bytes"] = 16 * 1024 * 1024 + 1
        with self.assertRaisesRegex(ValueError, "bounded"):
            artifact_bytes(call, manifest)
        self.assertEqual([], calls)

    def test_guest_graph_uses_server_artifact_root(self):
        calls = []
        read_graph(lambda name, args: calls.append((name, args)), "exact/block", None)
        self.assertEqual([("engineering_read", {"selectors": {"scope": "graphical", "blockPath": "exact/block"}})], calls)


if __name__ == "__main__":
    unittest.main()
