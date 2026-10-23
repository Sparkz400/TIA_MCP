# C01 coordinated paired HMI generation — 2026-10-20

The bounded coordinated workflow is implemented and qualified offline. It
generates 2–16 explicitly mapped paired Bool/integer text lists from one retained
Unified v7 review, accounts for every declared output and binding, and retains
one completion record. A conflict in any selected output suppresses the entire
candidate set. It preserves actual v1/v2/v3 author records and their original
bytes. No TIA, native worker, runtime connection or physical PLC was used.

Contract and supported boundaries:
[coordinated generation reference](../reference/unified-coordinated-generation.md)
and [`unified-text-list-set.v1.schema.json`](../../contracts/unified-text-list-set.v1.schema.json).

## Evidence

Evidence root: `artifacts/c01-coordinated-wave3-20261020`.

| Verification | Result | Retained evidence |
| --- | --- | --- |
| Isolated Host build | 0 warnings, 0 errors | `build-02` |
| Full HMI managed runner | Passed, including 147 coordinated checks, 315 numeric, 71 Boolean, 206 review, plus existing association/authoring suites | `full-managed-01` |
| Coordinated fixtures | 25 cases: 11 ready sets, 14 retained conflicts | `full-managed-01/text-coordinated/mcp-fixture.json` |
| Independent source/output oracle | Every case; 7 deliberate mutations refused | `mcp-01/verification.json` |
| Actual coordinated MCP | 175 checks; 2 clean offline Host lifetimes | `mcp-01/verification.json` |
| Actual prior-producer authors | 188 checks; 5 original v1/v2/v3 records, 34 original files, 2 fresh Host lifetimes | `prior-01/verification.json` |
| Deterministic partial-publication failure | `PackageChanged`; all 200 pre-existing store files unchanged; 0 completed sets published | `full-managed-01/text-coordinated/publication-failure.json` |

The full managed runner invokes coordinated fixtures by default. The narrow
`--coordinated-generation` argument is a convenience, not the only execution
path. Earlier development directories are retained; the table identifies the
completed evidence.

## What was exercised

Successful cases include mixed Bool/integer initial generation, consistent
language reordering, translated Bool updates, numeric state range changes,
unchanged regeneration, explicit adoption of existing v1 or v2/v3 author pairs,
an observed empty dependency list, and 16 coordinated outputs producing exactly
32 files. Selected manual peers remain byte-exact.

Conflicts include generated-current manual edits, missing numeric companion
files, manual ownership, source type disagreement, unused bindings, an omitted
retained output, asymmetric language order/default, case-insensitive generated
filename/library collisions, peer filename collision, required-but-unavailable
dependency observations, required-but-incomplete numeric domains, and original
dependency contradictions affecting both Bool and numeric outputs. Every
conflict case verifies empty candidates and null child IDs; the MCP harness
also verifies no new child author or candidate was published.

Coverage fixtures retain unselected captured members, the 4,096-observation
bound, the 16-level member-depth bound, truncated saved consumer paths and their
unobserved dependency scopes. Complete declared-output/binding coverage never
sets complete project/member/consumer coverage or dependency closure to true.

The Python oracle reconstructs selections from original review input, checks
original project XML member paths/types and saved graph edges, validates original
dependency indices, compares every generated YAML tree with independent
template expectations, and checks exact peer bytes, language order and
translation values. It rejects seven controls: promoted completeness, omitted
output, omitted unselected declaration, invented dependency coverage, changed
language ordering, changed translation, and swapped candidate ownership.

After a fresh Host restart, the MCP harness independently alters and removes
each retained candidate, input, peer, child author and reviewed source in an
isolated copy, then restores the original bytes. Every affected reopen refuses.
Honestly rehashed aggregate fields and altered original/derived review chains
also refuse. Exact original sets still reopen after restoring the controls.

The publication control blocks the second child's retained-input destination
with an empty directory. The first child has already published, so the test
observes real unreferenced artifacts and a temporary file. `PackageChanged`
prevents the completion record, and all prior bytes remain unchanged. After
releasing the injected block, one explicitly planned resubmission of the exact
unchanged request succeeds. This qualifies the local publication-failure
boundary; it is not a native import transaction or a process-kill claim.

The actual-prior fixture at
`fixtures/hmi-coordinated/retained-v1-v3` was copied from the earlier frozen
`pw20w2s2` packaged MCP stores, before coordinated implementation. Its manifest
records the original producer bundle, source and binary hashes. It includes
v1 authored bytes, initial and regenerated v2 Bool records, and initial and
regenerated v3 numeric records. New Hosts return their exact original receipts
and preserve all original stored bytes. These are actual prior-producer records,
not newly generated records merely carrying an old schema number.

## Reproduction

Use fresh output directories. These commands run only offline Hosts and
synthetic/supplied retained evidence:

```powershell
dotnet build src/TiaMcp.Host/TiaMcp.Host.csproj -c Release --artifacts-path artifacts/c01-coordinated-wave3-20261020/build-02
dotnet run --project tests/TiaMcp.HmiComponents.Tests/TiaMcp.HmiComponents.Tests.csproj -c Release --artifacts-path artifacts/c01-coordinated-wave3-20261020/test-build-01 -- artifacts/c01-coordinated-wave3-20261020/full-managed-01
python -B scripts/test_hmi_coordinated_generation_host.py --host artifacts/c01-coordinated-wave3-20261020/build-02/bin/TiaMcp.Host/release/TiaMcp.Host.exe --fixture artifacts/c01-coordinated-wave3-20261020/full-managed-01/text-coordinated/mcp-fixture.json --output artifacts/c01-coordinated-wave3-20261020/mcp-01
python -B scripts/qualify_hmi_coordinated_prior.py --host artifacts/c01-coordinated-wave3-20261020/build-02/bin/TiaMcp.Host/release/TiaMcp.Host.exe --output artifacts/c01-coordinated-wave3-20261020/prior-01
```

For global runners, use `<HmiComponents fixture root>/text-coordinated/mcp-fixture.json`.
The prior command defaults to the committed actual-producer fixture and needs
no pre-existing local artifact store.

## Hashes and remaining limits

| Artifact | SHA256 |
| --- | --- |
| Host executable | `7d82b4775930cb3b3dec415071665388c3bc52bb9cf3520805bebf7cc35e95ba` |
| Host DLL | `e54dbdecfcfc74456f117d38b0058c5d39f3e4eab6a3a94be9d9a8278887c4ca` |
| Core DLL | `668464839759f13c72cf71631ffc7b214b29eeb7fbb7d87c037a1a2edabfbe5a` |
| Coordinated managed verification | `907a43e77def80a4f257688711be3dfcd51bfda4a2f1230402040b965009e93a` |
| Actual MCP verification | `4f960870fc161e90039a9a4dd6fca67bfe72505d4c798f36002fcbae5c00cc84` |
| Actual-prior verification | `9e233acb9e1c7c41c226ad8dca11273f0110872894d31a1d70b30cc0932ca091` |
| Partial-publication evidence | `4520b031c8bc5ca0976d414e55f9cec5572cca6d555431ee3eed13e1f96b6bd0` |

Output additions/removals/renames, changed binding/profile/file/list/library
identities, implicit numeric type conversion, partial-set regeneration and
bindings on retained peers remain unsupported by this version. Existing plain
paired YAML formats are reused; no native import, post-import preservation,
authenticated source, complete consumer/member/dependency closure or runtime
semantics have been qualified. Native quarantine remains unchanged.
