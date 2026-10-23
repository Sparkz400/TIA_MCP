# Typed graphical text boundaries — 22 October 2026

This local offline increment fixes complete-string validation in the shared typed
LAD/FBD payload parser and public contracts. No TIA, PLCSIM or native worker was
started. No package was published or merged.

## Change and evidence

The original .NET regular expressions used `$`, which permits a match immediately
before a final newline. Five original-source probes accepted malformed identifiers.
Seven parser patterns now use `\A`/`\z`. Public JSON Schema patterns use the
ECMAScript-compatible `(?![\s\S])` absolute end assertion. Declaration schemas
also express the parser's existing simple ASCII-name grammar. New UA binding and
observation schemas use the same complete-string boundary for IDs and hashes;
their runtime readers already reject the malformed identifiers.

No inputs are trimmed or rewritten. Quoted/general symbolic components, paths and
comments retain their own existing grammar. Native integer ranges and graph
resolution remain separate validation steps.

Evidence is retained under `artifacts/overnight-20261022/typed-boundary/`:

- `typed-original-regression.log`: the new regression fails on the actual original
  parser; 30 of 31 typed groups pass, with the trailing-LF key wrongly accepted.
- `typed-fixed-fresh.log`: all 31 typed groups pass using fresh build outputs,
  including 156 text-boundary checks, the independent 79-network reader corpus and
  the existing 35-profile emitter evidence.
- `general-root-c.log`: the integrated current tree passes all 243 general groups.
  `root-build.log` records zero warnings/errors.
- `python-root-all-typed.log`: all 138 typed Python tests pass. Boundary cases cover
  eight restricted public fields; 84 existing typed fixture payloads also validate
  after resolving their documented fixture placeholders from original XML IDs.
- `python-root-ua.log`: all six UA binding/observation contract tests pass, including
  trailing-LF identity and snapshot-reference refusals.
- `ecmascript-patterns.json`: 64 checks pass with JavaScript regular expressions.
- `contracts-root.log`: 261 MCP tools, 56 mutation kinds and 335 features.
  `bindings-validation.log` confirms the regenerated source registry is valid
  with no unavailable evidence; release acceptance remains false.
- `integration-receipt.json` records the guarded integration from the isolated
  candidate. Later test-only handoff portability changes are described below.

The original small proof is retained separately at
`artifacts/overnight-20261022/typed-boundary-probe/`; its `baseline.log` shows five
malformed payloads accepted. Candidate work used a verified copy of the previous
2,799-file frozen source, with `baseline-inputs.json` retaining the inputs.

## Retained failed attempts and test portability

`typed-fixed.log` contains a stale-object failure: copying the fixed source back
preserved its timestamp, so MSBuild reused the original object. A fresh artifacts
path recompiles the exact candidate and passes. `python-typed.log` preserves the
initial fixture-test failure caused by validating unresolved `$first`/`$part:...`
harness placeholders as literal public payloads; the corrected test resolves them
using the original reference XML, and `python-typed-02.log` passes all 77 selected
tests. Neither failure was deleted or counted as a pass.

`general-fixed.log` passed 241/243 groups and exposed overly specific Add-In test
exception expectations. With identical binaries, the default C: temporary path
returned `IOException` for CreateNew against an existing directory; the D: path
passed all five original handoff groups (`handoff-original-d.log`). The test helper
now permits `UnauthorizedAccessException` or Windows FILE_EXISTS/ALREADY_EXISTS
I/O errors (80/183), while unrelated I/O failures still propagate. All existing
retained-state, interruption and no-replay assertions remain. The integrated full
run passes on C:, and `handoff-fixed-d.log` passes all five groups on D:.
No Add-In production source changed.

## Scope

These are offline parser, schema and lifecycle-fixture results. Native Framework
worker compilation, Siemens import/compile/reopen/recovery and physical behavior
remain unqualified. The observation package receipts describe their earlier frozen
sources; this subsequent repair has focused current-tree verification and has not
been represented as another full package qualification.
