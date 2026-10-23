# Bounded real-clock runtime capture qualification

The opt-in soak runs the production Host capture poller and Core capture store using `TimeProvider.System`. Its authenticated-session boundary remains an explicit test double: no device, network endpoint, TIA, PLCSIM or physical controller is contacted. Use this evidence for local scheduling, persistence and process/resource recovery only.

Build the managed test assembly in an isolated output directory, then run:

```powershell
dotnet build tests/TiaMcp.PhysicalRuntime.Tests/TiaMcp.PhysicalRuntime.Tests.csproj -c Release --artifacts-path artifacts/runtime-soak-build
python -B scripts/qualify_runtime_soak.py --assembly artifacts/runtime-soak-build/bin/TiaMcp.PhysicalRuntime.Tests/release/TiaMcp.PhysicalRuntime.Tests.dll --output artifacts/runtime-soak-new-run --seconds 60
```

The output directory must be new. Windows is required for the OS handle measurements. The requested acquisition interval must be 30–120 seconds. The default is 60 seconds; ordinary unit runs do not execute this opt-in soak. The runner retains original logs, child start/exit receipts, raw capture files, measurement samples and an independent verification manifest. A failed phase stays failed and prevents subsequent phases. It never deletes prior evidence.

The steady phase runs four concurrent captures, each with 16 signals and a 16 MiB retained-byte ceiling. A fifth worker must be refused before an intent is created. Every 37th read requests a 145 ms delay; other reads request 2 ms. These are actual asynchronous waits, not simulated elapsed time. The verifier checks the original OS timestamps and requires the slow reads to exceed the 100 ms polling interval. It does not assume each Windows timer wait exactly matches its requested delay. Timeout, transport failure, provider error, exact large integer/scientific values, structured values and 1 KiB strings remain distinguishable.

During acquisition, the test holds and touches 64 MiB of managed memory, opens 256 unnamed OS wait handles, and performs SHA-256 work on 1 MiB slices in a single asynchronous loop with 20 ms pauses. It samples managed/GC heap bytes, working set, private bytes, handle/thread counts and CPU time every 500 ms. Measured private-byte growth must remain below 384 MiB over baseline. After resource disposal, at least 200 handles must disappear, the remaining handle count must be within 64 of baseline, and managed memory must drop by more than 32 MiB from the loaded measurement. These are explicit bounded checks, not proof of a flat long-term memory slope or system-wide exhaustion.

After the acquisition interval, every capture enters a cancellable blocked read. Concurrent stop must drain within ten seconds, commit each cancellation and preserve gap/error/value semantics. Repeating stopped intents must not acquire reads or change original plan/head/segment bytes. A healthy capture must then succeed in the same process; all stopped captures must reopen with their terminal state.

A second owned process commits at least 128 read outcomes and deliberately calls `Environment.Exit(73)` while another read is pending and some completed outcomes remain uncommitted. This bypasses managed disposal; Windows process teardown releases the store lock. The runner waits on its original `Popen` process handle. A third process reopens the exact retained prefix as `interrupted`, with uncommitted loss unknown. Repeating its original intent must acquire zero reads and leave every pre-crash plan/head/segment byte unchanged. A new capture then acquires exactly 16 read outcomes.

The runner's timeout path can terminate only its own `Popen` child through that retained Windows handle. A timeout is a failed qualification, never a passing recovery case. The test executable launches no subprocesses. The intentional exit is an owned application-process failure; it is not a machine power-loss, kernel failure or transport-worker qualification.

The Python oracle reads original files directly, checks SHA-256 chains and cumulative counts, matches the acquisition ledger to per-cycle selections and explicit gaps, verifies exact values/errors and compares exports to the original rows. It checks real timing/resource observations and restart receipts. Eight mutation controls must be rejected: wrong signal, timeout changed to success, altered large integer, omitted gap with renumbered rows, replayed row with renumbered rows, wrong schedule, fabricated elapsed duration and unobserved handle pressure.

To independently recheck an existing completed run without launching a worker:

```powershell
python -B scripts/runtime_soak_checks.py artifacts/runtime-soak-new-run --receipt artifacts/runtime-soak-new-recheck.json
```

The existing actual disk-full harness is separate. Its VHD mount prerequisite remains blocked by missing privilege on this workstation; this soak never retries the mount, fills a real volume, or claims actual disk-full qualification. Longer soaks, OS exhaustion, real transport/device timing, online identity/program association and broader C03/D16 qualification remain open.
