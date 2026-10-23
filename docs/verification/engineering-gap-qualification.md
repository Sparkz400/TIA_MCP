# Selected engineering gap qualification

This qualification uses new worker-owned offline scratch projects on the installed TIA Portal V21. Evidence identifies the actual host and worker binaries, native exports, compile diagnostics, recovery archives and fresh TIA process IDs. Historical failed attempts remain retained. A successful fixture qualifies only its recorded CPU, firmware, object and operation.

All **seven selected fixtures** are qualified. The [machine-readable evidence index](../../artifacts/engineering-gap-evidence.json) verifies report/audit links and both build output manifests by SHA-256. Encoder completion uses a separate successful read-only continuation after a retained project-lock failure.

## Startup

The [empty-instance probe](../../artifacts/engineering-gap-startup-probe.json) succeeded in 15.118 seconds with the same Release worker used by the earlier timed-out attempt. Discovery, empty owned startup, status and detach passed. No project was opened or changed by that probe. Subsequent native safety and graphical runs also started and reopened their fixtures successfully in the ordinary Windows engineering session.

The previous 300-second timeouts did not recur in these runs. Their original cause remains undetermined; no timeout increase, automatic request retry or Openness security change is claimed as a fix. [The reproducible probe](../../scripts/qualify_tia_startup.py) records elapsed time and binary hashes, preserves previous reports and checks preservation of previously discovered processes/projects.

## Safety fingerprint correction

[Native report](../../artifacts/safety-gap-requalified.json) and [strict export audit](../../artifacts/safety-gap-native-preservation-audit.json) passed for CPU1511F-1 PN, `OrderNumber:6ES7 511-1FL03-0AB0/V4.1`, user F_FBD block `Main_Safety_RTG1` (FB1).

The requested change appends one existing comment. Import compiled and saved with **0 errors and 0 warnings**, then produced a durable post-change fingerprint in both the result and journal. Native generated `DB1_C` was again reported inconsistent, reproducing the state that caused the original fingerprint failure. The independent post-compile safety inspection matched `safetyAfter`. Twenty protected generated blocks, including `DB1_C`, retained explicit metadata/signature coverage and payload exclusions; `fingerprintCoverage.complete` remains false. Their generated payload bytes are not claimed as verified.

The changed F-block exactly matched the requested native XML, and five supporting user blocks stayed unchanged. Guarded `change_restore` succeeded before detachment, recovered original native XML and signatures, and retained the changed project. An unchanged import on the recovered copy compiled/saved with **0 errors and 0 warnings**. Both copies subsequently reopened in separate fresh TIA instances and matched their respective native XML, runtime-group metadata and program/block signatures. The independent audit checks 40 byte-verified native exports across six phases, preserving comments, processing instructions, native IDs, QName bindings and significant whitespace; it excludes only native top-level export provenance and serialization indentation.

Observed signatures stayed unchanged for this comment-only edit: collective `2584179645`, software `2584179644`, hardware `1`, communication address `0`, and selected block `4200226891`. This establishes comment preservation and the bounded fingerprint/recovery correction. Changed safety behavior, safety-unit edits, F-I/O, physical operation and safety acceptance remain outside the fixture.

## LAD/FBD preservation

[Native report](../../artifacts/engineering-gap-graphical.json) and [strict export/process audit](../../artifacts/engineering-gap-preservation-audit.json) passed for the selected LAD and FBD fragment patches on CPU1511-1 PN firmware 4.0. Each fixture compiled/saved with **0 errors and 0 warnings**, matched the complete expected native block, recovered its checkpoint, and independently reopened the retained changed and recovered projects with exact native payload equality. The audit verifies raw export and archive SHA-256 values and distinct process IDs.

Compilation evidence comes from each apply before its save. The subsequent graphical reopens are read-only preservation checks; they do not claim a separate post-reopen compilation or PLC execution. These fixtures do not qualify arbitrary graphical synthesis or other instruction/CPU combinations.

## SCL call preservation

The [call report](../../artifacts/engineering-gap-calls.json) completed all three selected input-argument edits on CPU1511-1 PN firmware 4.0. `LayerCaller` calls an FC, `LayerInstanceCaller` calls an FB with a global instance DB, and `LayerMultiCaller` uses a local FB multi-instance. Each changes only the selected `Enable` argument from `A` to `Alternate`, compiles/saves with **0 errors and 0 warnings**, restores the original checkpoint, and reopens both saved project copies in fresh owned TIA processes. Caller, callee, applicable instance DB, unselected arguments and native call/instance metadata match the expected state in every phase.

The [combined graphical/call audit](../../artifacts/engineering-gap-preservation-audit.json) independently verifies **40 whole-native-XML equality pairs**, **five checkpoint archive hashes**, and distinct apply/changed-reopen/recovered-reopen process IDs for all five fixtures. Graphical execution preceded the final comparator hardening; its separate final audit checks the retained native exports with the corrected comparator. The call report also records executing harness source hashes.

| Fixture | Apply process | Changed-copy reopen | Recovered-copy reopen | Compile errors / warnings |
|---|---:|---:|---:|---|
| `LayerLAD` | 38224 | 23276 | 8988 | 0 / 0 |
| `LayerFBD` | 8988 | 22516 | 44060 | 0 / 0 |
| `LayerCaller` | 1604 | 23720 | 21468 | 0 / 0 |
| `LayerInstanceCaller` | 21468 | 43116 | 41888 | 0 / 0 |
| `LayerMultiCaller` | 41888 | 32684 | 22072 | 0 / 0 |

Call compilation also occurs at apply before save. Reopen inspection does not execute another compile. Global FB-instance resolution remains explicitly unresolved for the native `HasQuotes` form, while local multi-instance resolution remains symbolic-only; preserving those exact observations does not turn them into resolved native object identities. Graphical call rewiring, instance retargeting and runtime behavior remain unqualified.

## Connected encoder dependencies

The [validated hardware recipe](../../fixtures/encoder-hardware/cpu1511-fw41-tm-posinput2-v10.json) supplies these exact dependencies:

| Dependency | Native observation |
|---|---|
| CPU | `OrderNumber:6ES7 511-1AL03-0AB0/V4.1` |
| Technology object | `TO_ExternalEncoder` version `10.0` |
| Module | TM PosInput 2, `OrderNumber:6ES7 551-1AB01-0AB0/V2.0`, rack slot 2 |
| Channel | Number 0, type `Technology`, I/O type `Complex` |
| Operating mode | `MotionControl`, native `System.UInt64` value `269` |
| Sensor dependencies | Type `0`, interface `1`, telegram `83`, offline automatic data adaptation `true` |
| Native connection | `SensorInterface.Connect(channel)`; input/output addresses `0` / `0`, no DB-member connection |

The guarded fixture seeder configures the observed hardware mode and uses the native encoder connection API. It captures native parameter access metadata and preserves read-only defaults. The production channel readback now includes channel number/type/I/O type and bounded parent/module identity; the companion also reads the exact project hardware path and operating mode.

[Native stages](../../artifacts/engineering-gap-encoder-requalified.json) passed device setup compilation with **0 errors and 1 warning** (the CPU display has no password protection). MCP then changed `Sensor.InverseDirection` from `false` to `true`, compiled/saved with **0 errors and 0 warnings**, and verified all **143** parameter records. The [strict edit audit](../../artifacts/engineering-gap-encoder-native-edit-audit.json) proves that only one native `StartValue=true` was inserted under the selected `Bool` member; default `false` is independently established by native parameter readback. Guarded checkpoint recovery exactly restored baseline XML, parameters and connections. The recovered copy was saved and its native compile check returned **0 errors and 0 warnings**; the compiler explicitly reported that all blocks were already up to date.

That original report remains `completed: false`: its first recovered-copy reopen hit TIA's explicit already-open/stale-lock error, which permits a two-minute release delay. The [separate read-only continuation](../../artifacts/engineering-gap-encoder-preservation.json) verified that original owned PID `16560` had exited, then opened the recovered copy in PID `1792` and the original changed copy in PID `41012`, each successfully on its first attempt. Both copies matched all 143 typed parameter records, complete native XML, exact channel/module connection and operating mode. No mutation was replayed and no lock file was removed.

The [final independent encoder audit](../../artifacts/engineering-gap-encoder-preservation-audit.json) binds the original and continuation reports by SHA-256, verifies the raw native exports, exact Boolean-only edit, checkpoint archive, distinct process IDs and both saved-copy comparisons. This combined evidence closes the selected encoder fixture; it does not relabel the original failed report. The earlier [read-only-parameter setup failure](../../artifacts/engineering-gap-encoder.json) is also retained.

The native seeder prepares the hardware prerequisite; this does not add a general MCP connection-write route or qualify standalone connected `to_create`. The earlier disconnected scalar-only encoder profile remains a negative fixture. Physical encoder behavior, commissioning, runtime execution and other CPU/module combinations remain unqualified. See the [recipe instructions](../../fixtures/technology-profiles/README.md#connected-external-encoder-recipe).

## Reproduction and evidence rules

Use an explicit host and worker from the same recorded build, and a short artifact root. The native build used for safety and graphical evidence is [the source/output snapshot](../../artifacts/deployment-build/36079332521b4e92b18f35d6e3cea984/build-outputs.json); its input manifest retains source hashes. The worker SHA-256 is `4b06dfb88d8072d37c74b9098669d527d2026a4d88c0fb695b046a6702f6ed53`.

Calls used that same snapshot. The encoder uses a [later isolated source/output snapshot](../../artifacts/deployment-build/fc36ed1044894049a2dbc58aee45ac2f/build-outputs.json) containing the channel-readback correction. The encoder report separately records the seeder binary and executed source/profile hashes.

```powershell
python scripts/smoke_safety_direct.py --host <host.exe> --worker <worker.exe> --artifacts <workspace>/artifacts --roundtrip --edit-comment --verify-recovery --report artifacts/<fresh-safety-report>.json
python scripts/validate_engineering_layers.py --host <host.exe> --worker <worker.exe> --artifacts <workspace>/artifacts --section graphical --report artifacts/<fresh-graphical-report>.json
python scripts/validate_engineering_layers.py --host <host.exe> --worker <worker.exe> --artifacts <workspace>/artifacts --section calls --report artifacts/<fresh-call-report>.json
```

Recovery occurs in the original applying TIA session because the bridge's process/revision guard intentionally rejects cross-session restoration. The harness then opens the retained changed and recovered paths separately. No mutation is automatically replayed after a timeout. Equality failures retain actual native exports and fail qualification; successful checkpoint recovery does not relabel a failed fixture as passed.

## Regression and isolation checks

The [full offline suite](../../artifacts/engineering-gap-final-regression.log) passed, including contract/catalog validation, Python conformance/runtime helpers, 82 C# checks, the compiled process-lifetime guard and 24 simulator-helper checks. The [channel-build C# run](../../artifacts/engineering-gap-encoder-csharp-tests.log) independently passed 82/82. Focused evidence tests comprise seven safety, thirteen preservation/audit and seven encoder checks; the final [thirteen-test preservation run](../../artifacts/engineering-gap-preservation-tests-final.log) includes the two subsequently added encoder XML-locator tests. These helper tests do not execute Siemens runtime behavior.

Both isolated builds succeeded with no compiler errors; NuGet audit-index lookup warnings were retained. [Final read-only discovery](../../artifacts/engineering-gap-final-discovery.json) confirmed that the pre-existing interactive TIA process `19292` remained available. Detach evidence releases owned Openness attachments; it does not claim synchronous exit of every Siemens background process.
