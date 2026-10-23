# Strict decoding of component artifacts — 23 October 2026

The production component artifact reader now rejects malformed encoded text after
verifying the original raw hash and byte count. Previously, `StreamReader` used
replacement fallback: distinct invalid byte sequences could become identical
replacement-character text before XML preservation comparison.

The unchanged original reader reproduced that defect. Fourteen hash-matching
malformed UTF-8/16/32 cases were accepted. A separate control used two different
raw hashes and different invalid bytes inside XML comments; both decoded documents
passed the actual native-tree preservation comparator as equal. These are authored
corruption cases, not evidence that Siemens emitted malformed exports.

The repair decodes the same bounded byte snapshot with strict decoders. It retains
the UTF-8 default and UTF-8/UTF-16/UTF-32 BOM support, checks UTF-32 BOMs before their
UTF-16 prefix, and converts decoding failures to `ComponentReadbackMismatch`.
Valid Unicode, including an actual encoded U+FFFD character, remains unchanged.
There is no second file read, and no new normalization or XML ignore rule.

## Verification

Evidence is under `artifacts/todo-component-encoding-20261023/`, pinned in
`verification.json`:

- `baseline.log`: 246 passing and 15 failing cases against the original production
  reader reproduce the missing refusals and false preservation comparison. The
  original source and tests are retained beside the log.
- `final-managed.log`: all 263 component checks pass. Eighteen new cases cover
  malformed encodings, the distinct-byte collapse, a genuine replacement character,
  and both root/caller artifact-bound planner paths with honestly updated hashes.
- `framework-run.log`: 30 checks pass against the exact production source compiled
  for .NET Framework 4.8. This artifact-reader probe checks the four original
  native exports, supported encodings, invalid sequences, hashes and size bounds.
  XML comparison dependencies are stubbed and never invoked in this probe; the
  full managed suite above exercises the real comparator.
- The full V21 SDK worker builds with zero warnings/errors. It was not executed.
- All 260 library Python tests and 116 component-pattern Python tests pass. These
  suites overlap and are not reported as 376 distinct tests. Contracts and source
  bindings validate without qualification promotion; whitespace checks pass.
- All four original xp01 fixture hashes match their recorded provenance.

This repairs a production content-verification boundary. It does not qualify
native installation, project preservation, save/reopen, recovery or runtime. The
earlier immutable package predates this repair; combined regression and delivery
verification remain separate.
