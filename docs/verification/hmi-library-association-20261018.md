# Original library-export associations — 18 October 2026

Status: full offline validation passed. Exact pushed commit and hosted-CI results
are recorded separately in `artifacts/hmi-library-association-01/integration.json`. Base: `6ae0208aed17934be037e1e9906016297cfa2141`.

The existing component review now accepts explicit v5 associations between an
independently retained full V21 FB library-version export and the library-instance
observation at an exact selected saved object path. It reports `match`, `different`
or `unavailable` identifier agreement and lists all selected component paths,
including those without an association. Matching identifiers grant no native
identity, source authenticity, content association, dependency closure or runtime
instance qualification. The imported object can have a different instance name.

The supported profile is grounded in the four unchanged xp01 full/default exports.
Both full exports carry the same GUID pair but have different original bytes;
default exports omit the required GUIDs and refuse this profile. The profile
requires the observed V21 envelope, one direct committed code-block version and
content FB, exact nonempty GUID scalars, internal type/content-name consistency
and unique nonempty export-local IDs. Those IDs never select a saved object.
Other library content formats are not inferred from this fixture.

V5 allows an empty expected-effect rule array, so an association does not require
an unrelated screen edit. Existing supported expected effects can still compose
with associations. Both use the same bounded strict UTF-8 XML reader and repeated
role byte budget. The reader preserves the prior v4 comparison behavior; DTD,
depth, event, encoding, descriptor-integrity and byte-budget refusals remain.

Publication, paging and reopening use the existing immutable store and rebuild
the report from original files and saved reviews. Rehashed invented results and
changed originals refuse. Existing v1-v4 definitions are unchanged. Four actual
prior v4 records and their 18 exact dependency files were retained before this
implementation; they supplement the existing nine v1-v3 records.

Evidence is being retained under `artifacts/hmi-library-association-01/`.
The first actual MCP attempt exposed a malformed synthetic project-root path in
the new test fixture; the fixture was corrected to the existing `/projects/`
contract without weakening the import guard. The failed run remains retained.

The final focused suite passes 64 association checks and the existing 206
component checks. Actual Host execution passes 578 MCP checks over two clean
lifetimes, including six v5 records and five failing independent mutation controls.
The existing expected-effect oracle also runs for v5. All 726 compatibility checks
pass over 13 actual v1-v4 records. Tests include independent before/after scope,
renamed instances, omitted/invalid observations, GUID contradictions, original
full/default exports, strict XML failures, repeated 9 MiB roles and a combined
screen/library 64 MiB budget. Prior schema definitions are unchanged.

Contract/source binding checks pass: 233 tools, 56 mutation kinds and 307 features.
All 112 historical evidence entries remain unchanged; 106 inputs are unavailable.
The full frozen aggregate and relocated package checks pass within the scope below. Complete supported dependency/consumer mapping, broader expected
effects and authored generation remain open. Native SDK and TIA qualification,
genuine FC/UDT/HMI library exports, compile/reopen and runtime acceptance are
unavailable here. This increment does not complete C01 or the original 44-workstream goal.

## Newly reproduced HMI readback defect

A separate audit of existing native `HmiReadback` found repeated version reads
across validation and report construction. The exact extracted production method,
linked production version guard and synthetic providers reproduce three failures:
a mixed type/version pair, `nativeLinkVerified` remaining true after the returned
version changes, and acceptance of a service whose instance is another object.
All three reproduce on both the portable runtime and actual x64 .NET Framework
with the same extracted method and synthetic providers.
Evidence is retained in `artifacts/hmi-readback-preflight-01/` with the exact source
and extracted-method hashes. No Siemens execution is claimed. Repair and regression
verification of this shared helper are the next bug task; the current v5 portable
review change does not repair or qualify that native route.

The same audit also reproduces both Classic prepare/apply library-link guards
treating a present service with a missing version as unlinked. These two checks
execute only the exact extracted guard blocks on portable and actual Framework
runtimes; no full native caller or mutation is executed. The repair must share
the coherent observation with these guards and refuse unknown link state before
preserving import. Five total reproductions and their exact scopes are retained.

## Full frozen verification

Both snapshots contain 2404 identical source files. All 44 managed
suites, 1,510 Python tests (one existing skip), 25 Explorer model/client tests and
Host workflows pass. All 38 package/relocation stages pass. Both executions include
206 existing component checks, 64 library association checks, 578 actual MCP checks,
five independent library mutation controls and 726 prior-version checks over 13
original records. Four independently checked v4 expected-effect records and their
four existing mutation controls are retained as well.

The 1727-file package manifest is
`2e0c0c45c68f7aaf6d0877d41bf67532380b9ffb37717ba8347af5be1f91e319`. Five packaged Explorer assets match the prior
browser-tested assets; no new browser execution is claimed. Only final docs differ
from the frozen source. V1-v4 schema definitions, original scope-reconciliation
files and all historical evidence entries remain unchanged. Native worker/SDK
compilation and native qualification remain unexecuted. These results establish
the described portable review behavior, not full C01 or full 44-workstream completion.

Six original v5 records and their 24 exact dependency files are retained separately
for future compatibility checks. All 132 section comparisons pass across two clean
producer Host lifetimes, with independent original-file/observation checks. This
does not qualify a future implementation or repair the HMI readback defects above.
