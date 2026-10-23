# Repeated real-clock runtime and advertised-profile qualification

The extended soak is opt-in. It runs the linked production Host poller and Core store for fifteen measured 60-second acquisition epochs in one owned Windows process. The authenticated session is still a test double. The separate profile qualifier uses the compiled production `WebRuntimeClient` against an independently scripted, pinned loopback TLS server. Neither qualifier contacts a physical device or launches TIA or PLCSIM.

Build in an isolated directory and use a new evidence directory:

```powershell
dotnet build tests/TiaMcp.PhysicalRuntime.Tests/TiaMcp.PhysicalRuntime.Tests.csproj -c Release --artifacts-path artifacts/runtime-long-build
python -B scripts/qualify_runtime_soak.py --assembly artifacts/runtime-long-build/bin/TiaMcp.PhysicalRuntime.Tests/release/TiaMcp.PhysicalRuntime.Tests.dll --output artifacts/runtime-long-new --long --epochs 15 --seconds 60
```

Long mode admits 2–15 epochs of 30–120 seconds, with no more than 900 requested acquisition seconds. A short trial does not qualify ten minutes of acquisition. The existing command without `--long` retains its single-epoch behavior. Actual process elapsed time includes cancellation, exports, resource release and repeated checks, so the fifteen-minute acquisition run takes longer than fifteen wall-clock minutes.

Each epoch retains the [short soak's](runtime-soak-qualification.md) four 16-signal workers, fifth-worker refusal before intent, mixed values/errors/gaps, 64 MiB touched allocation, 256 owned wait handles, bounded hashing load, blocked-read cancellation and subsequent healthy capture. Measurements use the system clock and real process resource counters. No production duration, read-count or storage limit is increased.

Each epoch uses a fresh bounded store because the production store admits at most 64 retained captures. Every epoch creates five captures. Fifteen epochs therefore exercise 75 captures through repeated allocation/release in the same process, without asserting that one store can exceed its admission limit. After each epoch, all five intents in every earlier store are reopened and repeated. The verifier requires zero acquisition calls, unchanged original plan/head/segment bytes and the original terminal states. This yields 600 repeated-intent observations over the full run.

The fixed cross-epoch allowances are relative to the first released epoch: at most 32 additional process handles, 64 MiB managed memory and 384 MiB private bytes. Existing per-epoch bounds still apply independently. Admission requires at least 3 GiB free; retained evidence is checked against a 1 GiB budget after every epoch. Resource slopes are descriptive measurements, not a pass criterion or a claim of no leaks. This is bounded owned load, not OS-wide resource exhaustion or actual disk-full.

After the long worker exits, the existing independent failure/recovery phases run. One owned process exits with code 73 while a read is pending; a separate process reopens only its original committed prefix, retains unknown uncommitted loss, performs no replay and acquires 16 outcomes in a new capture. The parent retains and waits each original Windows process handle. A timeout can terminate only that exact owned child and always fails qualification.

The independent Python reader validates original chain bytes, exports, acquisition ledgers, epoch chronology, earlier-intent identities and resource samples. Eight row/timing/resource mutations must fail in every epoch. Eight cross-epoch mutations exercise reordered epochs, invented process identity/birth, hidden replay, omitted earlier intents, changed original prefixes, excess handles and fabricated total acquisition. Managed process start time must match the original parent's observed birth time within one millisecond. All source and runtime inputs are hashed before and after execution.

The acquisition wait rechecks monotonic elapsed time after every timer callback and waits the positive, rounded-up remainder. Deterministic tests cover repeated early callbacks, exact/late completion and cancellation propagation. A Windows timer returning early cannot shorten the measured acquisition requirement.

To recheck retained evidence without launching a worker:

```powershell
python -B scripts/runtime_soak_checks.py artifacts/runtime-long-new --receipt artifacts/runtime-long-recheck.json
```

The profile qualifier has an explicit entrypoint separate from ordinary managed unit execution:

```powershell
dotnet artifacts/runtime-long-build/bin/TiaMcp.PhysicalRuntime.Tests/release/TiaMcp.PhysicalRuntime.Tests.dll --tls-fixtures artifacts/runtime-profile-tls
python -B scripts/qualify_runtime_profiles.py --assembly artifacts/runtime-long-build/bin/TiaMcp.PhysicalRuntime.Tests/release/TiaMcp.PhysicalRuntime.Tests.dll --tls artifacts/runtime-profile-tls --runtime-directory artifacts/host-build/bin/TiaMcp.Host/release --output artifacts/runtime-profile-new
```

`--runtime-directory` selects the exact production Host or relocated package's `TiaMcp.Core.dll` and `TiaMcp.Graph.dll`. The qualifier copies the test entrypoint and those DLLs into a fresh output subdirectory. Each compiled probe reports its loaded assembly paths and hashes; the receipt must match the requested DLLs. Input bytes must stay unchanged throughout the run. Omitting the option uses the test build's adjacent production DLLs.

Fifty fixtures cover independently advertised optional methods, required-method refusal, API version shapes, exact signed 64-bit quantity boundaries and unsupported aliases, authentication shapes/modes, request-size refusal before login, runtime timeout policy, identity revalidation and password-expiry metadata. Exact RPC sequences include discovery and cleanup. Absent optional methods must never be guessed. Eight altered-evidence controls reject promoted success, invented login dispatch, missing invalidation, rounded integer retention, promoted device qualification, omitted discovery, an unadvertised method and reordered cleanup.

The server binds only `127.0.0.1` on a disposable port, uses generated fixture certificates and synthetic credentials, and changes no OS trust. Every probe has its own original retained process handle and a bounded timeout. Aggregate/package runners can invoke this qualifier after generating their existing TLS fixtures; package checks should pass the relocated Host directory.

Passing profile fixtures establish parser/admission behavior for these explicit synthetic advertisements. They do not establish firmware coverage, device identity, online program association or full C03/D16 completion. Actual disk-full remains separately blocked by the existing VHD mount privilege failure; this workflow does not retry it.
