# Test Suite finding counters and result identity - 16 October 2026

**Later continuation:** Commit `bbbddba` passed Explorer `38949281252` and all offline jobs `38949281447`; the account refusal below describes the earlier commits. The [supplied-result review increment](testsuite-result-review-20261016.md) subsequently provides the user-facing projection caller identified as missing in this audit.

Base: `ef1e4617533b76b2e02f6beca2e71d7040fb72f0`. The preceding identity-text
increment has a passing 30-suite/1,280-Python-test local aggregate and portable
package/relocation receipt. Its hosted CI was refused before test steps by account
billing/spending limits; see [the exact record](testsuite-identity-text-20261016.md).
This increment changes the pure result/finding projection, its regressions and
source bindings. No native API or production admission is added.

## Reproduced failures and changes

`artifacts/testsuite-findings-dev/before.log` records two supplied message trees
whose state is `Success` but whose error or warning count is positive. The
normalizer correctly returns `executionError` or `inconclusive` and fails required
coverage. The old finding filter returned zero findings. The new regression fails
on that source in `artifacts/testsuite-findings-dev/test-before.log`.

`Findings` now includes positive error/warning counters as well as non-success
states. Every finding retains its original state, text, native path and exact
object join plus separate `errorCount` and `warningCount` values. Parent and child
counts are not added together; maximum integer values do not overflow. A clean
zero-count success still produces no finding. The existing five constructor and
deconstruction fields remain unchanged; the counters are additive properties.
No MCP tool or schema currently serializes this library-only finding record.

Direct finding inspection also accepted malformed definition IDs that the run
planner already refuses. `artifacts/testsuite-findings-dev/id-before.log` records
the failing identity regression. The common raw-result boundary now uses the
existing exact-text guard before serialization. Valid Unicode IDs stay exact.
This does not invent plan membership for standalone supplied findings.

Development `artifacts/testsuite-findings-dev/after-04.log` passes 580 managed
checks, 31 more than the prior 549-check suite. Earlier failed development runs
remain retained and are not passing validation receipts.

## Final validation

Final frozen source: `artifacts/testsuite-findings-01/source`, Git tree
`18e5ef4f5ecbf2a7c2d43bb9a921350d8865313f`, 1,821 files. Final-source checks pass:

- Complete Test Suite suite: 580 managed checks, including counter/identity
  regressions, existing scale, queue/recovery, result, scope and worker boundaries.
- Actual MCP Host: 46 checks, four zero-exit unforced lifetimes, no native workers.
- Style schemas: two valid documents, nine rejected adversaries, ten tool mappings.
- Host build: zero warnings/errors.
- Python aggregate: 1,280 tests, one unavailable-native-evidence skip. Contracts,
  source bindings and retained evidence inventory pass; 106 absent evidence files
  stay unverified and six available inputs match their historical hashes.

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/testsuite-findings-01/source-manifest.json` | `2b8906f339bb92851213a5799268a9d01ce454809665bf694527a845c00c07fb` |
| `artifacts/testsuite-findings-01/source/artifacts/targeted/verification.json` | `37b2226a9a775a345a0d01b0654e225acc931dfafbe26825649aac3ae932d97e` |
| `artifacts/testsuite-findings-01/source/artifacts/python-checks/result.json` | `c18fd4f03f0e16104c3aee87c75b83c144e25ec923b1bec1c283af07556592ff` |

All implementation, contract and harness bytes match the frozen source. Only
result documentation changes afterward. The preceding whole-repository aggregate,
browser and portable-package results belong to their recorded earlier sources;
they were not rerun for this pure result-projection delta. No native compilation,
execution, license/scope provider or qualification was tested.

The published-source CI outcome must be read separately; the previous commit's
jobs were refused before test execution by GitHub account billing/spending limits.
No account setting or workflow gate was changed to bypass that refusal.

## D06 continuation audit

| Obligation | Current evidence and next step |
| --- | --- |
| Definition/run/result/scope/job models | Implemented with exact identities, required coverage, raw result retention and durable operation tests. This finding repair closes one projection mismatch, not the entire result/diagnostic workflow. |
| Actual offline Host style queue | Ten tools cover prepare/review/status/stop/results and disabled execution/recovery. Real Host tests verify absence of native workers and retained state across restart. |
| Finding integration with health/diagnostics | The pure `Findings` method has no production caller. An offline supplied-result intake/review and exact saved-review object binding remain to design and deliver before claiming a user-facing health integration. No fabricated native results may fill that gap. |
| Process ancestry diagnostics | `test_qualify_testsuite_scope_probe.py` covers stale/reused PIDs, original descendant ancestry, observation loss and cleanup with synthetic process observations. Actual historical native ancestry failures still need the original native environment and retained original-lifetime evidence. |
| Scope/license provider contract | `ITestSuiteStyleSession.Observe` and `TestSuiteStyleProbeReceipt` already bind observations to the exact plan, project, session, installed adapter/profile, source/content and hardware identity. Shared orchestration verifies required probes and independent scope receipts before effects. Do not create a second competing provider interface. |
| Native provider implementation | `StylePrerequisiteProbe` deliberately returns incomplete scope and unknown execution license; the owning worker refuses before checkpoint/Run. Service/DLL presence, SetScope success, screenshots or diagnostic mentions cannot become a current complete provider. Requires supported-route research and native qualification. |
| Production admission | `TestSuiteProductionAdapter.QualifiedProfiles` remains empty. Application/system/endpoint execution and recovery retain their separate native/simulator/hardware obligations. |

The [full queue](../plans/offline-execution-20261016.md) remains open. No D06 or
release acceptance is inferred from synthetic checks or retained historical
observations. GitHub account billing/limits require account-owner action; offline
implementation remains possible while hosted jobs cannot start.
