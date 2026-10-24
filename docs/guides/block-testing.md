# Reusable isolated block testing

## 9 October continuation: measured timing and creation recovery

The runner supplies a fresh `creationId` before every `sim_create`; the audit persists it before dispatch. The simulator writes a current-user DPAPI-protected registration intent before the native call, then native identity and a completed storage-marker receipt. After a lost response, a fresh host with the same artifacts directory can use `sim_creation_status`. A `registrationIntent` without a returned native identity stays unresolved. `sim_select` with an explicit `creationId` can adopt only a completed receipt after the prior worker exits and the native ID/name/CPU/storage and marker all match. Adoption transfers the recorded worker under an exclusive file lease. It does not replay creation, submit credentials or return a new download ticket. Existing receipts and memory-card files are retained.

Elapsed timer assertions now reference an earlier named single-call step rather than equating requested host sleep with PLC elapsed time. The new `elapsed` assertion records `[current.begin - origin.end, current.end - origin.begin]`, clipped at zero and the authored timer preset. A one-millisecond resolution allowance is explicit in the TON fixture; measured uncertainty above 500 ms blocks qualification. This bounds host-observed call separation, includes intervening transport/report work, and does not establish scan synchronization or a PLC monotonic timestamp. Original failed reports and their suite hashes remain unchanged; revised suites produce new hashes. The nine-family timer suite adds an explicit zero-elapsed start assertion.

The host waits up to ten seconds for its exact worker to exit after pipe EOF and emits a structured `bridgeWorkerExit` diagnostic. A timeout records unverified exit without terminating native work. Host exit and TIA project-lock release remain distinct observations.

`scripts/run_block_tests.py` connects native FC/FB interface inspection, SCL test-program generation, the existing engineering/simulator MCP tools, and independently authored runtime expectations. The default command prepares reviewable artifacts without starting TIA or PLCSIM:

```powershell
python scripts/run_block_tests.py
python scripts/run_block_tests.py --fixture runtime-edges --fixture runtime-counters
python scripts/run_block_tests.py --suite path/to/custom-suite.json
```

Every suite directory contains its exact specification, declared interface, DUT/type sources, generated test program, symbol bindings, SHA-256 revision manifest and execution plan. A completed/nonempty report directory cannot be reused. Prepared results explicitly state `engineeringVerified: false`, `runtimeVerified: false` and `nativeCompileVerified: false`.

Use `--engineering-only` to qualify all nine generated families with TIA before attempting runtime execution. Native modes require an explicit engineering worker; selecting a Release host alone does not select the Release worker. For example:

```powershell
python scripts/run_block_tests.py --engineering-only `
  --host src/TiaMcp.Host/bin/Release/net10.0-windows/TiaMcp.Host.exe `
  --worker src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe
```

Each suite gets a new owned scratch TIA project. The runner imports and compiles the DUT and required types, reads their native interfaces, generates the harness from those interfaces, then imports, compiles, saves and exports the complete program. It disposes its TIA connection, starts an empty owned instance and opens the exact saved scratch `.ap21` path. It then exports all blocks and types again, requires native compile consistency and compares the XML, including interfaces and bodies. XML comparison excludes the export's `DocumentInfo` provenance and serializer indentation; program declarations and bodies must survive unchanged. Compilation is established before saving; the reopen stage verifies persistence without applying another change.

Engineering-only qualification neither requires simulator MCP tools nor calls PLCSIM tools. It requires no simulator worker, simulator instance, simulation download or runtime credential metadata. A successful `engineering-result.json` records `engineeringVerified: true` and `runtimeVerified: false`; behavioral test cases remain unexecuted. The run's `index.json` records the selected mode and the two independent verification flags.

To qualify execution as well, select `--live` and an explicit simulator worker:

```powershell
python scripts/run_block_tests.py --live `
  --host src/TiaMcp.Host/bin/Release/net10.0-windows/TiaMcp.Host.exe `
  --worker src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe `
  --sim-worker src/TiaMcp.Simulator.V8/bin/Release/net48/TiaMcp.Simulator.V8.exe
```

The live route performs the engineering checks, enables simulation compilation and runs each case on a fresh owned local simulated CPU. Add `--fixture runtime-boolean`, or repeat `--fixture`, to select families in either native mode. The modes are mutually exclusive. The runner does not accept an existing project or a physical PLC target. Native startup failures remain blocked evidence. `--timeout` bounds MCP requests, and `--report-directory` chooses a new artifact destination. Exit codes are 0 for preparation or a successful requested run, 1 for test/cleanup failure, and 2 for blocked qualification.

The qualification host closes before runtime cases begin. Every case and repetition then starts a fresh host and owned TIA session, reopens the exact qualified saved scratch project, and compares new native exports against the unchanged suite revision before creating its CPU. The selected binary manifest is checked before and after each case. Cleanup attempts owned CPU release, TIA detach and host close independently; uncertainty in any cleanup step stops later cases. `caseSessionIsolation`, each case's `engineeringSession`, native call audit and cleanup evidence retain these checks. This proves owned connection disposal; Siemens process exit is not independently asserted.

The first full runtime attempt (`artifacts/block-tests/runtime-all-20261008-owned-tls-01`) passed Boolean `truth-0`. `truth-1` stopped before credential submission when the fresh endpoint was absent from discovery; the next download lost the engineering worker, and later calls reported its faulted connection. All created CPUs were unregistered, but final TIA detach failed. That run remains failed. The subsequent runner uses the per-case session isolation described above; qualification still requires all nine families, 65 cases, 358 steps and 477 assertions.

The second runtime run (`artifacts/block-tests/runtime-all-20261008-owned-tls-02/index.json`) remains unqualified. Project locks, provisioning/restoration/isolation failures, native timeouts, a disconnected engineering worker, a report-persistence failure and one elapsed-time assertion failure are recorded; see the [runtime-proof TODO](../plans/runtime-proof-todo.md). Its completed Boolean report (`artifacts/block-tests/runtime-all-20261008-owned-tls-02/runtime-boolean/live-result.json`) contains six passing cases and two blocks at `project_open` (`truth-3`, `truth-7`) because TIA reported the project was already open under the same user and computer. Successful `tia_detach` and host exit did not independently prove the owned TIA process had exited or its project lock had cleared. Later read-only PID observations found those processes absent, which does not establish their state when the opens failed. No full nine-family pass is claimed.

To explicitly provision each protected scratch CPU's generated credential before normal power-on, add `--provision-runtime-credential` to a live run using a build that exposes `simulation_provision_scratch_credential`:

```powershell
python scripts/run_block_tests.py --live --provision-runtime-credential `
  --host <absolute-host.exe> --worker <absolute-engineering-worker.exe> `
  --sim-worker <absolute-simulator-worker.exe> `
  --report-directory artifacts/block-tests/<fresh-run-name>
```

This flag is rejected without `--live`. Only opted-in runs require the provisioning operation, and the flag is recorded in the index and each suite report. Original card hashes are verified before provisioning. The response must match that revision and exact instance, confirm empty startup and restoration of the original program card, preserve protection, identify a report in that instance's storage and return OFF. The backend independently rechecks the same original manifest after provisioning and records `restoredMemoryCard` before normal protected-program power-on. Runtime-created protection storage does not replace the original generated-card evidence. The installer-required restart, scoped credential provisioning and eight-assertion protected baseline have passed; nine-family runtime qualification remains pending; see [current runtime evidence](simulation.md#post-restart-check-and-credential-investigation--8-october-2026).

The additional `--trust-owned-empty-cpu-certificate` flag requires provisioning and explicit authorization for first-use trust on each fresh owned empty CPU. It is false by default and is recorded in the index, suite and instance evidence. Each attempt pins its first observed certificate and rejects any subsequent change; fresh instances have different fingerprints. The backend requires the exact attempt pin, one untruncated certificate, valid dates, revalidated ownership, and a recorded `Trusted` decision before normal startup. `certificateIdentityIndependentlyVerified` remains false: caller-authorized first-use trust does not establish CA authentication. The user authorized this scoped trust for the baseline and corpus. Attempt fifteen first passed the protected baseline, and attempt seventeen reconfirmed it with the current evidence snapshot (`artifacts/runtime-proof-20261008/evidence-snapshot-20261008T113602982412Z.json`) and binary manifest (`artifacts/runtime-proof-20261008/provisioned-17-binaries.json`). Nine-family runtime qualification remains incomplete.

## Execution and timing

The generated `Main` OB contains one request/acknowledgment gate. While the request equals the acknowledgment it makes no DUT calls. The client writes the inputs and instance selector, publishes a new request ID, and waits for that acknowledgment plus exactly one increment of the call counter. The OB copies inspectable state after the call and acknowledges last. This allows edge detection, counters and retained-state sequences to be tested with a known number of application calls even while acknowledgment reads use host polling.

**Application call control is not scan-controlled simulation.** Other CPU scans and PLC timer time continue between requests. The current backend reports `applicationCallControl: true` and `scanSynchronized: false`. A suite with `timingMode: "scan_controlled"` or `requireScanSynchronization: true` is blocked before startup when synchronization is unavailable. The legacy `sim_test` remains an ordinary polling tool; it is not promoted to scan control by this work.

Timer assertions use host monotonic intervals, including request, transport and polling delay. A timing window records the last nonmatching observation and first matching observation; a bracket overlapping the window boundary is blocked instead of passed. Such observations qualify only the stated observation window, never exact PLC timer accuracy or scan timing. `runtimeQualified` describes passed observed values/application call sequences; `scanQualified` is separate and remains false on this backend. Native compile success and offline test success cannot qualify runtime behavior.

## Sequences and assertions

`fixtures/runtime_catalog.py` supplies nine suites. Several reuse the original conformance sources and expected vectors; additional cases cover missing runtime behavior.

| Suite | Coverage |
|---|---|
| `runtime-boolean` | Complete three-input Boolean truth table |
| `runtime-arithmetic` | Signed INT boundaries, widened DINT arithmetic and INT/DINT/REAL conversion |
| `runtime-branches` | Every IF/ELSIF and CASE branch, boundaries and defaults |
| `runtime-edges` | Rising/falling edges and repeated unchanged inputs |
| `runtime-latches` | Set/reset-dominant behavior, retention and simultaneous priority |
| `runtime-timers` | TON start, elapsed time, cancellation, restart, completion window and reset |
| `runtime-counters` | CTU rising edges, held levels, threshold, SINT saturation and reset |
| `runtime-fb-instances` | Independent outer instances and child multi-instances |
| `runtime-arrays-udts` | Signed array indices, nested in/out values, strings and DB initialization |

This is bounded construct coverage, not proof of every conversion, IEC instruction or target CPU. For example, arithmetic tests cover representable widening, latches implement SR/RS semantics in SCL, and the counter uses native `CTU_SINT` so storage saturation needs only 128 rising edges. Native support for newly authored programs remains subject to compile and runtime qualification.

Each case supplies `initial` values and ordered `steps`. A step applies `set`, waits `waitMs`, makes `calls` to `target`, optionally applies a `pulse` with separate high/low call counts, and evaluates assertions. Omitted `calls` means one; use zero for pure initialization/read assertions or a standalone pulse. Partial input assignments retain earlier values within the case. Each case and each `repeat` starts from a new simulator deployment; reset inputs are explicit sequence stimuli, and STOP/RUN is never used as a retained-state reset.

```json
{
  "fixtureId": "edge-example",
  "timingMode": "application_call_controlled",
  "cases": [{
    "id": "rise-and-hold",
    "initial": {"dut.ClockValue": false},
    "repeat": 2,
    "steps": [
      {"calls": 1, "assertions": [{"kind": "exact", "path": "dut.Rising", "expected": false}]},
      {"set": {"dut.ClockValue": true}, "assertions": [{"kind": "transition", "path": "dut.Rising", "from": false, "to": true}]},
      {"calls": 3, "assertions": [{"kind": "exact", "path": "dut.Rising", "expected": false}]}
    ]
  }]
}
```

`exact` compares typed values; Boolean true cannot satisfy numeric 1. `approx` uses the larger of absolute tolerance and relative tolerance times the expected magnitude, without converting large integers through floating point. `transition` requires both the previous-step and current-step endpoints; it does not assert there were no intermediate states. `window` repeatedly calls the selected DUT until the expected value is observed or the deadline expires. It is relative to the first completed call in that step. Each step supports at most one window so multiple assertions cannot quietly acquire different time origins. Time inputs use integer milliseconds; LTime uses integer nanoseconds.

The entire suite is validated before native work: writeability, scalar types/ranges, targets, assertions, positive timing bounds and limits on cases, steps, repeats, calls and observations. Unsupported requests fail explicitly. The contracts are `contracts/block-test-suite.schema.json` and `contracts/block-test-result.schema.json`; semantic checks such as valid paths, unique IDs and timing bound ordering are enforced by the Python validator.

## Interfaces, wiring and reuse

The reusable APIs are:

```python
interface = inspect_interface(native_block_interface, native_type_definitions)
program = generate_program(interface, instances=("dut",), prefix="BlockTest")
report = execute_suite(suite, program, backend_factory, exact_revision, persist=save_report)
```

These live in `scripts/block_test_program.py` and `scripts/block_test_executor.py`. The existing native `block_interface`, `type_read` and `block_read` tools supply interface and revision evidence. `change_apply` supplies compile/save evidence; `tia_detach`, `tia_start` and `project_open` establish the reopen stage. `scripts/block_test_runtime.py` supplies `McpRuntimeBackend` for the local simulator.

The `--suite` option accepts one suite object or an array of suites. For the CLI, include the DUT's `blockName`, `language`, `source`, all expected block `names`, `instances`, an authored native-shaped `interface`, and `typeDefinitions` for offline preparation, in addition to `fixtureId` and `cases`. UDT imports also use `typeSource` and `typeNames`. Generated `suite.json` files provide complete examples. Both native modes replace authored declarations with native inspection before wiring the test program. Sources are imported into a new scratch project; the CLI does not mutate the original project containing the DUT.

Inspection separates input, output, in/out, return and instance-static declarations. SCL wiring uses named input/in/out assignments, output assignments, and FC return assignment; each FB target receives its own native instance DB. The test-data DB exposes scalar leaves of fixed arrays, inline structures and resolved UDTs. Supported scalars include Boolean, signed/unsigned 8–64-bit integers, bit strings, REAL/LREAL, TIME/LTIME and bounded STRING/WSTRING. String writes must fit both the declared capacity and the adapter's 4096 UTF-16-unit write limit. Types must be supported by both the inspector and runtime adapter; safe ASCII identifiers, fixed dimensions and expansion limits apply. Unknown types, unresolved dependencies, protected/incomplete interfaces, ambiguous names and unsupported nesting are rejected.

Native IEC internals without an inspectable interface are opaque. They stay in the real FB instance; the harness records this state-capture limitation and observes exposed Q/ET/CV outputs. It never reconstructs timer/counter memory or guesses byte offsets. Readable static data is copied to the test DB after each call. Test-data inputs require explicit case initialization; native FB instance start values are established by the fresh download.

## Evidence and qualification

Results are persisted during setup, every native operation, every observation, assertion failure and cleanup. Failures retain the applied inputs, expectation and actual value, readable state, host/native timestamps, exact suite/source/native-export revisions and per-instance generated memory-card file hashes. Before power-on, the backend checks the complete downloaded file manifest. It also checks the runtime's recorded program revision; instruction-memory readback is unavailable and is stated as a limitation. Credential metadata paths may be recorded, but download tickets/password-bearing fields are redacted.

Native runs record absolute selected executable paths and SHA-256 manifests of adjacent `.exe`, `.dll`, `.config` and `.json` files. This includes the host's managed assembly as well as its executable launcher. The engineering worker is always explicit; the simulator manifest is included only in live mode. These records identify the actual selected builds alongside suite, source and native export evidence.

An assertion failure stops that case; independent cases can continue after confirmed cleanup. A failed or uncertain simulator cleanup stops remaining cases. ID zero is a valid simulator identity and is cleaned up. Generated projects, sources and memory cards are retained for diagnosis.

A failed credential-provisioning call always stops the remaining runtime cases.
Even a complete worker cleanup report and exact simulator unregistration do not
reconcile the Host's durable mutation admission. The original error and cleanup
facts remain in `runtimeOutcomes`; when supplied by the Host, `mutationAdmission`
identifies the reservation for `engineering_operation_status`. Inspecting that
status neither releases resources nor authorizes retrying the credential operation.

Offline verification uses no Siemens process:

```powershell
python scripts/test_block_test_program.py
python scripts/test_block_test_executor.py
python scripts/test_runtime_catalog.py
python scripts/test_block_test_runtime.py
python scripts/test_block_test_stages.py
```

The tests cover interface/wiring rejection, deterministic generation, typed comparisons, sequence behavior, timing limitations, exact revision matching, fresh reset/repeat, handshake failures, engineering-only tool isolation, explicit worker selection, binary changes, save/reopen mismatches and cleanup. `scripts/Test.ps1` includes them and packaging includes the runner and fixtures.

On 2026-10-08, **all nine generated families passed native engineering qualification** using the explicitly selected Release host and worker. The run index (`artifacts/block-tests/engineering-all-20261008/index.json`) records their binary manifests and links each suite's engineering report. Every suite imported and compiled its native-interface-generated harness, exported its blocks/types, saved and reopened the exact owned scratch project, and retained matching XML payloads and native compile consistency. All 72 before/after XML artifacts were checked against their recorded hashes; the audit contains no simulator calls. The Python unittest suite passed 201 tests (log (`artifacts/block-tests/staged-python-tests-20261008.log`)).

**Full nine-family PLCSIM behavioral qualification remains incomplete.** The protected-baseline gate has passed: attempt seventeen reconfirmed RUN, typed I/O and all eight assertions on its recorded build. The second corpus run includes passing runtime cases alongside unresolved setup, cleanup and elapsed-time assertion failures; neither its partial results nor engineering success establish a complete runtime pass, retained-state coverage or timer accuracy. The [runtime-proof TODO](../plans/runtime-proof-todo.md) records the remaining work. Exact baseline evidence and earlier startup failures remain in [simulation verification](simulation.md#post-restart-check-and-credential-investigation--8-october-2026).


## Cooperative stopping and remaining cases

Native runs observe a presence-only stop file before each suite and before admitting
a new runtime case/repetition. The default is `<report-directory>/stop-request.json`;
`--stop-file <path>` selects another location. Create that file to request a stop.
Its contents are never read, and the runner never removes it. For example:

```powershell
New-Item -ItemType File -Path '<report-directory>/stop-request.json'
```

An active case finishes its steps and all cleanup first. Engineering preparation
finishes its existing detach/worker/TIA exit checks before runtime admission can stop.
The request does not interrupt a native call or shorten its timeout. Cleanup failures
and uncertain runtime state retain priority and their original failure details;
`--continue-on-failure` cannot override a stop request.

An incomplete requested stop records `admission: stopped_by_request`, explicit
attempted/passed/unattempted case coverage, and false full-runtime qualification.
Earlier assertion failures remain failures. If the last selected case has already
completed, its actual successful suite result is retained; the request prevents the
next suite. Engineering-only runs check requests at suite boundaries.

At native runtime completion, `remaining-cases.json` contains failed and unattempted
selected repetitions with exact authored suite hashes; it is omitted when none remain.
The index records its hash and the observed stop request. After reconciling any unknown
native outcomes, it may be supplied to a **new** `--case-selection` run in a fresh report
directory. This is a separate subset test, never automatic replay or a combined full-corpus
pass. Retain the original reports and stop file; use a fresh stop path for a later run.

The stop protocol and manifest generation have offline fake-backend/CLI regression
coverage in `scripts/test_block_test_stop.py`. A native requested-stop run is still
unqualified; this does not resolve the retained zero-address discovery failure.
