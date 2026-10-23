# Runtime capture pressure qualification — 20 October 2026

This increment adds bounded pressure tests and an independent retained-file oracle. Production runtime code, persistence formats and request limits are unchanged. It runs the production Host poller and Core store, replacing only the authenticated session and clock with explicit test fixtures. No network, Siemens product, PLCSIM or physical controller ran.

## Executed pressure checks

The mixed case performs 32,768 read attempts across 64 selected signals, retaining 32,800 rows and exactly 128 skipped poll slots. Its values alternate exact UInt64/Int64 limits, a decimal scientific lexeme, structured JSON, 1 KiB strings and Boolean values. Separate timeout, transport-failure and provider-invalid-address outcomes stay explicit. Repeated slow reads produce gaps without catch-up acquisition. No accepted result is dropped. The manual clock models 63.964 seconds of acquisition; this is an accelerated scheduling test, not an OS soak or real-time qualification.

The independent Python oracle reads original plans, head references and segment bytes directly. It verifies SHA-256 chains, sequence/cumulative accounting, the acquisition ledger against a separately calculated schedule, exact values, quality and export identity. Seven mutation controls are rejected: wrong signal, wrong cycle, missing gap, timeout converted to success, altered integer, replayed row and changed ledger time.

Eight waves saturate all four workers, refuse a fifth before durable intent, cancel every in-flight read and retain its cancellation. All 32 records reopen with the same terminal state. Timers reach zero after each wave; a 33rd healthy capture succeeds. A separate store case reserves seven 64 MiB budgets, refuses the eighth because retained bytes also count toward 512 MiB, and admits a replacement immediately after one writer finishes. These exercise actual application worker/reservation capacity; they do not exhaust system memory or handles.

## Actual disk-full prerequisite

`scripts/Test-RuntimeCaptureDiskFull.ps1` creates a new fixed 64 MiB VHD under the repository artifacts directory, verifies exact path/size/empty disk identity and uses only that disk. Its test body additionally requires the dedicated non-system drive root, bounded volume capacity, unique label and ownership token before any fill. A successful future run must observe an actual disk-full OS error, preserve the committed prefix, expose unknown uncommitted loss, release timers, refuse replay, export the retained prefix after freeing space and admit a healthy capture. Evidence stays outside the filled volume. The runner retains its VHD and dismounts only its exact owned disk after the test process completes.

On this workstation `New-VHD` succeeds, but `Mount-VHD -NoDriveLetter` fails with `Microsoft.HyperV.PowerShell.VirtualizationException`, `OperationFailed,Microsoft.Vhd.PowerShell.Cmdlets.MountVhd`, Windows `0x80070522`: the client lacks a required privilege. The probe and reusable runner both reproduce it. Both fixed VHDs remain detached. No existing volume was changed, mounted or filled. The disk-full test body compiles but could not execute; **actual disk-full remains open**, and injected or capacity-refusal checks are not reported as satisfying it.

## Retained results

Evidence root: `artifacts/runtime-pressure-20261020/`.

- `build-01.log` retains an initial test-only C# operator-precedence compilation error. `build-02.log` builds successfully with zero warnings/errors after its correction; this is not a production defect.
- `pressure-01.log` and `pressure-01/pressure-checks.json`: 71 pressure checks pass.
- `managed-01.log`: the complete physical-runtime suite passes 325 checks, including all 71 pressure checks, the existing 43 final-gap collision/recovery checks and seven Windows ACL checks. Its total may vary with concurrent status observations.
- `pressure-01/independent-oracle.json` and `managed-01/results.json.polling/pressure/independent-oracle.json`: independent file-chain/quality/schedule checks and all seven mutation controls pass on both executions.
- `disk-prerequisite.json`, `disk-mount-prerequisite.json` and `disk-full-run-01/run.json`: exact bounded-volume creation and mount prerequisite outcomes.
- `verification.json`: hashes of lane sources, tested assembly and principal receipts.

The offline and workstation runners execute the pressure checks through the existing physical-runtime suite and then invoke the independent oracle. The optional full-disk runner is separate because it needs volume-mount privilege. For an environment with that prerequisite:

```powershell
dotnet build tests/TiaMcp.PhysicalRuntime.Tests/TiaMcp.PhysicalRuntime.Tests.csproj -c Release --artifacts-path artifacts/runtime-pressure-build
./scripts/Test-RuntimeCaptureDiskFull.ps1 -TestAssembly artifacts/runtime-pressure-build/bin/TiaMcp.PhysicalRuntime.Tests/release/TiaMcp.PhysicalRuntime.Tests.dll -OutputDirectory artifacts/runtime-disk-full-new-run
```

The output directory must be new. The runner never requests elevation. Package/aggregate validation of the combined parallel batch is recorded separately by integration; these focused results do not stand in for that run. Broader device profiles, real OS memory/handle pressure, actual disk-full, native identity/timing, independent online program association and UA/control qualification remain open.
