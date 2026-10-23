"""Pure ownership and evidence guards for the native index qualification runner."""
import pathlib
import unittest

from qualify_project_index import generated_functions, source_for, validate_exhaustive, validate_owned_status, validate_page


class IndexQualifierGuards(unittest.TestCase):
    def test_generated_source_is_bounded_to_fixture_names(self):
        names = generated_functions(250, "TiaMcp_Index_test")
        self.assertEqual(250, len(set(names)))
        source = source_for(names[:100])
        self.assertEqual(100, source.count('FUNCTION "'))
        with self.assertRaises(AssertionError):
            source_for(names[:101])
        with self.assertRaises(AssertionError):
            source_for(["CustomerBlock"])
        with self.assertRaises(AssertionError):
            source_for([names[0], names[0]])

    def test_project_mutation_requires_exact_pid_and_copy(self):
        path = pathlib.Path("fixture/TiaMcpScratch_test.ap21").resolve()
        status = {"attached": True, "processId": 123, "ownsPortalProcess": True, "projects": [{"path": str(path)}]}
        validate_owned_status(status, 123, path, True)
        with self.assertRaises(AssertionError):
            validate_owned_status(status, 124, path)
        with self.assertRaises(AssertionError):
            validate_owned_status(status, 123, path.parent / "another.ap21")
        status["projects"].append({"path": str(path.parent / "extra.ap21")})
        with self.assertRaises(AssertionError):
            validate_owned_status(status, 123, path)

    def test_new_instance_requires_owned_empty_session(self):
        status = {"attached": True, "processId": 123, "ownsPortalProcess": False, "projects": []}
        with self.assertRaises(AssertionError):
            validate_owned_status(status, 123, require_owner=True)
        status["ownsPortalProcess"] = True
        validate_owned_status(status, 123, require_owner=True)
        status["projects"] = [{"path": "existing.ap21"}]
        with self.assertRaises(AssertionError):
            validate_owned_status(status, 123, require_owner=True)

    def test_pages_must_be_unique_exhaustive_and_sorted(self):
        validate_exhaustive([{"path": "/a"}, {"path": "/b"}], 2)
        for rows, count in (([{"path": "/a"}, {"path": "/a"}], 2),
                            ([{"path": "/b"}, {"path": "/a"}], 2),
                            ([{"path": "/a"}], 2)):
            with self.assertRaises(AssertionError):
                validate_exhaustive(rows, count)

    def page(self):
        return {"indexId": "one", "offset": 0, "freshness": "snapshotOnly", "valid": True,
                "totalMatches": 2, "rows": [{"path": "/a"}], "nextCursor": "next", "rowPayloadBytes": 20}

    def test_page_identity_and_total_must_be_stable(self):
        page = self.page()
        self.assertEqual(2, validate_page(page, 0, "one", 2))
        for offset, identity, total in ((1, "one", 2), (0, "two", 2), (0, "one", 3)):
            with self.assertRaises(AssertionError):
                validate_page(page, offset, identity, total)

    def test_unverified_query_must_not_claim_native_freshness(self):
        page = self.page()
        page["freshness"] = "metadataVerifiedAtResponse"
        with self.assertRaises(AssertionError):
            validate_page(page, 0, "one")

    def test_empty_continuation_cannot_hide_infinite_paging(self):
        page = self.page()
        page["rows"] = []
        with self.assertRaises(AssertionError):
            validate_page(page, 0, "one")

    def test_page_payload_bound_is_enforced(self):
        page = self.page()
        page["rowPayloadBytes"] = 512 * 1024 + 1
        with self.assertRaises(AssertionError):
            validate_page(page, 0, "one")


if __name__ == "__main__":
    unittest.main()
