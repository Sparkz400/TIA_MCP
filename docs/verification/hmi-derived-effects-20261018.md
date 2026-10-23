# Derived HMI expected effects — 18 October 2026

Status: full offline aggregate/package verification passed. Integration is recorded
in `artifacts/hmi-derived-effects-01/integration.json` when pushed. Base: `3921077170f24db0559e2e25184e59ce65629004`.
No Siemens installation, native execution or new qualification. The complete
44-workstream goal remains open.

The existing C01 review gains an explicit v4 expectation for the established
Classic screen BackColor preservation rule. It derives the expected document
from retained current bytes and a guarded scalar change, then checks proposed
and observed files independently. Equal supplied files with an unintended name,
peer property, comment, declaration or timestamp change now remain detectable.
Coverage identifies unassessed companion files; library/version/dependency
identity, complete consumers and native generation remain separate obligations.
The [reference](../reference/hmi-component-review.md#derived-expected-effects-v4)
defines exact input fields, bounds and interpretation.

The Core workflow compiles the same existing pure HMI preservation and recipe
rules used by the native adapter. No second native adapter, version manager,
arbitrary XML patch language or script evaluator was introduced. Historical
v1-v3 review reports continue through their original implementations.

## Reproduced declaration omission

`artifacts/hmi-derived-effects-01/declaration-before.log` records a failing
regression against the old preservation helper: `XNode.DeepEquals` did not
compare the XML declaration. The helper now also checks its presence and
version/encoding/standalone fields. The final guard suite passes 19 checks,
including the new exact and normalized comparison cases. The native mutation
path shares this repair but has not been compiled against the Siemens SDK or
qualified on a native target here.

## Verification scope

Focused managed and actual Host workflows exercise the original retained Classic
screen fixture, independently authored expected output, equal-but-wrong supplied
files, invalid XML, missing observations, request refusals, upload release,
restart, original-file corruption and rehashed report forgery. An independent
Python DOM oracle derives expected content from original inputs and has four
mutation controls. Existing opaque v3 comparisons remain visible alongside v4.

Nine actual prior-producer v1-v3 records are retained byte-for-byte in
`fixtures/hmi-components/retained-v1-v3/`, with producing binary/package hashes.
The focused compatibility run passes 416 checks over two Host lifetimes.
CI selections now retain the original HMI requests, source files and saved
reviews needed to independently recheck derived results.

Exact evidence is under `artifacts/hmi-derived-effects-01/`. Full-source aggregate, packaged-host behavior, source-binding reconciliation and
CI-selection replay pass within the scope below. The full C01/D15 scope is not complete. Remaining work
includes verified library/consumer associations, broader established formats,
authored generation, complete manual/unrelated content preservation and native
import/compile/reopen/recovery/visual/runtime acceptance.

## Final frozen verification

Both source snapshots contain 2372 identical files. The aggregate
passes 44 managed suites, 242 general checks, 1,510 Python tests (one existing
skip), 25 Explorer model/client checks and all Host workflows. The shared HMI
guard suite passes 19 checks, including declaration and opaque CDATA boundaries.
All 38 package/relocation stages pass. The 1703-file package has
manifest SHA-256 `cb01e2e799f8a1ff16fa8881748512e79386b93cd42b69fcde8604646baba3dd`.

Both runs pass 206 HMI component checks, 384 actual MCP checks and 416 checks
against nine original prior-producer v1-v3 records. The independent DOM oracle
checks four original v4 records, including identical wrong proposal/observation
files and malformed XML, with four mutation controls. Depth/event and repeated-role
byte bounds, ambiguous source envelopes, stale selectors, original-file corruption,
rehashed invented reports, upload release and restart are covered.

Both isolated CI-upload selections pass the independent original-input audit and
reopen all four derived records through the exact respective Host. All required
source files and saved-review dependencies are retained. No new browser run was
needed: the package's five Explorer assets match the prior browser-tested assets.
No native UI, product, import, compile, persistence or runtime acceptance is claimed.

All 112 historical evidence entries are unchanged, including 106 unavailable
inputs. Original scope reconciliation files remain byte-identical. Exact source
and binary hashes, CI selections and checks are retained in `verification.json`.
Only final documentation differs from the frozen snapshots. Full C01/D15 and the
44-workstream goal remain open.
