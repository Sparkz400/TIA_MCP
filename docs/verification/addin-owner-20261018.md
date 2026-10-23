# Add-In owner process observations — 18 October 2026

Baseline: `d12281667b28a03254e2f8f158eb55e93af22da7`. This prerequisite for AI-03 retains the OS identity of the worker's attachment and exposes an internal read-only context. It does not implement the complete handoff receiver.

## Implementation and limits

The process capture holds an OS handle, start time, executable path and Windows session. It rejects invalid/unavailable processes and birth times later than the initial observation. Changed PID, exit, query failure and disposal invalidate it permanently; observations never reopen a PID. Disposal releases the query handle and does not terminate the process. The native attach/start/detach paths capture and release this identity; the private context route rejects caller identity arguments and remains subject to existing native leases. The index classification explicitly keeps this read from invalidating project snapshots.

The context's native PID comparison is separate from producer authentication, live-selection verification and native qualification, which remain false. Initial acquisition, native proxy behavior, clock changes and actual attach/start/detach interoperability require native qualification. This is not an admission token or proof of a signed/loaded Add-In.

## Executed checks

- Three focused managed groups use real Windows child processes for exact retained identity, clone isolation, handle disposal without termination, process exit, changed identity, late birth and invalid capture. The linked production context projection uses a synthetic native-ID accessor and checks detached state, worker instance/generation, argument rejection and false qualification flags.
- The same production capture and projection compile against the installed .NET Framework runtime assemblies and execute under CLR 4 with `mscorlib` version `4.8.10008.0`. A standalone fixture checks original identity, disposal, unavailable capture and detached context. It substitutes only the native-ID accessor; no Siemens assembly is loaded.
- Six Python schema groups reject missing/extra fields, malformed IDs/times, contradictory states and manufactured native authority. An independent check validates the four actual Framework output documents and their retained identity transition.
- The first focused test run caught a wrong exception namespace in the test assertion and a nullable-context warning. Both were corrected. The read-only classification omission was found during integration inspection and corrected before regression freeze.

Evidence: `artifacts/addin-owner-01/focused-04.log`, `framework-02.log` and `framework-02/result.json`. Framework compilation uses existing Newtonsoft.Json and installed runtime assemblies; no Siemens product, SDK or new package was installed. Context7's lookup did not establish the requested Process APIs; the local Framework metadata and executed production-source fixture provide the direct compatibility check.

The frozen aggregate passes all 43 managed suites (including 231 general checks), 1,470 Python tests with one skip, the Framework fixture and independent schema checks, Explorer checks and all configured MCP workflows. All 29 package stages pass, including strict bundle hashes and relocation. The offline bundle contains 1,515 files; its manifest SHA-256 is `929bf4a3d32ec06ddda0b557f628d03f0f878639a5706825eff553751f9ca002`. Hash consistency does not establish publisher signing.

The runtime MCP workflows pass 644 aggregate checks and 642 package checks. Counts can vary with repeated status observations and one secret scan per retained file; the scenario counts match the previous bridge regression. The package has thirteen capture segments rather than fourteen, accounting for one fewer file scan. `artifacts/addin-owner-01/verification.json` and `runtime-check-count-details.json` retain the comparison. All 106 unavailable historical evidence entries and their original hashes are preserved unchanged. The full native worker cannot be compiled here because the Siemens SDK and Framework targeting pack are absent; the isolated Framework compilation is not a substitute for that build.

Both snapshots contain identical maps of 2,155 source files. Their receipts are `artifacts/addin-owner-01/aggregate-source-manifest.json` (SHA-256 `c984a5392e922f71ccb8722c3778864e4c61ee473688507a3bba8766f5f12724`) and `package-source-manifest.json` (SHA-256 `2433ba26c9562ab949cf3eef6b10804c93d505a00830d44c39ed03176580c41d`). Final verification permits only documentation changes after this freeze and preserves all 106 missing historical evidence bindings.

## Next work

Implement the bounded receiver in the existing Host, producer enrollment, one-use challenge consumption and durable receipt/recovery flow. Bind native context and selected objects through the existing worker, and qualify the complete native lifecycle when Siemens is available. The full 44-workstream goal remains active.
