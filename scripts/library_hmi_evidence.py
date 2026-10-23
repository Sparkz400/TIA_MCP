"""Independent acceptance rules for the deliberately narrow native HMI foundation probe."""

def assess(report):
    stages = report.get("stages", [])
    passed = {row.get("name") for row in stages if row.get("status") == "passed"}
    family = report.get("profile", {}).get("family")
    common = {"start-owned-tia", "create-owned-project", "catalog", "create-explicit-hmi-devices",
              "save-project", "close-owned-project", "reopen-owned-project", "verify-reopened-fixture",
              "archive-owned-project", "dispose-owned-portal-connection"}
    required = common | ({"export-native-classic-screen", "author-classic-screen-master",
                          "instantiate-classic-screen-master", "compile-classic-consumer"}
                         if family == "classic" else {"create-unified-screen-and-tag", "compile-unified"})
    failures = [row for row in stages if row.get("status") != "passed"]
    missing = sorted(required - passed)
    compiled = report.get("compile", {})
    foundation = (family in ("classic", "unified") and report.get("completed") is True
                  and not failures and not missing and compiled.get("errors") == 0
                  and report.get("persistenceVerified") is True)
    # The fixture does not run faceplates, library updates, recovery, visual or runtime assertions.
    # Therefore those gates are false even if an input report incorrectly claims otherwise.
    return {"family": family, "foundationVerified": foundation, "missingStages": missing,
            "failedStages": [row.get("name") for row in failures], "faceplateVerified": False,
            "recoveryVerified": False, "visualVerified": False, "runtimeVerified": False,
            "fullLibraryHmiAccepted": False}
