# Component artifact byte snapshots — 22 October 2026

The component preservation reader now hashes and decodes one bounded byte snapshot.
Previously it opened a path to hash the file, closed that handle, then opened the
path again to read XML. A replacement between those opens could supply XML that
had never matched the pinned hash. This affected source/base/oracle and installed
artifact reads through the shared content contract.

The repair opens the file once, reads at most 4 MiB plus one refusal byte, hashes
the captured bytes and decodes that same array. The length bound is checked while
reading as well as on seekable stream length. Existing UTF-8 default and BOM
detection behavior are preserved. Changed hashes, oversized content and interrupted
reads cannot return a partial or different text snapshot. Descriptor checks,
content preservation policy and native installation gates remain unchanged.

## Evidence

Evidence root: `artifacts/overnight-20261022/component-artifact-read/`.

- `original-race/result.json`: the actual original private artifact reader consumed
  a replacement in 2,665 attempts. Its pinned original SHA-256 was
  `c1811411c80e5a24b4d61688cade3ab6aa5f2ddf8d9ad5b72665ab36f96bb6c5`;
  the returned XML hashes to
  `9d754436b967fca82639347e98777cdeb8b43818820e3efaad9a2994e4e8b9a5`; `verification.json` independently confirms the
  consumed bytes exactly equal the replacement and differ from the original.
- `fixed-race/result.json`: the same probe completes 20,000 attempts against the
  fixed compiled reader, with 8,525 exact accepted reads, 11,475 refusals and no
  mismatches observed, while the writer completes 2,326 changes. This bounded
  stress result supplements the single-snapshot implementation and deterministic
  tests; it is not a proof of every possible scheduling interleaving.
- `fixed.log`: all 227 component integration groups pass, including fourteen new
  artifact groups, existing original xp01 native-export comparisons, typed
  builders, dependency capture, coordination and the production apply guard.
  New cases cover six encodings/BOM combinations, fragmented nonseekable reads,
  no second stream pass, exact/oversized byte limits, wrong hashes, unexpected
  trailing bytes, descriptor disagreement and interrupted reads.
- `framework-build.log` and `framework-run.log`: the exact production source builds
  for .NET Framework with zero warnings/errors and passes fifteen artifact-only
  checks, including all four original xp01 exports. The probe stubs XML
  preservation dependencies and never invokes them; this is not full worker or
  native qualification.
- `python-components.log` and `python-library.log`: all 35 and eight related Python
  tests pass. `contracts.log` retains 261 tools, 56 mutation kinds and 335 features.
- `bindings-validation.log`: regenerated source bindings are valid, with no
  unavailable evidence; release acceptance remains false.
- Original/fixed compiled assembly hashes and exact source hashes are retained in
  the probe receipts and `verification.json`. The original race, original source
  and original consumed XML remain unchanged.

All filesystem stress uses owned local fixture paths. No Siemens product, native
worker, PLC, publication or merge was involved. The existing complete observation
package receipts apply to their frozen sources; this later repair has the focused
verification described above. Native installation, complete recovery and runtime
acceptance remain deferred.
