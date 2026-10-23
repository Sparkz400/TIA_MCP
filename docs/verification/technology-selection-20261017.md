# Technology selection repair — 17 October 2026

Base: `34644b17f0332677d3eeacac5a6b11756557675a`.
No Siemens product, SDK, simulator or PLC is installed on this workstation.

## Change and coverage

Existing-object mutation preview previously cached a native reference but later
resolved a case-insensitive name again. The name scan had no total-item bound;
the fingerprint omitted group paths. This repair binds the resolved path/native
name into the hashed preview and uses it for apply, parameter fingerprinting and
after-evidence. Exact paths disambiguate group-local names; explicit path misses
never fall back to legacy names. Legacy name-only requests require uniqueness.

The existing bounded technology traversal is shared by reads and mutations.
Creation repeats its collision check immediately before dispatch and explicitly
rejects an existing-object path. Deletion verifies exact selected-path absence
following one dispatch. Type/version/path metadata participates in fingerprinting;
requested scalar fingerprints use the shared readable/type-checked helper.
The shared ScanBudget and ChangeText routines were moved without logic changes.

The qualification harness requires both read views to agree on the exact path and
PLC root before edits; native reads, edits and cleanup carry that resolved path.
The three affected mutation catalog qualifications remain unverified, preserving
all historical evidence references. Tool/mutation/feature totals are unchanged.

## Executed portable checks

The focused managed run (`artifacts/to-selection-tests-01.log`) passes all
173 general tests. Six new groups compile the actual production selection,
traversal, binding, deletion and fingerprint routines against synthetic native
shapes. Cases cover escaped/Unicode names, exact case, duplicate names and paths,
missing/mismatched/foreign/malformed selectors, fixed preview paths after moves,
50,000-entry and 64-level boundaries, incomplete absence, no-op/replacement/failed
deletes, failed post-delete scans, no replay and group-sensitive fingerprints.
These shapes do not compile or authenticate the Siemens SDK ABI.

The focused Python run (`artifacts/to-selection-python-01.log`) passes 18
family-oracle and 33 conformance tests. Baseline path/PLC contradictions refuse
before mutation; edit/read/delete callbacks assert the exact resolved path.

Final frozen Git tree: `0dcd6156934ccedaed917aa7df8361b2c2b5db5d` (1964 files).
Source manifest (`artifacts/to-select-01/source-manifest.json`) SHA-256:
`fe9e423df5e49897820a84becf63d79922659b4fa58ee638b1cbfd4121257bb7`.
Source ZIP SHA-256: `717bd91a35ac19234933cc90ad5db0fea9eb618dd5f6cfd798eb8ff5ae12be8a`.

The aggregate (`artifacts/to-select-01/source/artifacts/checks/result.json`)
passes 39 managed suites, 1,300 Python tests (one existing unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all offline MCP workflows. The
general executable repeats all 173 checks and the six new selection groups.
Run: 2026-10-17T05:45:09.1990467Z to 2026-10-17T05:55:22.8024000Z.
Receipt SHA-256: `c7c76a885733e97c9d383e3d59b5cd7a11a04b3418326307d274980b3fe15044`.
Console evidence (`artifacts/to-select-01/aggregate-console.log`).

All 22 package stages (`artifacts/to-select-01/source/artifacts/package-checks/result.json`)
pass, including relocation. Bundle `tia-mcp-offline_20261017_054531_1bffb355` contains 1389
manifest files. Receipt SHA-256: `2e0e5ea6eba6b6f3fc867a57832f9f12646324d874583c75a2eb723bba013c9f`.
Manifest SHA-256: `b1194decc4b10eb0cf3fdab1254af81506fbac8a9f44bf24e9f16fd3450aff76`.
ZIP SHA-256: `dc13ca5a022f55567d2b0885c459ce995acc299f244184c7cf401d4565dd768d`.
This offline package excludes native workers; it does not compile the Siemens SDK.
Hashes establish retained byte integrity, not publisher or native authenticity.

The source comparison (`artifacts/to-select-01/final-source-comparison.json`)
permits only completion-documentation changes after the frozen tests and verifies
all 106 unavailable historical evidence binding rows remain unchanged.
The integration receipt (`artifacts/to-select-01/integration.json`)
records the pushed commit, hosted CI outcome and cleanup.

## Remaining work

The later [child-discovery increment](technology-children-20261017.md) extends the
shared inventory to documented owner-relative service children and repairs live
Explorer expansion. This record retains the earlier group-only selection scope.

This closes the identified source-level selection gap, not TO-03/TO-04 or the
[44-workstream queue](../plans/offline-execution-20261016.md). Next, audit native
service/child and eligibility capture contracts against retained public metadata,
then implement only supportable offline portions of TO-04–12. Whole-object and
consumer preservation, durable native identity, runtime and motion acceptance
remain open. No native SDK build, TIA/PLCSIM/PLC operation, compile, recovery or
physical test ran. Metadata paths cannot prove identity of an indistinguishable
replacement. Hosted CI on base `34644b1` was refused before steps because GitHub
reported account payment/spending limits.
