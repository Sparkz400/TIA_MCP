# Generated conformance fixtures

`fixtures/catalog.py` contains fourteen deterministic fixture specifications, including independent expected vectors and target applicability. `scripts/generate_conformance.py` produces versioned manifests, reviewable SCL/AWL source files and SHA-256 indexes under `artifacts/conformance/generated`. Generating or validating fixtures does not start TIA.

The corpus defines engineering tests and intended runtime behavior separately. **A native compile pass only qualifies the requested engineering operations for the observed CPU/version.** It does not prove that expected runtime vectors were executed. S7-1200 applicability in a manifest is a candidate target, not evidence of a tested S7-1200 installation. The initial live runner always creates an S7-1500 CPU 1511-1 PN, exact catalog identifier `OrderNumber:6ES7 511-1AL03-0AB0/V4.0`.

On 2026-10-08, all fourteen engineering scenarios passed through the actual MCP server and TIA V21 on that CPU profile. Evidence is in `artifacts/conformance/live-first/index.json`, its fourteen `*.result.json` files, and exact archived `*.manifest.json` specifications. This includes native TON_TIME/CTU_INT compilation, FC and multi-instance call edges, and preservation of LAD/FBD interfaces and networks during operand substitution. The owned session detached successfully and the host exited 0. Runtime vectors remain unexecuted.

| Fixture | Targeted coverage | Independent expected observations |
|---|---|---|
| `scl-boolean` | OR, AND, NOT | Complete three-input Boolean truth table |
| `scl-branch` | IF/ELSIF, CASE, comparison | Negative/positive boundaries, equality and default branch |
| `scl-arithmetic` | Signed arithmetic, INT→DINT→REAL | Widened sums beyond INT range, negative difference, exact half values |
| `scl-udt-array` | UDT, nested structures, signed array bounds, DB values | Selected DB initial values and native type declarations |
| `scl-unicode-data` | UTF-8 comments, nested STRING/WSTRING, REAL array | Exact source Unicode bytes and declared initial values; native comment preservation remains separate |
| `scl-call` | FC invocation and named bindings | Exact native caller/callee edge and two functional vectors |
| `scl-multi-instance` | Two FB multi-instances | Separate scan-state sequences and native caller/callee edge |
| `scl-edge-latch` | Rising-edge logic, reset-dominant latch | Held input, new edge, state retention and simultaneous set/reset |
| `scl-iec-ton` | Native TON_TIME multi-instance | Reset and timed transitions with scan/sampling tolerance |
| `scl-iec-ctu` | Native CTU_INT multi-instance | Rising edges, held-high input, reached threshold and reset |
| `stl-boolean` | AWL/STL A, AN and assignment | Inhibit cases |
| `stl-arithmetic-compare` | AWL/STL integer addition and comparison | Signed inputs and equality; overflow is explicitly outside this fixture |
| `lad-operand` | KOP baseline import and existing operand substitution | Native interface/network preservation except the selected access; runtime follows alternate input |
| `fbd-operand` | FUP baseline import and existing operand substitution | Same bounded preservation check for FBD |

The LAD/FBD fixtures use `fixture_sources` to obtain native XML supported by the current worker, discover the actual compile-unit/access identifiers from its export, then substitute `EnableAlternate`. The preservation check compares the complete interface and network XML structures after applying exactly the expected component-name change. It detects unrelated wire and interface changes. It does not qualify arbitrary branch creation, timer/counter wiring, graphical layout, or every instruction family.

## Commands

Run pure tests and generate the corpus:

```powershell
python scripts/test_conformance.py
python scripts/generate_conformance.py
python scripts/run_conformance.py
```

The last command defaults to a dry run: it verifies manifests/source hashes and produces `skipped` qualification results, never `passed` engineering results. The pure tests require only Python's standard library. Their temporary files stay under this repository's `artifacts/conformance/test-tmp`.

An explicit live run uses the built Debug MCP host, creates a fresh worker-owned headless TIA session and scratch project, and leaves artifacts/checkpoints available for review:

```powershell
python scripts/run_conformance.py --live --phase engineering
```

Use `--host <absolute-executable>` to test another build, `--fixture scl-boolean --fixture scl-branch` for a subset, `--timeout 300` for the per-request timeout, and `--report-directory <directory>` for a chosen report location. `--continue-on-failure` continues independent fixtures after read-only failures or confirmed checkpoint recovery; it stops if a failed apply cannot be recovered. The runner never retries a failed apply.

No existing-project path, attach-to-process option or physical PLC download tool is exposed by this harness. Its strict step allowlist permits the required offline editing/inspection operations only. The generated source can be reviewed before the explicit live run. Changed generated artifacts require regeneration or deliberate regeneration of the manifest/index hashes; tampering is detected before TIA starts.

Generate deterministic naming variants without editing the specifications:

```powershell
python scripts/generate_conformance.py --prefix Variant2 --output artifacts/conformance/variant2
python scripts/run_conformance.py --generated artifacts/conformance/variant2
```

The same input prefix produces byte-identical manifests and sources. TIA runtime timestamps are recorded only in run results. Each live apply gets a run/fixture/step-specific idempotency key; repeating a different run cannot silently replay an old write.

## Manifest and evidence contracts

Manifests use `schemaVersion: 1`, stable `fixtureId`, `featureIds`, `profile`, ordered `steps`, independent `expectedBehavior`, and bounded `coverage`. Feature IDs are the public MCP tool names or `change.<kind>`. The repository's formal contracts are `contracts/fixture-manifest.schema.json` and `contracts/test-result.schema.json`.

Step arguments support three exact reference objects:

- `{ "$ref": "earlier_step/json/pointer" }` copies a value from an earlier successful tool result.
- `{ "$runKey": "step-name" }` derives a deterministic SHA-256 idempotency key within this specific run and fixture.
- `{ "$xmlAccess": { "stepId": "earlier_export", "symbol": "Enable", "attribute": "networkId" } }` resolves an unambiguous native network access. `accessUid` is the other supported attribute.

Expectations are explicit JSON-pointer assertions (`equals`, `nonempty`, `contains`) or domain assertions (`member`, `callEdge`, `symbolAccess`, `operandPreserved`). `member` verifies exact declaration hierarchy and type in parsed native sections. `callEdge` compares both terminal object names on the same native edge, preventing unrelated reference text from satisfying the expectation. Unknown tools, forward references, duplicate steps, invalid XML DTDs, ambiguous access selectors and altered source hashes are rejected.

Each fixture result records the observed environment, requested qualification phase, step arguments/results, native diagnostics, evidence hashes, and limitations. The exact tested manifest is archived beside the result; reusing a completed report directory is rejected. Result evidence hashes describe canonical JSON encoded as UTF-8, while manifest hashes describe the archived file bytes. Run results persist after every step. Failures preserve their original diagnostic while recording `change_status` and checkpoint recovery separately. The live session is detached in cleanup; no Siemens process object is disposed by this runner. A successful recovery does not turn the original failed fixture into a pass.

`run_conformance.py --live --phase runtime` exits with code 2 and produces **blocked** results because this entrypoint runs engineering manifests. The reusable [block-testing executor](block-testing.md) now supplies the runtime route: `python scripts/run_block_tests.py` prepares nine suites from reused conformance sources/expectations plus additional cases, and `--live` performs native inspection, harness generation and fresh-instance execution. Application-call control and host timing windows are explicit; true scan synchronization is not available in the current backend. PLCSIM runtime qualification remains pending, including resolution of the previously observed `ErrorLoadingDll (-21)`. The engineering runner's other exit codes remain 0 for a valid dry run or successful engineering phase, and 1 for native/assertion/setup/cleanup failure.

## Deliberate gaps

The initial corpus does not yet create software units, duplicate names in separate scopes, protected blocks, multilingual native comments, large generated projects, safety programs, hardware topologies or technology objects. Dedicated native fixture construction is needed for those features. In particular, unit routing must not be marked qualified from root-scope source tests or pure identity tests. Existing milestone hardware/TO/safety reports are separate evidence and do not expand this corpus's claimed instruction coverage.

The native nested-data test also exposed a normalization gap: scalar starts such as `Item.Enabled` and `Item.Meta.CountValue` appear in parsed interface sections, while individual starts inside `Item.Samples` are not surfaced there. The native block XML is preserved. Source generation/compilation must not be interpreted as qualification of normalized array-element initial values.

For CI, run `python scripts/test_conformance.py` without Siemens. The root test script can invoke that command and fail on its exit code. Run live fixtures on a dedicated Windows/TIA agent only after building and validating the worker lifecycle guard. Preserve each run directory and its referenced change journals/checkpoints as qualification artifacts.

The array-element normalization limitation recorded above describes the earlier corpus run. The [engineering coverage expansion](../reference/engineering-coverage.md) now exposes sparse indexed element values while retaining original XML; refer to its separate current evidence and remaining unresolved-path limits.
