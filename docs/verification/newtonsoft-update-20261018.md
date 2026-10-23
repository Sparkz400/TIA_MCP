# Newtonsoft.Json update and Framework fixture repair

The update from 13.0.3 to 13.0.4 covers exactly 29 native worker, harness and
managed-test project references in PR 10,
reviewed at `1e6aab8283e07374c3617686814cd744516039c0`. Each original PR change
is only the package-version replacement. The original PR head is retained as a
merge parent; final integration state is recorded in the local receipt below.

The review found a real validation defect: `Test-AddInOwner.ps1` selected a
hardcoded 13.0.3 DLL. A populated cache could silently test the old dependency,
while a clean cache containing only the newly declared version could fail.
The runner now reads the existing worker's PackageReference version and selects
that exact version from its existing local/global package search paths.

Three regression tests execute the runner's actual PowerShell path-selection
assignments. They cover both versions cached, only the old version cached, and
a clean global cache with a different declared version. All three fail against
the old runner and pass with the fix. Their dummy DLL files test path selection
only; actual assembly execution is checked separately below.

## Validation scope

The frozen aggregate passed all 43 managed suites, including 242 general checks,
and 1,483 Python tests with one pre-existing missing-native-evidence skip. It
also passed the Explorer model/client checks, independent retained-record
checks and all configured actual Host workflows. No browser rerun is claimed
for this dependency increment; the previous browser fixture evidence remains
separate.

The runtime MCP workflows recorded 643 aggregate and 642 package checks. Their
fixed scenario-label counts match the previous run exactly; differences are
status-poll and retained-file checks. The verifier independently reconciles
every retained-file secret check with the actual store files. Both runs pass
29 supplied-selection and 28 production receiver checks; production Add-In
enrollment remains unavailable and positive authority fixtures are synthetic.

The package run passed all 30 stages, including strict relocation, with 1,528
files and manifest SHA-256
`03f286f65402b3337ea361605cc1ca21cffd229f0f4eae6abb9031ca9b332913`.
This verifies retained byte consistency, not a publisher signature.

The actual Windows .NET Framework owner fixture passed. A separate executable
loaded the copied net45 Newtonsoft DLL and round-tripped JSON containing a
64-bit integer, Unicode, null and Boolean array values. Its observed product
version was `13.0.4+4e13299d4b0ec96bd4df9954ef646bd2d1b5bf2a`; its loaded-file
SHA-256 matched the exact restored package:
`f0c07af0e84d4dd4da4bd7823ba4535bc0481b3bf623ed40b659b68147a6bb75`.
The CLR reported `4.0.30319.42000`. These are actual Framework checks, with a
synthetic native-ID route and no Siemens API execution.

Both frozen source snapshots contain the same 2,174-file map, based on
`92e89d26dea1b19aac8d87d8d356d31682f098ef` plus the dependency/runner changes.
The aggregate source-manifest SHA-256 is
`a09a6029e21766cdc6a7232ed601110517c065f215b36919cd4a3890e7c15001`;
the package source-manifest SHA-256 is
`0c7f0eae00fb60ffa121f292586be5ae8496c3359f4f340806af7bce6f3c84c0`.
Only documentation changes after these snapshots. Three worker project hashes
are refreshed in implementation bindings; the original 106 unavailable
historical evidence rows and immutable 44-workstream baseline remain intact.

Evidence is retained under `artifacts/newtonsoft-update-01/`: the PR and official
release records, source manifests, negative-control log, Framework probe,
aggregate/package logs and result files, consolidated `verification.json`, and
post-push `integration.json`. Artifact paths are local evidence, not published
Git objects. [Official release notes](https://github.com/JamesNK/Newtonsoft.Json/releases/tag/13.0.4)
were reviewed; they do not certify this application's Siemens compatibility.

## Remaining work

The [C02/C03 identity contract](../plans/expansion-plans/composite-identity-contracts.md)
defines exact retained mappings and independent engineering/runtime/security
evidence. This increment documents that contract; it does not implement or test
the combined workflows. C02 typed evaluation and verified retained readers are
next. The full 44-workstream goal remains active.

Full affected native-project builds, TIA/PLCSIM/Test Suite runtime loading,
supported product profiles and native harness qualification remain untested.
Hosted CI was billing-blocked at the baseline; exact pushed-head results belong
in the integration receipt. No product installation or native acceptance is
claimed by the dependency update.
