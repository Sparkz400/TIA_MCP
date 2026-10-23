# Technology parameter read boundary - 17 October 2026

`EngineeringParameterInfo` previously read `Value` before sensitive-name exclusion,
accepted the first `Value` metadata declaration and did not check read access.
`technology_read` had a separate value-only path without that exclusion. Prefix
filtering could traverse all parameters without consuming a scan bound.

## Implemented source

`OpennessSession.TechnologyRead.cs` now owns the shared production reader used by
engineering technology reads, technology inspection (including project-model details),
and the post-mutation evidence recorder. The existing sensitive-name predicate moved
unchanged; its HMI/capability/hardware callers retain the same policy.

Sensitive names are excluded before explicit metadata/value getters. Other values
need one exact `Value` declaration with read access. Missing/duplicate metadata,
unreadable values, metadata/getter/renderer failures and supported-type metadata above
64 entries return explicit incomplete states. Native exception messages are not copied
because they may contain values. Actual runtime types and supplied type declarations
are retained separately; null does not imply a writable type. No setters are called.

The existing caller-specific renderers remain: engineering retains full scalar
strings; technology inspection uses its existing 4,096-character display limit.
Finite scalar JValues are complete observations; truncated strings, nonfinite values
and non-scalar representations are display-only. Exclusions, missing/duplicate names,
errors and display-only values cannot produce complete parameter coverage. These are
observed scalar values, not a transactional snapshot or whole-object/native eligibility
proof. Empty collections can be complete within an otherwise complete scope.

The capture helper scans at most 50,000 parameter entries, including prefix exclusions,
and still honors each caller's output budget. Filtered-out values are not read.
Engineering technology inventory reuses the bounded group traversal and records exact
paths, parameter visitation/truncation and scope completeness. Mutation evidence records
exact requested-name matches, incomplete parameter coverage and warnings independently
of the setter's verification result. Existing connection and native XML-export behavior
is unchanged; XML exports are not claimed to be redacted by this JSON read boundary.

The two live fixture harnesses now require explicit complete/readable capture evidence.
The connected-encoder harness also uses the shared parameter-map validator, rejecting
duplicate names instead of silently overwriting them. These are future live-run gates;
historical native artifacts and all 106 unavailable binding rows/hashes stay untouched.
The encoder save/reopen verifier imports that same validator; it now needs a baseline
with explicit capture evidence before launching a worker. Old reports remain historical
and are not rewritten or silently upgraded into fresh qualification.

## Verification

Focused linked-source run (`artifacts/to-read-tests-03.log`): **167/167** general
checks pass, including four new groups for exclusions/access, scalar and error states,
bounded filtered traversal, and incomplete/duplicate identity reporting. Production
routines are compiled with synthetic Siemens API shapes; callback renderers exercise
truncation and rendering errors. The full native inspection renderers and caller wiring
were inspected in source, not executed. The first build failed only because the new
tests used an unavailable assertion helper; the original log remains retained.

Python fixture gates (`artifacts/to-read-python-01.log`): 17 family and nine encoder
tests pass. They refuse explicit incomplete/missing capture flags before or after edits,
even when before/after values match. Synthetic callbacks do not qualify native edits.

Final frozen Git tree: `270ec31fba0804d1f87a7e748572208f76759d21` (1960 files).
Source manifest (`artifacts/to-read-02/source-manifest.json`) SHA-256:
`53310f3c4c8f2ebff4b0b7f93d39c4afb502df739414f7ede82c0a1a47568974`.
Source ZIP SHA-256: `9d7bab3f69a018408bfdd37934bf39320690f408dd9a8847e683d95f1ba1f31b`.

The aggregate (`artifacts/to-read-02/source/artifacts/checks/result.json`)
passes 39 managed suites, 1,299 Python tests (one existing unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all offline MCP workflows. The
general executable repeats all 167 checks and the four new read/capture groups.
Run: 2026-10-17T05:23:42.1616948Z to 2026-10-17T05:33:56.1759143Z.
Receipt SHA-256: `a4417a6e3d9d4eab637b4f73069bf638effeafe3182533ec177d8ccdd811cd8b`.
Console evidence (`artifacts/to-read-02/aggregate-console.log`).

All 22 package stages (`artifacts/to-read-02/source/artifacts/package-checks/result.json`)
pass, including relocation. Bundle `tia-mcp-offline_20261017_052406_cdcd8642` contains 1388
manifest files. Receipt SHA-256: `30dad51e1c93c0e73cef938a3bd22fa8680c6ec8bb924a7804c51e89b2148fed`.
Manifest SHA-256: `fd50140aafbf36993692c579d6c1ff5115bd1cc54b724063335a9eef5cc4b503`.
ZIP SHA-256: `7df7d66fdfbd4f289aea23195ecd64b4d99b60730c28d71eb18248328eb1da97`.
This offline package excludes native workers; it does not compile the Siemens SDK.
Hashes establish retained byte integrity, not publisher or native authenticity.

The source comparison (`artifacts/to-read-02/final-source-comparison.json`)
permits only completion-documentation changes after the frozen tests.
The integration receipt (`artifacts/to-read-02/integration.json`)
records the pushed commit, hosted CI outcome and cleanup.

The first frozen aggregate (`artifacts/to-read-01`) failed two older conformance
fixtures that omitted the new completeness fields. Their synthetic observation helper
now supplies the current fields so those tests again reach dependency refusal and
initialized creation. The corrected conformance file passes all 33 tests. The snapshot comparison (`artifacts/to-read-02/fixture-correction-comparison.json`) confirms that production code did not change for that fixture correction.
The failed aggregate (`artifacts/to-read-01/source/artifacts/checks/result.json`)
and console (`artifacts/to-read-01/aggregate-console.log`) are retained separately.

Three affected read/detail catalog entries are unverified for the changed source,
with historical evidence IDs preserved. Tool/mutation/feature totals remain 199/55/272.
No real Siemens SDK compilation, TIA, PLCSIM, PLC, native exports or runtime tests ran.
Native permission/metadata availability, completeness of service-owned children and
associations, native preservation and recovery remain open. The previously changed
write routes remain unverified too.

## Remaining work

The later [selection repair](technology-selection-20261017.md) addresses the
source-level path/scan gap described below. The following notes retain the scope
of this earlier read-boundary increment.

Next concrete source work: bounded exact-path selection for technology parameter edits
and deletion, including group identity, name/path mismatches and ambiguous legacy names.
RequiredTechnologyObject currently resolves a case-insensitive name through an
unbounded item scan; the read views already expose exact paths. This is part of
TO-03 owner/identity and TO-04 lifecycle work. Continue source-bound metadata/capture
and exact owner/service/eligibility evidence,
then TO-04–12 and the full [44-workstream queue](../plans/offline-execution-20261016.md).
This repair does not enumerate CPU-supported type/version pairs, capture every service
or child composition, add path-based mutation selection or qualify connections/motion.
Reuse the metadata-only public SDK inspector; supplied metadata is not a current-target
capability probe. Hosted CI on base `2528e14` was refused before execution because of
GitHub account payment/spending limits.
