# Technology fixture evidence and Git timeout CI - 17 October 2026

The D13 [implementation plan](../plans/expansion-plans/motion-and-technology-objects.md)
now covers the documented family/version matrix, service-owned children, native
connections, couplings, cam data and interpreter source/mappings. Official V21
sources and the current native routes were cross-checked. No Siemens products,
SDK, PLCSIM or PLC were used or installed. This increment repairs the existing
axis/encoder scalar fixture oracle; it does not implement all proposed TO services.

## Reproduced false acceptance

The original oracle compared requested scalars but did not require consistent,
complete before/after parameter and connection observations. It could pass despite
changed peers or metadata, partial reads and contradictory identities. Its language
assertions also disappeared under optimized Python. The retained initial
[negative run](../../artifacts/motion-oracle-red-01.log) has 26 failing subcases in
10 tests against that implementation. Those failures are not acceptance evidence.

A later [name intake reproduction](../../artifacts/motion-name-red-01.log) shows
8 invalid explicit existing names reaching a native callback or falling back to a
generated fixture name. Existing names now require nonblank bounded valid Unicode;
only an absent name selects creation of a generated fixture. Invalid names fail at
profile loading and again before the runtime's first native callback.

## Implemented boundary

The helper retains independent deep-copied engineering and inspection observations,
requires explicit completeness and exact selected type/version/name/session,
checks both parameter views agree, and rejects duplicate or unreadable rows. It
requires known write permission and a declared type before an existing scalar edit.
Only requested parameter values may differ; parameter membership, types/access,
other values, object/PLC identity and observed connections remain exact.

Dependency comparisons preserve JSON value kinds. Requested numeric comparison
uses exact equality except for explicitly declared System.Single precision; it
never accepts Boolean as a number or a broad relative tolerance. Explicit runtime
checks remain active under python -O. A failed observation leaves the evidence row
failed and does not trigger fixture deletion or automatic mutation retry. Successful
creation is followed by deletion only after checks pass, and deletion requires a
complete empty selection from the same project/session.

Creation's baseline is after initialized creation/compile. These checks establish
preservation of returned records only. They do not establish complete original XML,
values excluded or shortened by the display reader, save/reopen, all specialized
connections, native target compatibility or physical behavior. Scalar recipes with
intentional connection side effects need their own independently authored complete
effect oracle. Historical profiles and evidence are not requalified here.

## Hosted CI regression and repair

Hosted run [38996477836](https://github.com/Sparkz400/TIA_MCP/actions/runs/38996477836)
for 011033135bdd58e75d4ca868835580c00a0b94e1 passed Python, package and OPC UA,
but its managed suite failed one of 122 VCI checks. The retained
[failure log](../../artifacts/optext-01/hosted-failure.log) records FormatException
from parsing an empty child.pid file in CheckChildStopped. The 100 ms timeout could
terminate the fixture between file creation and completion of its PID write.

The fixture now writes a temporary receipt and atomically moves the completed file
to child.pid. A stop before publication remains the existing early-startup case;
a published PID is still checked for termination. The production Git runner already
kills and waits for its process in finally; no production behavior or timeout was
relaxed. The [focused suite](../../artifacts/motion-git-01.log) passes 122/122 checks:
84 pure and 38 explicitly selected isolated Git/process checks. The previous hosted
failure remains recorded rather than being replaced by a blind rerun.

## Frozen source and focused checks

Final validation source: artifacts/motion-03/source, tree
20eb5814129cfb2b9c245b504a0bc6439ba7ffb8, 1,937 files.
[Source manifest](../../artifacts/motion-03/source-manifest.json) SHA-256:
2a4337b02266c88bdaf5f43ddde9fa9c85200cb77dc00361fdf922b02a61822d.
Source archive SHA-256:
723aff36028b41a92a77cb5ce5699680e1f0d274a67eed249d23492a8a568453.
Earlier motion-01 and motion-02 snapshots were superseded before aggregate testing;
the first lacks the final name repair and both lack the Git fixture change.

Focused final Python checks:

- [15 normal tests](../../artifacts/motion-oracle-green-05.log).
- [15 optimized-Python tests](../../artifacts/motion-oracle-optimized-05.log).
- [33 existing conformance tests](../../artifacts/motion-conformance-05.log).

Two existing conformance mocks originally omitted native response fields now
required by the oracle; they were corrected to model actual wire responses, without
relaxing the product checks. The intermediate failed run remains in
artifacts/motion-conformance-01.log. Fixtures in these tests are synthetic.

## Full verification and integration

The [frozen aggregate](../../artifacts/motion-03/source/artifacts/checks/result.json)
passes 38 managed suites, 1,295 Python tests (one existing unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all MCP workflows. Its VCI suite
passes all 122 checks, including timeout and cancellation. The run spans
2026-10-17T03:08:10.9064122Z to 2026-10-17T03:18:00.8805252Z; receipt SHA-256:
44c76cae76d10aafd4565afd832a13473a341c65ebaca6ae0eea24e2d89dd7c0.

All 21 [package stages](../../artifacts/motion-03/source/artifacts/package-checks/result.json)
pass, including original and Unicode-path relocated bundle verification and all
existing packaged workflows. Receipt SHA-256:
0175c23ce69da1b791a24a15879f4f88e64fe997c7a61103a8de7cac9108f4a3.
Bundle tia-mcp-offline_20261017_030834_cfc1be8a contains 1,378 manifest files;
manifest SHA-256: 62551516d6798a632d54ffee5ed6a5bfc38d7c047c2a073fcdc4e7dfaad46de9.
ZIP SHA-256: 1c8a9489d78fb0545c820cd840b22476febc963717041416d3c700ffc80e88dd.
Hash consistency establishes integrity, not publisher authentication or native acceptance.

The [final source comparison](../../artifacts/motion-03/final-source-comparison.json)
permits only completion-documentation changes in status, TODO, queue and verification
records. Code, tests, fixtures, scripts and bindings remain identical to the tested
tree. The [integration receipt](../../artifacts/motion-03/integration.json) records
main's commit, push, hosted CI state and cleanup. New-commit hosted acceptance remains
separate from these local checks; the earlier 0110331 managed failure is retained above.

## Remaining scope

The catalog remains 197 tools, 55 mutation kinds and 270 features. All 106 unavailable
historical evidence bindings and hashes remain unchanged. TO-02 supplied profile and
relationship review is the next executable D13 slice. TO-03 through TO-12 and genuine
SDK compilation, native lifecycle/preservation, motion/simulation/physical acceptance
remain open. The full 44-workstream goal is not complete.
