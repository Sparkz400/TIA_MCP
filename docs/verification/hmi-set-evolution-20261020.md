# C01 explicit HMI set evolution — 2026-10-20

The compatible v2 workflow is implemented and qualified offline for explicit
Bool/integer output additions, removals, renames and same-type member rebinding.
It accounts for the exact predecessor and current declared scope, compares
baseline/current/proposed bytes, retains mixed v1/v2 lineage, and suppresses
candidate publication on conflict. Removals and renames remain effect previews;
no TIA, native worker, runtime connection or physical operation was invoked.

Contract and boundaries:
[set evolution reference](../reference/unified-text-list-set-evolution.md),
[`unified-text-list-set.v2.schema.json`](../../contracts/unified-text-list-set.v2.schema.json).
The existing v1 contract and original v1/v2/v3 single-output records remain
unchanged.

## Evidence

Evidence root: `artifacts/c01-evolution-wave4-20261020`.

| Verification | Result | Evidence |
| --- | --- | --- |
| Final isolated managed/Host builds | 0 warnings, 0 errors | `build-02`, `host-build-02` |
| Final managed evolution | 213 checks; 45 cases, 17 ready and 28 conflicts | `managed-03/text-evolution/verification.json` |
| Existing coordinated suite on final managed binary | 147 checks | `managed-03-console.log` |
| Full HMI managed regression before the final removal-property guard | 208 evolution checks, 147 coordinated, 315 numeric, 71 Bool, 206 review, existing association/authoring suites | `full-managed-01` |
| Final actual MCP | 362 checks, 2 clean offline Host lifetimes | `mcp-02/verification.json` |
| Independent original/output oracle | Every final fixture; 15 deliberate mutations refused | `mcp-02/verification.json` |
| Complete mixed-lineage MCP export | 38 exact downloaded files, 219,981 bytes; independently verified without the original store | `mcp-02/lineage-export`, `mcp-02/verification.json` |
| Actual prior-producer v1 sets | 142 checks; 2 original sets, original child authors, 30 original files; v1→v2→v2 evolution and fresh restart | `prior-02/verification.json` |
| Actual prior v1/v2/v3 authors | 188 checks; 5 original records and 34 original files | `prior-authors-02/verification.json` |
| Deterministic interrupted publication | 0 completed sets added; 414 existing files unchanged; 1 real immutable orphan candidate | `managed-03/text-evolution/publication-failure.json` |

The final removal guard requires the predecessor binding ID and property, in
addition to the already exact member/type and file pairs. It is exercised by a
new retained conflict in the final managed and actual MCP runs. Earlier
development evidence remains retained; the final rows identify the current
implementation.

## Exercised transitions and conflicts

Successful cases include v1-to-v2 unchanged evolution; added Bool and integer
outputs; output/file/list/library renames through a mixed chain; subsequent
regeneration from independently captured renamed observations; individual and
remove-all previews; addition after an empty predecessor; explicit Bool and
integer rebinding; subsequent exact rebound regeneration; whole-set
file/list/library swaps; case-only own-name changes; and exact independently
supplied proposed pairs.

Conflicts include omitted predecessor/current outputs, unaccounted peers and
bindings, manual ownership/current drift, incomplete or missing baseline/current
pairs, stale before member/component selections, conversion requests, implicit
rename/rebind, fake unchanged rebinding, asymmetric language order, filename/
library/list case collisions, peer collisions, captured unselected-name
collisions, required unavailable dependencies, and supplied proposed drift.
An addition cannot hide a captured sibling collision through the set of paths
that other outputs intend to vacate. Removal cannot silently change the
predecessor property. Empty predecessors do not bypass component lineage.

Every conflict case verifies empty published `files` and unchanged candidate/
author artifact sets. Dedicated manual and proposed-conflict cases contain
fresh candidate content that was not previously published. All original
baseline/current/proposed descriptors, relevant gaps and effect previews remain
available for review.

## Independent output and retention checks

The Python oracle reads original request bytes and saved project XML, derives
the exact selected before/after declaration types, reconstructs bounded member
coverage, checks original dependency observations and saved consumer evidence,
and compares every candidate YAML tree with independent template expectations.
It checks every output's exact predecessor/current mapping, fixed profile/type,
explicit binding action, original peer bytes, proposed comparisons, effect
preview and filename transfer.

Eleven result controls alter lineage, scope accounting, baselines, candidate
ownership, member selections, binding action, translations, language order,
effect previews, native-applicability flags and member coverage. Four additional
controls encode valid replacement YAML and honestly recalculate every affected
candidate descriptor/hash. They change a translation, language order, numeric
range and text reference. The independent original-intent oracle rejects all
15 controls.

After upload release and a fresh Host restart, the MCP qualifier reopens every
case exactly. In an isolated store copy it alters and removes candidates,
requests, peers, current sources, predecessor records, original v1 child
authors, author inputs and saved project reviews. Each affected reopen refuses.
Honestly rehashed derived set fields, removed-output omissions, review/input
chains and fabricated predecessor intent also refuse. Restored original bytes
reopen exactly.

The mixed-lineage export is downloaded through actual `artifact_read` calls.
The independent checker then verifies the chain using only those downloaded
files, including original v1 child authors and retained source evidence.

The publication control injects an empty directory at the next retained-input
destination after the request has been prepared. A genuinely new candidate is
published before that destination fails. No final set record appears, and all
414 pre-existing file bytes remain unchanged. After removing only the injected
empty directory, one explicitly planned resubmission of the unchanged request
succeeds. This verifies a local publication-failure boundary, not a native
transaction or a process-kill recovery claim.

## Actual prior producer

`fixtures/hmi-evolution/retained-v1-sets` contains 30 exact files copied from the
successful `pw20w3s1` packaged MCP store. Its manifest binds the original producer
verification and Host/Core hashes. It includes the actual initial and regenerated
v1 set records, their original reviews, source bytes, child authors and pairs.
It is not a new producer writing an old schema number.

Two clean new Host lifetimes return the exact original set and child-author
receipts. The qualifier explicitly creates v2 unchanged evolution from the
actual regenerated v1 record, then a v2 rename, releases uploads and reopens both
through the second Host. All copied and source fixture bytes remain unchanged.
The separate existing actual-author qualifier also passes all five prior
v1/v2/v3 records against the final Host.

## Reproduction and integration

Use fresh output directories. The default HMI managed runner invokes evolution;
`--set-evolution` is only a focused convenience and first produces its v1 source
fixtures.

```powershell
dotnet build tests/TiaMcp.HmiComponents.Tests/TiaMcp.HmiComponents.Tests.csproj -c Release --artifacts-path artifacts/c01-evolution-wave4-20261020/build-02
dotnet build src/TiaMcp.Host/TiaMcp.Host.csproj -c Release --artifacts-path artifacts/c01-evolution-wave4-20261020/host-build-02
dotnet artifacts/c01-evolution-wave4-20261020/build-02/bin/TiaMcp.HmiComponents.Tests/release/TiaMcp.HmiComponents.Tests.dll --set-evolution artifacts/c01-evolution-wave4-20261020/managed-03
python -B scripts/test_hmi_set_evolution_host.py --host artifacts/c01-evolution-wave4-20261020/host-build-02/bin/TiaMcp.Host/release/TiaMcp.Host.exe --fixture artifacts/c01-evolution-wave4-20261020/managed-03/text-evolution/mcp-fixture.json --output artifacts/c01-evolution-wave4-20261020/mcp-02
python -B scripts/qualify_hmi_set_evolution_prior.py --host artifacts/c01-evolution-wave4-20261020/host-build-02/bin/TiaMcp.Host/release/TiaMcp.Host.exe --output artifacts/c01-evolution-wave4-20261020/prior-02
python -B scripts/qualify_hmi_coordinated_prior.py --host artifacts/c01-evolution-wave4-20261020/host-build-02/bin/TiaMcp.Host/release/TiaMcp.Host.exe --output artifacts/c01-evolution-wave4-20261020/prior-authors-02
```

Global runners use `<HmiComponents fixture root>/text-evolution/mcp-fixture.json`.
The new prior qualifier defaults to the portable actual-producer fixture.
The two new commands form one package stage. Global catalog, registry, bindings,
aggregate runners and the final frozen package are coordinated separately.

| Artifact | SHA256 |
| --- | --- |
| Final Host executable | `7d82b4775930cb3b3dec415071665388c3bc52bb9cf3520805bebf7cc35e95ba` |
| Final Host DLL | `6cc89e035dd2b7a09301230fc88bad816a56660061e9531f824c82a497448a46` |
| Final Core DLL | `063b920bcc45b14a8a1afe6ff4cb790400aac282b42d6060c7a470c9aef53bf6` |
| Final managed evolution | `5f190932513a73bfa0b21b09a628c5140b2a6fd08e94a36be69f901d72425c7f` |
| Final actual MCP | `597b893bdf3cf8642bc2bc3e1ae33ce08f886d1e3a4ce92ea11ee44bffc04e60` |
| Actual prior sets | `e8849232e968ada2d631bd5484ce20199ed148a6b5c421afaf9b2e04067f170c` |
| Actual prior authors | `3665e3ce325f5bd47ae8b07f30139e941b559f82fae4b527bbfa894e56f64d8b` |
| Interrupted publication | `d6b743e48a56bda5c3f0c26a7fdb0abd3db566aee0f51facc763d1015439c728` |
| Actual v1-set fixture manifest | `f9df260f7092b649abdb52167fbd1158ede3da8d17e43fd945a1cafc2fd05ff7` |

## Remaining boundaries

Retained peers are fixed; unsupported resource formats, manual merges, type
conversion and native execution remain outside this increment. Coverage is
complete only for the explicitly declared predecessor/current set. Truncated
consumer/dependency observations, unselected members and unobserved project
content never become global completeness, ownership, source authentication or
post-import preservation claims. These local results do not assert that the
subsequent frozen aggregate or package run has completed.
