# Encoder compiler diagnostic admission — 17 October 2026

Six injected bad compilation receipts were accepted by the encoder preservation
auditor: missing targets, a Boolean error count, an Error target state, nested
Error diagnostics, a message without a state, and a non-object message. The
existing typed-route guard also accepted the last two cases. See the retained
[before probe](../../artifacts/encoder-compile-before.json) and
[after probe](../../artifacts/encoder-compile-after.json). These probes use synthetic
receipts and XML; no Siemens process ran.

The typed-route guard is now shared in `scripts/compilation_evidence.py`. It
requires an integer zero count, nonempty named targets, allowed target/message
states, explicit nontruncation, well-formed nested messages and absence of error
evidence. All original nested metadata checks remain active. Diagnostics are
checked without changing the receipt. Bounded recursion rejects unsupported
depth rather than silently omitting diagnostics.

The encoder producer checks scratch, hardware, seeder, edit and recovery
compilation. The read-only verifier refuses insufficient source compilation
before starting a Host. The independent auditor rechecks edit/recovery and current
source prerequisites, including agreement between retained session calls and the
flattened step list. Failed prerequisites cannot disappear from the evidence.
`compilationEvidenceVerified` remains false if any required compile check fails.
Legacy count-only evidence is retained but cannot complete the current audit;
earlier XML findings remain in a failed report rather than being promoted to full
qualification.

EncoderSeeder now records the containing device's native compiler state, named
target, complete enumerated messages and nontruncation. It links the existing
`CompilationEvidenceRules.cs` and requires that validator before reporting
completion or saving. `CompilerResult.State` already appears in the retained
[V21 API inventory](../plans/expansion-plans/api-coverage-inventory-20261014.json)
and in the worker's existing `ChangeCompilation` implementation. This source
change has not been built against the unavailable Siemens SDK.

## Verification

Six pure Python test methods exercise successful states, strict counts, target
and diagnostic shapes, nested errors, depth limits and retained metadata checks.
Two additional orchestration methods cover hidden errors at all five producer
phases, all five retained source compile locations, failed prerequisite statuses
and concealed call records. The existing success, cleanup and preservation
regressions remain active.

Targeted checks pass: 18 encoder lifecycle methods, six compiler evidence methods,
41 typed graphical methods and ten linked pure C# compilation groups. The
optimized-mode regression passes 63 focused cases under each of `-O` and `-OO`.
The C# test executable compiles the actual shared validator without a Siemens
reference; it does not compile or execute EncoderSeeder.

Frozen Git tree: `4b165584dda0c86b888ab6f71a066712708cd756` (1984 files).
[Source manifest](../../artifacts/encoder-compile-01/source-manifest.json) SHA-256:
`6b391b6256aaefa6e962158007c7115a4de44d3562dc52df18bb114197e7a462`.
Source ZIP SHA-256: `152d9d575fed96c792dbea0bf3e191017d064787de8e16a3b660a028ad3b3123`.

The [Python aggregate](../../artifacts/encoder-compile-01/source/artifacts/checks/result.json)
passes 1,342 tests with one unavailable-native-evidence skip, plus contracts,
source bindings and retained evidence inventory. Its optimization subprocess
checks 63 focused cases under each optimization mode; those child cases are not
added to the parent test count. Run: 2026-10-17T08:33:26.7751138Z to 2026-10-17T08:34:40.6513741Z.
Receipt SHA-256: `2acbbf1ff95b70a7b7af708f99effa8180d0a06a9190b04ab8efa93fc0e2f9ff`.
[Python console](../../artifacts/encoder-compile-01/python-console.log).

All ten [pure C# compilation groups](../../artifacts/encoder-compile-01/compiler-console.log)
pass from the frozen source. Console SHA-256: `9c62c416b5b89e51af9927b6e46afcbb8d197cfaa23694a37f406414510d204c`.
[Seeder project evaluation](../../artifacts/encoder-compile-01/seeder-project-items.json)
confirms the linked shared validator and net48 target; evaluation does not run an
SDK compilation or native code.

All 22 [package stages](../../artifacts/encoder-compile-01/source/artifacts/package-checks/result.json)
pass, including relocation. Bundle `tia-mcp-offline_20261017_083348_cd4f6974` contains 1402
manifest files. Receipt SHA-256: `f9698a0cc1df63259fc98a8f16c0bc5ac1b61f1899d6230dad130eefd014f38c`.
Manifest SHA-256: `b65602ff0bd4c674faefedddce2034b867555442d4c42fbb3fa12487f7a87156`.
ZIP SHA-256: `c70bc9e5570554caa77e87c305608ebf3895e7d48de9583d4a38dc0493c4b750`.
The offline package excludes native workers. Hashes establish byte consistency,
not publisher or native authenticity.

The [source comparison](../../artifacts/encoder-compile-01/final-source-comparison.json)
permits only completion-documentation changes after testing and verifies all 106
unavailable historical evidence binding rows remain unchanged. The
[integration receipt](../../artifacts/encoder-compile-01/integration.json) records
the pushed commit, hosted CI and cleanup. The full managed aggregate and browser
suite were not repeated for this increment.

## Remaining gates

This verifies diagnostic admission over supplied receipts, not that a native
collector selected every required target or that an omitted target compiled.
SDK build, native state/message collection, exact target coverage and native
save/reopen/recovery behavior remain unqualified. Recovery-copy endpoint
correspondence still requires independent evidence; stripping project context
is not an acceptable workaround. TO-03–12 and the full offline queue remain open.

No native evidence, unavailable binding hash or production profile is rewritten
or promoted by this repair. The prior full 39-suite aggregate remains recorded
in the [lifecycle increment](encoder-lifecycle-20261017.md); it is not claimed as
a newly executed full managed run for these Python/native-harness source changes.
