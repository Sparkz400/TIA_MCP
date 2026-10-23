# TO consumers and saved impact context - 17 October 2026

The v3 [workflow](../reference/technology-profile-review.md#declared-consumers-and-saved-impact-context-v3)
adds before/after parameter/relationship consumer associations and reuses the existing
bounded incoming saved-review dependency traversal. It never inserts supplied links
as captured graph edges. Both phases use the same saved review; paths are labelled
supplied context roots, not native changes or runtime effects. Exact identities,
missing subjects, unknown members, scope exclusions and traversal limits remain explicit.

No new tools, mutation kinds, dependencies or native operations were added. Catalog:
199 tools, 55 mutations, 272 features. All 106 unavailable historical evidence rows
and hashes are preserved; no qualification status is promoted.

## Focused evidence

Managed checks (`artifacts/to-consumers-tests-03/verification.json`): 205 passed.
Coverage includes exact Unicode/case/ID matching, broken subjects, missing/ambiguous/
mismatched/out-of-scope/virtual/unknown-scope nodes; both phases and changed/absent
associations; no inferred members/links; incoming transitive paths, diamonds, cycles,
unsupported/unresolved evidence, depth/path/edge/byte bounds, maximum 512 consumer
rows with deduplicated roots, unknown declarations, immutable inputs, v1/v2 receipts,
upload release, reopening and rehashed consumer/impact/version/authority forgeries.
Ambiguity lookup reads at most two matching nodes; a 10,000-duplicate-ID case verifies
all ambiguous peers are excluded without choosing an arbitrary node. The earlier
203-check run and first frozen package pass remain retained development evidence.

Actual MCP (`artifacts/to-consumers-mcp-02/verification.json`): 125 passed across
two clean Host lifetimes. Requests/records/pages validate against all three contract
versions. Tests upload/import the exact saved graph, page all v3 sections, compose
sources and consumers, release every upload, restart and reopen, and refuse invalid
intake/corruption/rehashed reports without modifying retained workflow bytes.
Native worker paths deliberately name absent executables. Host build: no warnings/errors.

Previous/current actual Host compatibility (`artifacts/to-consumers-compatibility-02.json`)
opens three historical v1/v2 records from the previous increment with both Host versions.
Receipts and all retained saved/workflow files remain identical; both processes exit
cleanly without native workers. Production source is unchanged since the focused Host build.

## Aggregate and package validation

Final source: artifacts/to-consumers-02/source, Git tree
54a334aadba734269549bd20b820c7cbea51ce25; 1950 tracked files.
Source manifest (`artifacts/to-consumers-02/source-manifest.json`) SHA-256:
39b472101053eae9820953f2c2a0c3575c971360643c3ed26f74256af91086ea.
Source ZIP SHA-256: 3324dbf819a6ae8812eb262ba232af79f39ef3a7fd20427b7ab689deeb9f45b4.

The aggregate (`artifacts/to-consumers-02/source/artifacts/checks/result.json`)
passes 39 managed suites, 1,295 Python tests (one existing unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all actual MCP workflows.
The TO workflow passes 205 managed and 125 actual MCP checks.
Run: 2026-10-17T04:20:41.6540845Z to 2026-10-17T04:31:01.5791605Z.
Receipt SHA-256: bef499e6ebc01d5afd6f5836cbc9a4e37130fc725bd236b84ba6523b2efd5b1d.
Console log (`artifacts/to-consumers-02/aggregate-console.log`) retains test output.

All 22 package stages (`artifacts/to-consumers-02/source/artifacts/package-checks/result.json`)
pass, including the 205 managed/125 MCP technology checks after Unicode relocation.
Receipt SHA-256: 072cdd8f620837883adbd2fb93c8ab9770dea1b709b500b15cb237979b2665bf.
Bundle tia-mcp-offline_20261017_042105_c1c63d9e: 1385 manifest files;
manifest SHA-256: dc72857e453a05b57b39e569b6156608832b74435ea909e544dfc2233e504715.
ZIP SHA-256: d055d65b28d09267eb6a8fa83b7d3ac9e75d13e3c0c3e56ef23d107f1fbac489.
Hashes establish integrity, not publisher authenticity or native qualification.

The earlier to-consumers-01 source and its validation are retained development evidence;
the final snapshot includes the bounded duplicate-ID lookup and its regression case.
The final source comparison (`artifacts/to-consumers-02/final-source-comparison.json`)
permits only completion-documentation changes after the tested snapshot. The
integration receipt (`artifacts/to-consumers-02/integration.json`) records the pushed
commit, its separately observed hosted CI state, and cleanup.

## Remaining acceptance

The offline supplied-consumer/source/impact handoff in TO-02 is implemented. No genuine
native format, member existence, consumer-link semantics, endpoint freshness, cross-PLC
impact, complete graph coverage, TO compatibility, SDK execution or motion is certified.
TO-03–12 and the full 44-workstream queue remain open. Siemens products were neither
installed nor used. Source base ce89c45 hosted jobs were refused before any step because
GitHub reported account payment/spending limits; local results are separate evidence.
