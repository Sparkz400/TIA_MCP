import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import validate_library_observations as validation

FIXTURE = validation.ROOT / "fixtures/libraries/observation-contracts"


class LibraryObservationContracts(unittest.TestCase):
    def setUp(self):
        self.identity = json.loads((FIXTURE / "identity.json").read_bytes())
        self.lock = json.loads((FIXTURE / "lock.json").read_bytes())

    def validate(self, value, kind="lock"):
        return validation.validate_bytes(json.dumps(value).encode(), kind)

    def reject(self, value, kind="lock"):
        with self.assertRaises(ValueError):
            self.validate(value, kind)

    def test_original_bytes_pin_and_no_qualification(self):
        for kind in ("identity", "lock"):
            raw = (FIXTURE / (kind + ".json")).read_bytes()
            result = validation.validate_bytes(raw, kind, hashlib.sha256(raw).hexdigest())
            self.assertEqual(len(raw), result["bytes"])
            self.assertFalse(result["qualificationGranted"])
            self.assertFalse(result["referencedFilesRead"])
            self.assertFalse(result["nativeExecuted"])
            with self.assertRaises(ValueError):
                validation.validate_bytes(raw + b" ", kind, result["sha256"])

    def test_full_string_identity_hash_and_guid_boundaries(self):
        for field in ("libraryId", "sessionGeneration", "revision", "sourceSha256"):
            for suffix in ("\n", "\r\n", " ", "\u2028"):
                value = copy.deepcopy(self.identity)
                value[field] = (value[field] or "a" * 64) + suffix
                with self.subTest(field=field, suffix=repr(suffix)):
                    self.reject(value, "identity")
        for key in ("typeGuid", "versionGuid"):
            value = copy.deepcopy(self.lock)
            value["selectedVersionsAndMasters"][0][key] += "\n"
            self.reject(value)

    def test_pin_wrong_type_or_digest_refuses(self):
        raw = (FIXTURE / "lock.json").read_bytes()
        for pin in ("b" * 64, "f" * 64 + "\n", "A" * 64, ""):
            with self.subTest(pin=pin), self.assertRaises(ValueError):
                validation.validate_bytes(raw, "lock", pin)

    def test_qualification_flags_cannot_be_promoted(self):
        for field, replacement in (("runtimeVerified", True), ("freshReopenVerified", True), ("nativeSaveCompleted", False)):
            value = copy.deepcopy(self.lock); value[field] = replacement
            self.reject(value)
        for field in ("runtimeVerified", "freshReopenVerified", "nativeSaveCompleted"):
            value = copy.deepcopy(self.lock); del value[field]
            self.reject(value)
        value = copy.deepcopy(self.lock); value["persistenceVerified"] = True
        self.reject(value)

    def test_required_identity_fields_and_observation_types(self):
        for field in self.identity:
            value = copy.deepcopy(self.identity); del value[field]
            self.reject(value, "identity")
        for field, replacement in (("processId", True), ("processId", 0), ("observedAtUtc", "yesterday"), ("writable", "true"), ("revision", None)):
            value = copy.deepcopy(self.identity); value[field] = replacement
            self.reject(value, "identity")

    def test_opened_global_identity_retains_explicit_source_flags(self):
        value = copy.deepcopy(self.identity)
        value.update(kind="UserGlobalLibrary", ownedWorkingCopy=True, sourceSnapshotOwned=True,
                     originalSourceModified=False, sourceSha256="c" * 64)
        self.validate(value, "identity")
        for field, replacement in (("sourceSnapshotOwned", False), ("originalSourceModified", True)):
            invalid = copy.deepcopy(value); invalid[field] = replacement
            self.reject(invalid, "identity")

    def test_dates_are_checked_without_optional_format_dependencies(self):
        for date in ("yesterday", "2026-02-30T00:00:00Z", "2026-10-23T24:00:00Z", "2026-10-23T00:00:00", "2026-10-23T00:00:00+00:99", "2026-10-23T00:00:00Z\n"):
            value = copy.deepcopy(self.lock); value["createdUtc"] = date
            with self.subTest(date=date):
                self.reject(value)
        for date in ("2024-02-29T23:59:59.1234567Z", "2026-10-23T02:00:00+02:00"):
            value = copy.deepcopy(self.lock); value["createdUtc"] = date
            self.validate(value)

    def test_dependency_unavailable_is_distinct_from_complete_empty(self):
        root = self.lock["dependencies"][0]["rootVersionGuid"]
        for dependency in ({"rootVersionGuid": root, "versions": []},
                           {"rootVersionGuid": root, "scopeComplete": False, "reason": "Unavailable native dependency read"}):
            value = copy.deepcopy(self.lock); value["dependencies"] = [dependency]
            self.validate(value)
        for dependency in ({"rootVersionGuid": root}, {"rootVersionGuid": root, "scopeComplete": True, "reason": "unknown"},
                           {"rootVersionGuid": root, "versions": [], "scopeComplete": False, "reason": "mixed"}):
            value = copy.deepcopy(self.lock); value["dependencies"] = [dependency]
            self.reject(value)

    def test_native_selection_and_dependency_bounds(self):
        value = copy.deepcopy(self.lock); value["selectedVersionsAndMasters"] *= 101
        self.reject(value)
        value = copy.deepcopy(self.lock); value["dependencies"][0]["versions"] *= 1001
        self.reject(value)
        value = copy.deepcopy(self.lock); value["sources"] *= 33
        self.reject(value)
        value = copy.deepcopy(self.lock); value["selectedVersionsAndMasters"] = [{"masterCopyPath": "/masterCopies/Fixture", "callerNote": "retained"}]
        self.validate(value)

    def test_partial_dependency_version_cannot_disappear(self):
        for field in ("typeGuid", "versionGuid", "requiredVersionGuids", "state", "isDefault"):
            value = copy.deepcopy(self.lock); del value["dependencies"][0]["versions"][0][field]
            self.reject(value)

    def test_opaque_native_payload_does_not_become_qualification(self):
        value = copy.deepcopy(self.lock)
        value["actualResult"] = {"unqualifiedNativeExtension": {"claimedSuccess": True}}
        value["compilation"] = {"errorCount": 0, "messagesComplete": False}
        result = self.validate(value)
        self.assertFalse(result["qualificationGranted"])

    def test_duplicate_keys_invalid_json_and_size_are_refused(self):
        for raw in (b'{"runtimeVerified":false,"runtimeVerified":true}', b'{"x":NaN}', b'\xff', b'{} trailing', b'x' * (validation.MAX_BYTES + 1)):
            with self.subTest(prefix=raw[:20]), self.assertRaises(ValueError):
                validation.validate_bytes(raw, "lock")

    def test_bom_is_hashed_as_original_bytes(self):
        raw = b"\xef\xbb\xbf" + (FIXTURE / "identity.json").read_bytes()
        result = validation.validate_bytes(raw, "identity", hashlib.sha256(raw).hexdigest())
        self.assertEqual(len(raw), result["bytes"])

    def test_cli_validates_without_following_embedded_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "receipt.json"
            value = copy.deepcopy(self.lock); value["journalPath"] = str(Path(directory) / "does-not-exist.json")
            raw = json.dumps(value).encode(); path.write_bytes(raw)
            command = [sys.executable, "-B", str(Path(validation.__file__)), "--kind", "lock", "--path", str(path), "--expected-sha256", hashlib.sha256(raw).hexdigest()]
            completed = subprocess.run(command, capture_output=True, text=True, timeout=30)
            self.assertEqual(0, completed.returncode, completed.stderr)
            self.assertTrue(json.loads(completed.stdout)["valid"])
            self.assertEqual(raw, path.read_bytes())
            path.write_bytes(raw + b" ")
            completed = subprocess.run(command, capture_output=True, text=True, timeout=30)
            self.assertEqual(1, completed.returncode)
            self.assertFalse(json.loads(completed.stdout)["valid"])


if __name__ == "__main__":
    unittest.main()
