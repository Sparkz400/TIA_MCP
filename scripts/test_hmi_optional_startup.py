"""Pure checks for optional-startup evidence validation; not native qualification."""
import copy
import unittest

from qualify_hmi_optional_startup import verify_absence_response, verify_wrong_version_response, verify_wrong_identity_response, verify_detached_response, record_host_exit, ps_literal


class OptionalStartupEvidenceTests(unittest.TestCase):
    def response(self):
        return {"modules": [{"family": family, "installed": False, "apiAvailability": "unavailable",
                "nativeQualification": "unverified", "license": "notProbed"} for family in ("classic", "unified")],
                "profiles": [{"profile": str(index)} for index in range(6)]}

    def test_both_absent_families_are_required(self):
        verify_absence_response(self.response())
        value = self.response()
        value["modules"].pop()
        with self.assertRaises(AssertionError): verify_absence_response(value)

    def test_absence_cannot_claim_native_or_license_qualification(self):
        for key, value in (("nativeQualification", "qualified"), ("license", "available"),
                           ("installed", True), ("apiAvailability", "available")):
            response = self.response()
            response["modules"][0][key] = value
            with self.assertRaises(AssertionError): verify_absence_response(response)

    def test_reduced_profile_scope_is_rejected(self):
        response = self.response()
        response["profiles"].pop()
        with self.assertRaises(AssertionError): verify_absence_response(response)

    def test_metadata_shell_paths_are_literal(self):
        self.assertEqual(ps_literal("C:/odd' name/$(secret)`x.dll"), "'C:/odd'' name/$(secret)`x.dll'")

    def test_detached_startup_cannot_hide_an_owned_process_or_project(self):
        value = {"attached": False, "ownsPortalProcess": False, "processId": None, "projects": []}
        verify_detached_response(value)
        for key, observed in (("attached", True), ("ownsPortalProcess", True), ("processId", 123), ("projects", [{}])):
            changed = dict(value, **{key: observed})
            with self.assertRaises(AssertionError): verify_detached_response(changed)

    def test_abnormal_shutdown_cannot_leave_the_probe_passed(self):
        for prior in ("passed", "blocked", "failed"):
            for exit_code in (1, -1, None):
                report = {"status": prior}
                record_host_exit(report, exit_code)
                self.assertEqual(report["status"], "failed")
                self.assertEqual(report["statusBeforeShutdownFailure"], prior)
        report = {"status": "blocked"}
        record_host_exit(report, 0)
        self.assertEqual(report["status"], "blocked")

    def test_wrong_version_requires_exact_actionable_unavailable_diagnostic(self):
        response = self.response()
        for row in response["modules"]:
            row["installed"] = True
            row["diagnostic"] = {"code": "HmiVersionMismatch", "requiredMajorVersion": 21, "observedMajorVersion": 20}
        verify_wrong_version_response(response, 20)
        response["modules"][0]["diagnostic"]["observedMajorVersion"] = 21
        with self.assertRaises(AssertionError): verify_wrong_version_response(response, 20)

    def test_v21_wrong_identity_cannot_be_reported_as_available(self):
        response = self.response()
        for row in response["modules"]:
            row["installed"] = True
            row["diagnostic"] = {"code": "HmiAssemblyIdentityMismatch", "observedMajorVersion": 21,
                "requiredAssemblyName": "Siemens.Engineering.WinCC" if row["family"] == "classic" else "Siemens.Engineering.WinCCUnified",
                "observedAssemblyName": "TiaMcp.OptionalStartup.WrongVersionFixture", "requiredPublicKeyToken": "observed-core-token", "observedPublicKeyToken": ""}
        verify_wrong_identity_response(response, "observed-core-token")
        response["modules"][0]["apiAvailability"] = "available"
        with self.assertRaises(AssertionError): verify_wrong_identity_response(response, "observed-core-token")


if __name__ == "__main__":
    unittest.main()
